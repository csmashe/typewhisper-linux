using System.Runtime.Versioning;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.UserData;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

[SupportedOSPlatform("linux")]
public sealed class LocalDataResetServiceTests : IDisposable
{
    private readonly string _tempDir = TestPaths.CreateTempDirectory("TypeWhisper.LocalDataResetServiceTests");
    private readonly string _dataDir;
    private readonly string _outsideDir;
    private readonly string _configDir;

    public LocalDataResetServiceTests()
    {
        _dataDir = Path.Join(_tempDir, "TypeWhisper");
        _outsideDir = Path.Join(_tempDir, "outside");
        _configDir = Path.Join(_tempDir, "config", "typewhisper");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_outsideDir);
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(_tempDir, "*", SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            TestPaths.DeleteDirectory(_tempDir);
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
        }
    }

    [Fact]
    public void Requesting_a_reset_records_it_owner_only_and_deletes_nothing()
    {
        Write("settings.json", "{}");
        Write("Data/history.json", "history");
        var sut = CreateService();

        sut.RequestReset(new LocalDataResetOptions(DeleteModels: false));

        Assert.True(sut.IsResetPending);
        Assert.True(File.Exists(Path.Join(_dataDir, "settings.json")));
        Assert.True(File.Exists(Path.Join(_dataDir, "Data", "history.json")));
        var marker = Path.Join(_dataDir, LocalDataResetService.PendingMarkerName);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(marker));
        Assert.Equal([LocalDataResetService.PendingMarkerName], Entries().Where(path => path.StartsWith('.')));
    }

    [Fact]
    public void Reset_deletes_user_data_and_secrets_and_keeps_models_and_installed_items()
    {
        WriteFixture();
        var sut = CreateService();
        sut.RequestReset(new LocalDataResetOptions(DeleteModels: false));

        var result = sut.FinishPendingReset();

        Assert.Equal(StartupResetStatus.Completed, result.Status);
        Assert.False(sut.IsResetPending);
        Assert.Equal(
            [
                ".hidden-not-ours/notes.txt",
                ".typewhisper-restore.lock",
                "Cli/typewhisper-cli",
                "ManagedArtifacts/journal.json",
                "Models/ggml-base.bin",
                "PluginData/com.example/Models/model.bin",
                "PluginData/com.example/Runtimes/cuda/libcudart.so.12",
                "PluginData/com.example/model/encoder.onnx",
                "Plugins/com.example/manifest.json",
                "Runtimes/cuda/libcudart.so.12",
                "backups/gnome-keybindings-1.txt",
                "notes.txt",
                "training/notes.txt",
            ],
            Entries()
        );
        Assert.Null(sut.TakeIncompleteReport());
    }

    [Fact]
    public void Reset_that_deletes_models_also_deletes_runtimes_and_plugin_folders()
    {
        WriteFixture();
        var sut = CreateService();
        sut.RequestReset(new LocalDataResetOptions(DeleteModels: true));

        var result = sut.FinishPendingReset();

        Assert.Equal(StartupResetStatus.Completed, result.Status);
        Assert.Equal(
            [
                ".hidden-not-ours/notes.txt",
                ".typewhisper-restore.lock",
                "Cli/typewhisper-cli",
                "ManagedArtifacts/journal.json",
                "Plugins/com.example/manifest.json",
                "backups/gnome-keybindings-1.txt",
                "notes.txt",
                "training/notes.txt",
            ],
            Entries()
        );
    }

    [Fact]
    public void Without_a_request_nothing_is_deleted()
    {
        WriteFixture();
        var before = Entries();

        var result = CreateService().FinishPendingReset();

        Assert.Equal(StartupResetStatus.None, result.Status);
        Assert.Equal(before, Entries());
    }

    [Fact]
    public void Links_are_deleted_as_links_and_their_targets_are_untouched()
    {
        File.WriteAllText(Path.Join(_outsideDir, "keep.txt"), "outside");
        Write("Data/history.json", "history");
        Directory.CreateSymbolicLink(Path.Join(_dataDir, "Data", "linked-folder"), _outsideDir);
        File.CreateSymbolicLink(Path.Join(_dataDir, "Data", "linked-file"), Path.Join(_outsideDir, "keep.txt"));
        Directory.CreateSymbolicLink(Path.Join(_dataDir, "Audio"), _outsideDir);
        var sut = CreateService();
        sut.RequestReset(new LocalDataResetOptions(DeleteModels: false));

        var result = sut.FinishPendingReset();

        Assert.Equal(StartupResetStatus.Completed, result.Status);
        Assert.Empty(Entries());
        Assert.Equal("outside", File.ReadAllText(Path.Join(_outsideDir, "keep.txt")));
    }

    [Fact]
    public void A_folder_swapped_for_a_link_during_the_reset_is_not_followed()
    {
        File.WriteAllText(Path.Join(_outsideDir, "keep.txt"), "outside");
        Write("Data/sub/history.json", "history");
        var swapped = false;
        var sut = CreateService(relativePath =>
        {
            if (relativePath != "Data/sub" || swapped)
            {
                return;
            }

            swapped = true;
            Directory.Delete(Path.Join(_dataDir, "Data", "sub"), recursive: true);
            Directory.CreateSymbolicLink(Path.Join(_dataDir, "Data", "sub"), _outsideDir);
        });
        sut.RequestReset(new LocalDataResetOptions(DeleteModels: false));

        var result = sut.FinishPendingReset();

        Assert.True(swapped);
        Assert.Equal(StartupResetStatus.Completed, result.Status);
        Assert.Equal("outside", File.ReadAllText(Path.Join(_outsideDir, "keep.txt")));
        Assert.False(Directory.Exists(Path.Join(_dataDir, "Data")));
    }

    [Fact]
    public void An_interrupted_reset_resumes_at_the_next_startup()
    {
        WriteFixture();
        CreateService().RequestReset(new LocalDataResetOptions(DeleteModels: false));
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local -- the hook picks the entry to stop at; stopping is its whole job.
        var interrupted = CreateService(relativePath =>
        {
            if (relativePath == "Logs")
            {
                throw new InvalidOperationException("process ended");
            }
        });

        Assert.Throws<InvalidOperationException>(interrupted.FinishPendingReset);
        Assert.True(interrupted.IsResetPending);

        var result = CreateService().FinishPendingReset();

        Assert.Equal(StartupResetStatus.Completed, result.Status);
        Assert.DoesNotContain(Entries(), path => path.StartsWith("Logs/", StringComparison.Ordinal));
        Assert.DoesNotContain("settings.json", Entries());
        Assert.False(CreateService().IsResetPending);
    }

    [Fact]
    public void Entries_that_cannot_be_deleted_are_reported_once_and_the_request_is_removed()
    {
        Write("Data/history.json", "history");
        Write("Data/locked/entry.json", "locked");
        File.SetUnixFileMode(Path.Join(_dataDir, "Data", "locked"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var sut = CreateService();
        sut.RequestReset(new LocalDataResetOptions(DeleteModels: false));

        var result = sut.FinishPendingReset();

        Assert.Equal(StartupResetStatus.Incomplete, result.Status);
        Assert.Equal(["Data/locked/entry.json"], result.Remaining);
        Assert.False(sut.IsResetPending);
        Assert.False(File.Exists(Path.Join(_dataDir, "Data", "history.json")));
        var report = sut.TakeIncompleteReport();
        Assert.NotNull(report);
        Assert.Equal(1, report.RemainingCount);
        Assert.Equal(["Data/locked/entry.json"], report.Remaining);
        Assert.Null(sut.TakeIncompleteReport());
    }

    [Fact]
    public void A_custom_model_folder_inside_the_data_folder_is_kept_with_models()
    {
        var modelFolder = Path.Join(_dataDir, "Data", "my-models");
        Write("settings.json", $$"""{"localModelStoragePath":"{{modelFolder}}"}""");
        Write("Data/history.json", "history");
        Write("Data/my-models/ggml-large.bin", "model");
        var sut = CreateService();
        sut.RequestReset(new LocalDataResetOptions(DeleteModels: false));

        var result = sut.FinishPendingReset();

        Assert.Equal(StartupResetStatus.Completed, result.Status);
        Assert.Equal(["Data/my-models/ggml-large.bin"], Entries());
    }

    [Fact]
    public void A_custom_model_folder_inside_the_data_folder_goes_when_models_do()
    {
        var modelFolder = Path.Join(_dataDir, "Data", "my-models");
        Write("settings.json", $$"""{"localModelStoragePath":"{{modelFolder}}"}""");
        Write("Data/my-models/ggml-large.bin", "model");
        var sut = CreateService();
        sut.RequestReset(new LocalDataResetOptions(DeleteModels: true));

        sut.FinishPendingReset();

        Assert.Empty(Entries());
    }

    [Fact]
    public void A_resumed_reset_still_keeps_the_custom_model_folder_after_settings_were_deleted()
    {
        var modelFolder = Path.Join(_dataDir, "Data", "my-models");
        Write("settings.json", $$"""{"localModelStoragePath":"{{modelFolder}}"}""");
        Write("Data/history.json", "history");
        Write("Data/my-models/ggml-large.bin", "model");
        CreateService().RequestReset(new LocalDataResetOptions(DeleteModels: false));
        var interrupted = CreateService(relativePath =>
        {
            if (relativePath != "settings.json")
            {
                return;
            }

            File.Delete(Path.Join(_dataDir, "settings.json"));
            throw new InvalidOperationException("process ended");
        });
        Assert.Throws<InvalidOperationException>(interrupted.FinishPendingReset);

        var result = CreateService().FinishPendingReset();

        Assert.Equal(StartupResetStatus.Completed, result.Status);
        Assert.Equal(["Data/my-models/ggml-large.bin"], Entries());
    }

    [Fact]
    public void Deleting_models_also_deletes_a_custom_model_folder_TypeWhisper_did_not_create()
    {
        var modelFolder = Path.Join(_dataDir, "custom-models");
        Write("settings.json", $$"""{"localModelStoragePath":"{{modelFolder}}"}""");
        Write("custom-models/PluginData/com.example/Models/model.bin", "model");
        Write("custom-models/ggml-base.bin", "model moved from Models");
        Write("notes.txt", "not ours");
        var sut = CreateService();
        sut.RequestReset(new LocalDataResetOptions(DeleteModels: true));

        var result = sut.FinishPendingReset();

        Assert.Equal(StartupResetStatus.Completed, result.Status);
        Assert.Equal(["notes.txt"], Entries());
    }

    [Fact]
    public void A_top_level_custom_model_folder_keeps_its_plugin_assets_and_shared_runtimes()
    {
        var modelFolder = Path.Join(_dataDir, "Data");
        Write("settings.json", $$"""{"localModelStoragePath":"{{modelFolder}}"}""");
        Write("Data/history.json", "history");
        Write("Data/PluginData/com.example/Models/model.bin", "model");
        Write("Data/Runtimes/cuda/libcublas.so.12", "runtime");
        var sut = CreateService();
        sut.RequestReset(new LocalDataResetOptions(DeleteModels: false));

        var result = sut.FinishPendingReset();

        Assert.Equal(StartupResetStatus.Completed, result.Status);
        Assert.Equal(["Data/PluginData/com.example/Models/model.bin", "Data/Runtimes/cuda/libcublas.so.12"], Entries());
    }

    [Fact]
    public void A_discovery_file_that_cannot_be_inspected_is_reported_rather_than_ending_startup()
    {
        Write("Data/history.json", "history");
        File.WriteAllText(Path.Join(_configDir, "api-discovery.json"), """{"token":"secret"}""");
        File.SetUnixFileMode(_configDir, UnixFileMode.None);
        var sut = CreateService();
        sut.RequestReset(new LocalDataResetOptions(DeleteModels: false));

        var result = sut.FinishPendingReset();

        Assert.Equal(StartupResetStatus.Incomplete, result.Status);
        Assert.Contains(Path.Join(_configDir, "api-discovery.json"), result.Remaining);
        Assert.False(sut.IsResetPending);
        Assert.DoesNotContain(Entries(), path => path.StartsWith("Data/", StringComparison.Ordinal));
    }

    [Fact]
    public void A_linked_plugin_data_folder_is_unlinked_when_keeping_models()
    {
        Directory.CreateDirectory(Path.Join(_outsideDir, "com.example"));
        File.WriteAllText(Path.Join(_outsideDir, "com.example", "settings.json"), "outside");
        Directory.CreateSymbolicLink(Path.Join(_dataDir, "PluginData"), _outsideDir);
        var sut = CreateService();
        sut.RequestReset(new LocalDataResetOptions(DeleteModels: false));

        var result = sut.FinishPendingReset();

        Assert.Equal(StartupResetStatus.Completed, result.Status);
        Assert.Empty(Entries());
        Assert.Equal("outside", File.ReadAllText(Path.Join(_outsideDir, "com.example", "settings.json")));
    }

    [Fact]
    public void A_staged_restore_that_cannot_be_fully_deleted_is_no_longer_a_restore_request()
    {
        Write(".typewhisper-restore-pending/pending-state.json", "{}");
        Write(".typewhisper-restore-pending/content/locked/settings.json", "{}");
        File.SetUnixFileMode(
            Path.Join(_dataDir, ".typewhisper-restore-pending", "content", "locked"),
            UnixFileMode.UserRead | UnixFileMode.UserExecute
        );
        var sut = CreateService();
        sut.RequestReset(new LocalDataResetOptions(DeleteModels: false));

        var result = sut.FinishPendingReset();

        Assert.Equal(StartupResetStatus.Incomplete, result.Status);
        Assert.False(Directory.Exists(Path.Join(_dataDir, ".typewhisper-restore-pending")));
        Assert.Equal(
            StartupRestoreStatus.None,
            SettingsBackupService.ApplyPendingRestoreAtStartup(_dataDir).Status
        );
    }

    [Fact]
    public void A_staged_restore_that_cannot_be_canceled_fails_the_reset_before_anything_is_deleted()
    {
        Write("Data/history.json", "history");
        Write(".typewhisper-restore-pending/pending-state.json", "{}");
        var sut = CreateService();
        sut.RequestReset(new LocalDataResetOptions(DeleteModels: false));
        File.SetUnixFileMode(_dataDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var result = sut.FinishPendingReset();

        File.SetUnixFileMode(_dataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Equal(StartupResetStatus.Failed, result.Status);
        Assert.True(sut.IsResetPending);
        Assert.True(File.Exists(Path.Join(_dataDir, "Data", "history.json")));
        Assert.True(File.Exists(Path.Join(_dataDir, ".typewhisper-restore-pending", "pending-state.json")));
    }

    [Fact]
    public void An_unreadable_request_still_resets_and_keeps_models()
    {
        Write("Data/history.json", "history");
        Write("Models/ggml-base.bin", "model");
        Write(LocalDataResetService.PendingMarkerName, "not json");
        var sut = CreateService();

        var result = sut.FinishPendingReset();

        Assert.Equal(StartupResetStatus.Completed, result.Status);
        Assert.Equal(["Models/ggml-base.bin"], Entries());
    }

    [Fact]
    public void Requesting_replaces_a_linked_marker_without_writing_through_it()
    {
        var target = Path.Join(_outsideDir, "target.txt");
        File.WriteAllText(target, "outside");
        var marker = Path.Join(_dataDir, LocalDataResetService.PendingMarkerName);
        File.CreateSymbolicLink(marker, target);

        CreateService().RequestReset(new LocalDataResetOptions(DeleteModels: false));

        Assert.Equal("outside", File.ReadAllText(target));
        Assert.Null(new FileInfo(marker).LinkTarget);
    }

    [Fact]
    public void A_stale_api_discovery_file_holding_the_token_is_deleted()
    {
        File.WriteAllText(Path.Join(_configDir, "api-discovery.json"), """{"token":"secret"}""");
        File.WriteAllText(Path.Join(_configDir, "other.json"), "kept");
        var sut = CreateService();
        sut.RequestReset(new LocalDataResetOptions(DeleteModels: false));

        sut.FinishPendingReset();

        Assert.False(File.Exists(Path.Join(_configDir, "api-discovery.json")));
        Assert.True(File.Exists(Path.Join(_configDir, "other.json")));
    }

    [Fact]
    public void A_startup_holding_the_lock_leaves_the_reset_for_later()
    {
        Write("Data/history.json", "history");
        CreateService().RequestReset(new LocalDataResetOptions(DeleteModels: false));

        StartupResetResult result;
        using (SettingsBackupService.AcquireStartupRestoreLock(_dataDir))
        {
            result = LocalDataResetService.ApplyPendingResetAtStartup(_dataDir);
        }

        Assert.Equal(StartupResetStatus.LockUnavailable, result.Status);
        Assert.True(File.Exists(Path.Join(_dataDir, "Data", "history.json")));
        Assert.True(CreateService().IsResetPending);
    }

    [Fact]
    public void A_reset_waits_while_the_instance_that_asked_is_still_running()
    {
        Write("Data/history.json", "history");
        using var requester = System.Diagnostics.Process.Start("sleep", "30");
        WriteMarker(requester.Id, LocalDataResetService.ProcessStartTime(requester.Id));
        try
        {
            var waiting = new LocalDataResetService(_dataDir, _configDir, requesterWait: TimeSpan.FromMilliseconds(200));

            var result = waiting.FinishPendingReset();

            Assert.Equal(StartupResetStatus.RequesterRunning, result.Status);
            Assert.True(File.Exists(Path.Join(_dataDir, "Data", "history.json")));
            Assert.True(waiting.IsResetPending);
        }
        finally
        {
            requester.Kill();
            requester.WaitForExit();
        }

        Assert.Equal(StartupResetStatus.Completed, CreateService().FinishPendingReset().Status);
        Assert.Empty(Entries());
    }

    [Fact]
    public void A_process_that_reused_the_requesters_id_is_not_waited_for()
    {
        Write("Data/history.json", "history");
        WriteMarker(Environment.ProcessId == 1 ? 2 : 1, "not-its-start-time");

        var result = new LocalDataResetService(_dataDir, _configDir, requesterWait: TimeSpan.FromSeconds(10))
            .FinishPendingReset();

        Assert.Equal(StartupResetStatus.Completed, result.Status);
    }

    private void WriteMarker(int requesterProcessId, string? requesterStartTime) =>
        Write(
            LocalDataResetService.PendingMarkerName,
            $$"""{"version":1,"deleteModels":false,"requesterProcessId":{{requesterProcessId}},"requesterStartTime":"{{requesterStartTime}}"}"""
        );

    private LocalDataResetService CreateService(Action<string>? beforeOpen = null) =>
        new(_dataDir, _configDir, beforeOpen);

    private void WriteFixture()
    {
        Write("settings.json", """{"openAiApiKey":"enc-openai"}""");
        Write("settings.json.bak", "{}");
        Write("settings.json.broken-1", "{}");
        Write("linux-preferences.json", "{}");
        Write("Data/history.json", "history");
        Write("Data/dictionary.json", "dictionary");
        Write("PluginData/com.example/settings.json", """{"secret:apiKey":"enc-plugin"}""");
        Write("PluginData/com.example/collection.json", "collection");
        Write("PluginData/com.example/Models/model.bin", "model");
        Write("PluginData/com.example/scratch/request.txt", "scratch");
        Write("PluginData/com.example/Runtimes/cuda/libcudart.so.12", "runtime");
        Write("PluginData/com.example/model/encoder.onnx", "model");
        Write("PluginData/loose.json", "loose");
        Write("Audio/dictation-1.wav", "audio");
        Write("Logs/settings.log", "log");
        Write("backups/gnome-keybindings-1.txt", "keybindings");
        Write("Models/ggml-base.bin", "model");
        Write("Runtimes/cuda/libcudart.so.12", "runtime");
        Write("Plugins/com.example/manifest.json", "plugin");
        Write("Cli/typewhisper-cli", "cli");
        Write("Runtime/control.sock", "socket");
        Write("ManagedArtifacts/journal.json", "journal");
        Write("secret-protection.key", "key");
        Write("retired-provider-secrets.quarantine.json", "quarantine");
        Write(".typewhisper-restore.lock", "");
        Write(".typewhisper-restore-pending/pending-state.json", "{}");
        Write(".typewhisper-restore-staging-1/content/settings.json", "{}");
        Write(".hidden-not-ours/notes.txt", "not ours");
        Write("training/notes.txt", "not ours");
        Write("notes.txt", "not ours");
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Join(_dataDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // Files and links below the data folder, sorted; an empty folder left in place is not listed.
    private string[] Entries() =>
        Directory.EnumerateFileSystemEntries(
                _dataDir,
                "*",
                new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }
            )
            .Where(path => !Directory.Exists(path) || new FileInfo(path).LinkTarget is not null)
            .Select(path => Path.GetRelativePath(_dataDir, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
}
