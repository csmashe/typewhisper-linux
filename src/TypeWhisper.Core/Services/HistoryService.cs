using System.Diagnostics;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
///     File-backed <see cref="IHistoryService" />: persists transcription records as JSON, caches
///     them with running totals, and deletes the associated audio files when records are removed.
/// </summary>
public sealed partial class HistoryService : IHistoryService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly string? _audioDirectory;
    private readonly string _filePath;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private List<TranscriptionRecord> _cache = [];

    private bool _cacheLoaded;

    // Set when the on-disk file existed but couldn't be read; SaveToDisk
    // refuses to write while set to prevent a transient IO error from
    // replacing the user's history with a one-entry file.
    private bool _cacheLoadFailed;
    private List<string> _distinctApps = [];
    private double _totalDuration;

    private int _totalRecords;
    private int _totalWords;

    public HistoryService(string filePath, string? audioDirectory = null)
    {
        _filePath = filePath;
        _audioDirectory = audioDirectory;
    }

    public IReadOnlyList<TranscriptionRecord> Records
    {
        get
        {
            EnsureCacheLoaded();
            lock (_gate)
            {
                return _cache.ToList();
            }
        }
    }

    public event Action? RecordsChanged;

    // Fast path when already loaded; the fallback triggers a synchronous load via Records.
    public int TotalRecords => _cacheLoaded ? _totalRecords : Records.Count;
    public int TotalWords => _cacheLoaded ? _totalWords : Records.Sum(r => r.WordCount);

    public double TotalDuration =>
        _cacheLoaded ? _totalDuration : Records.Sum(r => r.DurationSeconds);

    public async Task EnsureLoadedAsync()
    {
        if (_cacheLoaded)
        {
            return;
        }

        await _loadLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cacheLoaded)
            {
                return;
            }

            var records = await Task.Run(LoadFromDisk).ConfigureAwait(false);
            lock (_gate)
            {
                _cache = records;
                RebuildStats();
                _cacheLoaded = true;
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public IReadOnlyList<string> GetDistinctApps()
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            return _distinctApps.ToList();
        }
    }

    public void AddRecord(TranscriptionRecord record)
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            // Stage on a copy and persist before mutating _cache so a save failure
            // can't leave in-memory state ahead of disk.
            var newCache = new List<TranscriptionRecord>(_cache.Count + 1) { record };
            newCache.AddRange(_cache);
            SaveToDisk(newCache);

            _cache = newCache;
            _totalRecords++;
            _totalWords += record.WordCount;
            _totalDuration += record.DurationSeconds;
            if (
                !string.IsNullOrEmpty(record.AppProcessName)
                && !_distinctApps.Contains(record.AppProcessName, StringComparer.OrdinalIgnoreCase)
            )
            {
                _distinctApps.Add(record.AppProcessName);
                _distinctApps.Sort(StringComparer.OrdinalIgnoreCase);
            }
        }

        RecordsChanged?.Invoke();
    }

    public void UpdateRecord(string id, string finalText)
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            var idx = _cache.FindIndex(r => r.Id == id);
            if (idx < 0)
            {
                return;
            }

            var old = _cache[idx];
            var updated = old with { FinalText = finalText };
            var newCache = new List<TranscriptionRecord>(_cache) { [idx] = updated };
            SaveToDisk(newCache);

            _cache = newCache;
            _totalWords += updated.WordCount - old.WordCount;
        }

        RecordsChanged?.Invoke();
    }

    public void SetPendingCorrectionSuggestions(
        string id,
        IReadOnlyList<CorrectionSuggestion> suggestions
    )
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            var idx = _cache.FindIndex(r => r.Id == id);
            if (idx < 0)
            {
                return;
            }

            var newCache = new List<TranscriptionRecord>(_cache);
            newCache[idx] = newCache[idx] with { PendingCorrectionSuggestions = suggestions.ToList() };
            SaveToDisk(newCache);

            _cache = newCache;
        }

        RecordsChanged?.Invoke();
    }

    public void DeleteRecord(string id)
    {
        EnsureCacheLoaded();
        string? removedAudioFileName;
        lock (_gate)
        {
            var idx = _cache.FindIndex(r => r.Id == id);
            if (idx < 0)
            {
                return;
            }

            var removed = _cache[idx];
            var newCache = new List<TranscriptionRecord>(_cache);
            newCache.RemoveAt(idx);
            SaveToDisk(newCache);

            _cache = newCache;
            _totalRecords--;
            _totalWords -= removed.WordCount;
            _totalDuration -= removed.DurationSeconds;
            removedAudioFileName = removed.AudioFileName;

            RebuildDistinctApps();
        }

        DeleteAudioFile(removedAudioFileName);
        RecordsChanged?.Invoke();
    }

    public void ClearAll()
    {
        EnsureCacheLoaded();
        List<string?> audioFiles;
        lock (_gate)
        {
            // Persist the empty list before clearing in-memory state; otherwise
            // a save failure would delete audio files for records still in history.json.
            audioFiles = _cache.Select(r => r.AudioFileName).ToList();
            SaveToDisk([]);

            _cache.Clear();
            _totalRecords = 0;
            _totalWords = 0;
            _totalDuration = 0;
            _distinctApps.Clear();
        }

        DeleteAudioFiles(audioFiles);
        RecordsChanged?.Invoke();
    }

    public IReadOnlyList<TranscriptionRecord> Search(string query)
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return _cache.ToList();
            }

            return _cache
                .Where(r =>
                    r.FinalText.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || r.RawText.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || (r.AppName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                )
                .ToList();
        }
    }

    public void PurgeOldRecords(TimeSpan? retention)
    {
        if (retention is null)
        {
            return;
        }

        EnsureCacheLoaded();
        var cutoff = DateTime.UtcNow - retention.Value;
        List<string?> removedAudioFiles;

        lock (_gate)
        {
            removedAudioFiles = _cache
                .Where(r => r.CreatedAt < cutoff)
                .Select(r => r.AudioFileName)
                .ToList();

            if (removedAudioFiles.Count == 0)
            {
                return;
            }

            var newCache = _cache.Where(r => r.CreatedAt >= cutoff).ToList();
            SaveToDisk(newCache);

            _cache = newCache;
            RebuildStats();
        }

        DeleteAudioFiles(removedAudioFiles);
        RecordsChanged?.Invoke();
    }

    // Synchronous fallback. Do not call from a thread that already holds _loadLock — deadlock.
    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded)
        {
            return;
        }

        _loadLock.Wait();
        try
        {
            if (_cacheLoaded)
            {
                return;
            }

            var records = LoadFromDisk();
            lock (_gate)
            {
                _cache = records;
                RebuildStats();
                _cacheLoaded = true;
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private List<TranscriptionRecord> LoadFromDisk()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        string json;
        try
        {
            json = File.ReadAllText(_filePath);
        }
        catch (Exception ex)
        {
            // Unreadable (transient IO / permissions). Set _cacheLoadFailed so
            // SaveToDisk won't overwrite existing data on the next AddRecord.
            Trace.WriteLine($"[HistoryService] Failed to read history from '{_filePath}': {ex}");
            _cacheLoadFailed = true;
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<TranscriptionRecord>>(json) ?? [];
        }
        catch (JsonException ex)
        {
            Trace.WriteLine($"[HistoryService] Failed to parse history from '{_filePath}': {ex}");
            PreserveBrokenFile(_filePath);
            return [];
        }
    }

    private void SaveToDisk(IReadOnlyList<TranscriptionRecord> records)
    {
        if (_cacheLoadFailed)
        {
            Trace.WriteLine(
                $"[HistoryService] Skipping save to '{_filePath}': previous load failed and overwriting would discard existing data."
            );
            throw new IOException(
                $"Refusing to overwrite '{_filePath}' because the previous load failed."
            );
        }

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(records, s_jsonOptions);

        // Atomic write via temp file so a mid-write crash can't truncate history.
        var tempPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            if (File.Exists(_filePath))
            {
                File.Replace(tempPath, _filePath, null);
            }
            else
            {
                File.Move(tempPath, _filePath);
            }
        }
        catch
        {
            if (!File.Exists(tempPath))
            {
                throw;
            }

            try { File.Delete(tempPath); }
            catch
            {
                /* best effort */
            }

            throw;
        }
    }

    private static void PreserveBrokenFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var brokenPath = $"{path}.broken-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(path, brokenPath);
            Trace.WriteLine(
                $"[HistoryService] Preserved unreadable file as {brokenPath}"
            );
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[HistoryService] Could not preserve unreadable file: {ex.Message}"
            );
        }
    }

    private void RebuildStats()
    {
        _totalRecords = _cache.Count;
        _totalWords = _cache.Sum(r => r.WordCount);
        _totalDuration = _cache.Sum(r => r.DurationSeconds);
        RebuildDistinctApps();
    }

    private void RebuildDistinctApps()
    {
        _distinctApps = _cache
            .Select(r => r.AppProcessName)
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order()
            .ToList()!;
    }

    private void DeleteAudioFile(string? audioFileName)
    {
        if (string.IsNullOrEmpty(audioFileName) || string.IsNullOrEmpty(_audioDirectory))
        {
            return;
        }

        try
        {
            // history.json can be hand-edited, so treat audioFileName as untrusted:
            // strip directory components and confirm the resolved path stays inside
            // _audioDirectory before deleting.
            var safeName = Path.GetFileName(audioFileName);
            if (string.IsNullOrEmpty(safeName) || Path.IsPathRooted(audioFileName))
            {
                return;
            }

            var directoryRoot = Path.GetFullPath(_audioDirectory);
            var separator = Path.DirectorySeparatorChar;
            if (!directoryRoot.EndsWith(separator))
            {
                directoryRoot += separator;
            }

            var candidate = Path.GetFullPath(Path.Join(_audioDirectory, safeName));
            if (!candidate.StartsWith(directoryRoot, StringComparison.Ordinal))
            {
                return;
            }

            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
        catch (Exception ex)
        {
            // Best-effort cleanup: a locked file or permission error shouldn't fail the
            // record delete, but log it so a recurring problem (e.g. orphaned audio) is visible.
            Trace.WriteLine(
                $"[HistoryService] Could not delete audio file '{audioFileName}': {ex.Message}"
            );
        }
    }

    private void DeleteAudioFiles(IEnumerable<string?> audioFileNames)
    {
        foreach (var audioFileName in audioFileNames)
        {
            DeleteAudioFile(audioFileName);
        }
    }
}