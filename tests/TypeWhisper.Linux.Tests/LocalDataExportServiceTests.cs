using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.UserData;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

[SupportedOSPlatform("linux")]
public sealed class LocalDataExportServiceTests : IDisposable
{
    private readonly string _tempDir = TestPaths.CreateTempDirectory("TypeWhisper.LocalDataExportServiceTests");
    private readonly string _dataDir;
    private readonly string _destination;

    public LocalDataExportServiceTests()
    {
        _dataDir = Path.Join(_tempDir, "TypeWhisper");
        _destination = Path.Join(_tempDir, "export", "typewhisper-data.zip");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(Path.GetDirectoryName(_destination)!);
    }

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
    public async Task Export_copies_user_data_and_leaves_out_secrets_models_runtimes_and_plugins()
    {
        Write("settings.json", """{"language":"de","openAiApiKey":"enc-openai","groqApiKey":"enc-groq","apiServerBearerToken":"enc-token"}""");
        Write("settings.json.bak", """{"language":"en","openAiApiKey":"enc-old"}""");
        Write("linux-preferences.json", """{"theme":"dark"}""");
        Write("Data/history.json", "history");
        Write("Data/dictionary.json", "dictionary");
        Write("PluginData/com.example/settings.json", """{"model":"large","secret:apiKey":"enc-plugin"}""");
        Write("PluginData/com.example/collection.json", "collection");
        Write("PluginData/com.example/Models/model.bin", "model");
        Write("PluginData/com.example/scratch/request.txt", "scratch");
        Write("PluginData/com.example/.ctc-assets.lock", "lock");
        Write("PluginData/com.example/download.partial", "partial");
        Write("PluginData/com.example/store.json.tmp", "temp");
        Write("Audio/dictation-1.wav", "audio");
        Write("Logs/settings.log", "log");
        Write("backups/gnome-keybindings-1.txt", "keybindings");
        Write("Models/ggml-base.bin", "model");
        Write("Runtimes/cuda/libcudart.so.12", "runtime");
        Write("Plugins/com.example/manifest.json", "plugin");
        Write("Cli/typewhisper-cli", "cli");
        Write("Runtime/api.lock", "runtime");
        Write("ManagedArtifacts/journal.json", "journal");
        Write("secret-protection.key", "key");
        Write("retired-provider-secrets.quarantine.json", "quarantine");
        Write(".typewhisper-restore.lock", "");
        Write("training/notes.txt", "not ours");
        Write("notes.txt", "not ours");

        var result = await Export(includeRecordings: true);

        var entries = ReadEntries();
        Assert.Equal(
            [
                "README.txt",
                "data/Audio/dictation-1.wav",
                "data/Data/dictionary.json",
                "data/Data/history.json",
                "data/Logs/settings.log",
                "data/PluginData/com.example/collection.json",
                "data/PluginData/com.example/settings.json",
                "data/backups/gnome-keybindings-1.txt",
                "data/linux-preferences.json",
                "data/settings.json",
                "data/settings.json.bak",
                "settings-backup.zip",
                "typewhisper-export.json",
            ],
            entries.Keys.Order(StringComparer.Ordinal)
        );
        Assert.Equal(10, result.FileCount);
        Assert.True(result.IncludesSettingsBackup);
        Assert.Empty(result.Skipped);
        Assert.Equal(["notes.txt", "training/"], result.Unrecognized);

        var settings = JsonNode.Parse(entries["data/settings.json"])!.AsObject();
        Assert.Equal("de", (string?)settings["language"]);
        Assert.False(settings.ContainsKey("openAiApiKey"));
        Assert.False(settings.ContainsKey("groqApiKey"));
        Assert.False(settings.ContainsKey("apiServerBearerToken"));
        Assert.DoesNotContain("enc-old", entries["data/settings.json.bak"]);
        var pluginSettings = JsonNode.Parse(entries["data/PluginData/com.example/settings.json"])!.AsObject();
        Assert.Equal("large", (string?)pluginSettings["model"]);
        Assert.False(pluginSettings.ContainsKey("secret:apiKey"));

        var manifest = JsonNode.Parse(entries["typewhisper-export.json"])!.AsObject();
        Assert.Equal("data-export", (string?)manifest["kind"]);
        Assert.Contains("recordings", manifest["included"]!.AsArray().Select(node => (string?)node));
        Assert.Contains("secrets", manifest["excluded"]!.AsArray().Select(node => (string?)node));
        Assert.Contains("training/", entries["README.txt"]);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(_destination)
        );
        Assert.Equal(["typewhisper-data.zip"], Directory.GetFiles(Path.GetDirectoryName(_destination)!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Export_without_recordings_leaves_audio_out_and_says_so()
    {
        Write("settings.json", "{}");
        Write("Audio/dictation-1.wav", "audio");

        await Export(includeRecordings: false);

        var entries = ReadEntries();
        Assert.DoesNotContain("data/Audio/dictation-1.wav", entries.Keys);
        var manifest = JsonNode.Parse(entries["typewhisper-export.json"])!.AsObject();
        Assert.Contains("recordings", manifest["excluded"]!.AsArray().Select(node => (string?)node));
    }

    [Fact]
    public async Task Included_settings_backup_restores_without_secrets()
    {
        Write("settings.json", """{"language":"de","openAiApiKey":"enc-openai"}""");
        Write("Data/snippets.json", "[\"exported\"]");
        Write("PluginData/com.example/settings.json", """{"model":"large","secret:apiKey":"enc-plugin"}""");
        Write("PluginData/com.example/memories.json", "memories");
        Write("Audio/dictation-1.wav", "audio");
        await Export(includeRecordings: true);

        var backupPath = Path.Join(_tempDir, "settings-backup.zip");
        await using (var archive = await ZipFile.OpenReadAsync(_destination))
        {
            await archive.GetEntry(LocalDataExportService.SettingsBackupEntryName)!.ExtractToFileAsync(backupPath);
        }

        var target = Path.Join(_tempDir, "target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Join(target, "settings.json"), """{"language":"en"}""");
        var restore = new SettingsBackupService(target);
        var staged = restore.StageRestore(backupPath);
        var applied = restore.ApplyPendingRestoreAtStartup();

        Assert.Equal(4, staged.FileCount);
        Assert.Equal(StartupRestoreStatus.Applied, applied.Status);
        var settings = await File.ReadAllTextAsync(Path.Join(target, "settings.json"));
        Assert.Contains("\"de\"", settings);
        Assert.DoesNotContain("openAiApiKey", settings);
        Assert.Equal("[\"exported\"]", await File.ReadAllTextAsync(Path.Join(target, "Data", "snippets.json")));
        Assert.DoesNotContain(
            "secret:",
            await File.ReadAllTextAsync(Path.Join(target, "PluginData", "com.example", "settings.json"))
        );
        Assert.Equal("memories", await File.ReadAllTextAsync(Path.Join(target, "PluginData", "com.example", "memories.json")));
        Assert.False(Directory.Exists(Path.Join(target, "Audio")));
    }

    [Fact]
    public async Task Links_are_not_followed_and_are_reported()
    {
        var outside = Path.Join(_tempDir, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Join(outside, "private.txt"), "outside-content");
        Write("Data/history.json", "history");
        File.CreateSymbolicLink(Path.Join(_dataDir, "Data", "linked.json"), Path.Join(outside, "private.txt"));
        Directory.CreateSymbolicLink(Path.Join(_dataDir, "Data", "linked-folder"), outside);
        Directory.CreateSymbolicLink(Path.Join(_dataDir, "Audio"), outside);
        // Excluded categories are left out silently, link or not.
        Directory.CreateSymbolicLink(Path.Join(_dataDir, "Models"), outside);

        var result = await Export(includeRecordings: true);

        Assert.Equal(
            [
                new LocalDataExportSkip("Audio", LocalDataExportSkipReason.Link),
                new LocalDataExportSkip("Data/linked-folder", LocalDataExportSkipReason.Link),
                new LocalDataExportSkip("Data/linked.json", LocalDataExportSkipReason.Link),
            ],
            result.Skipped
        );
        var entries = ReadEntries();
        Assert.Contains("data/Data/history.json", entries.Keys);
        Assert.DoesNotContain(entries.Values, content => content.Contains("outside-content"));
        Assert.Contains("Data/linked.json: is a link, which is not followed", entries["README.txt"]);
    }

    [Fact]
    public async Task A_hard_link_to_the_protection_key_is_left_out()
    {
        Write("secret-protection.key", "key-bytes");
        Write("settings.json", "{}");
        Directory.CreateDirectory(Path.Join(_dataDir, "Data"));
        CreateHardLink(Path.Join(_dataDir, "secret-protection.key"), Path.Join(_dataDir, "Data", "copy.json"));

        var result = await Export(includeRecordings: true);

        Assert.Equal(
            [new LocalDataExportSkip("Data/copy.json", LocalDataExportSkipReason.ProtectionKey)],
            result.Skipped
        );
        Assert.DoesNotContain(ReadEntries().Values, content => content.Contains("key-bytes"));
    }

    [Fact]
    public async Task A_folder_swapped_for_a_link_while_exporting_is_not_followed()
    {
        var outside = Path.Join(_tempDir, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Join(outside, "notes.json"), "outside-content");
        Write("Data/inner/notes.json", "inside-content");
        var inner = Path.Join(_dataDir, "Data", "inner");

        var result = await Export(
            includeRecordings: true,
            beforeOpen: relativePath =>
            {
                if (relativePath != "Data/inner/notes.json")
                {
                    return;
                }

                Directory.Move(inner, inner + "-moved");
                Directory.CreateSymbolicLink(inner, outside);
            }
        );

        Assert.Equal(
            [new LocalDataExportSkip("Data/inner/notes.json", LocalDataExportSkipReason.Changed)],
            result.Skipped
        );
        Assert.DoesNotContain(ReadEntries().Values, content => content.Contains("outside-content"));
    }

    [Fact]
    public async Task A_folder_swapped_for_a_link_to_plugin_settings_is_not_exported_unredacted()
    {
        Write("PluginData/com.example/settings.json", """{"secret:apiKey":"enc-plugin"}""");
        Write("Data/sub/settings.json", "{}");
        var sub = Path.Join(_dataDir, "Data", "sub");

        var result = await Export(
            includeRecordings: true,
            beforeOpen: relativePath =>
            {
                if (relativePath != "Data/sub/settings.json")
                {
                    return;
                }

                Directory.Move(sub, sub + "-moved");
                Directory.CreateSymbolicLink(sub, Path.Join(_dataDir, "PluginData", "com.example"));
            }
        );

        Assert.Contains(new LocalDataExportSkip("Data/sub/settings.json", LocalDataExportSkipReason.Changed), result.Skipped);
        Assert.DoesNotContain(ReadEntries().Values, content => content.Contains("enc-plugin"));
    }

    [Fact]
    public async Task Unreadable_files_and_folders_are_reported()
    {
        if (Environment.UserName == "root")
        {
            return;
        }

        Write("Data/history.json", "history");
        Write("Data/locked.json", "locked");
        Write("Logs/private/app.log", "log");
        File.SetUnixFileMode(Path.Join(_dataDir, "Data", "locked.json"), UnixFileMode.None);
        File.SetUnixFileMode(Path.Join(_dataDir, "Logs", "private"), UnixFileMode.None);
        try
        {
            var result = await Export(includeRecordings: true);

            Assert.Equal(
                [
                    new LocalDataExportSkip("Data/locked.json", LocalDataExportSkipReason.Unreadable),
                    new LocalDataExportSkip("Logs/private/", LocalDataExportSkipReason.Unreadable),
                ],
                result.Skipped
            );
            Assert.Contains("data/Data/history.json", ReadEntries().Keys);
        }
        finally
        {
            File.SetUnixFileMode(Path.Join(_dataDir, "Logs", "private"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Theory]
    [InlineData("settings.json")]
    [InlineData("PluginData/com.example/settings.json")]
    public async Task Settings_that_cannot_be_parsed_are_left_out_rather_than_exported_with_secrets(string relativePath)
    {
        Write(relativePath, "{\"openAiApiKey\":\"enc-openai\"");
        Write("Data/history.json", "history");

        var result = await Export(includeRecordings: true);

        Assert.Equal(
            [new LocalDataExportSkip(relativePath, LocalDataExportSkipReason.SecretsNotRemovable)],
            result.Skipped
        );
        var entries = ReadEntries();
        Assert.DoesNotContain("data/" + relativePath, entries.Keys);
        Assert.True(result.IncludesSettingsBackup);
    }

    [Fact]
    public async Task Copies_a_plugin_settings_store_keeps_beside_its_file_have_their_secrets_removed_too()
    {
        Write("PluginData/com.example/settings.json", """{"model":"large","secret:apiKey":"enc-current"}""");
        Write("PluginData/com.example/settings.json.bak", """{"model":"small","secret:apiKey":"enc-backup"}""");
        Write("PluginData/com.example/settings.json.broken-20261017-abc", "{\"secret:apiKey\":\"enc-broken\"");
        Write("settings.json.broken-20261017-abc", "{\"openAiApiKey\":\"enc-root-broken\"");

        var result = await Export(includeRecordings: true);

        Assert.Equal(
            [
                new LocalDataExportSkip(
                    "PluginData/com.example/settings.json.broken-20261017-abc",
                    LocalDataExportSkipReason.SecretsNotRemovable
                ),
            ],
            result.Skipped
        );
        Assert.Empty(result.Unrecognized);
        var entries = ReadEntries();
        Assert.Contains("\"small\"", entries["data/PluginData/com.example/settings.json.bak"]);
        Assert.DoesNotContain(entries.Values, content => content.Contains("enc-"));
        await using var archive = await ZipFile.OpenReadAsync(_destination);
        await using var backup = await ZipArchive.CreateAsync(
            await archive.GetEntry(LocalDataExportService.SettingsBackupEntryName)!.OpenAsync(),
            ZipArchiveMode.Read,
            leaveOpen: false,
            entryNameEncoding: null
        );
        Assert.All(
            backup.Entries,
            entry =>
            {
                using var reader = new StreamReader(entry.Open());
                Assert.DoesNotContain("enc-", reader.ReadToEnd());
            }
        );
    }

    [Fact]
    public async Task A_custom_model_folder_inside_an_exported_folder_stays_out()
    {
        var modelFolder = Path.Join(_dataDir, "Data", "downloads");
        Write("settings.json", "{\"localModelStoragePath\":" + System.Text.Json.JsonSerializer.Serialize(modelFolder) + "}");
        Write("Data/history.json", "history");
        Write("Data/downloads/PluginData/com.example/Models/model.bin", "model");

        var result = await Export(includeRecordings: true);

        Assert.Empty(result.Skipped);
        Assert.DoesNotContain(ReadEntries().Keys, name => name.Contains("downloads"));
        Assert.Contains("data/Data/history.json", ReadEntries().Keys);
    }

    [Fact]
    public async Task A_model_folder_shared_with_user_data_keeps_only_its_plugin_assets_out()
    {
        var modelFolder = Path.Join(_dataDir, "Data");
        Write("settings.json", "{\"localModelStoragePath\":" + System.Text.Json.JsonSerializer.Serialize(modelFolder) + "}");
        Write("Data/history.json", "history");
        Write("Data/PluginData/com.example/Models/model.bin", "model");

        await Export(includeRecordings: true);

        var entries = ReadEntries();
        Assert.Contains("data/Data/history.json", entries.Keys);
        Assert.DoesNotContain(entries.Keys, name => name.Contains("model.bin"));
    }

    [Fact]
    public async Task Settings_with_a_byte_order_mark_are_exported_without_secrets()
    {
        Write("PluginData/com.example/settings.json", "\uFEFF{\"model\":\"large\",\"secret:apiKey\":\"enc-plugin\"}");

        var result = await Export(includeRecordings: true);

        Assert.Empty(result.Skipped);
        var settings = ReadEntries()["data/PluginData/com.example/settings.json"];
        Assert.Contains("large", settings);
        Assert.DoesNotContain("enc-plugin", settings);
    }

    [Fact]
    public async Task An_oversized_settings_file_is_left_out_rather_than_read_whole()
    {
        Write("PluginData/com.example/settings.json", "{\"secret:apiKey\":\"" + new string('x', 17 * 1024 * 1024) + "\"}");

        var result = await Export(includeRecordings: true);

        Assert.Equal(
            [new LocalDataExportSkip("PluginData/com.example/settings.json", LocalDataExportSkipReason.SecretsNotRemovable)],
            result.Skipped
        );
    }

    [Fact]
    public async Task Legacy_plain_text_webhook_headers_are_removed()
    {
        Write(
            "PluginData/com.typewhisper.webhook/webhooks.json",
            """[{"name":"Hook","url":"https://example.invalid/hook","headers":{"Authorization":"Bearer plain"}}]"""
        );

        await Export(includeRecordings: true);

        var webhooks = ReadEntries()["data/PluginData/com.typewhisper.webhook/webhooks.json"];
        Assert.Contains("https://example.invalid/hook", webhooks);
        Assert.DoesNotContain("Bearer plain", webhooks);
    }

    [Fact]
    public async Task A_destination_inside_the_data_folder_is_refused()
    {
        Write("settings.json", "{}");
        var linkToData = Path.Join(_tempDir, "link-to-data");
        Directory.CreateSymbolicLink(linkToData, _dataDir);
        var service = new LocalDataExportService(_dataDir);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExportAsync(Path.Join(_dataDir, "Data", "export.zip"), new LocalDataExportOptions(true))
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExportAsync(Path.Join(linkToData, "export.zip"), new LocalDataExportOptions(true))
        );
        Assert.Empty(Directory.GetFiles(_dataDir, "*.zip", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_canceled_export_keeps_the_previous_file_and_leaves_nothing_behind()
    {
        Write("Data/history.json", "history");
        Write("Logs/settings.log", "log");
        await File.WriteAllTextAsync(_destination, "previous");
        using var cancellation = new CancellationTokenSource();
        // ReSharper disable once AccessToDisposedClosure -- the export that invokes it completes before the using scope ends.
        var service = new LocalDataExportService(_dataDir, _ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExportAsync(_destination, new LocalDataExportOptions(true), cancellationToken: cancellation.Token)
        );

        // ReSharper disable once MethodSupportsCancellation -- the only token in scope is the one this test already canceled.
        Assert.Equal("previous", await File.ReadAllTextAsync(_destination));
        Assert.Equal(["typewhisper-data.zip"], Directory.GetFiles(Path.GetDirectoryName(_destination)!).Select(Path.GetFileName));
    }

    private Task<LocalDataExportResult> Export(bool includeRecordings, Action<string>? beforeOpen = null) =>
        new LocalDataExportService(_dataDir, beforeOpen).ExportAsync(
            _destination,
            new LocalDataExportOptions(includeRecordings)
        );

    private Dictionary<string, string> ReadEntries()
    {
        using var archive = ZipFile.OpenRead(_destination);
        return archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using var reader = new StreamReader(entry.Open());
                return reader.ReadToEnd();
            }
        );
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Join(_dataDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void CreateHardLink(string existing, string link)
    {
        using var process = System.Diagnostics.Process.Start("ln", [existing, link]);
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
