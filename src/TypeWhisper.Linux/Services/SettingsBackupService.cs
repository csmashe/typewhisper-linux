using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using TypeWhisper.Core;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.Services;

// ReSharper disable once NotAccessedPositionalProperty.Global  UncompressedBytes carried in the backup result record's data shape
public sealed record SettingsBackupResult(int FileCount, long UncompressedBytes);

internal enum StartupRestoreStatus
{
    None,
    Applied,
    PriorGenerationRestored,
    LockUnavailable,
    UnresolvedFailure,
}

internal sealed record StartupRestoreResult(
    StartupRestoreStatus Status,
    Exception? Error = null
);

internal delegate void RestoreCommitObserver(string relativePath, int committedFileCount);

internal delegate void BackupEntryObserver(string relativePath);

internal sealed class RestoreInterruptionException(string message) : Exception(message);

public sealed partial class SettingsBackupService
{
    private const string ManifestEntryName = "typewhisper-backup.json";
    private const string PendingDirectoryName = ".typewhisper-restore-pending";
    private const string StagingDirectoryPrefix = ".typewhisper-restore-staging-";
    private const string RestoreLockFileName = ".typewhisper-restore.lock";
    private const string PendingMarkerFileName = "pending-state.json";
    private const string JournalFileName = "restore-journal.json";
    private const string ContentDirectoryName = "content";
    private const string PreparedDirectoryName = "prepared";
    private const string RollbackDirectoryName = "rollback";
    private const string RollbackWorkDirectoryName = "rollback-work";
    private const int PendingStateVersion = 1;
    private const int JournalVersion = 1;

    // The real manifest is a few hundred bytes; cap it so a decompression-bomb
    // manifest can't be materialized into memory before shape validation runs.
    private const long MaxManifestBytes = 64 * 1024;

    // Path/extension validation says nothing about size: a decompression bomb made
    // entirely of allowed paths would still fill the disk during staging. Cap the
    // restored total and entry count well above any real settings backup and abort
    // as soon as an entry would cross the line.
    private const long MaxRestoreBytes = 512L * 1024 * 1024;
    private const int MaxRestoreEntries = 50_000;

    private const string ManifestApp = "TypeWhisper";
    private const string ManifestKind = "settings-backup";

    private static readonly string[] s_rootFiles =
    [
        "settings.json",
        "settings.json.bak",
        "linux-preferences.json",
    ];

    private static readonly string[] s_backupDirectoryRoots = ["Data", "PluginData"];

    private static readonly string[] s_manifestIncludes =
        ["settings", "linux-preferences", "data", "plugin-data"];

    private static readonly string[] s_manifestExcludes = ["models", "audio", "logs", "plugins"];

    // .so is matched separately to also catch versioned sonames (libfoo.so.12).
    private static readonly string[] s_executableExtensions = [".dll", ".dylib", ".exe"];

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private static readonly JsonSerializerOptions s_transactionJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _basePath;
    private readonly string _protectionKeyPath;
    private readonly RestoreCommitObserver? _commitObserver;
    private readonly Action? _cleanupObserver;
    private readonly SecretProtectionMigrationService _secretMigration;
    private readonly BackupEntryObserver? _backupSkipObserver;
    private readonly BackupEntryObserver? _backupPreOpenObserver;
    // Call-scoped: set at the top of each CreateBackup and read during that walk
    // only. Relies on IsBackupBusy serializing CreateBackup; not concurrency-safe.
    private FileIdentity? _protectionKeyIdentity;

    internal SettingsBackupService(
        string basePath,
        RestoreCommitObserver? commitObserver = null,
        Action? cleanupObserver = null,
        SecretProtectionMigrationService? secretMigration = null,
        BackupEntryObserver? backupSkipObserver = null,
        BackupEntryObserver? backupPreOpenObserver = null
    )
    {
        _basePath = Path.GetFullPath(basePath);
        _protectionKeyPath = Path.GetFullPath(
            Path.Join(
                _basePath,
                Path.GetFileName(TypeWhisperEnvironment.SecretProtectionKeyFilePath)
            )
        );
        _commitObserver = commitObserver;
        _cleanupObserver = cleanupObserver;
        _secretMigration =
            secretMigration ?? new SecretProtectionMigrationService(_basePath);
        _backupSkipObserver = backupSkipObserver;
        _backupPreOpenObserver = backupPreOpenObserver;
    }

    internal string PendingDirectoryPath => Path.Join(_basePath, PendingDirectoryName);

