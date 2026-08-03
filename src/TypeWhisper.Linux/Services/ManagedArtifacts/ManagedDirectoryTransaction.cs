using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Linux.Services.ManagedArtifacts;

/// <summary>
///     Journaled same-filesystem directory replacement. A caller stages a complete tree,
///     commits it under a per-artifact cross-process lock, then either accepts the new live
///     tree or rolls it back to the saved tree.
/// </summary>
public sealed class ManagedDirectoryTransaction
{
    private const string BackupDirectoryName = "backup";
    private const string JournalFileName = "pending.json";
    private const string LockFileName = "transaction.lock";
    private const string StagePrefix = "stage-";
    private const string RejectedPrefix = "rejected-";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
    private static readonly TimeSpan s_lockAcquisitionTimeout = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_processLocks = new(
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal
    );

    private readonly bool _cleanupAbandonedStages;
    private readonly Func<ManagedDirectoryCheckpoint, CancellationToken, Task>? _checkpoint;
    private readonly ManagedDirectoryRecoveryMode _recoveryMode;
    private readonly string _stateRoot;
    private readonly bool _useCrossProcessLock;

    public ManagedDirectoryTransaction(
        string stateRoot,
        ManagedDirectoryRecoveryMode recoveryMode = ManagedDirectoryRecoveryMode.RestoreBackup
    )
        : this(
            stateRoot,
            recoveryMode,
            useCrossProcessLock: true,
            cleanupAbandonedStages: false,
            checkpoint: null
        ) { }

    internal ManagedDirectoryTransaction(
        string stateRoot,
        ManagedDirectoryRecoveryMode recoveryMode,
        bool useCrossProcessLock,
        bool cleanupAbandonedStages,
        Func<ManagedDirectoryCheckpoint, CancellationToken, Task>? checkpoint = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        _stateRoot = Path.GetFullPath(stateRoot);
        _recoveryMode = recoveryMode;
        _useCrossProcessLock = useCrossProcessLock;
        _cleanupAbandonedStages = cleanupAbandonedStages;
        _checkpoint = checkpoint;
    }

    /// <summary>Creates a unique staging directory below the transaction state root.</summary>
    public string CreateStageDirectory(string artifactId)
    {
        var paths = PreparePaths(artifactId);
        while (true)
        {
            var stage = Path.Join(paths.Directory, $"{StagePrefix}{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(stage);
                return stage;
            }
            catch (IOException) when (Directory.Exists(stage) || File.Exists(stage))
            {
                // An astronomically unlikely GUID collision is harmless; choose another name.
            }
        }
    }

    /// <summary>
    ///     Recovers an interrupted replacement before the live directory is used.
    /// </summary>
    public async Task RecoverAsync(
        string artifactId,
        string destinationPath,
        CancellationToken ct = default
    )
    {
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        ValidateDestinationPath(artifactId, fullDestinationPath);
        var paths = PreparePaths(artifactId);
        await using var artifactLock = await AcquireLockAsync(paths, ct).ConfigureAwait(false);
        RecoverUnderLock(paths, fullDestinationPath, preservedStage: null);
        CleanupStateDirectory(paths);
    }

