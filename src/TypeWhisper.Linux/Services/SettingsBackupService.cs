using System.IO;
using System.IO.Compression;
using System.Text.Json;
using TypeWhisper.Core;

namespace TypeWhisper.Linux.Services;

public sealed record SettingsBackupResult(int FileCount, long UncompressedBytes);

public sealed class SettingsBackupService
{
    private const string ManifestEntryName = "typewhisper-backup.json";
    private static readonly string[] RootFiles =
    [
        "settings.json",
        "settings.json.bak",
        "linux-preferences.json"
    ];

    private static readonly string[] BackupDirectoryRoots =
    [
        "Data",
        "PluginData"
    ];

    private static readonly string[] RestoreDirectoryRoots =
    [
        "Data",
        "PluginData",
        "Plugins"
    ];

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
            throw new ArgumentException("Backup path is required.", nameof(destinationZipPath));

        var destinationDirectory = Path.GetDirectoryName(destinationZipPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        var tempPath = destinationZipPath + ".tmp";
        if (File.Exists(tempPath))
            File.Delete(tempPath);

        var fileCount = 0;
        long bytes = 0;

        using (var archive = ZipFile.Open(tempPath, ZipArchiveMode.Create))
        {
            var manifest = new
            {
                app = "TypeWhisper",
                kind = "settings-backup",
                createdUtc = DateTimeOffset.UtcNow,
                includes = new[] { "settings", "linux-preferences", "data", "plugin-data" },
                excludes = new[] { "models", "audio", "logs", "plugins" }
            };
            var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
            using (var writer = new StreamWriter(manifestEntry.Open()))
                writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            foreach (var relativeFile in RootFiles)
            {
                var path = Path.Combine(_basePath, relativeFile);
                AddFileIfExists(archive, path, relativeFile, ref fileCount, ref bytes);
            }

            foreach (var root in BackupDirectoryRoots)
            {
                var rootPath = Path.Combine(_basePath, root);
                if (!Directory.Exists(rootPath))
                    continue;

                foreach (var path in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(_basePath, path);
                    if (ShouldSkipPortableEntry(relativePath))
                        continue;

                    AddFileIfExists(archive, path, relativePath, ref fileCount, ref bytes);
                }
            }
        }

        File.Move(tempPath, destinationZipPath, overwrite: true);
        return new SettingsBackupResult(fileCount, bytes);
    }

    public SettingsBackupResult RestoreBackup(string sourceZipPath)
    {
        if (string.IsNullOrWhiteSpace(sourceZipPath) || !File.Exists(sourceZipPath))
            throw new FileNotFoundException("Backup file was not found.", sourceZipPath);

        var tempDir = Path.Combine(Path.GetTempPath(), $"typewhisper-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            using var archive = ZipFile.OpenRead(sourceZipPath);
            ValidateArchive(archive);

            var fileCount = 0;
            long bytes = 0;

            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.Length == 0 || entry.FullName.EndsWith("/", StringComparison.Ordinal))
                    continue;

                if (string.Equals(entry.FullName, ManifestEntryName, StringComparison.Ordinal))
                    continue;

                if (ShouldSkipPortableEntry(entry.FullName))
                    continue;

                var targetPath = GetSafeDestinationPath(tempDir, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                entry.ExtractToFile(targetPath, overwrite: true);
                fileCount++;
                bytes += entry.Length;
            }

            Directory.CreateDirectory(_basePath);

            foreach (var relativeFile in RootFiles)
            {
                var restoredPath = Path.Combine(tempDir, relativeFile);
                if (!File.Exists(restoredPath))
                    continue;

                var targetPath = Path.Combine(_basePath, relativeFile);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(restoredPath, targetPath, overwrite: true);
            }

            foreach (var root in RestoreDirectoryRoots)
            {
                var restoredRoot = Path.Combine(tempDir, root);
                if (!Directory.Exists(restoredRoot))
                    continue;

                var targetRoot = Path.Combine(_basePath, root);
                Directory.CreateDirectory(targetRoot);

                foreach (var restoredFile in Directory.EnumerateFiles(restoredRoot, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(restoredRoot, restoredFile);
                    var targetPath = Path.Combine(targetRoot, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    File.Copy(restoredFile, targetPath, overwrite: true);
                }
            }

            return new SettingsBackupResult(fileCount, bytes);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
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
        ref long bytes)
    {
        if (!File.Exists(path))
            return;

        var entryName = NormalizeEntryName(relativePath);
        archive.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal);
        fileCount++;
        bytes += new FileInfo(path).Length;
    }

    private static void ValidateArchive(ZipArchive archive)
    {
        if (archive.GetEntry(ManifestEntryName) is null)
            throw new InvalidDataException("This is not a TypeWhisper settings backup.");

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Length == 0 || entry.FullName.EndsWith("/", StringComparison.Ordinal))
                continue;

            if (string.Equals(entry.FullName, ManifestEntryName, StringComparison.Ordinal))
                continue;

            if (!IsAllowedEntry(entry.FullName))
                throw new InvalidDataException($"Backup contains an unsupported path: {entry.FullName}");

            _ = GetSafeDestinationPath(Path.GetTempPath(), entry.FullName);
        }
    }

    private static bool IsAllowedEntry(string entryName)
    {
        var normalized = NormalizeEntryName(entryName);
        if (RootFiles.Contains(normalized, StringComparer.Ordinal))
            return true;

        return RestoreDirectoryRoots.Any(root =>
            normalized.StartsWith(root + "/", StringComparison.Ordinal));
    }

    private static bool ShouldSkipPortableEntry(string relativePath)
    {
        var parts = NormalizeEntryName(relativePath).Split('/');
        return parts.Any(part => string.Equals(part, "Models", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetSafeDestinationPath(string rootPath, string entryName)
    {
        var normalized = NormalizeEntryName(entryName);
        if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"Backup contains an unsafe path: {entryName}");

        var fullRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var destination = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        if (!destination.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"Backup contains an unsafe path: {entryName}");

        return destination;
    }

    private static string NormalizeEntryName(string path) =>
        path.Replace('\\', '/').TrimStart('/');
}
