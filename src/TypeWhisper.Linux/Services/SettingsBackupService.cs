using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    UnresolvedFailure
}

internal sealed record StartupRestoreResult(
    StartupRestoreStatus Status,
    Exception? Error = null
);

internal delegate void RestoreCommitObserver(string relativePath, int committedFileCount);

internal sealed class RestoreInterruptionException(string message) : Exception(message);

public sealed class SettingsBackupService
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

    private const string ManifestApp = "TypeWhisper";
    private const string ManifestKind = "settings-backup";

    private static readonly string[] s_rootFiles =
    [
        "settings.json",
        "settings.json.bak",
        "linux-preferences.json"
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
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _basePath;
    private readonly RestoreCommitObserver? _commitObserver;
    private readonly Action? _cleanupObserver;

    public SettingsBackupService()
        : this(TypeWhisperEnvironment.BasePath)
    {
    }

    internal SettingsBackupService(
        string basePath,
        RestoreCommitObserver? commitObserver = null,
        Action? cleanupObserver = null
    )
    {
        _basePath = Path.GetFullPath(basePath);
        _commitObserver = commitObserver;
        _cleanupObserver = cleanupObserver;
    }

    internal string PendingDirectoryPath => Path.Join(_basePath, PendingDirectoryName);

    public SettingsBackupResult CreateBackup(string destinationZipPath)
    {
        if (string.IsNullOrWhiteSpace(destinationZipPath))
        {
            throw new ArgumentException("Backup path is required.", nameof(destinationZipPath));
        }

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
                excludes = s_manifestExcludes
            };
            var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
            using (var writer = new StreamWriter(manifestEntry.Open()))
            {
                writer.Write(JsonSerializer.Serialize(manifest, s_jsonOptions));
            }

            foreach (var relativeFile in s_rootFiles)
            {
                var path = Path.Join(_basePath, relativeFile);
                AddFileIfExists(archive, path, relativeFile, ref fileCount, ref bytes);
            }

            foreach (var root in s_backupDirectoryRoots)
            {
                var rootPath = Path.Join(_basePath, root);
                if (!Directory.Exists(rootPath))
                {
                    continue;
                }

                foreach (
                    var path in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
                )
                {
                    var relativePath = Path.GetRelativePath(_basePath, path);
                    // Skip model assets and executables (re-downloadable runtimes):
                    // restore drops executables, so exporting them would bloat the
                    // archive with files it can never restore.
                    if (
                        ShouldSkipPortableEntry(relativePath)
                        || IsExecutableEntry(NormalizeEntryName(relativePath))
                    )
                    {
                        continue;
                    }

                    AddFileIfExists(archive, path, relativePath, ref fileCount, ref bytes);
                }
            }
        }

        // Atomic rename: write to .tmp so a crash mid-backup leaves the previous
        // archive intact; the orphan .tmp is cleaned up on the next run.
        File.Move(tempPath, destinationZipPath, true);
        return new SettingsBackupResult(fileCount, bytes);
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

                var targetPath = GetSafeDestinationPath(contentDirectory, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                entry.ExtractToFile(targetPath, true);
                fileCount++;
                bytes += entry.Length;
            }

            WriteDurableJson(
                Path.Join(stagingDirectory, PendingMarkerFileName),
                new PendingState
                {
                    Version = PendingStateVersion,
                    FileCount = fileCount,
                    UncompressedBytes = bytes
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
                _ => throw new InvalidDataException("The settings restore journal phase is invalid.")
            };
        }

        _ = ReadAndValidatePendingState();
        var candidates = EnumeratePendingCandidates();
        var items = candidates
            .Select(relativePath => new RestoreJournalItem
            {
                RelativePath = relativePath,
                OriginallyExisted = File.Exists(GetLiveTargetPath(relativePath))
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
            Items = items
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
                    Items = items
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
            Items = journal.Items
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

    private static void AddFileIfExists(
        ZipArchive archive,
        string path,
        string relativePath,
        ref int fileCount,
        ref long bytes
    )
    {
        if (!File.Exists(path))
        {
            return;
        }

        var entryName = NormalizeEntryName(relativePath);
        archive.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal);
        fileCount++;
        bytes += new FileInfo(path).Length;
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
            using var stream = manifestEntry.Open();
            manifest = JsonSerializer.Deserialize<BackupManifest>(stream);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
        {
            throw new InvalidDataException(Loc.Instance["About.BackupInvalidManifest"], ex);
        }

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

    private enum RestoreJournalPhase
    {
        Prepared,
        Committed,
        RolledBack
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
