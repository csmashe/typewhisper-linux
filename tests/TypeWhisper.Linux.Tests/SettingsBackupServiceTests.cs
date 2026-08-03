using System.IO.Compression;
using System.Text.Json;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SettingsBackupServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions s_indentedJson = new() { WriteIndented = true };

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
    public void StageRestore_then_startup_apply_overwrites_settings_and_user_data()
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

        var result = service.StageRestore(backupPath);
        var applyResult = service.ApplyPendingRestoreAtStartup();

        // 3, not 4: the native .so is never exported (re-downloadable runtime).
        Assert.Equal(3, result.FileCount);
        Assert.Equal(StartupRestoreStatus.Applied, applyResult.Status);
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
    public void StageRestore_skips_models_from_older_backup_archives()
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

        var result = service.StageRestore(backupPath);
        var applyResult = service.ApplyPendingRestoreAtStartup();

        Assert.Equal(2, result.FileCount);
        Assert.Equal(StartupRestoreStatus.Applied, applyResult.Status);
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
    public void StageRestore_rejects_plugin_content_without_touching_live_files()
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
        var pendingBefore = StageValidPending(service, "plugins-valid-pending.zip");

        var exception = Assert.Throws<InvalidDataException>(() =>
            service.StageRestore(backupPath)
        );

        Assert.Contains("plugin", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old settings", File.ReadAllText(Path.Join(targetData, "settings.json")));
        Assert.Equal(
            "untouched",
            File.ReadAllText(Path.Join(livePluginDirectory, "marker.txt"))
        );
        Assert.False(Directory.Exists(Path.Join(targetData, "Plugins", "evil")));
        Assert.Equal(pendingBefore, SnapshotFiles(service.PendingDirectoryPath));
    }

    [Fact]
    public void StageRestore_rejects_entries_outside_exporter_allowlist()
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
        var pendingBefore = StageValidPending(service, "unsupported-valid-pending.zip");

        Assert.Throws<InvalidDataException>(() => service.StageRestore(backupPath));
        Assert.Equal("old settings", File.ReadAllText(Path.Join(targetData, "settings.json")));
        Assert.Equal(pendingBefore, SnapshotFiles(service.PendingDirectoryPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    public void StageRestore_rejects_invalid_manifest_without_staging(string manifest)
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
        var pendingBefore = StageValidPending(service, "manifest-valid-pending.zip");

        Assert.Throws<InvalidDataException>(() => service.StageRestore(backupPath));
        Assert.Equal("old settings", File.ReadAllText(Path.Join(targetData, "settings.json")));
        Assert.Equal(pendingBefore, SnapshotFiles(service.PendingDirectoryPath));
    }

    [Fact]
    public void StageRestore_rejects_executable_outside_exported_roots()
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
        var pendingBefore = StageValidPending(service, "executable-valid-pending.zip");

        var exception = Assert.Throws<InvalidDataException>(() =>
            service.StageRestore(backupPath)
        );
        Assert.Contains("executable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old settings", File.ReadAllText(Path.Join(targetData, "settings.json")));
        Assert.Equal(pendingBefore, SnapshotFiles(service.PendingDirectoryPath));
    }

    [Theory]
    [InlineData("Data/payload.dll")]
    [InlineData("PluginData/com.typewhisper.whisper-cpp/Runtimes/whisper-cuda/x/runtimes/cuda/linux-x64/libwhisper.so")]
    [InlineData("PluginData/runtime/libprovider.dylib")]
    [InlineData("PluginData/com.typewhisper.whisper-cpp/Cuda/libcudart.so.12")]
    [InlineData("PluginData/runtime/libstdc++.so.6")]
    public void StageRestore_skips_executables_under_exported_roots(string entryName)
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

        var result = service.StageRestore(backupPath);
        var applyResult = service.ApplyPendingRestoreAtStartup();

        Assert.Equal(1, result.FileCount);
        Assert.Equal(StartupRestoreStatus.Applied, applyResult.Status);
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
    public void StageRestore_rejects_oversized_manifest()
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
        var pendingBefore = StageValidPending(service, "oversized-valid-pending.zip");

        Assert.Throws<InvalidDataException>(() => service.StageRestore(backupPath));
        Assert.Equal("old settings", File.ReadAllText(Path.Join(targetData, "settings.json")));
        Assert.Equal(pendingBefore, SnapshotFiles(service.PendingDirectoryPath));
    }

    [Fact]
    public void StageRestore_tolerates_allowed_directory_placeholders()
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

        var result = service.StageRestore(backupPath);
        var applyResult = service.ApplyPendingRestoreAtStartup();

        Assert.Equal(1, result.FileCount);
        Assert.Equal(StartupRestoreStatus.Applied, applyResult.Status);
        Assert.True(File.Exists(Path.Join(targetData, "Data", "nested", "value.json")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("Data/../../escape.txt")]
    [InlineData(@"Data\..\..\escape.txt")]
    public void StageRestore_rejects_path_traversal(string entryName)
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
        var pendingBefore = StageValidPending(service, "traversal-valid-pending.zip");

        Assert.Throws<InvalidDataException>(() => service.StageRestore(backupPath));
        Assert.False(File.Exists(Path.Join(_tempDir, "escape.txt")));
        Assert.False(File.Exists(Path.Join(targetData, "escape.txt")));
        Assert.Equal(pendingBefore, SnapshotFiles(service.PendingDirectoryPath));
    }

    [Fact]
    public void StageRestore_does_not_mutate_live_files()
    {
        var sourceData = Path.Join(_tempDir, "stage-source");
        var targetData = Path.Join(_tempDir, "stage-target");
        var backupPath = Path.Join(_tempDir, "stage.zip");
        var liveSettingsPath = Path.Join(targetData, "settings.json");
        var liveProfilesPath = Path.Join(targetData, "Data", "profiles.json");
        var livePluginSettingsPath = Path.Join(
            targetData,
            "PluginData",
            "sample.plugin",
            "settings.json"
        );

        Write(Path.Join(sourceData, "settings.json"), "{\"language\":\"fr\"}");
        WriteProfiles(
            Path.Join(sourceData, "Data", "profiles.json"),
            CreateProfile("restored", "Restored")
        );
        Write(
            Path.Join(sourceData, "PluginData", "sample.plugin", "settings.json"),
            "{\"generation\":\"restored\"}"
        );
        Write(liveSettingsPath, "{\"language\":\"en\"}");
        WriteProfiles(liveProfilesPath, CreateProfile("old", "Old"));
        Write(livePluginSettingsPath, "{\"generation\":\"old\"}");

        var settingsBefore = File.ReadAllBytes(liveSettingsPath);
        var profilesBefore = File.ReadAllBytes(liveProfilesPath);
        var pluginSettingsBefore = File.ReadAllBytes(livePluginSettingsPath);
        new SettingsBackupService(sourceData).CreateBackup(backupPath);
        var service = new SettingsBackupService(targetData);

        var result = service.StageRestore(backupPath);

        Assert.Equal(3, result.FileCount);
        Assert.Equal(settingsBefore, File.ReadAllBytes(liveSettingsPath));
        Assert.Equal(profilesBefore, File.ReadAllBytes(liveProfilesPath));
        Assert.Equal(pluginSettingsBefore, File.ReadAllBytes(livePluginSettingsPath));
        Assert.True(Directory.Exists(service.PendingDirectoryPath));
    }

    [Fact]
    public void Stale_cache_write_after_staging_cannot_win_over_startup_apply()
    {
        var sourceData = Path.Join(_tempDir, "stale-source");
        var targetData = Path.Join(_tempDir, "stale-target");
        var backupPath = Path.Join(_tempDir, "stale.zip");
        var profilesPath = Path.Join(targetData, "Data", "profiles.json");
        WriteProfiles(profilesPath, CreateProfile("old", "Old"));
        WriteProfiles(
            Path.Join(sourceData, "Data", "profiles.json"),
            CreateProfile("restored", "Restored")
        );
        new SettingsBackupService(sourceData).CreateBackup(backupPath);

        var staleService = new ProfileService(profilesPath);
        Assert.Equal("old", Assert.Single(staleService.Profiles).Id);
        var backupService = new SettingsBackupService(targetData);

        backupService.StageRestore(backupPath);
        staleService.AddProfile(CreateProfile("stale-added", "Stale added"));

        Assert.Equal(
            ["old", "stale-added"],
            ReadProfiles(profilesPath).Select(profile => profile.Id).Order().ToArray()
        );

        var applyResult = backupService.ApplyPendingRestoreAtStartup();
        var freshService = new ProfileService(profilesPath);

        Assert.Equal(StartupRestoreStatus.Applied, applyResult.Status);
        Assert.Equal("restored", Assert.Single(freshService.Profiles).Id);
        Assert.Equal("restored", Assert.Single(ReadProfiles(profilesPath)).Id);
    }

    [Fact]
    public void Fresh_services_serve_restored_root_data_and_plugin_settings_after_apply()
    {
        var sourceData = Path.Join(_tempDir, "fresh-source");
        var targetData = Path.Join(_tempDir, "fresh-target");
        var backupPath = Path.Join(_tempDir, "fresh.zip");
        Write(Path.Join(sourceData, "settings.json"), "{\"language\":\"fr\"}");
        WriteProfiles(
            Path.Join(sourceData, "Data", "profiles.json"),
            CreateProfile("restored", "Restored")
        );
        Write(
            Path.Join(sourceData, "PluginData", "sample.plugin", "settings.json"),
            "{\"generation\":\"restored\"}"
        );
        Write(Path.Join(targetData, "settings.json"), "{\"language\":\"en\"}");
        WriteProfiles(
            Path.Join(targetData, "Data", "profiles.json"),
            CreateProfile("old", "Old")
        );
        Write(
            Path.Join(targetData, "PluginData", "sample.plugin", "settings.json"),
            "{\"generation\":\"old\"}"
        );

        new SettingsBackupService(sourceData).CreateBackup(backupPath);
        var backupService = new SettingsBackupService(targetData);
        backupService.StageRestore(backupPath);

        var applyResult = backupService.ApplyPendingRestoreAtStartup();
        var settings = new SettingsService(Path.Join(targetData, "settings.json"));
        var profiles = new ProfileService(Path.Join(targetData, "Data", "profiles.json"));
        var pluginHost = new PluginHostServices(
            "sample.plugin",
            Path.Join(_tempDir, "plugin-binaries"),
            Mock.Of<IActiveWindowService>(),
            Mock.Of<IPluginEventBus>(),
            profiles,
            pluginDataRoot: Path.Join(targetData, "PluginData")
        );

        Assert.Equal(StartupRestoreStatus.Applied, applyResult.Status);
        Assert.Equal("fr", settings.Current.Language);
        Assert.Equal("restored", Assert.Single(profiles.Profiles).Id);
        Assert.Equal("restored", pluginHost.GetSetting<string>("generation"));
    }

    [Fact]
    public void Apply_failure_during_commit_rolls_back_exact_prior_generation()
    {
        var targetData = Path.Join(_tempDir, "rollback-target");
        var backupPath = Path.Join(_tempDir, "rollback.zip");
        var existingPath = Path.Join(targetData, "settings.json");
        var absentPath = Path.Join(targetData, "Data", "history.json");
        var originalBytes = "{\n  \"language\": \"en\"\n}"u8.ToArray();
        WriteBytes(existingPath, originalBytes);
        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
        {
            WriteValidManifest(archive);
            WriteEntry(archive, "Data/history.json", "[]");
            WriteEntry(archive, "settings.json", "{\"language\":\"fr\"}");
        }

        var service = new SettingsBackupService(
            targetData,
            // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local -- required by the RestoreCommitObserver signature; used to inject a mid-commit failure
            (_, committedFileCount) =>
            {
                // Fail after both targets commit, so rollback overwrites an
                // already-changed file, keeping the assertion below non-tautological.
                if (committedFileCount == 2)
                {
                    throw new IOException("Injected commit failure.");
                }
            }
        );
        service.StageRestore(backupPath);

        var result = service.ApplyPendingRestoreAtStartup();

        Assert.Equal(StartupRestoreStatus.PriorGenerationRestored, result.Status);
        Assert.Equal(originalBytes, File.ReadAllBytes(existingPath));
        Assert.False(File.Exists(absentPath));
        Assert.False(Directory.Exists(service.PendingDirectoryPath));
        Assert.Equal(
            StartupRestoreStatus.None,
            new SettingsBackupService(targetData).ApplyPendingRestoreAtStartup().Status
        );
    }

    [Fact]
    public void Prepared_journal_is_recovered_before_a_new_apply()
    {
        var targetData = Path.Join(_tempDir, "prepared-target");
        var backupPath = Path.Join(_tempDir, "prepared.zip");
        var settingsPath = Path.Join(targetData, "settings.json");
        var profilesPath = Path.Join(targetData, "Data", "profiles.json");
        Write(settingsPath, "{\"language\":\"en\"}");
        WriteProfiles(profilesPath, CreateProfile("old", "Old"));
        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
        {
            WriteValidManifest(archive);
            WriteEntry(archive, "settings.json", "{\"language\":\"fr\"}");
            WriteEntry(
                archive,
                "Data/profiles.json",
                JsonSerializer.Serialize(new[] { CreateProfile("restored", "Restored") })
            );
        }

        var interruptedService = new SettingsBackupService(
            targetData,
            // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local -- required by the RestoreCommitObserver signature; used to inject a mid-commit failure
            (_, committedFileCount) =>
            {
                if (committedFileCount == 1)
                {
                    throw new RestoreInterruptionException("Simulated process interruption.");
                }
            }
        );
        interruptedService.StageRestore(backupPath);

        Assert.Throws<RestoreInterruptionException>(
            interruptedService.ApplyPendingRestoreAtStartup
        );
        Assert.Equal("restored", Assert.Single(ReadProfiles(profilesPath)).Id);

        var recoveryResult = new SettingsBackupService(targetData)
            .ApplyPendingRestoreAtStartup();

        Assert.Equal(StartupRestoreStatus.PriorGenerationRestored, recoveryResult.Status);
        Assert.Contains("en", File.ReadAllText(settingsPath));
        Assert.Equal("old", Assert.Single(ReadProfiles(profilesPath)).Id);
        Assert.False(Directory.Exists(interruptedService.PendingDirectoryPath));
    }

    [Fact]
    public void Missing_rollback_snapshot_fails_closed_and_retains_recovery_evidence()
    {
        var targetData = Path.Join(_tempDir, "missing-rollback-target");
        var backupPath = Path.Join(_tempDir, "missing-rollback.zip");
        var profilesPath = Path.Join(targetData, "Data", "profiles.json");
        var settingsPath = Path.Join(targetData, "settings.json");
        Write(profilesPath, "old profiles");
        Write(settingsPath, "old settings");
        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
        {
            WriteValidManifest(archive);
            WriteEntry(archive, "Data/profiles.json", "restored profiles");
            WriteEntry(archive, "settings.json", "restored settings");
        }

        var interruptedService = new SettingsBackupService(
            targetData,
            // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local -- required by the RestoreCommitObserver signature; used to inject a mid-commit failure
            (_, committedFileCount) =>
            {
                if (committedFileCount == 1)
                {
                    throw new RestoreInterruptionException("Simulated process interruption.");
                }
            }
        );
        interruptedService.StageRestore(backupPath);

        Assert.Throws<RestoreInterruptionException>(
            interruptedService.ApplyPendingRestoreAtStartup
        );
        Assert.Equal("restored profiles", File.ReadAllText(profilesPath));
        Assert.Equal("old settings", File.ReadAllText(settingsPath));

        var journalPath = Path.Join(
            interruptedService.PendingDirectoryPath,
            "restore-journal.json"
        );
        var missingRollbackPath = Path.Join(
            interruptedService.PendingDirectoryPath,
            "rollback",
            "Data",
            "profiles.json"
        );
        var remainingRollbackPath = Path.Join(
            interruptedService.PendingDirectoryPath,
            "rollback",
            "settings.json"
        );
        Assert.True(File.Exists(journalPath));
        Assert.Contains("\"Phase\": \"Prepared\"", File.ReadAllText(journalPath));
        Assert.True(File.Exists(missingRollbackPath));
        Assert.True(File.Exists(remainingRollbackPath));
        File.Delete(missingRollbackPath);
        var liveProfilesBeforeRecovery = File.ReadAllBytes(profilesPath);
        var liveSettingsBeforeRecovery = File.ReadAllBytes(settingsPath);
        var pendingBeforeRecovery = SnapshotFiles(interruptedService.PendingDirectoryPath);

        var recoveryResult = new SettingsBackupService(targetData)
            .ApplyPendingRestoreAtStartup();

        Assert.Equal(StartupRestoreStatus.UnresolvedFailure, recoveryResult.Status);
        var recoveryErrors = Assert.IsType<AggregateException>(recoveryResult.Error);
        Assert.Equal(2, recoveryErrors.InnerExceptions.Count);
        var rollbackError = Assert.IsType<InvalidDataException>(
            recoveryErrors.InnerExceptions[1]
        );
        Assert.Equal(
            "The rollback snapshot for 'Data/profiles.json' is missing.",
            rollbackError.Message
        );
        Assert.Equal(liveProfilesBeforeRecovery, File.ReadAllBytes(profilesPath));
        Assert.Equal(liveSettingsBeforeRecovery, File.ReadAllBytes(settingsPath));
        Assert.True(File.Exists(journalPath));
        Assert.True(File.Exists(remainingRollbackPath));
        Assert.Equal("old settings", File.ReadAllText(remainingRollbackPath));
        Assert.Equal(
            pendingBeforeRecovery,
            SnapshotFiles(interruptedService.PendingDirectoryPath)
        );
    }

    [Fact]
    public void Unexpected_startup_apply_exception_returns_unresolved_failure()
    {
        var targetData = Path.Join(_tempDir, "unexpected-apply-target");
        var settingsPath = Path.Join(targetData, "settings.json");
        Write(settingsPath, "old settings");
        var service = new SettingsBackupService(targetData);
        Write(service.PendingDirectoryPath, "not a directory");

        var result = service.ApplyPendingRestoreAtStartup();

        Assert.Equal(StartupRestoreStatus.UnresolvedFailure, result.Status);
        var error = Assert.IsType<InvalidDataException>(result.Error);
        Assert.Equal("The staged settings restore path is not a directory.", error.Message);
        Assert.Equal("old settings", File.ReadAllText(settingsPath));
        Assert.Equal("not a directory", File.ReadAllText(service.PendingDirectoryPath));
    }

    [Fact]
    public void Committed_journal_is_not_rolled_back_when_cleanup_was_interrupted()
    {
        var targetData = Path.Join(_tempDir, "committed-target");
        var backupPath = Path.Join(_tempDir, "committed.zip");
        var settingsPath = Path.Join(targetData, "settings.json");
        Write(settingsPath, "{\"language\":\"en\"}");
        CreateBackupWithEntry(backupPath, "settings.json", "{\"language\":\"fr\"}");
        var service = new SettingsBackupService(
            targetData,
            cleanupObserver: () => throw new IOException("Injected cleanup interruption.")
        );
        service.StageRestore(backupPath);

        var applyResult = service.ApplyPendingRestoreAtStartup();

        Assert.Equal(StartupRestoreStatus.Applied, applyResult.Status);
        Assert.Contains("fr", File.ReadAllText(settingsPath));
        Assert.True(Directory.Exists(service.PendingDirectoryPath));

        var recoveryResult = new SettingsBackupService(targetData)
            .ApplyPendingRestoreAtStartup();

        Assert.Equal(StartupRestoreStatus.Applied, recoveryResult.Status);
        Assert.Contains("fr", File.ReadAllText(settingsPath));
        Assert.False(Directory.Exists(service.PendingDirectoryPath));
    }

    [Fact]
    public void RolledBack_journal_does_not_replay_old_snapshot_after_cleanup_was_interrupted()
    {
        var targetData = Path.Join(_tempDir, "rolled-back-target");
        var backupPath = Path.Join(_tempDir, "rolled-back.zip");
        var settingsPath = Path.Join(targetData, "settings.json");
        Write(settingsPath, "{\"language\":\"en\"}");
        CreateBackupWithEntry(backupPath, "settings.json", "{\"language\":\"fr\"}");
        var service = new SettingsBackupService(
            targetData,
            (_, _) => throw new IOException("Injected commit failure."),
            () => throw new IOException("Injected cleanup interruption.")
        );
        service.StageRestore(backupPath);

        var applyResult = service.ApplyPendingRestoreAtStartup();

        Assert.Equal(StartupRestoreStatus.PriorGenerationRestored, applyResult.Status);
        Assert.True(Directory.Exists(service.PendingDirectoryPath));
        Write(settingsPath, "{\"language\":\"newer\"}");

        var recoveryResult = new SettingsBackupService(targetData)
            .ApplyPendingRestoreAtStartup();

        Assert.Equal(StartupRestoreStatus.PriorGenerationRestored, recoveryResult.Status);
        Assert.Contains("newer", File.ReadAllText(settingsPath));
        Assert.False(Directory.Exists(service.PendingDirectoryPath));
    }

    [Fact]
    public void Concurrent_startup_apply_is_exclusive()
    {
        var targetData = Path.Join(_tempDir, "locked-target");
        var backupPath = Path.Join(_tempDir, "locked.zip");
        var settingsPath = Path.Join(targetData, "settings.json");
        Write(settingsPath, "{\"language\":\"en\"}");
        CreateBackupWithEntry(backupPath, "settings.json", "{\"language\":\"fr\"}");
        var service = new SettingsBackupService(targetData);
        service.StageRestore(backupPath);
        var pendingBefore = SnapshotFiles(service.PendingDirectoryPath);

        using (SettingsBackupService.AcquireStartupRestoreLock(targetData))
        {
            var contenderResult = new SettingsBackupService(targetData)
                .ApplyPendingRestoreAtStartup();

            Assert.Equal(StartupRestoreStatus.LockUnavailable, contenderResult.Status);
            Assert.Contains("en", File.ReadAllText(settingsPath));
            Assert.Equal(pendingBefore, SnapshotFiles(service.PendingDirectoryPath));
        }

        Assert.Equal(StartupRestoreStatus.Applied, service.ApplyPendingRestoreAtStartup().Status);
        Assert.Contains("fr", File.ReadAllText(settingsPath));
    }

    [Fact]
    public void Validation_failure_preserves_live_and_previously_staged_generation()
    {
        var targetData = Path.Join(_tempDir, "preserved-target");
        var validBackupPath = Path.Join(_tempDir, "preserved-valid.zip");
        var invalidBackupPath = Path.Join(_tempDir, "preserved-invalid.zip");
        var settingsPath = Path.Join(targetData, "settings.json");
        Write(settingsPath, "{\"language\":\"en\"}");
        CreateBackupWithEntry(validBackupPath, "settings.json", "{\"language\":\"fr\"}");
        using (var archive = ZipFile.Open(invalidBackupPath, ZipArchiveMode.Create))
        {
            WriteValidManifest(archive);
            WriteEntry(archive, "settings.json", "{\"language\":\"de\"}");
            WriteEntry(archive, "Other/data.json", "{}");
        }

        var service = new SettingsBackupService(targetData);
        service.StageRestore(validBackupPath);
        var pendingBefore = SnapshotFiles(service.PendingDirectoryPath);

        Assert.Throws<InvalidDataException>(() => service.StageRestore(invalidBackupPath));

        Assert.Equal("{\"language\":\"en\"}", File.ReadAllText(settingsPath));
        Assert.Equal(pendingBefore, SnapshotFiles(service.PendingDirectoryPath));
    }

    private static Profile CreateProfile(string id, string name)
    {
        return new Profile
        {
            Id = id,
            Name = name,
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch,
        };
    }

    private static void WriteProfiles(string path, params Profile[] profiles)
    {
        Write(
            path,
            JsonSerializer.Serialize(profiles, s_indentedJson)
        );
    }

    private static Profile[] ReadProfiles(string path)
    {
        return JsonSerializer.Deserialize<Profile[]>(File.ReadAllText(path)) ?? [];
    }

    private string[] StageValidPending(SettingsBackupService service, string backupFileName)
    {
        var backupPath = Path.Join(_tempDir, backupFileName);
        CreateBackupWithEntry(backupPath, "settings.json", "{\"language\":\"fr\"}");
        service.StageRestore(backupPath);
        return SnapshotFiles(service.PendingDirectoryPath);
    }

    private static void CreateBackupWithEntry(
        string backupPath,
        string entryName,
        string content
    )
    {
        using var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create);
        WriteValidManifest(archive);
        WriteEntry(archive, entryName, content);
    }

    private static string[] SnapshotFiles(string directory)
    {
        return Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path =>
                $"{Path.GetRelativePath(directory, path)}:{Convert.ToBase64String(File.ReadAllBytes(path))}"
            )
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static void WriteBytes(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
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
