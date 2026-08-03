using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.Core;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.Services;

// ReSharper disable once NotAccessedPositionalProperty.Global  UncompressedBytes carried in the backup result record's data shape
public sealed record SettingsBackupResult(int FileCount, long UncompressedBytes);

public sealed class SettingsBackupService
{
    private const string ManifestEntryName = "typewhisper-backup.json";

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

    private readonly string _basePath;

    public SettingsBackupService()
        : this(TypeWhisperEnvironment.BasePath)
    {
    }

    internal SettingsBackupService(string basePath)
    {
        _basePath = Path.GetFullPath(basePath);
    }

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

    public SettingsBackupResult RestoreBackup(string sourceZipPath)
    {
        if (string.IsNullOrWhiteSpace(sourceZipPath) || !File.Exists(sourceZipPath))
        {
            throw new FileNotFoundException("Backup file was not found.", sourceZipPath);
        }

        // Extract into a temp dir first; only copy into _basePath after all
        // entries are validated, so a corrupt archive can't leave a mixed state.
        var tempDir = Path.Join(Path.GetTempPath(), $"typewhisper-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

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

                var targetPath = GetSafeDestinationPath(tempDir, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                entry.ExtractToFile(targetPath, true);
                fileCount++;
                bytes += entry.Length;
            }

            Directory.CreateDirectory(_basePath);

            foreach (var relativeFile in s_rootFiles)
            {
                var restoredPath = Path.Join(tempDir, relativeFile);
                if (!File.Exists(restoredPath))
                {
                    continue;
                }

                var targetPath = Path.Join(_basePath, relativeFile);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(restoredPath, targetPath, true);
            }

            foreach (var root in s_backupDirectoryRoots)
            {
                var restoredRoot = Path.Join(tempDir, root);
                if (!Directory.Exists(restoredRoot))
                {
                    continue;
                }

                var targetRoot = Path.Join(_basePath, root);
                Directory.CreateDirectory(targetRoot);

                foreach (
                    var restoredFile in Directory.EnumerateFiles(
                        restoredRoot,
                        "*",
                        SearchOption.AllDirectories
                    )
                )
                {
                    var relativePath = Path.GetRelativePath(restoredRoot, restoredFile);
                    var targetPath = Path.Join(targetRoot, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    File.Copy(restoredFile, targetPath, true);
                }
            }

            return new SettingsBackupResult(fileCount, bytes);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
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
