using System.IO.Compression;
using TypeWhisper.Linux.Services;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SettingsBackupServiceTests : IDisposable
{
    private readonly string _tempDir = TestPaths.CreateTempDirectory(
        "TypeWhisper.SettingsBackupServiceTests"
    );

    public void Dispose()
    {
        try
        {
            TestPaths.DeleteDirectory(_tempDir);
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
        }
    }

    [Fact]
    public void CreateBackup_includes_settings_and_user_data_but_skips_generated_content()
    {
        var appData = Path.Join(_tempDir, "app-data");
        var backupPath = Path.Join(_tempDir, "backup.zip");
        Write(Path.Join(appData, "settings.json"), "{}");
        Write(Path.Join(appData, "linux-preferences.json"), "{}");
        Write(Path.Join(appData, "Data", "profiles.json"), "profiles");
        Write(Path.Join(appData, "PluginData", "FileMemory", "memories.json"), "memories");
        Write(
            Path.Join(
                appData,
                "PluginData",
                "com.typewhisper.whisper-cpp",
                "Models",
                "ggml-base.bin"
            ),
            "model"
        );
        Write(
            Path.Join(
                appData,
                "PluginData",
                "com.typewhisper.sherpa-onnx",
                "Models",
                "parakeet",
                "encoder.onnx"
            ),
            "model"
        );
        Write(Path.Join(appData, "Plugins", "Sample", "manifest.json"), "plugin");
        Write(Path.Join(appData, "Models", "large.bin"), "model");
        Write(Path.Join(appData, "Audio", "capture.wav"), "audio");
        Write(Path.Join(appData, "Logs", "app.log"), "log");

        var service = new SettingsBackupService(appData);

        var result = service.CreateBackup(backupPath);

        Assert.Equal(4, result.FileCount);
        using var archive = ZipFile.OpenRead(backupPath);
        var entries = archive.Entries.Select(e => e.FullName).Order().ToArray();
        Assert.Contains("typewhisper-backup.json", entries);
        Assert.Contains("settings.json", entries);
        Assert.Contains("linux-preferences.json", entries);
        Assert.Contains("Data/profiles.json", entries);
        Assert.Contains("PluginData/FileMemory/memories.json", entries);
        Assert.DoesNotContain(
            "PluginData/com.typewhisper.whisper-cpp/Models/ggml-base.bin",
            entries
        );
        Assert.DoesNotContain(
            "PluginData/com.typewhisper.sherpa-onnx/Models/parakeet/encoder.onnx",
            entries
        );
        Assert.DoesNotContain("Plugins/Sample/manifest.json", entries);
        Assert.DoesNotContain("Models/large.bin", entries);
        Assert.DoesNotContain("Audio/capture.wav", entries);
        Assert.DoesNotContain("Logs/app.log", entries);
    }

    [Fact]
    public void RestoreBackup_overwrites_settings_and_user_data()
    {
        var sourceData = Path.Join(_tempDir, "source");
        var targetData = Path.Join(_tempDir, "target");
        var backupPath = Path.Join(_tempDir, "backup.zip");
        Write(Path.Join(sourceData, "settings.json"), "{\"language\":\"de\"}");
        Write(Path.Join(sourceData, "Data", "snippets.json"), "[\"restored\"]");
        Write(Path.Join(sourceData, "PluginData", "FileMemory", "memories.json"), "restored");
        Write(Path.Join(sourceData, "PluginData", "runtime", "libprovider.so"), "native");
        var restoredSo = Path.Join(targetData, "PluginData", "runtime", "libprovider.so");
        Write(
            Path.Join(
                sourceData,
                "PluginData",
                "com.typewhisper.whisper-cpp",
                "Models",
                "ggml-base.bin"
            ),
            "model"
        );
        Write(Path.Join(targetData, "settings.json"), "{\"language\":\"en\"}");
        Write(Path.Join(targetData, "Data", "snippets.json"), "[\"old\"]");

        new SettingsBackupService(sourceData).CreateBackup(backupPath);
        var service = new SettingsBackupService(targetData);

        var result = service.RestoreBackup(backupPath);

        // 3, not 4: the native .so is never exported (re-downloadable runtime).
        Assert.Equal(3, result.FileCount);
        Assert.Contains("de", File.ReadAllText(Path.Join(targetData, "settings.json")));
        Assert.Equal(
            "[\"restored\"]",
            File.ReadAllText(Path.Join(targetData, "Data", "snippets.json"))
        );
        Assert.Equal(
            "restored",
            File.ReadAllText(Path.Join(targetData, "PluginData", "FileMemory", "memories.json"))
        );
        Assert.False(File.Exists(restoredSo));
        Assert.False(
            File.Exists(
                Path.Join(
                    targetData,
                    "PluginData",
                    "com.typewhisper.whisper-cpp",
                    "Models",
                    "ggml-base.bin"
                )
            )
        );
    }

    [Fact]
    public void RestoreBackup_skips_models_from_older_backup_archives()
    {
        var backupPath = Path.Join(_tempDir, "old-backup.zip");
        var targetData = Path.Join(_tempDir, "target");
        Directory.CreateDirectory(_tempDir);
        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
        {
            WriteValidManifest(archive);
            WriteEntry(archive, "settings.json", "{}");
            WriteEntry(archive, "PluginData/com.typewhisper.whisper-cpp/settings.json", "{}");
            WriteEntry(
                archive,
                "PluginData/com.typewhisper.whisper-cpp/Models/ggml-base.bin",
                "model"
            );
        }

        var service = new SettingsBackupService(targetData);

        var result = service.RestoreBackup(backupPath);

        Assert.Equal(2, result.FileCount);
        Assert.True(File.Exists(Path.Join(targetData, "settings.json")));
        Assert.True(
            File.Exists(
                Path.Join(
                    targetData,
                    "PluginData",
                    "com.typewhisper.whisper-cpp",
                    "settings.json"
                )
            )
        );
        Assert.False(
            File.Exists(
                Path.Join(
                    targetData,
                    "PluginData",
                    "com.typewhisper.whisper-cpp",
                    "Models",
                    "ggml-base.bin"
                )
            )
        );
    }

    [Fact]
    public void RestoreBackup_rejects_plugin_content_without_touching_live_files()
    {
        var backupPath = Path.Join(_tempDir, "plugins.zip");
        var targetData = Path.Join(_tempDir, "target");
        var livePluginDirectory = Path.Join(targetData, "Plugins", "existing");
        Write(Path.Join(targetData, "settings.json"), "old settings");
        Write(Path.Join(livePluginDirectory, "marker.txt"), "untouched");
        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
        {
            WriteValidManifest(archive);
            WriteEntry(archive, "settings.json", "new settings");
            WriteEntry(archive, "Plugins/evil/manifest.json", "{}");
            WriteEntry(archive, "Plugins/evil/evil.dll", "malicious");
        }

        var service = new SettingsBackupService(targetData);

        var exception = Assert.Throws<InvalidDataException>(() =>
            service.RestoreBackup(backupPath)
        );

        Assert.Contains("plugin", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old settings", File.ReadAllText(Path.Join(targetData, "settings.json")));
        Assert.Equal(
            "untouched",
            File.ReadAllText(Path.Join(livePluginDirectory, "marker.txt"))
        );
        Assert.False(Directory.Exists(Path.Join(targetData, "Plugins", "evil")));
    }

    [Fact]
    public void RestoreBackup_rejects_entries_outside_exporter_allowlist()
    {
        var backupPath = Path.Join(_tempDir, "unsupported.zip");
        var targetData = Path.Join(_tempDir, "target");
        Write(Path.Join(targetData, "settings.json"), "old settings");
        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
        {
            WriteValidManifest(archive);
            WriteEntry(archive, "settings.json", "new settings");
            WriteEntry(archive, "Other/data.json", "{}");
        }

        var service = new SettingsBackupService(targetData);

        Assert.Throws<InvalidDataException>(() => service.RestoreBackup(backupPath));
        Assert.Equal("old settings", File.ReadAllText(Path.Join(targetData, "settings.json")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    public void RestoreBackup_rejects_invalid_manifest_without_restoring(string manifest)
    {
        var backupPath = Path.Join(_tempDir, "invalid-manifest.zip");
        var targetData = Path.Join(_tempDir, "target");
        Write(Path.Join(targetData, "settings.json"), "old settings");
        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "typewhisper-backup.json", manifest);
            WriteEntry(archive, "settings.json", "new settings");
        }

        var service = new SettingsBackupService(targetData);

        Assert.Throws<InvalidDataException>(() => service.RestoreBackup(backupPath));
        Assert.Equal("old settings", File.ReadAllText(Path.Join(targetData, "settings.json")));
    }

    [Fact]
    public void RestoreBackup_rejects_executable_outside_exported_roots()
    {
        var backupPath = Path.Join(_tempDir, "executable.zip");
        var targetData = Path.Join(_tempDir, "target");
        Write(Path.Join(targetData, "settings.json"), "old settings");
        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
        {
            WriteValidManifest(archive);
            WriteEntry(archive, "payload.exe", "malicious");
        }

        var service = new SettingsBackupService(targetData);

        var exception = Assert.Throws<InvalidDataException>(() =>
            service.RestoreBackup(backupPath)
        );
        Assert.Contains("executable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old settings", File.ReadAllText(Path.Join(targetData, "settings.json")));
    }

    [Theory]
    [InlineData("Data/payload.dll")]
    [InlineData("PluginData/com.typewhisper.whisper-cpp/Runtimes/whisper-cuda/x/runtimes/cuda/linux-x64/libwhisper.so")]
    [InlineData("PluginData/runtime/libprovider.dylib")]
    [InlineData("PluginData/com.typewhisper.whisper-cpp/Cuda/libcudart.so.12")]
    [InlineData("PluginData/runtime/libstdc++.so.6")]
    public void RestoreBackup_skips_executables_under_exported_roots(string entryName)
    {
        var backupPath = Path.Join(_tempDir, "legacy.zip");
        var targetData = Path.Join(_tempDir, "target");
        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
        {
            WriteValidManifest(archive);
            WriteEntry(archive, "settings.json", "restored");
            WriteEntry(archive, entryName, "native");
        }

        var service = new SettingsBackupService(targetData);

        var result = service.RestoreBackup(backupPath);

        Assert.Equal(1, result.FileCount);
        Assert.Equal("restored", File.ReadAllText(Path.Join(targetData, "settings.json")));
        Assert.False(File.Exists(Path.Join(targetData, NormalizeSeparators(entryName))));
    }

    private static string NormalizeSeparators(string entryName)
    {
        return entryName.Replace('/', Path.DirectorySeparatorChar);
    }

    [Fact]
    public void CreateBackup_excludes_native_runtime_executables()
    {
        var appData = Path.Join(_tempDir, "app-data");
        var backupPath = Path.Join(_tempDir, "backup.zip");
        Write(Path.Join(appData, "settings.json"), "{}");
        Write(Path.Join(appData, "PluginData", "FileMemory", "memories.json"), "memories");
        Write(
            Path.Join(appData, "PluginData", "runtime", "libprovider.so"),
            "native"
        );

        new SettingsBackupService(appData).CreateBackup(backupPath);

        using var archive = ZipFile.OpenRead(backupPath);
        var entries = archive.Entries.Select(e => e.FullName).ToArray();
        Assert.Contains("PluginData/FileMemory/memories.json", entries);
        Assert.DoesNotContain("PluginData/runtime/libprovider.so", entries);
    }

    [Fact]
    public void RestoreBackup_rejects_oversized_manifest()
    {
        var backupPath = Path.Join(_tempDir, "big-manifest.zip");
        var targetData = Path.Join(_tempDir, "target");
        Write(Path.Join(targetData, "settings.json"), "old settings");
        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "typewhisper-backup.json", new string('a', 128 * 1024));
            WriteEntry(archive, "settings.json", "new settings");
        }

        var service = new SettingsBackupService(targetData);

        Assert.Throws<InvalidDataException>(() => service.RestoreBackup(backupPath));
        Assert.Equal("old settings", File.ReadAllText(Path.Join(targetData, "settings.json")));
    }

    [Fact]
    public void RestoreBackup_tolerates_allowed_directory_placeholders()
    {
        var backupPath = Path.Join(_tempDir, "directories.zip");
        var targetData = Path.Join(_tempDir, "target");
        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
        {
            WriteValidManifest(archive);
            archive.CreateEntry("Data/");
            archive.CreateEntry("Data/nested/");
            archive.CreateEntry("PluginData/");
            WriteEntry(archive, "Data/nested/value.json", "{}");
        }

        var service = new SettingsBackupService(targetData);

        var result = service.RestoreBackup(backupPath);

        Assert.Equal(1, result.FileCount);
        Assert.True(File.Exists(Path.Join(targetData, "Data", "nested", "value.json")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("Data/../../escape.txt")]
    [InlineData(@"Data\..\..\escape.txt")]
    public void RestoreBackup_rejects_path_traversal(string entryName)
    {
        var backupPath = Path.Join(_tempDir, "bad.zip");
        Directory.CreateDirectory(_tempDir);
        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
        {
            WriteValidManifest(archive);
            WriteEntry(archive, entryName, "bad");
        }

        var targetData = Path.Join(_tempDir, "target");
        var service = new SettingsBackupService(targetData);

        Assert.Throws<InvalidDataException>(() => service.RestoreBackup(backupPath));
        Assert.False(File.Exists(Path.Join(_tempDir, "escape.txt")));
        Assert.False(File.Exists(Path.Join(targetData, "escape.txt")));
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static void WriteValidManifest(ZipArchive archive)
    {
        WriteEntry(
            archive,
            "typewhisper-backup.json",
            """
            {
              "app": "TypeWhisper",
              "kind": "settings-backup",
              "createdUtc": "2026-07-13T12:00:00Z",
              "includes": ["settings", "linux-preferences", "data", "plugin-data"],
              "excludes": ["models", "audio", "logs", "plugins"]
            }
            """
        );
    }
}
