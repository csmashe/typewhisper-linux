using System.IO;
using Moq;
using TypeWhisper.Plugin.Webhook;
using TypeWhisper.PluginSDK;
using TypeWhisper.Linux.Services.Plugins;

namespace TypeWhisper.PluginSystem.Tests;

public class WebhookCollectionSettingsTests : IDisposable
{
    private const string CollectionKey = "webhooks";

    private readonly string _tempDir;

    public WebhookCollectionSettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tw-webhook-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private string ConfigPath => Path.Combine(_tempDir, "webhooks.json");

    private static PluginCollectionItem Item(
        string name,
        string url = "https://example.com/hook",
        string method = "POST",
        string headers = "",
        string profiles = "",
        string enabled = "true",
        string? id = null)
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
            values["__id"] = id;
        return new PluginCollectionItem(values);
    }

    [Fact]
    public async Task SetItems_ThenGetItems_RoundTripsAndWritesJson()
    {
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);

        var result = await plugin.SetItemsAsync(CollectionKey,
        [
            Item("Hook A", "https://a.example/x", "POST"),
            Item("Hook B", "http://b.example/y", "PUT", enabled: "false"),
        ]);

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
        await plugin.SetItemsAsync(CollectionKey,
        [
            Item("Known", id: knownId),
            Item("Fresh"),
        ]);

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

        await plugin.SetItemsAsync(CollectionKey,
        [
            Item("On", enabled: "true"),
            Item("Off", enabled: "false"),
        ]);

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

    [Fact]
    public async Task ValidationFailure_FtpUrl_FailsWithWebhookNameInMessage()
    {
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);

        var bad = await plugin.SetItemsAsync(CollectionKey,
            [Item("MyHook", url: "ftp://example.com/x")]);

        Assert.False(bad.IsSuccess);
        Assert.Contains("MyHook", bad.Message);
    }

    [Fact]
    public async Task ValidationFailure_BadMethod_Fails()
    {
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);

        var bad = await plugin.SetItemsAsync(CollectionKey,
            [Item("MethodHook", method: "DELETE")]);

        Assert.False(bad.IsSuccess);
        Assert.Contains("MethodHook", bad.Message);
    }

    [Fact]
    public async Task ValidationFailure_MalformedHeaderLine_Fails()
    {
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);

        // Header line without a colon separator.
        var bad = await plugin.SetItemsAsync(CollectionKey,
            [Item("HeaderHook", headers: "ThisLineHasNoColon")]);

        Assert.False(bad.IsSuccess);
        Assert.Contains("HeaderHook", bad.Message);
    }

    [Fact]
    public async Task UrlValidation_AcceptsHttpAndHttps_RejectsOthers()
    {
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);

        Assert.True((await plugin.SetItemsAsync(CollectionKey,
            [Item("H", url: "http://example.com")])).IsSuccess);
        Assert.True((await plugin.SetItemsAsync(CollectionKey,
            [Item("H", url: "https://example.com")])).IsSuccess);

        Assert.False((await plugin.SetItemsAsync(CollectionKey,
            [Item("H", url: "ws://example.com")])).IsSuccess);
        Assert.False((await plugin.SetItemsAsync(CollectionKey,
            [Item("H", url: "example.com")])).IsSuccess);
    }

    [Fact]
    public async Task Headers_RoundTrip_ValueContainingColon_SplitsOnFirstColonOnly()
    {
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);

        const string headerText = "Authorization: Bearer abc123\nX-Url: https://a.b/c";
        await plugin.SetItemsAsync(CollectionKey, [Item("H", headers: headerText)]);

        var items = await plugin.GetItemsAsync(CollectionKey);
        var roundTripped = items[0].Values["headers"] ?? "";

        var lines = roundTripped.Split('\n');
        Assert.Contains("Authorization: Bearer abc123", lines);
        // The colon inside the value must survive intact.
        Assert.Contains("X-Url: https://a.b/c", lines);
    }

    [Fact]
    public async Task Profiles_RoundTrip_MultipleLines()
    {
        var plugin = new WebhookPlugin();
        plugin.SetDataDirectory(_tempDir);

        await plugin.SetItemsAsync(CollectionKey,
            [Item("H", profiles: "Work\nPersonal\nGaming")]);

        var items = await plugin.GetItemsAsync(CollectionKey);
        var profiles = (items[0].Values["profiles"] ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

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

        await plugin.SetItemsAsync(CollectionKey,
        [
            Item("Live1"),
            Item("Live2"),
        ]);

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

    private static IPluginHostServices CreateHost(string dataDir)
    {
        var host = new Mock<IPluginHostServices>();
        host.SetupGet(h => h.PluginDataDirectory).Returns(dataDir);
        host.SetupGet(h => h.EventBus).Returns(new PluginEventBus());
        return host.Object;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup in tests.
        }
    }
}
