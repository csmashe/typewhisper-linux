using System.Net;
using System.Text.Json;
using Moq;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Plugin.Webhook;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class WebhookCollectionSettingsTests : IDisposable
{
    private const string CollectionKey = "webhooks";

    private readonly string _tempDir;

    public WebhookCollectionSettingsTests()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "tw-webhook-test-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best-effort cleanup in tests.
        }
    }

    [Fact]
    public async Task SetItems_ThenGetItems_RoundTripsAndWritesJson()
    {
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);

        var result = await plugin.SetItemsAsync(
            CollectionKey,
            [
                Item("Hook A", "https://a.example/x"),
                Item("Hook B", "http://b.example/y", "PUT", enabled: "false"),
            ]
        );

        Assert.True(result.IsSuccess);
        Assert.True(File.Exists(ConfigPath));

        var items = await plugin.GetItemsAsync(CollectionKey);
        Assert.Equal(2, items.Count);
        Assert.Equal("Hook A", items[0].Values["name"]);
        Assert.Equal("https://a.example/x", items[0].Values["url"]);
        Assert.Equal("POST", items[0].Values["method"]);
        Assert.Equal("Hook B", items[1].Values["name"]);
        Assert.Equal("PUT", items[1].Values["method"]);
        Assert.Equal("false", items[1].Values["enabled"]);
    }

    [Fact]
    public async Task SetItems_PreservesKnownId_AndGeneratesFreshOne()
    {
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);

        var knownId = Guid.NewGuid().ToString("D");
        await plugin.SetItemsAsync(CollectionKey, [Item("Known", id: knownId), Item("Fresh")]);

        var items = await plugin.GetItemsAsync(CollectionKey);
        Assert.Equal(knownId, items.Single(i => i.Values["name"] == "Known").Values["__id"]);

        var freshId = items.Single(i => i.Values["name"] == "Fresh").Values["__id"];
        Assert.True(Guid.TryParse(freshId, out var parsed));
        Assert.NotEqual(Guid.Empty, parsed);
    }

    [Fact]
    public async Task BooleanField_MapsToIsEnabled()
    {
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);

        await plugin.SetItemsAsync(
            CollectionKey,
            [Item("On", enabled: "true"), Item("Off", enabled: "false")]
        );

        var reloaded = new WebhookPlugin();
        reloaded.SetDataDirectory(_tempDir);
        var items = await reloaded.GetItemsAsync(CollectionKey);

        Assert.Equal("true", items.Single(i => i.Values["name"] == "On").Values["enabled"]);
        Assert.Equal("false", items.Single(i => i.Values["name"] == "Off").Values["enabled"]);
    }

    [Fact]
    public async Task ValidationFailure_EmptyName_LeavesJsonUnchanged()
    {
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);

        var ok = await plugin.SetItemsAsync(CollectionKey, [Item("Valid")]);
        Assert.True(ok.IsSuccess);
        var before = await File.ReadAllBytesAsync(ConfigPath);

        var bad = await plugin.SetItemsAsync(CollectionKey, [Item("")]);
        Assert.False(bad.IsSuccess);

        var after = await File.ReadAllBytesAsync(ConfigPath);
        Assert.Equal(before, after);
    }

    // Validation messages are now localized via the host. Activate the plugin
    // with a host whose Localization resolves the plugin's real en.json so the
    // embedded webhook name / reason appears in the failure message, exactly as
    // it does in production.
    private async Task<WebhookPlugin> ActivatedPluginAsync()
    {
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);

        var pluginDir = Path.GetFullPath(
            Path.Join("..", "..", "..", "..", "..", "plugins", "TypeWhisper.Plugin.Webhook"),
            AppContext.BaseDirectory
        );
        var host = new Mock<IPluginHostServices>();
        host.SetupGet(h => h.EventBus).Returns(Mock.Of<IPluginEventBus>());
        host.SetupGet(h => h.PluginDataDirectory).Returns(_tempDir);
        host.SetupGet(h => h.Localization).Returns(new PluginLocalization(pluginDir, "en"));

        await plugin.ActivateAsync(host.Object);
        return plugin;
    }

    [Fact]
    public async Task ValidationFailure_FtpUrl_FailsWithWebhookNameInMessage()
    {
        var plugin = await ActivatedPluginAsync();

        var bad = await plugin.SetItemsAsync(
            CollectionKey,
            [Item("MyHook", "ftp://example.com/x")]
        );

        Assert.False(bad.IsSuccess);
        Assert.Contains("MyHook", bad.Message);
    }

    [Fact]
    public async Task ValidationFailure_BadMethod_Fails()
    {
        var plugin = await ActivatedPluginAsync();

        var bad = await plugin.SetItemsAsync(CollectionKey, [Item("MethodHook", method: "DELETE")]);

        Assert.False(bad.IsSuccess);
        Assert.Contains("MethodHook", bad.Message);
    }

    [Fact]
    public async Task ValidationFailure_MalformedHeaderLine_Fails()
    {
        var plugin = await ActivatedPluginAsync();

        var bad = await plugin.SetItemsAsync(
            CollectionKey,
            [Item("HeaderHook", headers: "ThisLineHasNoColon")]
        );

        Assert.False(bad.IsSuccess);
        Assert.Contains("HeaderHook", bad.Message);
    }

    [Fact]
    public async Task UrlValidation_AcceptsHttpAndHttps_RejectsOthers()
    {
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);

        Assert.True(
            (
                await plugin.SetItemsAsync(CollectionKey, [Item("H", "http://example.com")])
            ).IsSuccess
        );
        Assert.True(
            (
                await plugin.SetItemsAsync(CollectionKey, [Item("H", "https://example.com")])
            ).IsSuccess
        );

        Assert.False(
            (
                await plugin.SetItemsAsync(CollectionKey, [Item("H", "ws://example.com")])
            ).IsSuccess
        );
        Assert.False(
            (await plugin.SetItemsAsync(CollectionKey, [Item("H", "example.com")])).IsSuccess
        );
    }

    [Fact]
    public async Task Headers_RoundTrip_ValueContainingColon_SplitsOnFirstColonOnly()
    {
        var host = new TestHost(_tempDir);
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);
        await plugin.ActivateAsync(host);

        const string headerText = "Authorization: Bearer abc123\nX-Url: https://a.b/c";
        await plugin.SetItemsAsync(CollectionKey, [Item("H", headers: headerText)]);

        var items = await plugin.GetItemsAsync(CollectionKey);
        var roundTripped = items[0].Values["headers"] ?? "";
        var webhookId = Guid.Parse(items[0].Values["__id"]!);

        var lines = roundTripped.Split('\n');
        Assert.Contains("Authorization: <stored securely>", lines);
        Assert.Contains("X-Url: <stored securely>", lines);
        Assert.Equal(
            "Bearer abc123",
            host.Secrets[WebhookPlugin.GetHeaderSecretReference(webhookId, "Authorization")]
        );
        // Splitting on the first colon keeps the colon inside the stored value.
        Assert.Equal(
            "https://a.b/c",
            host.Secrets[WebhookPlugin.GetHeaderSecretReference(webhookId, "X-Url")]
        );
    }

    [Fact]
    public void HeadersField_IsSecretMultiline()
    {
        var plugin = new WebhookPlugin();

        var field = Assert.Single(
            Assert.Single(plugin.GetCollectionDefinitions()).ItemFields,
            definition => definition.Key == "headers"
        );

        Assert.True(field.IsSecret);
        Assert.Equal(PluginSettingKind.Multiline, field.Kind);
    }

    [Fact]
    public async Task SetItems_HeaderValuesStoredAsSecretsAndAbsentFromJson()
    {
        var host = new TestHost(_tempDir);
        var plugin = await ActivateAsync(host);
        var id = Guid.NewGuid();

        var result = await plugin.SetItemsAsync(
            CollectionKey,
            [
                Item(
                    "Secure",
                    headers: "Authorization: Bearer top-secret\nX-Trace: trace-secret",
                    id: id.ToString("D")
                ),
            ]
        );

        Assert.True(result.IsSuccess);
        var json = await File.ReadAllTextAsync(ConfigPath);
        Assert.DoesNotContain("Bearer top-secret", json);
        Assert.DoesNotContain("trace-secret", json);
        Assert.DoesNotContain("\"headers\"", json);
        Assert.Contains("\"headerSecretReferences\"", json);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(ConfigPath)
            );
        }
        Assert.Equal(
            "Bearer top-secret",
            host.Secrets[WebhookPlugin.GetHeaderSecretReference(id, "authorization")]
        );
        Assert.Equal(
            "trace-secret",
            host.Secrets[WebhookPlugin.GetHeaderSecretReference(id, "x-trace")]
        );
    }

    [Fact]
    public async Task GetItems_StoredHeadersUseRedactionPlaceholder()
    {
        var host = new TestHost(_tempDir);
        var plugin = await ActivateAsync(host);

        await plugin.SetItemsAsync(
            CollectionKey,
            [Item("Secure", headers: "Authorization: Bearer hidden\nX-Token: also-hidden")]
        );
        await plugin.DeactivateAsync();
        var reader = new WebhookPlugin();
        reader.SetDataDirectory(_tempDir);

        var item = Assert.Single(await reader.GetItemsAsync(CollectionKey));
        var lines = item.Values["headers"]!.Split('\n');
        Assert.Contains("Authorization: <stored securely>", lines);
        Assert.Contains("X-Token: <stored securely>", lines);
        Assert.DoesNotContain("hidden", item.Values["headers"]);
    }

    [Fact]
    public async Task SetItems_UnchangedPlaceholderPreservesExistingSecretReference()
    {
        var host = new TestHost(_tempDir);
        var plugin = await ActivateAsync(host);
        var id = Guid.NewGuid();
        await plugin.SetItemsAsync(
            CollectionKey,
            [
                Item(
                    "Secure",
                    headers: "Authorization: original-value",
                    id: id.ToString("D")
                ),
            ]
        );
        var reference = WebhookPlugin.GetHeaderSecretReference(id, "Authorization");
        host.StoreCalls.Clear();

        var item = Assert.Single(await plugin.GetItemsAsync(CollectionKey));
        var updatedValues = item.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        updatedValues["name"] = "Renamed";
        var result = await plugin.SetItemsAsync(
            CollectionKey,
            [new PluginCollectionItem(updatedValues)]
        );

        Assert.True(result.IsSuccess);
        Assert.Empty(host.StoreCalls);
        Assert.Equal("original-value", host.Secrets[reference]);
        Assert.Contains(reference, await File.ReadAllTextAsync(ConfigPath));
    }

    [Fact]
    public async Task SetItems_ChangedHeaderValueReplacesSecret()
    {
        var host = new TestHost(_tempDir);
        var plugin = await ActivateAsync(host);
        var id = Guid.NewGuid();
        await plugin.SetItemsAsync(
            CollectionKey,
            [Item("Secure", headers: "X-Key: old-value", id: id.ToString("D"))]
        );
        var reference = WebhookPlugin.GetHeaderSecretReference(id, "X-Key");
        host.StoreCalls.Clear();

        var result = await plugin.SetItemsAsync(
            CollectionKey,
            [Item("Secure", headers: "x-key: new:value", id: id.ToString("D"))]
        );

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { (reference, "new:value") }, host.StoreCalls);
        Assert.Equal("new:value", host.Secrets[reference]);
        Assert.Contains(reference, await File.ReadAllTextAsync(ConfigPath));
    }

    [Fact]
    public async Task SetItems_RemovedHeaderDeletesSecretAfterConfigCommit()
    {
        var host = new TestHost(_tempDir);
        var plugin = await ActivateAsync(host);
        var id = Guid.NewGuid();
        await plugin.SetItemsAsync(
            CollectionKey,
            [Item("Secure", headers: "Authorization: old-value", id: id.ToString("D"))]
        );
        var reference = WebhookPlugin.GetHeaderSecretReference(id, "Authorization");
        var configWasCommittedBeforeDelete = false;
        host.BeforeDelete = deletedReference =>
        {
            if (deletedReference == reference)
            {
                configWasCommittedBeforeDelete = !File.ReadAllText(ConfigPath)
                    .Contains(reference, StringComparison.Ordinal);
            }
        };

        var result = await plugin.SetItemsAsync(
            CollectionKey,
            [Item("Secure", headers: "", id: id.ToString("D"))]
        );

        Assert.True(result.IsSuccess);
        Assert.True(configWasCommittedBeforeDelete);
        Assert.Equal([reference], host.DeleteCalls);
        Assert.DoesNotContain(reference, host.Secrets.Keys);
    }

    [Fact]
    public async Task SetItems_DeleteFailureLeavesOrphanAfterSuccessfulCommit()
    {
        var host = new TestHost(_tempDir);
        var plugin = await ActivateAsync(host);
        var id = Guid.NewGuid();
        await plugin.SetItemsAsync(
            CollectionKey,
            [Item("Secure", headers: "Authorization: old-value", id: id.ToString("D"))]
        );
        var reference = WebhookPlugin.GetHeaderSecretReference(id, "Authorization");
        host.DeleteSecretException = new IOException("delete failed");

        var result = await plugin.SetItemsAsync(
            CollectionKey,
            [Item("Secure", headers: "", id: id.ToString("D"))]
        );

        Assert.True(result.IsSuccess);
        Assert.Contains(reference, host.Secrets.Keys);
        Assert.DoesNotContain(reference, await File.ReadAllTextAsync(ConfigPath));
    }

    // Duplicating a row or renaming a header carries the placeholder over to a name with
    // nothing stored behind it.
    [Fact]
    public async Task SetItems_NewHeaderPlaceholderIsRejected()
    {
        var host = new TestHost(_tempDir);
        var plugin = await ActivateAsync(host);
        var id = Guid.NewGuid();

        var result = await plugin.SetItemsAsync(
            CollectionKey,
            [
                Item(
                    "Secure",
                    headers: "X-Literal: <stored securely>",
                    id: id.ToString("D")
                ),
            ]
        );

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain(
            WebhookPlugin.GetHeaderSecretReference(id, "X-Literal"),
            host.Secrets.Keys
        );
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task SetItems_HeaderWithoutActivationFailsWithoutPlaintextFallback()
    {
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);

        var result = await plugin.SetItemsAsync(
            CollectionKey,
            [Item("Secure", headers: "Authorization: must-not-leak")]
        );

        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(ConfigPath));
        Assert.Empty(Directory.EnumerateFiles(_tempDir));
    }

    [Fact]
    public async Task SetItems_SecretStoreFailureLeavesConfigurationUnchanged()
    {
        var host = new TestHost(_tempDir);
        var plugin = await ActivateAsync(host);
        await plugin.SetItemsAsync(CollectionKey, [Item("Original")]);
        var originalBytes = await File.ReadAllBytesAsync(ConfigPath);
        host.StoreSecretException = new IOException("secret store failed");

        var result = await plugin.SetItemsAsync(
            CollectionKey,
            [Item("Changed", headers: "Authorization: must-not-leak")]
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(ConfigPath));
        Assert.DoesNotContain("must-not-leak", await File.ReadAllTextAsync(ConfigPath));
        Assert.Equal("Original", plugin.Service!.SnapshotWebhooks().Single().Name);
    }

    [Fact]
    public async Task SetItems_ConfigWriteFailureFailsClosed()
    {
        var host = new TestHost(_tempDir);
        var plugin = await ActivateAsync(host);
        var id = Guid.NewGuid();
        await plugin.SetItemsAsync(
            CollectionKey,
            [Item("Secure", headers: "Authorization: old-value", id: id.ToString("D"))]
        );
        var reference = WebhookPlugin.GetHeaderSecretReference(id, "Authorization");
        var backupPath = Path.Join(_tempDir, "webhooks.backup.json");
        File.Move(ConfigPath, backupPath);
        Directory.CreateDirectory(ConfigPath);

        var result = await plugin.SetItemsAsync(
            CollectionKey,
            [
                Item(
                    "Changed",
                    headers: "Authorization: replacement-value",
                    id: id.ToString("D")
                ),
            ]
        );

        Assert.False(result.IsSuccess);
        Assert.Equal("replacement-value", host.Secrets[reference]);
        Assert.Empty(host.DeleteCalls);
        Assert.DoesNotContain("replacement-value", await File.ReadAllTextAsync(backupPath));
        Assert.Empty(Directory.EnumerateFiles(_tempDir, "*.tmp"));
        Assert.Equal("Secure", plugin.Service!.SnapshotWebhooks().Single().Name);
    }

    [Fact]
    public async Task SendWebhooksAsync_ResolvesSecretHeaderBeforeDelivery()
    {
        var host = new TestHost(_tempDir);
        var plugin = await ActivateAsync(host);
        await plugin.SetItemsAsync(
            CollectionKey,
            [Item("Secure", headers: "Authorization: Bearer delivered-secret")]
        );

        var handler = new CapturingHandler(request =>
        {
            Assert.Equal(
                "Bearer delivered-secret",
                request.Headers.GetValues("Authorization").Single()
            );
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var service = new WebhookService(host, _tempDir, handler);

        try
        {
            await service.SendWebhooksAsync(
                new TranscriptionCompletedEvent { Text = "hello" }
            );
        }
        finally
        {
            service.Dispose();
        }

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ActivateAsync_MigratesLegacyPlaintextHeadersAndSecuresConfigFile()
    {
        var id = Guid.NewGuid();
        var legacyJson = JsonSerializer.Serialize(
            new[]
            {
                new
                {
                    id,
                    name = "Legacy",
                    url = "https://example.com/hook",
                    httpMethod = "POST",
                    headers = new Dictionary<string, string>
                    {
                        ["Authorization"] = "Bearer legacy-secret",
                        ["X-Url"] = "https://a.example/value",
                    },
                    isEnabled = true,
                    profileFilter = Array.Empty<string>(),
                },
            }
        );
        await File.WriteAllTextAsync(ConfigPath, legacyJson);
        const UnixFileMode expectedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                ConfigPath,
                expectedMode | UnixFileMode.GroupRead | UnixFileMode.OtherRead
            );
        }

        var host = new TestHost(_tempDir)
        {
            BeforeStore = (_, _) =>
            {
                if (!OperatingSystem.IsWindows())
                    Assert.Equal(expectedMode, File.GetUnixFileMode(ConfigPath));
            },
        };
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);

        await plugin.ActivateAsync(host);

        Assert.Equal(
            "Bearer legacy-secret",
            host.Secrets[WebhookPlugin.GetHeaderSecretReference(id, "Authorization")]
        );
        Assert.Equal(
            "https://a.example/value",
            host.Secrets[WebhookPlugin.GetHeaderSecretReference(id, "X-Url")]
        );
        var migratedJson = await File.ReadAllTextAsync(ConfigPath);
        Assert.DoesNotContain("legacy-secret", migratedJson);
        Assert.DoesNotContain("https://a.example/value", migratedJson);
        Assert.DoesNotContain("\"headers\"", migratedJson);
        Assert.Contains("\"headerSecretReferences\"", migratedJson);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(expectedMode, File.GetUnixFileMode(ConfigPath));
    }

    [Fact]
    public async Task SetItems_BeforeActivation_PreservesUnmigratedLegacyHeaders()
    {
        var id = Guid.NewGuid();
        var legacyJson = JsonSerializer.Serialize(
            new[]
            {
                new
                {
                    id,
                    name = "Legacy",
                    url = "https://example.com/hook",
                    httpMethod = "POST",
                    headers = new Dictionary<string, string>
                    {
                        ["Authorization"] = "Bearer legacy-secret",
                    },
                    isEnabled = true,
                    profileFilter = Array.Empty<string>(),
                },
            }
        );
        await File.WriteAllTextAsync(ConfigPath, legacyJson);

        // Disabled plugin: never activated, so migration has not run and the
        // legacy plaintext headers are not visible in the settings UI.
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);

        var items = await plugin.GetItemsAsync(CollectionKey);
        var updatedValues = items.Single().Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        updatedValues["name"] = "Renamed";

        var result = await plugin.SetItemsAsync(
            CollectionKey,
            [new PluginCollectionItem(updatedValues)]
        );

        // The save must fail closed rather than silently drop the plaintext
        // headers, and the on-disk secret must survive untouched.
        Assert.False(result.IsSuccess);
        Assert.Contains("Bearer legacy-secret", await File.ReadAllTextAsync(ConfigPath));
    }

    [Fact]
    public async Task Profiles_RoundTrip_MultipleLines()
    {
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);

        await plugin.SetItemsAsync(CollectionKey, [Item("H", profiles: "Work\nPersonal\nGaming")]);

        var items = await plugin.GetItemsAsync(CollectionKey);
        var profiles = (items[0].Values["profiles"] ?? "").Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries
        );

        Assert.Equal(["Work", "Personal", "Gaming"], profiles);
    }

    [Fact]
    public async Task Profiles_BlankText_ProducesEmptyFilter()
    {
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);

        await plugin.SetItemsAsync(CollectionKey, [Item("H", profiles: "   ")]);

        var items = await plugin.GetItemsAsync(CollectionKey);
        Assert.Equal("", items[0].Values["profiles"]);
    }

    [Fact]
    public async Task SetItems_WhenActivated_UpdatesLiveService()
    {
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);
        await plugin.ActivateAsync(CreateHost(_tempDir));

        Assert.NotNull(plugin.Service);
        Assert.Empty(plugin.Service!.Webhooks);

        await plugin.SetItemsAsync(CollectionKey, [Item("Live1"), Item("Live2")]);

        Assert.Equal(2, plugin.Service.Webhooks.Count);
        Assert.Equal("Live1", plugin.Service.Webhooks[0].Name);
        Assert.Equal("Live2", plugin.Service.Webhooks[1].Name);
    }

    [Fact]
    public async Task GetItems_WithoutActivation_ReadsJsonFromDisk()
    {
        var writer = new WebhookPlugin();
        writer.SetDataDirectory(_tempDir);
        await writer.SetItemsAsync(CollectionKey, [Item("Persisted")]);

        var reader = new WebhookPlugin();
        reader.SetDataDirectory(_tempDir);
        Assert.Null(reader.Service);

        var items = await reader.GetItemsAsync(CollectionKey);
        Assert.Single(items);
        Assert.Equal("Persisted", items[0].Values["name"]);
    }

    [Fact]
    public async Task GetItems_UnknownCollection_ReturnsEmpty()
    {
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);
        var items = await plugin.GetItemsAsync("not-a-collection");
        Assert.Empty(items);
    }

    private string ConfigPath => Path.Join(_tempDir, "webhooks.json");

    private async Task<WebhookPlugin> ActivateAsync(TestHost host)
    {
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);
        await plugin.ActivateAsync(host);
        return plugin;
    }

    private static PluginCollectionItem Item(
        string name,
        string url = "https://example.com/hook",
        string method = "POST",
        string headers = "",
        string profiles = "",
        string enabled = "true",
        string? id = null
    )
    {
        var values = new Dictionary<string, string?>
        {
            ["name"] = name,
            ["url"] = url,
            ["method"] = method,
            ["headers"] = headers,
            ["profiles"] = profiles,
            ["enabled"] = enabled,
        };
        if (id is not null)
        {
            values["__id"] = id;
        }

        return new PluginCollectionItem(values);
    }

    private static IPluginHostServices CreateHost(string dataDir)
    {
        var host = new Mock<IPluginHostServices>();
        host.SetupGet(h => h.PluginDataDirectory).Returns(dataDir);
        host.SetupGet(h => h.EventBus).Returns(new PluginEventBus());
        return host.Object;
    }

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder
    ) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(responder(request));
        }
    }

    private sealed class TestHost(string dataDirectory) : IPluginHostServices
    {
        public Dictionary<string, string> Secrets { get; } = [];
        public List<(string Reference, string Value)> StoreCalls { get; } = [];
        public List<string> DeleteCalls { get; } = [];
        public Exception? StoreSecretException { get; set; }
        public Exception? DeleteSecretException { get; set; }
        public Action<string, string>? BeforeStore { get; init; }
        public Action<string>? BeforeDelete { get; set; }

        public Task StoreSecretAsync(string key, string value)
        {
            BeforeStore?.Invoke(key, value);
            if (StoreSecretException is not null)
                throw StoreSecretException;

            StoreCalls.Add((key, value));
            Secrets[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadSecretAsync(string key) =>
            Task.FromResult(Secrets.GetValueOrDefault(key));

        public Task DeleteSecretAsync(string key)
        {
            BeforeDelete?.Invoke(key);
            DeleteCalls.Add(key);
            if (DeleteSecretException is not null)
                throw DeleteSecretException;

            Secrets.Remove(key);
            return Task.CompletedTask;
        }

        public T? GetSetting<T>(string key) => default;
        public void SetSetting<T>(string key, T value) { }
        public string PluginDataDirectory => dataDirectory;
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new PluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public IPluginLocalization Localization { get; } = new TestLocalization();
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() { }
    }

    private sealed class TestLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) =>
            args.Length == 0 ? key : $"{key}: {string.Join(", ", args)}";
    }
}
