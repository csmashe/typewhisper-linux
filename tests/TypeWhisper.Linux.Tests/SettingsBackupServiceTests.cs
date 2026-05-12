using System.IO.Compression;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SettingsBackupServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"tw-backup-test-{Guid.NewGuid():N}");

    [Fact]
    public void CreateBackup_includes_settings_and_user_data_but_skips_generated_content()
    {
        var appData = Path.Combine(_tempDir, "app-data");
        var backupPath = Path.Combine(_tempDir, "backup.zip");
        Write(Path.Combine(appData, "settings.json"), "{}");
        Write(Path.Combine(appData, "linux-preferences.json"), "{}");
        Write(Path.Combine(appData, "Data", "profiles.json"), "profiles");
        Write(Path.Combine(appData, "PluginData", "FileMemory", "memories.json"), "memories");
        Write(Path.Combine(appData, "PluginData", "com.typewhisper.whisper-cpp", "Models", "ggml-base.bin"), "model");
        Write(Path.Combine(appData, "PluginData", "com.typewhisper.sherpa-onnx", "Models", "parakeet", "encoder.onnx"), "model");
        Write(Path.Combine(appData, "Plugins", "Sample", "manifest.json"), "plugin");
        Write(Path.Combine(appData, "Models", "large.bin"), "model");
        Write(Path.Combine(appData, "Audio", "capture.wav"), "audio");
        Write(Path.Combine(appData, "Logs", "app.log"), "log");

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
        Assert.DoesNotContain("PluginData/com.typewhisper.whisper-cpp/Models/ggml-base.bin", entries);
        Assert.DoesNotContain("PluginData/com.typewhisper.sherpa-onnx/Models/parakeet/encoder.onnx", entries);
        Assert.DoesNotContain("Plugins/Sample/manifest.json", entries);
        Assert.DoesNotContain("Models/large.bin", entries);
        Assert.DoesNotContain("Audio/capture.wav", entries);
        Assert.DoesNotContain("Logs/app.log", entries);
    }

    [Fact]
    public void RestoreBackup_overwrites_settings_and_user_data()
    {
        var sourceData = Path.Combine(_tempDir, "source");
        var targetData = Path.Combine(_tempDir, "target");
        var backupPath = Path.Combine(_tempDir, "backup.zip");
        Write(Path.Combine(sourceData, "settings.json"), "{\"language\":\"de\"}");
        Write(Path.Combine(sourceData, "Data", "snippets.json"), "[\"restored\"]");
        Write(Path.Combine(sourceData, "PluginData", "FileMemory", "memories.json"), "restored");
        Write(Path.Combine(sourceData, "PluginData", "com.typewhisper.whisper-cpp", "Models", "ggml-base.bin"), "model");
        Write(Path.Combine(targetData, "settings.json"), "{\"language\":\"en\"}");
        Write(Path.Combine(targetData, "Data", "snippets.json"), "[\"old\"]");

        new SettingsBackupService(sourceData).CreateBackup(backupPath);
        var service = new SettingsBackupService(targetData);

        var result = service.RestoreBackup(backupPath);

        Assert.Equal(3, result.FileCount);
        Assert.Contains("de", File.ReadAllText(Path.Combine(targetData, "settings.json")));
        Assert.Equal("[\"restored\"]", File.ReadAllText(Path.Combine(targetData, "Data", "snippets.json")));
        Assert.Equal("restored", File.ReadAllText(Path.Combine(targetData, "PluginData", "FileMemory", "memories.json")));
        Assert.False(File.Exists(Path.Combine(targetData, "PluginData", "com.typewhisper.whisper-cpp", "Models", "ggml-base.bin")));
    }

    [Fact]
    public void RestoreBackup_skips_models_from_older_backup_archives()
    {
        var backupPath = Path.Combine(_tempDir, "old-backup.zip");
        var targetData = Path.Combine(_tempDir, "target");
        Directory.CreateDirectory(_tempDir);
        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("typewhisper-backup.json");
            WriteEntry(archive, "settings.json", "{}");
            WriteEntry(archive, "PluginData/com.typewhisper.whisper-cpp/settings.json", "{}");
            WriteEntry(archive, "PluginData/com.typewhisper.whisper-cpp/Models/ggml-base.bin", "model");
        }

        var service = new SettingsBackupService(targetData);

        var result = service.RestoreBackup(backupPath);

        Assert.Equal(2, result.FileCount);
        Assert.True(File.Exists(Path.Combine(targetData, "settings.json")));
        Assert.True(File.Exists(Path.Combine(targetData, "PluginData", "com.typewhisper.whisper-cpp", "settings.json")));
        Assert.False(File.Exists(Path.Combine(targetData, "PluginData", "com.typewhisper.whisper-cpp", "Models", "ggml-base.bin")));
    }

    [Fact]
    public void RestoreBackup_rejects_unsupported_paths()
    {
        var backupPath = Path.Combine(_tempDir, "bad.zip");
        Directory.CreateDirectory(_tempDir);
        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("typewhisper-backup.json");
            var entry = archive.CreateEntry("../escape.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("bad");
        }

        var service = new SettingsBackupService(Path.Combine(_tempDir, "target"));

        Assert.Throws<InvalidDataException>(() => service.RestoreBackup(backupPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
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
}
