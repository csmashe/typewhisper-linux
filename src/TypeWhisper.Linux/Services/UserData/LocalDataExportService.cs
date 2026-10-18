using System.ComponentModel;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using TypeWhisper.Core;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.Services.UserData;

/// <summary>What an export of all data carries beyond the categories it always includes.</summary>
public sealed record LocalDataExportOptions(bool IncludeRecordings);

/// <summary>Why an entry that belongs in the export is missing from it.</summary>
public enum LocalDataExportSkipReason
{
    /// <summary>Permission denied or another read error.</summary>
    Unreadable,

    /// <summary>A symbolic link; links are never followed.</summary>
    Link,

    /// <summary>A socket, pipe or device.</summary>
    NotRegularFile,

    /// <summary>Replaced, moved or removed while it was being read.</summary>
    Changed,

    /// <summary>A settings file that could not be parsed, so its secrets could not be removed.</summary>
    SecretsNotRemovable,

    /// <summary>Another name for the key that protects stored secrets.</summary>
    ProtectionKey,
}

/// <summary>An entry left out of the export; folders end in a slash.</summary>
public sealed record LocalDataExportSkip(string RelativePath, LocalDataExportSkipReason Reason);

/// <summary>What an export of all data wrote.</summary>
/// <param name="FileCount">Files copied from the data folder.</param>
/// <param name="Bytes">Their combined size before compression.</param>
/// <param name="IncludesSettingsBackup">Whether the restorable settings backup was added.</param>
/// <param name="Skipped">Entries that belong in the export but could not be copied.</param>
/// <param name="Unrecognized">Top-level entries TypeWhisper did not create, left out.</param>
public sealed record LocalDataExportResult(
    int FileCount,
    long Bytes,
    bool IncludesSettingsBackup,
    IReadOnlyList<LocalDataExportSkip> Skipped,
    IReadOnlyList<string> Unrecognized
);

/// <summary>Progress of a running export.</summary>
public readonly record struct LocalDataExportProgress(int FileCount, long Bytes);

/// <summary>
///     "Export all data": one .zip holding a copy of the data folder's user data
///     (<see cref="LocalDataInventory" />), a settings backup that Restore accepts, a manifest and a
///     README. Stored secrets never enter it: the protection key and retired secrets stay out,
///     and secret values are removed from every settings file copied.
/// </summary>
/// <remarks>
///     Links are never followed: each file is opened without following a final link and must then
///     resolve to the path that was listed, so a folder swapped for a link cannot redirect a read.
///     The archive is written owner-only beside the destination and renamed over it when complete.
/// </remarks>
public sealed class LocalDataExportService
{
    private const string ReadMeEntryName = "README.txt";
    private const string ManifestEntryName = "typewhisper-export.json";
    public const string SettingsBackupEntryName = "settings-backup.zip";
    private const string DataFolderName = "data";