    /// <summary>Recovers every journal found below this transaction's state root.</summary>
    public async Task RecoverAllAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_stateRoot))
        {
            return;
        }

        foreach (
            var artifactDirectory in Directory
                .GetDirectories(_stateRoot, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal)
        )
        {
            ct.ThrowIfCancellationRequested();
            var paths = new DirectoryPaths(artifactDirectory);
            var journal = TryReadJournal(paths);
            if (journal is null)
            {
                continue;
            }

            await RecoverAsync(journal.ArtifactId, journal.DestinationPath, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Deletes stages, rejected trees, and caller scratch left by an interrupted or
    ///     rolled-back transaction, so failed attempts don't accumulate forever. Run after
    ///     <see cref="RecoverAllAsync" /> and before any new transaction; artifacts whose
    ///     journal or backup survived recovery are skipped so nothing recoverable is discarded.
    /// </summary>
    public async Task PurgeAbandonedArtifactsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_stateRoot))
        {
            return;
        }

        foreach (
            var artifactDirectory in Directory
                .GetDirectories(_stateRoot, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal)
        )
        {
            ct.ThrowIfCancellationRequested();
            var paths = new DirectoryPaths(artifactDirectory);
            if (File.Exists(paths.Journal) || Directory.Exists(paths.Backup))
            {
                continue;
            }

            // Checked before locking so the steady state — one lock file per installed
            // artifact and nothing else — costs no lock acquisitions at all.
            if (!HasPurgeableEntries(paths))
            {
                continue;
            }

            await using var artifactLock = await AcquireLockAsync(paths, ct).ConfigureAwait(false);
            if (File.Exists(paths.Journal) || Directory.Exists(paths.Backup))
            {
                continue;
            }

            foreach (var entry in Directory.GetFileSystemEntries(artifactDirectory))
            {
                if (string.Equals(entry, paths.Lock, PathComparison))
                {
                    continue;
                }

                try
                {
                    if (Directory.Exists(entry))
                    {
                        Directory.Delete(entry, recursive: true);
                    }
                    else
                    {
                        File.Delete(entry);
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(
                        $"[ManagedDirectoryTransaction] Failed to purge '{entry}': {ex.Message}"
                    );
                }
            }
        }
    }

    private static bool HasPurgeableEntries(DirectoryPaths paths)
    {
        return Directory
            .EnumerateFileSystemEntries(paths.Directory)
            .Any(entry => !string.Equals(entry, paths.Lock, PathComparison));
    }

    /// <summary>
    ///     Publishes a validated stage and retains the previous live directory as a backup.
    ///     The returned commit must be completed or rolled back by the caller.
    /// </summary>
    public async Task<ManagedDirectoryCommit> CommitAsync(
        string artifactId,
        string stagePath,
        string destinationPath,
        Func<CancellationToken, Task>? beforePublish = null,
        CancellationToken ct = default
    )
    {
        var paths = PreparePaths(artifactId);
        var fullStagePath = Path.GetFullPath(stagePath);
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        ValidateDestinationPath(artifactId, fullDestinationPath);
        ValidateStagePath(paths, fullStagePath);

        var artifactLock = await AcquireLockAsync(paths, ct).ConfigureAwait(false);
        try
        {
            RecoverUnderLock(paths, fullDestinationPath, fullStagePath);
            if (!Directory.Exists(fullStagePath))
            {
                throw new DirectoryNotFoundException(
                    $"Managed directory stage does not exist: {fullStagePath}"
                );
            }

            if (Directory.Exists(paths.Backup) || File.Exists(paths.Backup))
            {
                throw new IOException(
                    $"Managed directory backup is already present: {paths.Backup}"
                );
            }

            var oldExists = Directory.Exists(fullDestinationPath);
            if (beforePublish is not null)
            {
                await beforePublish(ct).ConfigureAwait(false);
            }

            var journal = new ManagedDirectoryJournal
            {
                ArtifactId = artifactId,
                DestinationPath = fullDestinationPath,
                OldExists = oldExists,
                RecoveryMode = _recoveryMode,
                State = ManagedDirectoryJournalState.Pending,
            };
            WriteJournal(paths, journal);
            await CheckpointAsync(ManagedDirectoryCheckpoint.AfterJournal, ct)
                .ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            if (oldExists)
            {
                Directory.Move(fullDestinationPath, paths.Backup);
            }

            await CheckpointAsync(ManagedDirectoryCheckpoint.AfterBackup, CancellationToken.None)
                .ConfigureAwait(false);

            try
            {
                Directory.Move(fullStagePath, fullDestinationPath);
            }
            catch (Exception commitException)
            {
                try
                {
                    if (oldExists && Directory.Exists(paths.Backup))
                    {
                        Directory.Move(paths.Backup, fullDestinationPath);
                    }

                    DeleteJournal(paths);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "Failed to publish the staged directory and restore its previous tree.",
                        commitException,
                        rollbackException
                    );
                }

                throw;
            }

            await CheckpointAsync(ManagedDirectoryCheckpoint.AfterPublish, CancellationToken.None)
                .ConfigureAwait(false);
            return new ManagedDirectoryCommit(paths, journal, artifactLock);
        }
        catch
        {
            await artifactLock.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void ValidateStagePath(DirectoryPaths paths, string stagePath)
    {
        var parent = Path.GetDirectoryName(stagePath);
        if (
            !string.Equals(parent, paths.Directory, PathComparison)
            || !Path.GetFileName(stagePath).StartsWith(StagePrefix, StringComparison.Ordinal)
        )
        {
            throw new ArgumentException(
                "A managed directory stage must be a direct stage-* child of its artifact state directory.",
                nameof(stagePath)
            );
        }
    }

    private void ValidateDestinationPath(string artifactId, string destinationPath)
    {
        var stateParent = Path.GetDirectoryName(_stateRoot);
        if (
            stateParent is null
            || !string.Equals(
                Path.GetDirectoryName(destinationPath),
                stateParent,
                PathComparison
            )
            || !string.Equals(
                Path.GetFileName(destinationPath),
                artifactId,
                StringComparison.Ordinal
            )
        )
        {
            throw new ArgumentException(
                "A managed directory destination must be the artifact-named sibling of the transaction state root.",
                nameof(destinationPath)
            );
        }
    }

    private DirectoryPaths PreparePaths(string artifactId)
    {
        ValidateArtifactId(artifactId);
        Directory.CreateDirectory(_stateRoot);
        var directory = Path.Join(_stateRoot, artifactId);
        Directory.CreateDirectory(directory);
        return new DirectoryPaths(directory);
    }

    private static void ValidateArtifactId(string artifactId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);
        if (
            artifactId is "." or ".."
            || artifactId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
                >= 0
            || Path.IsPathRooted(artifactId)
        )
        {
            throw new ArgumentException("Artifact ids must be single safe path segments.", nameof(artifactId));
        }
    }

    private async Task<IAsyncDisposable> AcquireLockAsync(
        DirectoryPaths paths,
        CancellationToken ct
    )
    {
        if (!_useCrossProcessLock)
        {
            return NoopAsyncDisposable.Instance;
        }

        var processLock = s_processLocks.GetOrAdd(paths.Lock, _ => new SemaphoreSlim(1, 1));
        await processLock.WaitAsync(ct).ConfigureAwait(false);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                paths.Lock,
                new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.ReadWrite,
                    Options = FileOptions.Asynchronous,
                }
            );

            // A filesystem without working byte-range locks (some FUSE and network-backed
            // homes) fails every attempt with IOException, which is indistinguishable from
            // contention; without a deadline startup recovery would wait forever.
            var deadline = DateTime.UtcNow + s_lockAcquisitionTimeout;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                IOException lastFailure;
                try
                {
                    if (OperatingSystem.IsMacOS())
                    {
                        throw new PlatformNotSupportedException(
                            "Managed directory locking requires Linux or Windows file locks."
                        );
                    }

                    stream.Lock(0, 1);
                    return new DirectoryLock(stream, processLock);
                }
                catch (IOException ex)
                {
                    lastFailure = ex;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException(
                        $"Could not lock '{paths.Lock}' within {s_lockAcquisitionTimeout.TotalSeconds:0} "
                            + "seconds; it is either held by another process or the filesystem does "
                            + "not support byte-range locks.",
                        lastFailure
                    );
                }

                await Task.Delay(25, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            processLock.Release();
            throw;
        }
    }

    private void RecoverUnderLock(
        DirectoryPaths paths,
        string destinationPath,
        string? preservedStage
    )
    {
        var journal = TryReadJournal(paths);
        if (journal is not null)
        {
            if (
                !string.Equals(journal.DestinationPath, destinationPath, PathComparison)
                || !string.Equals(journal.ArtifactId, Path.GetFileName(paths.Directory), StringComparison.Ordinal)
            )
            {
                throw new InvalidDataException(
                    $"Managed directory journal does not match '{destinationPath}'."
                );
            }

            RecoverJournaledSwap(paths, destinationPath, journal);
            DeleteJournal(paths);
        }
        else
        {
            RecoverLegacySwap(paths, destinationPath);
        }

        if (!_cleanupAbandonedStages)
        {
            return;
        }

        foreach (
            var stage in Directory
                .GetDirectories(paths.Directory, $"{StagePrefix}*", SearchOption.TopDirectoryOnly)
                .Where(stage =>
                    !string.Equals(stage, preservedStage, PathComparison)
                )
                .OrderBy(path => path, StringComparer.Ordinal)
        )
        {
            Directory.Delete(stage, recursive: true);
        }
    }

    private static void RecoverJournaledSwap(
        DirectoryPaths paths,
        string destinationPath,
        ManagedDirectoryJournal journal
    )
    {
        var liveExists = Directory.Exists(destinationPath);
        var backupExists = Directory.Exists(paths.Backup);
        var keepPublished =
            journal.State == ManagedDirectoryJournalState.Activated
            || journal.RecoveryMode == ManagedDirectoryRecoveryMode.KeepPublished;

        if (keepPublished)
        {
            // ReSharper disable once ConvertIfStatementToSwitchStatement -- the named conditions
            // say which recovery state we are in; a switch over a positional (bool, bool) tuple
            // would make the reader decode the pair instead.
            if (liveExists && backupExists)
            {
                Directory.Delete(paths.Backup, recursive: true);
            }
            else if (!liveExists && backupExists)
            {
                Directory.Move(paths.Backup, destinationPath);
            }

            return;
        }

        if (backupExists)
        {
            if (liveExists)
            {
                Directory.Move(destinationPath, CreateRejectedPath(paths));
            }

            Directory.Move(paths.Backup, destinationPath);
            return;
        }

        // ReSharper disable once ConvertIfStatementToSwitchStatement -- the named conditions say
        // which recovery state we are in; a switch over a positional (bool, bool) tuple would
        // make the reader decode the pair instead.
        if (!journal.OldExists && liveExists)
        {
            Directory.Move(destinationPath, CreateRejectedPath(paths));
        }
        else if (journal.OldExists && !liveExists)
        {
            throw new InvalidDataException(
                $"Interrupted directory transaction for '{journal.ArtifactId}' has neither live tree nor backup."
            );
        }
    }

    private static void RecoverLegacySwap(DirectoryPaths paths, string destinationPath)
    {
        if (!Directory.Exists(paths.Backup))
        {
            return;
        }

        if (Directory.Exists(destinationPath))
        {
            Directory.Delete(paths.Backup, recursive: true);
        }
        else
        {
            Directory.Move(paths.Backup, destinationPath);
        }
    }

    private static ManagedDirectoryJournal? TryReadJournal(DirectoryPaths paths)
    {
        if (!File.Exists(paths.Journal))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ManagedDirectoryJournal>(
                    File.ReadAllText(paths.Journal),
                    s_jsonOptions
                )
                ?? throw new InvalidDataException(
                    $"Managed directory journal is empty: {paths.Journal}"
                );
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Managed directory journal is invalid: {paths.Journal}",
                ex
            );
        }
    }

    private static void WriteJournal(DirectoryPaths paths, ManagedDirectoryJournal journal)
    {
        AtomicFileWrite.WriteAllText(
            paths.Journal,
            JsonSerializer.Serialize(journal, s_jsonOptions)
        );
    }

    private static void DeleteJournal(DirectoryPaths paths)
    {
        if (File.Exists(paths.Journal))
        {
            File.Delete(paths.Journal);
        }
    }

    private static string CreateRejectedPath(DirectoryPaths paths)
    {
        string rejected;
        do
        {
            rejected = Path.Join(paths.Directory, $"{RejectedPrefix}{Guid.NewGuid():N}");
        } while (Directory.Exists(rejected) || File.Exists(rejected));

        return rejected;
    }

    private void CleanupStateDirectory(DirectoryPaths paths)
    {
        if (
            !_useCrossProcessLock
            && Directory.Exists(paths.Directory)
            && !Directory.EnumerateFileSystemEntries(paths.Directory).Any()
        )
        {
            Directory.Delete(paths.Directory);
        }

        if (
            !_useCrossProcessLock
            && Directory.Exists(_stateRoot)
            && !Directory.EnumerateFileSystemEntries(_stateRoot).Any()
        )
        {
            Directory.Delete(_stateRoot);
        }
    }

    private Task CheckpointAsync(ManagedDirectoryCheckpoint checkpoint, CancellationToken ct)
    {
        return _checkpoint?.Invoke(checkpoint, ct) ?? Task.CompletedTask;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public sealed class ManagedDirectoryCommit : IAsyncDisposable
    {
        private IAsyncDisposable? _artifactLock;
        private readonly ManagedDirectoryJournal _journal;
        private readonly DirectoryPaths _paths;

        internal ManagedDirectoryCommit(
            DirectoryPaths paths,
            ManagedDirectoryJournal journal,
            IAsyncDisposable artifactLock
        )
        {
            _paths = paths;
            _journal = journal;
            _artifactLock = artifactLock;
        }

        /// <summary>Marks the published tree activated, then removes its backup.</summary>
        public Task CompleteAsync()
        {
            EnsureOpen();
            WriteJournal(
                _paths,
                _journal with { State = ManagedDirectoryJournalState.Activated }
            );

            // The activated journal is the durable decision, so the cleanup after it must
            // not fail the commit — startup recovery finishes it from the same journal.
            try
            {
                if (Directory.Exists(_paths.Backup))
                {
                    Directory.Delete(_paths.Backup, recursive: true);
                }

                DeleteJournal(_paths);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"[ManagedDirectoryTransaction] Left cleanup for '{_journal.ArtifactId}' "
                        + $"to startup recovery: {ex.Message}"
                );
            }

            return Task.CompletedTask;
        }

        /// <summary>
        ///     Moves the rejected live tree aside and restores the prior tree. The rejected
        ///     path is returned for diagnostics and later cleanup.
        /// </summary>
        public Task<string?> RollbackAsync()
        {
            EnsureOpen();
            string? rejected = null;
            Exception? rejectException = null;
            if (Directory.Exists(_journal.DestinationPath))
            {
                rejected = CreateRejectedPath(_paths);
                try
                {
                    Directory.Move(_journal.DestinationPath, rejected);
                }
                catch (Exception ex)
                {
                    rejectException = ex;
                }
            }

            if (rejectException is not null)
            {
                return Task.FromException<string?>(
                    new IOException(
                        "Could not move the rejected managed directory aside.",
                        rejectException
                    )
                );
            }

            try
            {
                if (_journal.OldExists)
                {
                    if (!Directory.Exists(_paths.Backup))
                    {
                        throw new DirectoryNotFoundException(
                            $"Managed directory backup is missing: {_paths.Backup}"
                        );
                    }

                    Directory.Move(_paths.Backup, _journal.DestinationPath);
                }

                DeleteJournal(_paths);
                return Task.FromResult(rejected);
            }
            catch (Exception restoreException)
            {
                return Task.FromException<string?>(restoreException);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_artifactLock is null)
            {
                return;
            }

            var artifactLock = _artifactLock;
            _artifactLock = null;
            await artifactLock.DisposeAsync().ConfigureAwait(false);
        }

        private void EnsureOpen()
        {
            ObjectDisposedException.ThrowIf(_artifactLock is null, this);
        }
    }

    internal sealed record ManagedDirectoryJournal
    {
        public string ArtifactId { get; init; } = "";
        public string DestinationPath { get; init; } = "";
        public bool OldExists { get; init; }
        public ManagedDirectoryRecoveryMode RecoveryMode { get; init; }
        public ManagedDirectoryJournalState State { get; init; }
    }

    internal sealed class DirectoryPaths(string directory)
    {
        public string Directory { get; } = directory;
        public string Backup { get; } = Path.Join(directory, BackupDirectoryName);
        public string Journal { get; } = Path.Join(directory, JournalFileName);
        public string Lock { get; } = Path.Join(directory, LockFileName);
    }

    private sealed class DirectoryLock(FileStream stream, SemaphoreSlim processLock)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            processLock.Release();
        }
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public static NoopAsyncDisposable Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal enum ManagedDirectoryJournalState
    {
        Pending,
        Activated,
    }
}

public enum ManagedDirectoryRecoveryMode
{
    RestoreBackup,
    KeepPublished,
}

internal enum ManagedDirectoryCheckpoint
{
    AfterJournal,
    AfterBackup,
    AfterPublish,
}
