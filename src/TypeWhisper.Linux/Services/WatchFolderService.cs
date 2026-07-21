using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using TypeWhisper.Core;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Linux.Services;

public sealed class WatchFolderService : IDisposable, IAsyncDisposable
{
    private const int MaxExportPathAttempts = 1000;
    private static readonly TimeSpan s_workerDrainDeadline = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    };

    private readonly ConcurrentDictionary<string, WatchFolderRun> _activeFiles = new(
        StringComparer.OrdinalIgnoreCase
    );

    private readonly List<WatchFolderHistoryItem> _history = [];
    private readonly string _historyPath;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Lock _persistenceGate = new();
    private readonly HashSet<string> _processedFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _processedFingerprintsPath;
    private readonly Lock _stateGate = new();
    private readonly Func<Task, TimeSpan, Task> _waitForWorkers;
    private volatile WatchFolderRun? _currentRun;
    private WatchFolderRun? _currentlyProcessingRun;
    private string? _currentlyProcessing;
    private bool _disposed;
    private string? _watchPath;

    public WatchFolderService()
        : this(TypeWhisperEnvironment.DataPath)
    {
    }

    internal WatchFolderService(string dataPath)
        : this(dataPath, static (workers, timeout) => workers.WaitAsync(timeout))
    {
    }

    internal WatchFolderService(
        string dataPath,
        Func<Task, TimeSpan, Task> waitForWorkers
    )
    {
        _waitForWorkers = waitForWorkers;
        Directory.CreateDirectory(dataPath);
        _processedFingerprintsPath = Path.Join(dataPath, "watch-folder-processed.json");
        _historyPath = Path.Join(dataPath, "watch-folder-history.json");
        LoadProcessedFingerprints();
        LoadHistory();
    }

    // ReSharper disable once UnusedAutoPropertyAccessor.Global  public service-state accessor exposing the active watch path (parallels CurrentlyProcessing/IsRunning)
    public string? WatchPath
    {
        get
        {
            lock (_stateGate)
            {
                return _watchPath;
            }
        }
    }

    public string? CurrentlyProcessing
    {
        get
        {
            lock (_stateGate)
            {
                return _currentlyProcessing;
            }
        }
    }

    public bool IsRunning => _currentRun is not null;

    internal WatchFolderRun? CurrentRun => _currentRun;

    public IReadOnlyList<WatchFolderHistoryItem> History
    {
        get
        {
            lock (_stateGate)
            {
                return _history.ToList();
            }
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(DisposeAsyncCore());
    }

    public void Start(
        WatchFolderOptions options,
        Func<
            WatchFolderTranscriptionRequest,
            CancellationToken,
            Task<WatchFolderTranscriptionResult>
        > transcribeHandler
    )
    {
        _lifecycleGate.Wait();
        try
        {
            ThrowIfDisposed();
            StopCoreAsync().ConfigureAwait(false).GetAwaiter().GetResult();

            if (string.IsNullOrWhiteSpace(options.WatchPath))
            {
                throw new ArgumentException("Watch folder path is required.", nameof(options));
            }

            Directory.CreateDirectory(options.WatchPath);
            if (!string.IsNullOrWhiteSpace(options.OutputPath))
            {
                Directory.CreateDirectory(options.OutputPath);
            }

            StartRun(options, transcribeHandler);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Stop()
    {
        StopAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void ClearHistory()
    {
        lock (_stateGate)
        {
            _history.Clear();
        }

        SaveHistory();
        OnStateChanged();
    }

    public event EventHandler? StateChanged;
    // ReSharper disable once EventNeverSubscribedTo.Global -- public API; raised for each processed file for external/future subscribers.
    public event EventHandler<WatchFolderHistoryItem>? FileProcessed;

    private void StartRun(
        WatchFolderOptions options,
        Func<
            WatchFolderTranscriptionRequest,
            CancellationToken,
            Task<WatchFolderTranscriptionResult>
        > transcribeHandler
    )
    {
        var cancellationSource = new CancellationTokenSource();
        FileSystemWatcher? watcher = null;
        WatchFolderRun run;
        try
        {
            watcher = new FileSystemWatcher(options.WatchPath)
            {
                NotifyFilter =
                    NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            run = new WatchFolderRun(
                cancellationSource,
                options,
                transcribeHandler,
                watcher
            );
            watcher.Created += (_, e) => TryScanEventFolder(run, e.FullPath);
            watcher.Changed += (_, e) => TryScanEventFolder(run, e.FullPath);
            watcher.Renamed += (_, e) => TryScanEventFolder(run, e.FullPath);
            watcher.EnableRaisingEvents = true;
        }
        catch
        {
            watcher?.Dispose();
            cancellationSource.Dispose();
            throw;
        }

        // ReSharper disable once MethodSupportsCancellation -- the worker observes run.CancellationSource internally; passing the token to Task.Run would leave a Canceled task for StopCoreAsync to await.
        var queueWorker = Task.Run(() => ProcessQueueAsync(run));
        // Periodic rescan catches files missed when the OS event buffer overflows.
        // ReSharper disable once MethodSupportsCancellation -- the worker observes run.CancellationSource internally; passing the token to Task.Run would leave a Canceled task for StopCoreAsync to await.
        var rescanWorker = Task.Run(() => RescanLoopAsync(run));
        run.SetWorkers(queueWorker, rescanWorker);

        lock (_stateGate)
        {
            _watchPath = options.WatchPath;
            _currentlyProcessing = null;
            _currentlyProcessingRun = null;
            _currentRun = run;
        }

        ScanFolder(run, options.WatchPath);
        OnStateChanged();
    }

    private async Task StopCoreAsync()
    {
        WatchFolderRun? run;
        lock (_stateGate)
        {
            run = _currentRun;
            _currentRun = null;
        }

        if (run is not null)
        {
            try
            {
                run.Watcher.EnableRaisingEvents = false;
            }
            catch (ObjectDisposedException)
            {
                // A concurrent watcher callback can observe disposal while retiring the run.
            }

            run.Watcher.Dispose();
            try
            {
                // ReSharper disable once MethodHasAsyncOverload -- CancelAsync would add a yield point between watcher teardown and worker cancellation that a concurrent Start could interleave with.
                run.CancellationSource.Cancel();
            }
            catch (AggregateException ex)
            {
                Debug.WriteLine($"WatchFolder cancellation callback failed: {ex}");
            }
        }

        lock (_stateGate)
        {
            _watchPath = null;
            if (run is null || ReferenceEquals(_currentlyProcessingRun, run))
            {
                _currentlyProcessing = null;
                _currentlyProcessingRun = null;
            }
        }

        OnStateChanged();
        if (run is null)
        {
            return;
        }

        var timedOut = false;
        try
        {
            await _waitForWorkers(run.WorkerCompletion, s_workerDrainDeadline)
                .ConfigureAwait(false);
        }
        catch (TimeoutException) when (!run.WorkerCompletion.IsCompleted)
        {
            timedOut = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WatchFolder worker stopped with an error: {ex}");
        }

        if (timedOut)
        {
            run.SetRetiredCleanup(ObserveRetiredRunAsync(run));
            return;
        }

        run.DisposeCancellationSource();
    }

    private static async Task ObserveRetiredRunAsync(WatchFolderRun run)
    {
        try
        {
            await run.WorkerCompletion.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Retired WatchFolder worker stopped with an error: {ex}");
        }
        finally
        {
            run.DisposeCancellationSource();
        }
    }

    private async Task DisposeAsyncCore()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void TryScanEventFolder(WatchFolderRun run, string filePath)
    {
        if (!IsRunCurrentAndLive(run))
        {
            return;
        }

        try
        {
            var folderPath = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                ScanFolder(run, folderPath);
            }
        }
        catch (Exception ex) when (IsExpectedFolderScanException(ex))
        {
            Debug.WriteLine($"WatchFolder event scan failed: {ex}");
        }
    }

    private void ScanFolder(WatchFolderRun run, string folderPath)
    {
        if (!IsRunCurrentAndLive(run) || !Directory.Exists(folderPath))
        {
            return;
        }

        try
        {
            foreach (
                var filePath in Directory
                    .EnumerateFiles(folderPath)
                    .Where(AudioFileService.IsSupported)
                    .OrderBy(Path.GetFileName)
            )
            {
                if (!IsRunCurrentAndLive(run))
                {
                    return;
                }

                EnqueueFile(run, filePath);
            }
        }
        catch (Exception ex) when (IsExpectedFolderScanException(ex))
        {
            Debug.WriteLine($"WatchFolder scan failed: {ex}");
        }
    }

    private void EnqueueFile(WatchFolderRun run, string filePath)
    {
        if (!IsRunCurrentAndLive(run))
        {
            return;
        }

        var fullPath = Path.GetFullPath(filePath);
        if (_activeFiles.ContainsKey(fullPath))
        {
            return;
        }

        var fingerprint = CreateFingerprint(fullPath);
        if (fingerprint is null || IsKnownFingerprint(run, fingerprint))
        {
            return;
        }

        if (!run.QueuedFiles.TryAdd(fullPath, 0))
        {
            return;
        }

        if (!IsRunCurrentAndLive(run))
        {
            run.QueuedFiles.TryRemove(fullPath, out _);
            return;
        }

        run.PendingFiles.Enqueue(fullPath);
    }

    private async Task ProcessQueueAsync(WatchFolderRun run)
    {
        var ct = run.CancellationSource.Token;
        while (!ct.IsCancellationRequested)
        {
            if (!run.PendingFiles.TryDequeue(out var filePath))
            {
                try
                {
                    await Task.Delay(500, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            run.QueuedFiles.TryRemove(filePath, out _);
            try
            {
                await ProcessFileAsync(run, filePath, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RescanLoopAsync(WatchFolderRun run)
    {
        var ct = run.CancellationSource.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                ScanFolder(run, run.Options.WatchPath);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (IsExpectedFolderScanException(ex))
            {
                Debug.WriteLine($"WatchFolder rescan failed: {ex}");
            }
        }
    }

    private async Task ProcessFileAsync(
        WatchFolderRun run,
        string filePath,
        CancellationToken ct
    )
    {
        filePath = Path.GetFullPath(filePath);
        var fileName = Path.GetFileName(filePath);
        string? fingerprint = null;
        if (!_activeFiles.TryAdd(filePath, run))
        {
            return;
        }

        try
        {
            // Inside the try so a throwing state notification still runs the finally that
            // releases this run's reservation; _activeFiles is never cleared on stop.
            SetCurrentlyProcessing(run, fileName);
            await WaitForFileReadyAsync(filePath, ct);
            ct.ThrowIfCancellationRequested();
            if (!IsRunCurrentAndLive(run))
            {
                return;
            }

            fingerprint = CreateFingerprint(filePath);
            if (fingerprint is null || IsKnownFingerprint(run, fingerprint))
            {
                return;
            }

            var result = await run.TranscribeHandler(
                new WatchFolderTranscriptionRequest(filePath),
                ct
            );
            ct.ThrowIfCancellationRequested();
            if (!IsRunCurrentAndLive(run))
            {
                return;
            }

            var outputFolder = string.IsNullOrWhiteSpace(run.Options.OutputPath)
                ? run.Options.WatchPath
                : run.Options.OutputPath!;
            Directory.CreateDirectory(outputFolder);

            var artifact = WatchFolderExportBuilder.Build(
                run.Options.OutputFormat,
                result,
                fileName,
                ResolveEngineName(result),
                DateTime.Now
            );
            ct.ThrowIfCancellationRequested();
            if (!IsRunCurrentAndLive(run))
            {
                return;
            }

            var outputPath = CommitExport(
                outputFolder,
                Path.GetFileNameWithoutExtension(filePath),
                artifact,
                ct
            );

            string? sourceDeletionError = null;
            if (run.Options.DeleteSource)
            {
                // The export write ignores the token; re-check so a Stop that lands mid-commit
                // can't still delete the source.
                ct.ThrowIfCancellationRequested();
                if (!IsRunCurrentAndLive(run))
                {
                    return;
                }

                try
                {
                    File.Delete(filePath);
                }
                catch (Exception ex)
                {
                    sourceDeletionError =
                        $"Transcribed, but the source file could not be deleted: {ex.Message}";
                    Debug.WriteLine($"WatchFolder source deletion failed: {ex}");
                }
            }

            ct.ThrowIfCancellationRequested();
            if (!IsRunCurrentAndLive(run))
            {
                return;
            }

            AddProcessedFingerprint(run, fingerprint);
            AddHistory(
                run,
                new WatchFolderHistoryItem
                {
                    Id = Guid.NewGuid().ToString(),
                    FileName = fileName,
                    ProcessedAtUtc = DateTime.UtcNow,
                    OutputPath = outputPath,
                    Success = true,
                    ErrorMessage = sourceDeletionError,
                }
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException) when (!File.Exists(filePath))
        {
            Debug.WriteLine($"WatchFolder skipped deleted file: {filePath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WatchFolder transcription failed: {ex.Message}");
            if (!IsRunCurrentAndLive(run))
            {
                return;
            }

            if (fingerprint is not null)
            {
                AddFailedFingerprint(run, fingerprint);
            }

            AddHistory(
                run,
                new WatchFolderHistoryItem
                {
                    Id = Guid.NewGuid().ToString(),
                    FileName = fileName,
                    ProcessedAtUtc = DateTime.UtcNow,
                    OutputPath = "",
                    Success = false,
                    ErrorMessage = ex.Message,
                }
            );
        }
        finally
        {
            _activeFiles.TryRemove(new KeyValuePair<string, WatchFolderRun>(filePath, run));
            ClearCurrentlyProcessing(run);
        }
    }

    private static string CommitExport(
        string outputFolder,
        string baseName,
        WatchFolderExportArtifact artifact,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();

        for (var attempt = 0; attempt < MaxExportPathAttempts; attempt++)
        {
            var fileName = attempt == 0
                ? $"{baseName}.{artifact.FileExtension}"
                : $"{baseName} ({attempt}).{artifact.FileExtension}";
            var outputPath = Path.Join(outputFolder, fileName);
            if (PathIsOccupied(outputPath))
            {
                continue;
            }

            try
            {
                AtomicFileWrite.WriteAllTextCreateNew(outputPath, artifact.Content);
                return outputPath;
            }
            catch (IOException) when (PathIsOccupied(outputPath))
            {
                // Another actor claimed the candidate after the fast-path check; try the
                // next suffix.
            }
        }

        throw new IOException(
            $"Could not create a unique watch-folder export for '{baseName}.{artifact.FileExtension}' "
            + $"after {MaxExportPathAttempts} attempts."
        );
    }

    // A directory on the candidate name also blocks the export move, so treat it as taken.
    private static bool PathIsOccupied(string path)
    {
        return File.Exists(path) || Directory.Exists(path);
    }

    private static string ResolveEngineName(WatchFolderTranscriptionResult result)
    {
        if (
            !string.IsNullOrWhiteSpace(result.EngineId)
            && !string.IsNullOrWhiteSpace(result.ModelId)
        )
        {
            return $"{result.EngineId} / {result.ModelId}";
        }

        return result.EngineId ?? result.ModelId ?? "Default";
    }

    private bool IsRunCurrentAndLive(WatchFolderRun run)
    {
        return ReferenceEquals(_currentRun, run)
               && !run.CancellationSource.IsCancellationRequested;
    }

    private void SetCurrentlyProcessing(WatchFolderRun run, string fileName)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(_currentRun, run) || run.CancellationSource.IsCancellationRequested)
            {
                return;
            }

            _currentlyProcessing = fileName;
            _currentlyProcessingRun = run;
        }

        if (IsRunCurrentAndLive(run))
        {
            OnStateChanged();
        }
    }

    private void ClearCurrentlyProcessing(WatchFolderRun run)
    {
        lock (_stateGate)
        {
            if (
                !ReferenceEquals(_currentRun, run)
                || !ReferenceEquals(_currentlyProcessingRun, run)
            )
            {
                return;
            }

            _currentlyProcessing = null;
            _currentlyProcessingRun = null;
        }

        if (IsRunCurrentAndLive(run))
        {
            OnStateChanged();
        }
    }

    internal bool OwnsActiveFile(WatchFolderRun run, string filePath)
    {
        return _activeFiles.TryGetValue(Path.GetFullPath(filePath), out var owner)
               && ReferenceEquals(owner, run);
    }

    private bool IsKnownFingerprint(WatchFolderRun run, string fingerprint)
    {
        lock (_persistenceGate)
        {
            if (_processedFingerprints.Contains(fingerprint))
            {
                return true;
            }
        }

        lock (run.FailedFingerprintsGate)
        {
            return run.FailedFingerprints.Contains(fingerprint);
        }
    }

    private void AddProcessedFingerprint(WatchFolderRun run, string fingerprint)
    {
        if (!IsRunCurrentAndLive(run))
        {
            return;
        }

        lock (run.FailedFingerprintsGate)
        {
            run.FailedFingerprints.Remove(fingerprint);
        }

        lock (_persistenceGate)
        {
            if (!IsRunCurrentAndLive(run))
            {
                return;
            }

            _processedFingerprints.Add(fingerprint);
            SaveProcessedFingerprintsCore();
        }
    }

    private static void AddFailedFingerprint(WatchFolderRun run, string fingerprint)
    {
        lock (run.FailedFingerprintsGate)
        {
            run.FailedFingerprints.Add(fingerprint);
        }
    }

    private void AddHistory(WatchFolderRun run, WatchFolderHistoryItem item)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(_currentRun, run) || run.CancellationSource.IsCancellationRequested)
            {
                return;
            }

            _history.Insert(0, item);
            if (_history.Count > 100)
            {
                _history.RemoveRange(100, _history.Count - 100);
            }
        }

        SaveHistory();
        if (IsRunCurrentAndLive(run))
        {
            FileProcessed?.Invoke(this, item);
        }

        if (IsRunCurrentAndLive(run))
        {
            OnStateChanged();
        }
    }

    private static async Task WaitForFileReadyAsync(string path, CancellationToken ct)
    {
        // Poll until size+mtime are stable across two 250 ms reads and exclusive
        // open succeeds — guards against files still being written by a recorder
        // or copy. Up to 40 × 250 ms = 10 s before giving up.
        long? previousLength = null;
        DateTime? previousWrite = null;
        var stableReads = 0;

        for (var i = 0; i < 40; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Watch folder file no longer exists.", path);
            }

            try
            {
                var info = new FileInfo(path);
                await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                var currentLength = info.Length;
                var currentWrite = info.LastWriteTimeUtc;

                if (previousLength == currentLength && previousWrite == currentWrite)
                {
                    stableReads++;
                }
                else
                {
                    stableReads = 0;
                }

                if (stableReads >= 1)
                {
                    return;
                }

                previousLength = currentLength;
                previousWrite = currentWrite;
            }
            catch (IOException)
            {
                stableReads = 0;
            }
            catch (UnauthorizedAccessException)
            {
                stableReads = 0;
            }

            await Task.Delay(250, ct);
        }

        throw new IOException("Watch folder file is still being written.");
    }

    private static string? CreateFingerprint(string path)
    {
        // path+size+mtime is cheaper than a content hash for large audio files
        // and is stable once the file stops changing.
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var info = new FileInfo(path);
            return $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return null;
        }
    }

    private void LoadProcessedFingerprints()
    {
        try
        {
            if (!File.Exists(_processedFingerprintsPath))
            {
                return;
            }

            var json = File.ReadAllText(_processedFingerprintsPath);
            var loaded = JsonSerializer.Deserialize<HashSet<string>>(json, s_jsonOptions);
            if (loaded is null)
            {
                return;
            }

            foreach (var fingerprint in loaded)
            {
                _processedFingerprints.Add(fingerprint);
            }
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            Debug.WriteLine($"Failed to load watch folder fingerprints: {ex}");
        }
    }

    private void SaveProcessedFingerprintsCore()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_processedFingerprintsPath)!);
            var json = JsonSerializer.Serialize(_processedFingerprints, s_jsonOptions);
            File.WriteAllText(_processedFingerprintsPath, json);
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            Debug.WriteLine($"Failed to save watch folder fingerprints: {ex}");
        }
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(_historyPath))
            {
                return;
            }

            var json = File.ReadAllText(_historyPath);
            var loaded = JsonSerializer.Deserialize<List<WatchFolderHistoryItem>>(
                json,
                s_jsonOptions
            );
            if (loaded is null)
            {
                return;
            }

            _history.Clear();
            _history.AddRange(loaded.Take(100));
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            Debug.WriteLine($"Failed to load watch folder history: {ex}");
        }
    }

    private void SaveHistory()
    {
        try
        {
            List<WatchFolderHistoryItem> snapshot;
            lock (_stateGate)
            {
                snapshot = _history.Take(100).ToList();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
            var json = JsonSerializer.Serialize(snapshot, s_jsonOptions);
            File.WriteAllText(_historyPath, json);
        }
        catch (Exception ex) when (IsExpectedPersistenceException(ex))
        {
            Debug.WriteLine($"Failed to save watch folder history: {ex}");
        }
    }

    private void OnStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // A notification subscriber must not abort lifecycle cleanup (worker drain / CTS disposal).
            Debug.WriteLine($"WatchFolder StateChanged subscriber threw: {ex}");
        }
    }

    private static bool IsExpectedFolderScanException(Exception ex)
    {
        return ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException;
    }

    private static bool IsExpectedPersistenceException(Exception ex)
    {
        return ex
            is IOException
            or UnauthorizedAccessException
            or DirectoryNotFoundException
            or JsonException
            or NotSupportedException;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    internal sealed class WatchFolderRun
    {
        private int _cancellationSourceDisposed;

        internal WatchFolderRun(
            CancellationTokenSource cancellationSource,
            WatchFolderOptions options,
            Func<
                WatchFolderTranscriptionRequest,
                CancellationToken,
                Task<WatchFolderTranscriptionResult>
            > transcribeHandler,
            FileSystemWatcher watcher
        )
        {
            CancellationSource = cancellationSource;
            Options = options;
            TranscribeHandler = transcribeHandler;
            Watcher = watcher;
        }

        internal CancellationTokenSource CancellationSource { get; }
        internal WatchFolderOptions Options { get; }

        internal Func<
            WatchFolderTranscriptionRequest,
            CancellationToken,
            Task<WatchFolderTranscriptionResult>
        > TranscribeHandler { get; }

        internal FileSystemWatcher Watcher { get; }
        internal ConcurrentQueue<string> PendingFiles { get; } = [];

        internal ConcurrentDictionary<string, byte> QueuedFiles { get; } = new(
            StringComparer.OrdinalIgnoreCase
        );

        internal Lock FailedFingerprintsGate { get; } = new();
        internal HashSet<string> FailedFingerprints { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Task WorkerCompletion { get; private set; } = Task.CompletedTask;
        internal Task RetiredCleanup { get; private set; } = Task.CompletedTask;

        internal void SetWorkers(Task queueWorker, Task rescanWorker)
        {
            WorkerCompletion = Task.WhenAll(queueWorker, rescanWorker);
        }

        internal void SetRetiredCleanup(Task retiredCleanup)
        {
            RetiredCleanup = retiredCleanup;
        }

        internal void DisposeCancellationSource()
        {
            if (Interlocked.Exchange(ref _cancellationSourceDisposed, 1) == 0)
            {
                CancellationSource.Dispose();
            }
        }
    }
}