    public SettingsBackupResult CreateBackup(string destinationZipPath)
    {
        if (string.IsNullOrWhiteSpace(destinationZipPath))
        {
            throw new ArgumentException("Backup path is required.", nameof(destinationZipPath));
        }

        var migration = _secretMigration.MigrateAll();
        if (migration.HasUnresolvedSecrets)
        {
            throw new InvalidOperationException(
                Loc.Instance.GetString(
                    "Security.BackupBlockedByUnresolvedSecrets",
                    migration.UnresolvedSecretCount
                )
            );
        }

        // Captured AFTER migration: migrating legacy secrets can create the key,
        // and a pre-migration capture would leave that new key unguarded.
        _protectionKeyIdentity = NativeFile.GetRegularFileIdentity(_protectionKeyPath);

        var destinationDirectory = Path.GetDirectoryName(destinationZipPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        var tempPath = destinationZipPath + ".tmp";
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        var published = false;
        try
        {
            var fileCount = 0;
            long bytes = 0;

            using (var archive = ZipFile.Open(tempPath, ZipArchiveMode.Create))
            {
                var manifest = new
                {
                    app = ManifestApp,
                    kind = ManifestKind,
                    createdUtc = DateTimeOffset.UtcNow,
                    includes = s_manifestIncludes,
                    excludes = s_manifestExcludes,
                };
                var manifestEntry = archive.CreateEntry(
                    ManifestEntryName,
                    CompressionLevel.Optimal
                );
                using (var writer = new StreamWriter(manifestEntry.Open()))
                {
                    writer.Write(JsonSerializer.Serialize(manifest, s_jsonOptions));
                }

                foreach (var relativeFile in s_rootFiles)
                {
                    var path = Path.Join(_basePath, relativeFile);
                    AddBackupCandidate(
                        archive,
                        path,
                        relativeFile,
                        ref fileCount,
                        ref bytes
                    );
                }

                foreach (var root in s_backupDirectoryRoots)
                {
                    var rootPath = Path.Join(_basePath, root);
                    var rootKind = NativeFile.GetEntryKind(rootPath);
                    switch (rootKind)
                    {
                        case BackupEntryKind.Absent:
                            continue;
                        case BackupEntryKind.Directory:
                            AddDirectoryEntries(
                                archive,
                                rootPath,
                                ref fileCount,
                                ref bytes
                            );
                            break;
                        case BackupEntryKind.SymbolicLink:
                            AddLinkedDirectoryRoot(
                                archive,
                                rootPath,
                                root,
                                ref fileCount,
                                ref bytes
                            );
                            break;
                        default:
                            WarnSkippedEntry(root);
                            break;
                    }
                }
            }

            // Atomic rename: write to .tmp so a crash mid-backup leaves the previous
            // archive intact; the orphan .tmp is cleaned up on the next run.
            File.Move(tempPath, destinationZipPath, true);
            published = true;
            return new SettingsBackupResult(fileCount, bytes);
        }
        finally
        {
            if (!published)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best effort cleanup of only this backup's incomplete archive.
                }
            }
        }
    }

    public SettingsBackupResult StageRestore(string sourceZipPath)
    {
        if (string.IsNullOrWhiteSpace(sourceZipPath) || !File.Exists(sourceZipPath))
        {
            throw new FileNotFoundException("Backup file was not found.", sourceZipPath);
        }

        Directory.CreateDirectory(_basePath);
        // Keep staging on the same filesystem as the live tree. Publication is a
        // single directory rename, and the running process never reads this generation.
        var stagingDirectory = Path.Join(
            _basePath,
            $"{StagingDirectoryPrefix}{Guid.NewGuid():N}"
        );
        var contentDirectory = Path.Join(stagingDirectory, ContentDirectoryName);
        Directory.CreateDirectory(contentDirectory);
        var published = false;

        try
        {
            using var archive = ZipFile.OpenRead(sourceZipPath);
            ValidateArchive(archive);

            var fileCount = 0;
            long bytes = 0;

            foreach (var entry in archive.Entries)
            {
                if (
                    entry.FullName.Length == 0
                    || entry.FullName.EndsWith('/')
                )
                {
                    continue;
                }

                if (string.Equals(entry.FullName, ManifestEntryName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (ShouldSkipPortableEntry(entry.FullName))
                {
                    continue;
                }

                // Drop executables validation tolerated under exported roots, so no
                // native runtime from an untrusted archive ever reaches disk.
                if (IsExecutableEntry(NormalizeEntryName(entry.FullName)))
                {
                    continue;
                }

                if (fileCount >= MaxRestoreEntries)
                {
                    throw new InvalidDataException(Loc.Instance["About.BackupTooLarge"]);
                }

                var targetPath = GetSafeDestinationPath(contentDirectory, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                // Count the bytes actually written, not entry.Length — the declared
                // length comes from the archive and a crafted one can understate it.
                bytes += ExtractCapped(entry, targetPath, MaxRestoreBytes - bytes);
                fileCount++;
            }

            WriteDurableJson(
                Path.Join(stagingDirectory, PendingMarkerFileName),
                new PendingState
                {
                    Version = PendingStateVersion,
                    FileCount = fileCount,
                    UncompressedBytes = bytes,
                }
            );

            // A second staging request may have won the publication race while
            // this archive was being extracted. Never replace that valid request.
            if (PendingPathExists())
            {
                throw new InvalidOperationException(
                    "A settings restore is already staged. Quit and reopen TypeWhisper to apply it."
                );
            }

            Directory.Move(stagingDirectory, PendingDirectoryPath);
            published = true;

            return new SettingsBackupResult(fileCount, bytes);
        }
        finally
        {
            if (!published)
            {
                try
                {
                    if (Directory.Exists(stagingDirectory))
                    {
                        Directory.Delete(stagingDirectory, true);
                    }
                }
                catch
                {
                    // Best effort cleanup of only the unique directory this call created.
                }
            }
        }
    }

    internal static StartupRestoreResult ApplyPendingRestoreAtStartup(string basePath)
    {
        return new SettingsBackupService(basePath).ApplyPendingRestoreAtStartup();
    }

    internal StartupRestoreResult ApplyPendingRestoreAtStartup()
    {
        FileStream restoreLock;
        try
        {
            restoreLock = AcquireStartupRestoreLock(_basePath);
        }
        catch (IOException ex)
        {
            return new StartupRestoreResult(StartupRestoreStatus.LockUnavailable, ex);
        }
        catch (Exception ex)
        {
            return new StartupRestoreResult(StartupRestoreStatus.UnresolvedFailure, ex);
        }

        using (restoreLock)
        {
            try
            {
                return ApplyPendingRestoreUnderLock();
            }
            catch (RestoreInterruptionException)
            {
                // Test seam: models a process disappearing, skipping the ordinary
                // caught-exception rollback below.
                throw;
            }
            catch (Exception ex)
            {
                return new StartupRestoreResult(StartupRestoreStatus.UnresolvedFailure, ex);
            }
        }
    }

    internal static FileStream AcquireStartupRestoreLock(string basePath)
    {
        var fullBasePath = Path.GetFullPath(basePath);
        Directory.CreateDirectory(fullBasePath);
        return new FileStream(
            Path.Join(fullBasePath, RestoreLockFileName),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
    }

    private StartupRestoreResult ApplyPendingRestoreUnderLock()
    {
        if (File.Exists(PendingDirectoryPath))
        {
            throw new InvalidDataException(
                "The staged settings restore path is not a directory."
            );
        }

        if (!Directory.Exists(PendingDirectoryPath))
        {
            return new StartupRestoreResult(StartupRestoreStatus.None);
        }

        var journalPath = Path.Join(PendingDirectoryPath, JournalFileName);
        if (File.Exists(journalPath))
        {
            var journal = ReadAndValidateJournal(journalPath);
            return journal.Phase switch
            {
                RestoreJournalPhase.Prepared => RollBackPreparedTransaction(
                    journal,
                    new IOException("An interrupted settings restore was recovered.")
                ),
                RestoreJournalPhase.Committed => FinishCommittedTransaction(),
                RestoreJournalPhase.RolledBack => FinishRolledBackTransaction(),
                _ => throw new InvalidDataException("The settings restore journal phase is invalid."),
            };
        }

        _ = ReadAndValidatePendingState();
        var candidates = EnumeratePendingCandidates();
        var items = candidates
            .Select(relativePath => new RestoreJournalItem
            {
                RelativePath = relativePath,
                OriginallyExisted = File.Exists(GetLiveTargetPath(relativePath)),
            })
            .ToArray();

        try
        {
            PrepareTransactionFiles(items);
        }
        catch (Exception ex)
        {
            MarkUncommittedRequestRolledBackBestEffort(items);
            return new StartupRestoreResult(StartupRestoreStatus.PriorGenerationRestored, ex);
        }

        var preparedJournal = new RestoreJournal
        {
            Version = JournalVersion,
            Phase = RestoreJournalPhase.Prepared,
            Items = items,
        };

        try
        {
            WriteJournal(preparedJournal);
        }
        catch (Exception ex)
        {
            MarkUncommittedRequestRolledBackBestEffort(items);
            return new StartupRestoreResult(StartupRestoreStatus.PriorGenerationRestored, ex);
        }

        try
        {
            for (var index = 0; index < items.Length; index++)
            {
                var item = items[index];
                var preparedPath = GetPendingArtifactPath(
                    PreparedDirectoryName,
                    item.RelativePath
                );
                var targetPath = GetLiveTargetPath(item.RelativePath);
                File.Move(preparedPath, targetPath, true);
                _commitObserver?.Invoke(item.RelativePath, index + 1);
            }

            WriteJournal(CloneJournalWithPhase(preparedJournal, RestoreJournalPhase.Committed));
        }
        catch (RestoreInterruptionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return RollBackPreparedTransaction(preparedJournal, ex);
        }

        TryCleanupPendingDirectory();
        return new StartupRestoreResult(StartupRestoreStatus.Applied);
    }

    private string[] EnumeratePendingCandidates()
    {
        var contentDirectory = Path.Join(PendingDirectoryPath, ContentDirectoryName);
        if (!Directory.Exists(contentDirectory))
        {
            throw new InvalidDataException("The staged settings restore content is missing.");
        }

        return Directory
            .EnumerateFiles(contentDirectory, "*", SearchOption.AllDirectories)
            .Select(path => NormalizeEntryName(Path.GetRelativePath(contentDirectory, path)))
            .Select(ValidateRelativeTargetPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private bool PendingPathExists()
    {
        return Directory.Exists(PendingDirectoryPath) || File.Exists(PendingDirectoryPath);
    }

    private void PrepareTransactionFiles(IReadOnlyList<RestoreJournalItem> items)
    {
        foreach (var item in items)
        {
            var relativePath = ValidateRelativeTargetPath(item.RelativePath);
            var sourcePath = GetSafeDestinationPath(
                Path.Join(PendingDirectoryPath, ContentDirectoryName),
                relativePath
            );
            var preparedPath = GetPendingArtifactPath(PreparedDirectoryName, relativePath);
            var targetPath = GetLiveTargetPath(relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            CopyFileDurable(sourcePath, preparedPath);

            if (item.OriginallyExisted)
            {
                CopyFileDurable(
                    targetPath,
                    GetPendingArtifactPath(RollbackDirectoryName, relativePath)
                );
            }
        }
    }

    private StartupRestoreResult RollBackPreparedTransaction(
        RestoreJournal journal,
        Exception applyError
    )
    {
        try
        {
            foreach (var item in journal.Items)
            {
                var targetPath = GetLiveTargetPath(item.RelativePath);
                if (!item.OriginallyExisted)
                {
                    File.Delete(targetPath);
                    continue;
                }

                var rollbackPath = GetPendingArtifactPath(
                    RollbackDirectoryName,
                    item.RelativePath
                );
                if (!File.Exists(rollbackPath))
                {
                    throw new InvalidDataException(
                        $"The rollback snapshot for '{item.RelativePath}' is missing."
                    );
                }

                var rollbackWorkPath = GetPendingArtifactPath(
                    RollbackWorkDirectoryName,
                    item.RelativePath
                );
                CopyFileDurable(rollbackPath, rollbackWorkPath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Move(rollbackWorkPath, targetPath, true);
            }

            WriteJournal(CloneJournalWithPhase(journal, RestoreJournalPhase.RolledBack));
        }
        catch (Exception rollbackError)
        {
            return new StartupRestoreResult(
                StartupRestoreStatus.UnresolvedFailure,
                new AggregateException(
                    "The settings restore failed and its prior generation could not be fully restored.",
                    applyError,
                    rollbackError
                )
            );
        }

        TryCleanupPendingDirectory();
        return new StartupRestoreResult(
            StartupRestoreStatus.PriorGenerationRestored,
            applyError
        );
    }

    private StartupRestoreResult FinishCommittedTransaction()
    {
        TryCleanupPendingDirectory();
        return new StartupRestoreResult(StartupRestoreStatus.Applied);
    }

    private StartupRestoreResult FinishRolledBackTransaction()
    {
        TryCleanupPendingDirectory();
        return new StartupRestoreResult(StartupRestoreStatus.PriorGenerationRestored);
    }

    private void MarkUncommittedRequestRolledBackBestEffort(RestoreJournalItem[] items)
    {
        try
        {
            WriteJournal(
                new RestoreJournal
                {
                    Version = JournalVersion,
                    Phase = RestoreJournalPhase.RolledBack,
                    Items = items,
                }
            );
            TryCleanupPendingDirectory();
        }
        catch
        {
            // No live target was changed. Leaving the complete staged request in
            // place is safe; a future startup may retry preparation under the lock.
        }
    }

    private void TryCleanupPendingDirectory()
    {
        try
        {
            _cleanupObserver?.Invoke();
            if (Directory.Exists(PendingDirectoryPath))
            {
                Directory.Delete(PendingDirectoryPath, true);
            }
        }
        catch
        {
            // Terminal journal phases make interrupted cleanup idempotent.
        }
    }

    private PendingState ReadAndValidatePendingState()
    {
        var markerPath = Path.Join(PendingDirectoryPath, PendingMarkerFileName);
        var state = ReadJson<PendingState>(markerPath);
        if (
            state.Version != PendingStateVersion
            || state.FileCount < 0
            || state.UncompressedBytes < 0
        )
        {
            throw new InvalidDataException("The staged settings restore marker is invalid.");
        }

        return state;
    }

    private RestoreJournal ReadAndValidateJournal(string journalPath)
    {
        var journal = ReadJson<RestoreJournal>(journalPath);
        if (
            journal.Version != JournalVersion
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- deserialized JSON can be null despite the non-null annotation; validation must reject it
            || journal.Items is null
            || !Enum.IsDefined(journal.Phase)
        )
        {
            throw new InvalidDataException("The settings restore journal is invalid.");
        }

        var validatedPaths = journal.Items
            .Select(item => ValidateRelativeTargetPath(item.RelativePath))
            .ToArray();
        if (
            validatedPaths.Distinct(StringComparer.Ordinal).Count() != validatedPaths.Length
            || !validatedPaths.SequenceEqual(
                validatedPaths.Order(StringComparer.Ordinal),
                StringComparer.Ordinal
            )
        )
        {
            throw new InvalidDataException("The settings restore journal paths are invalid.");
        }

        return journal;
    }

    private string ValidateRelativeTargetPath(string relativePath)
    {
        var normalized = NormalizeEntryName(relativePath);
        if (
            !string.Equals(normalized, relativePath, StringComparison.Ordinal)
            || !IsAllowedEntry(normalized, false)
            || ShouldSkipPortableEntry(normalized)
            || IsExecutableEntry(normalized)
        )
        {
            throw new InvalidDataException(
                $"The settings restore contains an invalid target path: {relativePath}"
            );
        }

        _ = GetSafeDestinationPath(_basePath, normalized);
        return normalized;
    }

    private string GetLiveTargetPath(string relativePath)
    {
        return GetSafeDestinationPath(_basePath, ValidateRelativeTargetPath(relativePath));
    }

    private string GetPendingArtifactPath(string directoryName, string relativePath)
    {
        return GetSafeDestinationPath(
            Path.Join(PendingDirectoryPath, directoryName),
            ValidateRelativeTargetPath(relativePath)
        );
    }

    private void WriteJournal(RestoreJournal journal)
    {
        WriteDurableJson(Path.Join(PendingDirectoryPath, JournalFileName), journal);
    }

    private static RestoreJournal CloneJournalWithPhase(
        RestoreJournal journal,
        RestoreJournalPhase phase
    )
    {
        return new RestoreJournal
        {
            Version = journal.Version,
            Phase = phase,
            Items = journal.Items,
        };
    }

    private static T ReadJson<T>(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, s_transactionJsonOptions)
                   ?? throw new InvalidDataException($"'{Path.GetFileName(path)}' is empty.");
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' is invalid or unreadable.",
                ex
            );
        }
    }

    private static void WriteDurableJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";
        using (var stream = new FileStream(
                   tempPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   4096,
                   FileOptions.WriteThrough
               ))
        {
            JsonSerializer.Serialize(stream, value, s_transactionJsonOptions);
            stream.Flush(true);
        }

        File.Move(tempPath, path, true);
    }

    private static void CopyFileDurable(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );
        using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.WriteThrough
        );
        source.CopyTo(destination);
        destination.Flush(true);
    }

    private void AddDirectoryEntries(
        ZipArchive archive,
        string directoryPath,
        ref int fileCount,
        ref long bytes
    )
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            var relativePath = NormalizeEntryName(Path.GetRelativePath(_basePath, path));
            var kind = NativeFile.GetEntryKind(path);
            switch (kind)
            {
                case BackupEntryKind.Directory:
                    if (!ShouldSkipPortableEntry(relativePath))
                    {
                        // openat2 RESOLVE_BENEATH traversal is deferred; a same-privilege attacker can already read reachable non-key files.
                        AddDirectoryEntries(archive, path, ref fileCount, ref bytes);
                    }

                    break;
                case BackupEntryKind.RegularFile:
                    if (
                        !ShouldSkipPortableEntry(relativePath)
                        && !IsExecutableEntry(relativePath)
                    )
                    {
                        AddRegularFile(
                            archive,
                            path,
                            relativePath,
                            ref fileCount,
                            ref bytes
                        );
                    }

                    break;
                case BackupEntryKind.SymbolicLink:
                    AddLinkedNestedEntry(
                        archive,
                        path,
                        relativePath,
                        ref fileCount,
                        ref bytes
                    );
                    break;
                default:
                    WarnSkippedEntry(relativePath);
                    break;
            }
        }
    }

    private void AddBackupCandidate(
        ZipArchive archive,
        string path,
        string relativePath,
        ref int fileCount,
        ref long bytes
    )
    {
        switch (NativeFile.GetEntryKind(path))
        {
            case BackupEntryKind.RegularFile:
                AddRegularFile(archive, path, relativePath, ref fileCount, ref bytes);
                break;
            case BackupEntryKind.SymbolicLink:
                AddLinkedFile(
                    archive,
                    path,
                    relativePath,
                    ref fileCount,
                    ref bytes
                );
                break;
            case BackupEntryKind.Absent:
                break;
            default:
                WarnSkippedEntry(relativePath);
                break;
        }
    }

    private void AddRegularFile(
        ZipArchive archive,
        string path,
        string relativePath,
        ref int fileCount,
        ref long bytes
    )
    {
        var entryName = NormalizeEntryName(relativePath);
        _backupPreOpenObserver?.Invoke(entryName);
        using var source = NativeFile.OpenRegularFile(
            path,
            entryName,
            _protectionKeyIdentity
        );
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        var lastWriteTime = new DateTimeOffset(
            File.GetLastWriteTimeUtc(source.SafeFileHandle)
        );
        entry.LastWriteTime = lastWriteTime.Year is < 1980 or > 2107
            ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
            : lastWriteTime;
        using var destination = entry.Open();
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            destination.Write(buffer, 0, read);
            written += read;
        }

        fileCount++;
        bytes += written;
    }

    private void AddLinkedFile(
        ZipArchive archive,
        string path,
        string relativePath,
        ref int fileCount,
        ref long bytes
    )
    {
        ThrowIfProtectionKeyLink(path, relativePath);
        var resolved = ResolveFinalLinkTarget(path, directoryLink: false);
        if (
            resolved is not null
            && NativeFile.GetEntryKind(resolved.FullName) == BackupEntryKind.RegularFile
        )
        {
            AddRegularFile(
                archive,
                resolved.FullName,
                relativePath,
                ref fileCount,
                ref bytes
            );
            return;
        }

        HandleSkippedLink(path, relativePath);
    }

    private void AddLinkedNestedEntry(
        ZipArchive archive,
        string path,
        string relativePath,
        ref int fileCount,
        ref long bytes
    )
    {
        ThrowIfProtectionKeyLink(path, relativePath);
        var resolved = ResolveFinalLinkTarget(path, directoryLink: false);
        if (
            resolved is not null
            && NativeFile.GetEntryKind(resolved.FullName) == BackupEntryKind.RegularFile
            && !ShouldSkipPortableEntry(relativePath)
            && !IsExecutableEntry(relativePath)
        )
        {
            AddRegularFile(
                archive,
                resolved.FullName,
                relativePath,
                ref fileCount,
                ref bytes
            );
            return;
        }

        HandleSkippedLink(path, relativePath);
    }

    private void AddLinkedDirectoryRoot(
        ZipArchive archive,
        string path,
        string relativePath,
        ref int fileCount,
        ref long bytes
    )
    {
        ThrowIfProtectionKeyLink(path, relativePath);
        var resolved = ResolveFinalLinkTarget(path, directoryLink: true);
        if (
            resolved is not null
            && NativeFile.GetEntryKind(resolved.FullName) == BackupEntryKind.Directory
        )
        {
            AddDirectoryEntries(archive, path, ref fileCount, ref bytes);
            return;
        }

        HandleSkippedLink(path, relativePath);
    }

    private void HandleSkippedLink(string path, string relativePath)
    {
        ThrowIfProtectionKeyLink(path, relativePath);
        WarnSkippedEntry(relativePath);
    }

    private void ThrowIfProtectionKeyLink(string path, string relativePath)
    {
        if (!ResolvesToProtectionKey(path))
        {
            return;
        }

        // The key must never share an archive with the secrets it protects.
        throw new InvalidOperationException(
            $"Backup entry '{NormalizeEntryName(relativePath)}' resolves to the protection key."
        );
    }

    private static FileSystemInfo? ResolveFinalLinkTarget(string path, bool directoryLink)
    {
        try
        {
            return directoryLink
                ? Directory.ResolveLinkTarget(path, returnFinalTarget: true)
                : File.ResolveLinkTarget(path, returnFinalTarget: true);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private bool ResolvesToProtectionKey(string path)
    {
        try
        {
            // ResolveLinkTarget interprets a relative target from the link's containing
            // directory and, with returnFinalTarget, follows each remaining link in the chain.
            var resolved = File.ResolveLinkTarget(path, returnFinalTarget: true);
            return resolved is not null
                && string.Equals(
                    Path.GetFullPath(resolved.FullName),
                    _protectionKeyPath,
                    StringComparison.Ordinal
                );
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void WarnSkippedEntry(string relativePath)
    {
        var entryName = NormalizeEntryName(relativePath);
        Trace.TraceWarning(
            $"[SettingsBackupService] Skipped non-regular backup entry '{entryName}'."
        );
        _backupSkipObserver?.Invoke(entryName);
    }

    /// <summary>
    ///     Extracts one entry, aborting as soon as it would write more than
    ///     <paramref name="remainingBytes" />. Returns the bytes actually written.
    ///     A partial file is left behind; the caller discards the staging tree.
    /// </summary>
    private static long ExtractCapped(
        ZipArchiveEntry entry,
        string targetPath,
        long remainingBytes
    )
    {
        long written = 0;
        using (var source = entry.Open())
        using (
            var destination = new FileStream(
                targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None
            )
        )
        {
            var buffer = new byte[81920];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > remainingBytes)
                {
                    throw new InvalidDataException(Loc.Instance["About.BackupTooLarge"]);
                }

                destination.Write(buffer, 0, read);
            }
        }

        // Parity with ExtractToFile, which carries the archive timestamp across.
        File.SetLastWriteTimeUtc(targetPath, entry.LastWriteTime.UtcDateTime);
        return written;
    }

    private static void ValidateArchive(ZipArchive archive)
    {
        var manifestEntries = archive
            .Entries.Where(entry =>
                string.Equals(entry.FullName, ManifestEntryName, StringComparison.Ordinal)
            )
            .ToArray();

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Length == 0)
            {
                continue;
            }

            if (string.Equals(entry.FullName, ManifestEntryName, StringComparison.Ordinal))
            {
                continue;
            }

            var normalized = NormalizeEntryName(entry.FullName);
            var isDirectory = normalized.EndsWith('/');
            var validatedPath = isDirectory ? normalized.TrimEnd('/') : normalized;

            if (IsPluginEntry(validatedPath))
            {
                throw new InvalidDataException(Loc.Instance["About.BackupContainsPlugins"]);
            }

            // Executables are never written on restore (see extraction loop): a
            // plugin installer trusts an existing runtime file without re-verifying
            // its checksum, so a crafted .so under a runtime path would be loaded
            // and run on next launch. Under the exported roots we skip (not reject)
            // to keep legacy archives — whose exporter bundled runtimes — restorable;
            // outside them an executable has no legitimate origin, so fail closed.
            if (!isDirectory && IsExecutableEntry(validatedPath))
            {
                if (IsWithinBackupDirectoryRoot(validatedPath))
                {
                    continue;
                }

                throw new InvalidDataException(
                    Loc.Instance.GetString("About.BackupExecutablePath", entry.FullName)
                );
            }

            if (!IsAllowedEntry(validatedPath, isDirectory))
            {
                throw new InvalidDataException(
                    Loc.Instance.GetString("About.BackupUnsupportedPath", entry.FullName)
                );
            }

            _ = GetSafeDestinationPath(Path.GetTempPath(), validatedPath);
        }

        if (manifestEntries.Length != 1)
        {
            throw new InvalidDataException(Loc.Instance["About.BackupInvalid"]);
        }

        ValidateManifest(manifestEntries[0]);
    }

    private static void ValidateManifest(ZipArchiveEntry manifestEntry)
    {
        if (manifestEntry.Length > MaxManifestBytes)
        {
            throw new InvalidDataException(Loc.Instance["About.BackupInvalidManifest"]);
        }

        BackupManifest? manifest;
        try
        {
            // The declared Length above is only the zip's own claim; the deflate stream can expand
            // far past it, so enforce the cap on the bytes actually read before deserializing.
            using var stream = manifestEntry.Open();
            using var bounded = ReadBounded(stream, MaxManifestBytes);
            manifest = JsonSerializer.Deserialize<BackupManifest>(bounded);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
        {
            throw new InvalidDataException(Loc.Instance["About.BackupInvalidManifest"], ex);
        }

        // The exact Includes/Excludes match is the manifest's only cross-version gate (there is no
        // schema version): it stops an older build restoring a newer archive over live data whose
        // per-file schemas it can't read. Don't relax it without adding a real version field.
        if (
            manifest is null
            || !string.Equals(manifest.App, ManifestApp, StringComparison.Ordinal)
            || !string.Equals(manifest.Kind, ManifestKind, StringComparison.Ordinal)
            || manifest.CreatedUtc is null
            || manifest.Includes is null
            || !manifest.Includes.SequenceEqual(s_manifestIncludes, StringComparer.Ordinal)
            || manifest.Excludes is null
            || !manifest.Excludes.SequenceEqual(s_manifestExcludes, StringComparer.Ordinal)
        )
        {
            throw new InvalidDataException(Loc.Instance["About.BackupInvalidManifest"]);
        }
    }

    // Copies at most maxBytes from source, throwing once a byte beyond the cap arrives.
    private static MemoryStream ReadBounded(Stream source, long maxBytes)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                buffer.Dispose();
                throw new InvalidDataException(Loc.Instance["About.BackupInvalidManifest"]);
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }

    private static bool IsAllowedEntry(string entryName, bool isDirectory)
    {
        if (!isDirectory && s_rootFiles.Contains(entryName, StringComparer.Ordinal))
        {
            return true;
        }

        return s_backupDirectoryRoots.Any(root =>
            isDirectory && string.Equals(entryName, root, StringComparison.Ordinal)
            || entryName.StartsWith(root + "/", StringComparison.Ordinal)
        );
    }

    private static bool IsPluginEntry(string entryName)
    {
        return string.Equals(entryName, "Plugins", StringComparison.Ordinal)
            || entryName.StartsWith("Plugins/", StringComparison.Ordinal);
    }

    private static bool IsExecutableEntry(string entryName)
    {
        var fileName = entryName[(entryName.LastIndexOf('/') + 1)..];

        // Versioned sonames (libcudart.so.12) are native code with a numeric final
        // "extension", so match ".so" anywhere in the suffix, not just at the end.
        if (
            fileName.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains(".so.", StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        return s_executableExtensions.Contains(
            Path.GetExtension(fileName),
            StringComparer.OrdinalIgnoreCase
        );
    }

    private static bool IsWithinBackupDirectoryRoot(string entryName)
    {
        return s_backupDirectoryRoots.Any(root =>
            entryName.StartsWith(root + "/", StringComparison.Ordinal)
        );
    }

    private static bool ShouldSkipPortableEntry(string relativePath)
    {
        var parts = NormalizeEntryName(relativePath).Split('/');
        return parts.Any(part => string.Equals(part, "Models", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetSafeDestinationPath(string rootPath, string entryName)
    {
        var normalized = NormalizeEntryName(entryName);
        // Reject traversal components before GetFullPath to block crafted paths
        // like "../../.bashrc". The subsequent StartsWith check is defense-in-depth
        // for platform-specific tricks GetFullPath may canonicalize differently.
        if (
            Path.IsPathRooted(normalized)
            || normalized.Split('/').Any(part => part is "" or "." or "..")
        )
        {
            throw new InvalidDataException(
                Loc.Instance.GetString("About.BackupUnsafePath", entryName)
            );
        }

        var fullRoot = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var destination = Path.GetFullPath(Path.Join(fullRoot, normalized));
        return !destination.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? throw new InvalidDataException(
                Loc.Instance.GetString("About.BackupUnsafePath", entryName)
            )
            : destination;
    }

    private static string NormalizeEntryName(string path)
    {
        return path.Replace('\\', '/');
    }

    private enum BackupEntryKind
    {
        Absent,
        RegularFile,
        Directory,
        SymbolicLink,
        Unsupported,
    }

    private readonly record struct FileIdentity(uint DevMajor, uint DevMinor, ulong Ino);

    private static partial class NativeFile
    {
        private const int AtCurrentWorkingDirectory = -100;
        private const int AtSymlinkNoFollow = 0x100;
        private const int AtEmptyPath = 0x1000;
        private const uint StatxType = 0x0001;
        private const uint StatxIno = 0x0100;
        private const int OpenReadOnly = 0;
        private const int OpenCloseOnExec = 0x80000;
        private const int OpenNoFollow = 0x20000;
        private const int OpenNonBlock = 0x800;
        private const ushort FileTypeMask = 0xF000;
        private const ushort FileTypeRegular = 0x8000;
        private const ushort FileTypeDirectory = 0x4000;
        private const ushort FileTypeSymbolicLink = 0xA000;

        internal static BackupEntryKind GetEntryKind(string path)
        {
            var info = new FileInfo(path);
            info.Refresh();
            if (info.LinkTarget is not null)
            {
                return BackupEntryKind.SymbolicLink;
            }

            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException(
                    "No-follow settings backup export requires Linux."
                );
            }

            var result = statx(
                AtCurrentWorkingDirectory,
                path,
                AtSymlinkNoFollow,
                StatxType,
                out var stat
            );
            if (result == 0)
            {
                return KindFromMode(stat.Mode);
            }

            var error = Marshal.GetLastPInvokeError();
            return error is 2 or 20
                ? BackupEntryKind.Absent
                : throw new Win32Exception(error, $"Could not inspect '{path}'.");
        }

        internal static FileIdentity? GetRegularFileIdentity(string path)
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException(
                    "No-follow settings backup export requires Linux."
                );
            }

            // FOLLOW semantics on purpose: the identity to guard is the inode the
            // key consumers actually read, and they read through symlinks. A
            // no-follow capture of a symlinked key would stat the link's inode,
            // yield null, and silently disarm the guard for the whole backup.
            if (
                statx(
                    AtCurrentWorkingDirectory,
                    path,
                    0,
                    StatxType | StatxIno,
                    out var stat
                ) == 0
            )
            {
                // A key that exists but is not a regular file must fail closed:
                // "could not identify the key" and "there is no key" would
                // otherwise both disable the identity check.
                return KindFromMode(stat.Mode) == BackupEntryKind.RegularFile
                    ? GetIdentity(stat)
                    : throw new InvalidOperationException(
                        $"Protection key '{Path.GetFileName(path)}' is not a regular file."
                    );
            }

            var error = Marshal.GetLastPInvokeError();
            return error is 2 or 20
                ? null
                : throw new Win32Exception(error, $"Could not inspect '{path}'.");
        }

        internal static FileStream OpenRegularFile(
            string path,
            string displayName,
            FileIdentity? protectionKeyIdentity
        )
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException(
                    "No-follow settings backup export requires Linux."
                );
            }

            // O_NOFOLLOW cannot prevent swapped-in device side effects before statx rejects it; O_NONBLOCK covers FIFOs.
            var descriptor = open(
                path,
                OpenReadOnly | OpenCloseOnExec | OpenNoFollow | OpenNonBlock,
                0
            );
            if (descriptor < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is 2 or 20 or 40)
                {
                    throw new IOException(
                        $"Backup entry '{displayName}' changed while it was being opened."
                    );
                }

                throw new Win32Exception(
                    error,
                    $"Could not safely open backup entry '{displayName}'."
                );
            }

            var handle = new SafeFileHandle(descriptor, ownsHandle: true);
            try
            {
                if (
                    statx(
                        descriptor,
                        string.Empty,
                        AtEmptyPath,
                        StatxType | StatxIno,
                        out var stat
                    ) != 0
                )
                {
                    throw new Win32Exception(
                        Marshal.GetLastPInvokeError(),
                        $"Could not inspect open backup entry '{displayName}'."
                    );
                }

                if (KindFromMode(stat.Mode) != BackupEntryKind.RegularFile)
                {
                    throw new IOException(
                        $"Backup entry '{displayName}' stopped being a regular file before backup."
                    );
                }

                if (
                    protectionKeyIdentity is { } keyIdentity
                    && GetIdentity(stat) == keyIdentity
                )
                {
                    throw new InvalidOperationException(
                        $"Backup entry '{displayName}' resolves to the protection key."
                    );
                }

                return new FileStream(handle, FileAccess.Read);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static FileIdentity GetIdentity(StatxBuffer stat)
        {
            return new FileIdentity(stat.DevMajor, stat.DevMinor, stat.Ino);
        }

        private static BackupEntryKind KindFromMode(ushort mode)
        {
            return (ushort)(mode & FileTypeMask) switch
            {
                FileTypeRegular => BackupEntryKind.RegularFile,
                FileTypeDirectory => BackupEntryKind.Directory,
                FileTypeSymbolicLink => BackupEntryKind.SymbolicLink,
                _ => BackupEntryKind.Unsupported,
            };
        }

        [StructLayout(LayoutKind.Sequential, Size = 256)]
        private struct StatxBuffer
        {
            public uint Mask;
            public uint BlockSize;
            public ulong Attributes;
            public uint LinkCount;
            public uint UserId;
            public uint GroupId;
            public ushort Mode;
            public ushort Spare0;
            public ulong Ino;
            public ulong Size;
            public ulong Blocks;
            public ulong AttributesMask;
            public StatxTimestamp Atime;
            public StatxTimestamp Btime;
            public StatxTimestamp Ctime;
            public StatxTimestamp Mtime;
            public uint RdevMajor;
            public uint RdevMinor;
            public uint DevMajor;
            public uint DevMinor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StatxTimestamp
        {
            public long Seconds;
            public uint Nanoseconds;
            public int Padding;
        }

        // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int statx(
            int directoryFileDescriptor,
            string path,
            int flags,
            uint mask,
            out StatxBuffer buffer
        );

        // ReSharper disable once InconsistentNaming -- native libc function name; LibraryImport EntryPoint defaults to the method name.
        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int open(string path, int flags, uint mode);
    }

    private enum RestoreJournalPhase
    {
        Prepared,
        Committed,
        RolledBack,
    }

    private sealed class PendingState
    {
        public int Version { get; init; }
        public int FileCount { get; init; }
        public long UncompressedBytes { get; init; }
    }

    private sealed class RestoreJournal
    {
        public int Version { get; init; }
        public RestoreJournalPhase Phase { get; init; }
        public RestoreJournalItem[] Items { get; init; } = [];
    }

    private sealed class RestoreJournalItem
    {
        public string RelativePath { get; init; } = "";
        public bool OriginallyExisted { get; init; }
    }

    private sealed class BackupManifest
    {
        [JsonPropertyName("app")]
        public string? App { get; init; }

        [JsonPropertyName("kind")]
        public string? Kind { get; init; }

        [JsonPropertyName("createdUtc")]
        public DateTimeOffset? CreatedUtc { get; init; }

        [JsonPropertyName("includes")]
        public string[]? Includes { get; init; }

        [JsonPropertyName("excludes")]
        public string[]? Excludes { get; init; }
    }
}