    private const int ManifestVersion = 1;
    private const long MaxSettingsBytes = 16 * 1024 * 1024;
    private const string WebhookPluginFolder = "com.typewhisper.webhook";

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    // Already compressed; deflating them again costs time and saves nothing.
    private static readonly HashSet<string> s_storedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".webm", ".mp4", ".zip", ".gz", ".png", ".jpg", ".jpeg",
    };

    private static readonly string[] s_rootSettingsFiles = ["settings.json", "settings.json.bak"];

    private readonly string _basePath;
    private readonly Action<string>? _beforeOpen;

    public LocalDataExportService()
        : this(TypeWhisperEnvironment.BasePath) { }

    internal LocalDataExportService(string basePath, Action<string>? beforeOpen = null)
    {
        _basePath = Path.GetFullPath(basePath);
        _beforeOpen = beforeOpen;
    }

    /// <summary>Writes the archive to <paramref name="destination" />.</summary>
    /// <exception cref="ArgumentException">The destination is inside the data folder.</exception>
    /// <exception cref="OperationCanceledException">Canceled; nothing is left at the destination.</exception>
    public Task<LocalDataExportResult> ExportAsync(
        string destination,
        LocalDataExportOptions options,
        IProgress<LocalDataExportProgress>? progress = null,
        CancellationToken cancellationToken = default
    ) => Task.Run(() => Export(destination, options, progress, cancellationToken), cancellationToken);

    private LocalDataExportResult Export(
        string destination,
        LocalDataExportOptions options,
        IProgress<LocalDataExportProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Exporting all data requires Linux.");
        }

        var root = ResolveLinks(_basePath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(Loc.Instance["About.ExportNoDataFolder"]);
        }

        var target = Path.GetFullPath(destination);
        var folder =
            Path.GetDirectoryName(target)
            ?? throw new ArgumentException(Loc.Instance["About.ExportInsideDataFolder"], nameof(destination));
        // An archive inside the data folder would be copied into itself.
        if (IsSameOrInside(root, Path.Join(ResolveLinks(folder), Path.GetFileName(target))))
        {
            throw new ArgumentException(Loc.Instance["About.ExportInsideDataFolder"], nameof(destination));
        }

        var name = ".typewhisper-export-" + Guid.NewGuid().ToString("N");
        var temporary = Path.Join(folder, name + ".tmp");
        // The settings backup is built beside the archive and copied into it at the end.
        var backupTemporary = Path.Join(folder, name + ".backup.tmp");
        try
        {
            LocalDataExportResult result;
            using (var stream = CreateOwnerOnly(temporary, FileAccess.Write))
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
                using (var backup = CreateOwnerOnly(backupTemporary, FileAccess.ReadWrite))
                using (var run = new ExportRun(this, root, archive, backup, options, progress, cancellationToken))
                {
                    result = run.Execute();
                }

                stream.Flush(flushToDisk: true);
            }

            if (!TryDelete(backupTemporary))
            {
                throw new IOException(Loc.Instance.GetString("About.ExportPartialFileLeft", backupTemporary));
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
            return result;
        }
        catch (Exception ex)
        {
            // A partial archive holds the same data as a finished one, so one left behind is named.
            var left = new[] { temporary, backupTemporary }.Where(path => !TryDelete(path)).ToArray();
            if (left.Length > 0)
            {
                throw new IOException(
                    Loc.Instance.GetString("About.ExportPartialFileLeft", string.Join(", ", left)),
                    ex
                );
            }

            throw;
        }
    }

    // The archive holds history and recordings, so its files are owner-only from creation.
    [SupportedOSPlatform("linux")]
    private static FileStream CreateOwnerOnly(string path, FileAccess access) =>
        new(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = access,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }
        );

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsSameOrInside(string root, string path) =>
        string.Equals(path, root, StringComparison.Ordinal)
        || path.StartsWith(root + "/", StringComparison.Ordinal);

    // Resolves every existing link along the path, so a link to the data folder is recognized.
    private static string ResolveLinks(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var current = "/";
        foreach (var segment in full.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, segment);
            try
            {
                if (new FileInfo(current).LinkTarget is not null
                    && File.ResolveLinkTarget(current, returnFinalTarget: true) is { } resolved)
                {
                    current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved.FullName));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable part is compared as written; the rest is still resolved.
            }
        }

        return current;
    }

    private static string Describe(LocalDataCategory category) =>
        category switch
        {
            LocalDataCategory.Settings => "app settings and Linux preferences, with stored secrets removed",
            LocalDataCategory.AppData =>
                "history, dictionary, profiles, snippets, prompt actions, usage statistics and the error log",
            LocalDataCategory.PluginData =>
                "plugin settings and collections (scripts, webhooks, memories), with stored secrets removed",
            LocalDataCategory.Recordings => "dictation recovery recordings and Recorder recordings",
            LocalDataCategory.Logs => "application logs",
            LocalDataCategory.ShortcutBackups => "copies of GNOME keyboard shortcuts taken before TypeWhisper changed them",
            LocalDataCategory.Secrets =>
                "API keys, plugin secrets, the local API token and the key that encrypts them",
            LocalDataCategory.Models =>
                "downloaded models, including a custom model folder and folders inside a plugin's folder",
            LocalDataCategory.Runtimes => "downloaded GPU runtimes",
            LocalDataCategory.InstalledPlugins => "installed plugins",
            LocalDataCategory.CommandLineTool => "the installed command line tool",
            LocalDataCategory.Internal => "sockets, locks and temporary files",
            LocalDataCategory.Unrecognized => "files and folders TypeWhisper did not create",
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
        };

    private static string Describe(LocalDataExportSkipReason reason) =>
        reason switch
        {
            LocalDataExportSkipReason.Unreadable => "could not be read",
            LocalDataExportSkipReason.Link => "is a link, which is not followed",
            LocalDataExportSkipReason.NotRegularFile => "is not a regular file",
            LocalDataExportSkipReason.Changed => "changed while it was being read",
            LocalDataExportSkipReason.SecretsNotRemovable => "could not be read as settings, so its secrets could not be removed",
            LocalDataExportSkipReason.ProtectionKey => "is another name for the key that protects stored secrets",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
        };

    /// <summary>One export: the walk over the data folder and everything it accumulates.</summary>
    private sealed class ExportRun(
        LocalDataExportService owner,
        string root,
        ZipArchive archive,
        FileStream backupStream,
        LocalDataExportOptions options,
        IProgress<LocalDataExportProgress>? progress,
        CancellationToken cancellationToken
    ) : IDisposable
    {
        private readonly SettingsBackupService.GuardedSecretFile[] _guarded =
            new SettingsBackupService(root).CaptureGuardedSecretFiles();

        private readonly bool _canVerifyOpenedPath = Directory.Exists("/proc/self/fd");
        private readonly string? _modelStorage = ResolveModelStorageInside(root);
        private readonly List<LocalDataExportSkip> _skipped = [];
        private readonly List<string> _unrecognized = [];
        private readonly HashSet<LocalDataCategory> _exported = [];

        // Null once the settings backup cannot be made.
        private SettingsBackupService.PortableBackupWriter? _backup = new(backupStream);
        private int _backupFileCount;
        private long _backupBytes;
        private int _fileCount;
        private long _bytes;

        public LocalDataExportResult Execute()
        {
            foreach (var entry in ListFolder(root, "") ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry);
                var kind = GetKind(entry, name);
                if (kind is null or SettingsBackupService.BackupEntryKind.Absent)
                {
                    continue;
                }

                var category = ClassifyTopLevel(name, kind.Value);
                if (category == LocalDataCategory.Unrecognized)
                {
                    _unrecognized.Add(kind == SettingsBackupService.BackupEntryKind.Directory ? name + "/" : name);
                    continue;
                }

                if (!IsExported(category))
                {
                    continue;
                }

                if (entry == _modelStorage)
                {
                    continue;
                }

                _exported.Add(category);
                // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault -- the default arm covers sockets, pipes and devices.
                switch (kind)
                {
                    case SettingsBackupService.BackupEntryKind.Directory:
                        CopyFolder(entry, [name], category);
                        break;
                    case SettingsBackupService.BackupEntryKind.RegularFile:
                        CopyFile(entry, [name], category);
                        break;
                    case SettingsBackupService.BackupEntryKind.SymbolicLink:
                        Skip(name, LocalDataExportSkipReason.Link);
                        break;
                    default:
                        Skip(name, LocalDataExportSkipReason.NotRegularFile);
                        break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var includesBackup = WriteSettingsBackup();
            var result = new LocalDataExportResult(_fileCount, _bytes, includesBackup, _skipped, _unrecognized);
            WriteText(ManifestEntryName, Manifest(result));
            WriteText(ReadMeEntryName, ReadMe(result));
            return result;
        }

        // A custom model folder may sit inside an exported folder; its models stay out like any other.
        private static string? ResolveModelStorageInside(string root)
        {
            try
            {
                // Opened like every exported file: no link, no pipe or device, and bounded.
                using var source = SettingsBackupService.NativeFile.OpenRegularFile(
                    Path.Join(root, "settings.json"),
                    "settings.json",
                    []
                );
                if (source.Length > MaxSettingsBytes)
                {
                    return null;
                }

                var settings = JsonNode.Parse(source) as JsonObject;
                var configured = AppSettings.NormalizeLocalModelStoragePath(
                    settings?["localModelStoragePath"] is JsonValue value && value.TryGetValue<string>(out var path)
                        ? path
                        : null
                );
                var resolved = configured is null ? null : ResolveLinks(configured);
                if (resolved is null || resolved != root && !resolved.StartsWith(root + "/", StringComparison.Ordinal))
                {
                    return null;
                }

                // Plugins keep models in its PluginData folder. The data folder already leaves plugin
                // subfolders out; a top-level folder shares it with user data, so only that subfolder stays out.
                if (resolved == root)
                {
                    return null;
                }

                return Path.GetDirectoryName(resolved) == root
                    ? Path.Join(resolved, LocalModelStoragePaths.PluginDataFolderName)
                    : resolved;
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        private static LocalDataCategory ClassifyTopLevel(string name, SettingsBackupService.BackupEntryKind kind)
        {
            if (kind != SettingsBackupService.BackupEntryKind.SymbolicLink)
            {
                return LocalDataInventory.ClassifyTopLevel(name, kind == SettingsBackupService.BackupEntryKind.Directory);
            }

            // A link is not followed, so whether it names a folder or a file is unknown.
            var asFolder = LocalDataInventory.ClassifyTopLevel(name, isDirectory: true);
            return asFolder != LocalDataCategory.Unrecognized
                ? asFolder
                : LocalDataInventory.ClassifyTopLevel(name, isDirectory: false);
        }

        private bool IsExported(LocalDataCategory category) =>
            LocalDataInventory.AlwaysExported.Contains(category)
            || category == LocalDataCategory.Recordings && options.IncludeRecordings;

        private void CopyFolder(string path, List<string> segments, LocalDataCategory category)
        {
            var relativeFolder = string.Join('/', segments);
            foreach (var entry in ListFolder(path, relativeFolder + "/") ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry);
                List<string> childSegments = [.. segments, name];
                var kind = GetKind(entry, string.Join('/', childSegments));
                // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault -- the default arm covers sockets, pipes and devices.
                switch (kind)
                {
                    case null:
                    case SettingsBackupService.BackupEntryKind.Absent:
                        break;
                    case SettingsBackupService.BackupEntryKind.Directory:
                        if (
                            entry != _modelStorage
                            && LocalDataInventory.IsFolderExportedBelow(category, childSegments)
                        )
                        {
                            CopyFolder(entry, childSegments, category);
                        }

                        break;
                    case SettingsBackupService.BackupEntryKind.RegularFile:
                        if (LocalDataInventory.IsExportedBelow(category, childSegments))
                        {
                            CopyFile(entry, childSegments, category);
                        }

                        break;
                    case SettingsBackupService.BackupEntryKind.SymbolicLink:
                        if (
                            LocalDataInventory.IsExportedBelow(category, childSegments)
                            || LocalDataInventory.IsFolderExportedBelow(category, childSegments)
                        )
                        {
                            Skip(string.Join('/', childSegments), LocalDataExportSkipReason.Link);
                        }

                        break;
                    default:
                        if (LocalDataInventory.IsExportedBelow(category, childSegments))
                        {
                            Skip(string.Join('/', childSegments), LocalDataExportSkipReason.NotRegularFile);
                        }

                        break;
                }
            }
        }

        private string[]? ListFolder(string path, string relativeFolder)
        {
            try
            {
                var entries = Directory.GetFileSystemEntries(path);
                Array.Sort(entries, StringComparer.Ordinal);
                return entries;
            }
            catch (DirectoryNotFoundException) when (relativeFolder.Length > 0)
            {
                // Removed during the export.
                return null;
            }
            catch (Exception ex) when (relativeFolder.Length > 0 && ex is IOException or UnauthorizedAccessException)
            {
                Skip(relativeFolder, LocalDataExportSkipReason.Unreadable);
                return null;
            }
        }

        private SettingsBackupService.BackupEntryKind? GetKind(string path, string relativePath)
        {
            try
            {
                return SettingsBackupService.NativeFile.GetEntryKind(path);
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
            {
                Skip(relativePath, LocalDataExportSkipReason.Unreadable);
                return null;
            }
        }

        private void CopyFile(string path, List<string> segments, LocalDataCategory category)
        {
            var relativePath = string.Join('/', segments);
            owner._beforeOpen?.Invoke(relativePath);
            FileStream source;
            try
            {
                source = SettingsBackupService.NativeFile.OpenRegularFile(path, relativePath, _guarded);
            }
            catch (InvalidOperationException)
            {
                Skip(relativePath, LocalDataExportSkipReason.ProtectionKey);
                return;
            }
            catch (Exception ex) when (ex is Win32Exception { NativeErrorCode: 6 or 19 } or IOException)
            {
                // Removed, replaced by a link, or (ENXIO/ENODEV) by a socket or device after it was listed.
                Skip(relativePath, LocalDataExportSkipReason.Changed);
                return;
            }
            catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
            {
                Skip(relativePath, LocalDataExportSkipReason.Unreadable);
                return;
            }

            using (source)
            {
                // A folder on the way may have been swapped for a link since it was listed, even one
                // inside the data folder whose files need other handling.
                if (_canVerifyOpenedPath && !IsOpenedAt(source, root + "/" + relativePath))
                {
                    Skip(relativePath, LocalDataExportSkipReason.Changed);
                    return;
                }

                var lastWriteTime = new DateTimeOffset(File.GetLastWriteTimeUtc(source.SafeFileHandle).ToLocalTime());
                var redact = RedactorFor(segments);
                var backedUp = IsBackedUp(category, segments);
                // Read whole only when it must be redacted or still fits the settings backup.
                if (!backedUp || redact is null && !BackupHasRoomFor(source.Length))
                {
                    if (backedUp)
                    {
                        AbandonBackup();
                    }

                    CopyStream(source, relativePath, lastWriteTime);
                    return;
                }

                ReadOnlyMemory<byte>? read;
                try
                {
                    // Redacting holds the whole file and its parsed form, so settings files are bounded.
                    read = ReadAll(source, redact is null ? long.MaxValue : MaxSettingsBytes);
                }
                catch (IOException)
                {
                    Skip(relativePath, LocalDataExportSkipReason.Unreadable);
                    return;
                }

                if (read is not { } content || redact is not null && !TryRedact(redact, ref content))
                {
                    Skip(relativePath, LocalDataExportSkipReason.SecretsNotRemovable);
                    return;
                }

                WriteEntry(relativePath, content.Span, lastWriteTime);
                AddToBackup(relativePath, content.Span, lastWriteTime);
            }
        }

        private static bool IsOpenedAt(FileStream source, string expectedPath)
        {
            try
            {
                var opened = new FileInfo($"/proc/self/fd/{source.SafeFileHandle.DangerousGetHandle()}").LinkTarget;
                return string.Equals(opened, expectedPath, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        // The categories a settings backup carries; the rest is only copied into the archive.
        private static bool IsBackedUp(LocalDataCategory category, List<string> segments) =>
            category is LocalDataCategory.AppData or LocalDataCategory.PluginData
            || category == LocalDataCategory.Settings && segments.Count == 1;

        // A store keeps copies beside its file (settings.json.bak, settings.json.broken-*)
        // that hold the same secrets.
        private static Func<JsonNode, bool>? RedactorFor(List<string> segments) =>
            segments switch
            {
                [var name] when s_rootSettingsFiles.Contains(name) => RemoveRootSecrets,
                ["PluginData", _, var name] when IsStoreFile(name, "settings.json") => RemovePluginSecrets,
                ["PluginData", WebhookPluginFolder, var name] when IsStoreFile(name, "webhooks.json") =>
                    RemoveLegacyWebhookHeaders,
                _ => null,
            };

        private static bool TryRedact(Func<JsonNode, bool> strip, ref ReadOnlyMemory<byte> content)
        {
            try
            {
                // The app's stores accept a UTF-8 byte order mark.
                var json = content.Span;
                if (json.StartsWith(Encoding.UTF8.Preamble))
                {
                    json = json[Encoding.UTF8.Preamble.Length..];
                }

                if (JsonNode.Parse(json) is not { } document || !strip(document))
                {
                    return false;
                }

                content = Encoding.UTF8.GetBytes(document.ToJsonString(s_jsonOptions));
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool IsStoreFile(string name, string storeName) =>
            name == storeName || name.StartsWith(storeName + ".", StringComparison.Ordinal);

        private static bool RemoveRootSecrets(JsonNode document)
        {
            if (document is not JsonObject settings)
            {
                return false;
            }

            foreach (var property in SecretProtectionMigrationService.RootSecretProperties)
            {
                settings.Remove(property);
            }

            return true;
        }

        private static bool RemovePluginSecrets(JsonNode document)
        {
            if (document is not JsonObject settings)
            {
                return false;
            }

            foreach (var key in settings
                         .Select(property => property.Key)
                         .Where(key => key.StartsWith(SecretProtectionMigrationService.SecretPrefix, StringComparison.Ordinal))
                         .ToList())
            {
                settings.Remove(key);
            }

            return true;
        }

        // Webhooks saved before header values moved to the secret store kept them in plain text;
        // they stay there until the plugin is next activated.
        private static bool RemoveLegacyWebhookHeaders(JsonNode document)
        {
            if (document is not JsonArray webhooks || webhooks.Any(webhook => webhook is not JsonObject))
            {
                return false;
            }

            foreach (var webhook in webhooks.Cast<JsonObject>())
            {
                foreach (var key in webhook
                             .Select(property => property.Key)
                             .Where(key => string.Equals(key, "headers", StringComparison.OrdinalIgnoreCase))
                             .ToList())
                {
                    webhook.Remove(key);
                }
            }

            return true;
        }

        // Null when the file holds more than limit bytes.
        private ReadOnlyMemory<byte>? ReadAll(FileStream source, long limit)
        {
            using var buffer = new MemoryStream((int)Math.Min(Math.Min(source.Length, limit), Array.MaxLength));
            var chunk = new byte[81920];
            int read;
            while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (buffer.Length + read > limit)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
        }

        private void CopyStream(FileStream source, string relativePath, DateTimeOffset lastWriteTime)
        {
            var entry = CreateEntry(DataFolderName + "/" + relativePath, lastWriteTime);
            long written = 0;
            using (var target = entry.Open())
            {
                var chunk = new byte[81920];
                int read;
                while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    target.Write(chunk, 0, read);
                    written += read;
                }
            }

            // What was read, not the current length: another writer may change the file meanwhile.
            Count(written);
        }

        private void WriteEntry(string relativePath, ReadOnlySpan<byte> content, DateTimeOffset lastWriteTime)
        {
            using (var target = CreateEntry(DataFolderName + "/" + relativePath, lastWriteTime).Open())
            {
                target.Write(content);
            }

            Count(content.Length);
        }

        private ZipArchiveEntry CreateEntry(string entryName, DateTimeOffset lastWriteTime)
        {
            var entry = archive.CreateEntry(
                entryName,
                s_storedExtensions.Contains(Path.GetExtension(entryName))
                    ? CompressionLevel.NoCompression
                    : CompressionLevel.Fastest
            );
            // Zip keeps only the wall-clock time, read back as local.
            entry.LastWriteTime = lastWriteTime.Year is < 1980 or > 2107
                ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
                : lastWriteTime;
            return entry;
        }

        private void Count(long bytes)
        {
            _fileCount++;
            _bytes += bytes;
            progress?.Report(new LocalDataExportProgress(_fileCount, _bytes));
        }

        private bool BackupHasRoomFor(long bytes) =>
            _backup is not null && SettingsBackupService.FitsRestoreLimits(_backupFileCount + 1, _backupBytes + bytes);

        private void AddToBackup(string relativePath, ReadOnlySpan<byte> content, DateTimeOffset lastWriteTime)
        {
            if (_backup is null)
            {
                return;
            }

            if (
                !BackupHasRoomFor(content.Length)
                || !SettingsBackupService.PortableBackupWriter.Carries(relativePath)
            )
            {
                AbandonBackup();
                return;
            }

            _backupFileCount++;
            _backupBytes += content.Length;
            _backup.Add(relativePath, content, lastWriteTime);
        }

        // Restore would refuse the backup; the data folder copy still carries everything.
        private void AbandonBackup()
        {
            _backup?.Dispose();
            _backup = null;
        }

        private bool WriteSettingsBackup()
        {
            if (_backup is null)
            {
                return false;
            }

            _backup.Dispose();
            _backup = null;
            using var target = archive.CreateEntry(SettingsBackupEntryName, CompressionLevel.NoCompression).Open();
            backupStream.Position = 0;
            var chunk = new byte[81920];
            int read;
            while ((read = backupStream.Read(chunk, 0, chunk.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                target.Write(chunk, 0, read);
            }

            return true;
        }

        public void Dispose() => _backup?.Dispose();

        private void Skip(string relativePath, LocalDataExportSkipReason reason) =>
            _skipped.Add(new LocalDataExportSkip(relativePath, reason));

        private void WriteText(string entryName, string text)
        {
            using var target = archive.CreateEntry(entryName, CompressionLevel.Optimal).Open();
            target.Write(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));
        }

        private string Manifest(LocalDataExportResult result)
        {
            var manifest = new JsonObject
            {
                ["app"] = "TypeWhisper",
                ["kind"] = "data-export",
                ["version"] = ManifestVersion,
                ["createdUtc"] = DateTimeOffset.UtcNow,
                ["included"] = ToArray(_exported.Order()),
                ["excluded"] = ToArray(Enum.GetValues<LocalDataCategory>().Where(category => !IsExported(category))),
                ["settingsBackup"] = result.IncludesSettingsBackup,
                ["fileCount"] = result.FileCount,
                ["bytes"] = result.Bytes,
                ["skipped"] = new JsonArray(
                    result.Skipped
                        .Select(JsonNode (skip) => new JsonObject
                        {
                            ["path"] = skip.RelativePath,
                            ["reason"] = ToCamelCase(skip.Reason.ToString()),
                        })
                        .ToArray()
                ),
                ["unrecognized"] = new JsonArray(result.Unrecognized.Select(name => (JsonNode?)name).ToArray()),
            };
            return manifest.ToJsonString(s_jsonOptions);

            static JsonArray ToArray(IEnumerable<LocalDataCategory> categories) =>
                new(categories.Select(category => (JsonNode?)ToCamelCase(category.ToString())).ToArray());
        }

        private static string ToCamelCase(string name) => char.ToLowerInvariant(name[0]) + name[1..];

        private string ReadMe(LocalDataExportResult result)
        {
            var text = new StringBuilder()
                .Append("TypeWhisper data export\n")
                .Append("Created ").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm zzz")).Append("\n\n");
            if (result.IncludesSettingsBackup)
            {
                text.Append(SettingsBackupEntryName)
                    .Append(": your settings, history, dictionary, profiles, snippets, prompt actions and plugin data ")
                    .Append("as a settings backup. Restore it under About > Settings Backup > Restore. ")
                    .Append("It carries no API keys, so enter them again after restoring.\n");
            }
            else
            {
                text.Append("No settings backup could be made from this data: it is larger than Restore accepts ")
                    .Append("or holds a file a settings backup cannot carry. The data folder below holds the same data.\n");
            }

            text.Append(DataFolderName).Append("/: a copy of these parts of your TypeWhisper data folder:\n");
            foreach (var category in _exported.Order())
            {
                text.Append("  - ").Append(Describe(category)).Append('\n');
            }

            text.Append("\nNot included:\n");
            foreach (var category in Enum.GetValues<LocalDataCategory>().Where(category => !IsExported(category)))
            {
                text.Append("  - ").Append(Describe(category)).Append('\n');
            }

            text.Append("  - files TypeWhisper keeps outside its data folder: the autostart entry, ")
                .Append("environment settings, browser launcher copies and the local API discovery file\n")
                .Append("\nWebhook URLs and other plugin settings are copied as they are; ")
                .Append("a webhook URL that carries a token still carries it here.\n");

            if (result.Unrecognized.Count > 0)
            {
                text.Append("\nThese entries in the data folder were not created by TypeWhisper and were left out:\n");
                foreach (var name in result.Unrecognized)
                {
                    text.Append("  ").Append(name).Append('\n');
                }
            }

            // ReSharper disable once InvertIf -- the last optional section; inverting it only duplicates the return.
            if (result.Skipped.Count > 0)
            {
                text.Append("\nThese files and folders are missing from this export:\n");
                foreach (var skip in result.Skipped)
                {
                    text.Append("  ").Append(skip.RelativePath).Append(": ").Append(Describe(skip.Reason)).Append('\n');
                }
            }

            return text.ToString();
        }
    }
}
