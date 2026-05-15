using System.IO;
using Moq;
using TypeWhisper.Plugin.Script;
using TypeWhisper.PluginSDK;
using TypeWhisper.Linux.Services.Plugins;

namespace TypeWhisper.PluginSystem.Tests;

public class ScriptCollectionSettingsTests : IDisposable
{
    private const string CollectionKey = "scripts";

    private readonly string _tempDir;

    public ScriptCollectionSettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tw-script-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private static PluginCollectionItem Item(
        string name, string command, string shell = "", string enabled = "true", string? id = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["name"] = name,
            ["command"] = command,
            ["shell"] = shell,
            ["enabled"] = enabled,
        };
        if (id is not null)
            values["__id"] = id;
        return new PluginCollectionItem(values);
    }

    private string ConfigPath => Path.Combine(_tempDir, "scripts.json");

    [Fact]
    public async Task SetItems_ThenGetItems_RoundTripsAndWritesJson()
    {
        var plugin = new ScriptPlugin();
        plugin.SetDataDirectory(_tempDir);

        var result = await plugin.SetItemsAsync(CollectionKey,
        [
            Item("First", "echo hello", "bash"),
            Item("Second", "echo world", "sh", enabled: "false"),
        ]);

        Assert.True(result.IsSuccess);
        Assert.True(File.Exists(ConfigPath));

        var items = await plugin.GetItemsAsync(CollectionKey);
        Assert.Equal(2, items.Count);

        Assert.Equal("First", items[0].Values["name"]);
        Assert.Equal("echo hello", items[0].Values["command"]);
        Assert.Equal("bash", items[0].Values["shell"]);
        Assert.Equal("true", items[0].Values["enabled"]);

        Assert.Equal("Second", items[1].Values["name"]);
        Assert.Equal("false", items[1].Values["enabled"]);
    }

    [Fact]
    public async Task SetItems_PreservesKnownId()
    {
        var plugin = new ScriptPlugin();
        plugin.SetDataDirectory(_tempDir);

        var knownId = Guid.NewGuid().ToString("D");
        await plugin.SetItemsAsync(CollectionKey, [Item("Keep", "echo x", id: knownId)]);

        var items = await plugin.GetItemsAsync(CollectionKey);
        Assert.Single(items);
        Assert.Equal(knownId, items[0].Values["__id"]);
    }

    [Fact]
    public async Task SetItems_NewItemWithoutId_GetsParseableGuid()
    {
        var plugin = new ScriptPlugin();
        plugin.SetDataDirectory(_tempDir);

        // No __id key at all.
        await plugin.SetItemsAsync(CollectionKey, [Item("Fresh", "echo x")]);

        var items = await plugin.GetItemsAsync(CollectionKey);
        Assert.Single(items);
        var rawId = items[0].Values["__id"];
        Assert.True(Guid.TryParse(rawId, out var parsed));
        Assert.NotEqual(Guid.Empty, parsed);

        // Blank __id also gets a fresh GUID.
        await plugin.SetItemsAsync(CollectionKey, [Item("Blank", "echo x", id: "")]);
        var items2 = await plugin.GetItemsAsync(CollectionKey);
        Assert.True(Guid.TryParse(items2[0].Values["__id"], out var parsed2));
        Assert.NotEqual(Guid.Empty, parsed2);
    }

    [Fact]
    public async Task BooleanField_MapsToIsEnabled()
    {
        var plugin = new ScriptPlugin();
        plugin.SetDataDirectory(_tempDir);

        await plugin.SetItemsAsync(CollectionKey,
        [
            Item("On", "echo a", enabled: "true"),
            Item("Off", "echo b", enabled: "false"),
        ]);

        // Reload via a fresh non-activated plugin to confirm persistence.
        var reloaded = new ScriptPlugin();
        reloaded.SetDataDirectory(_tempDir);
        var items = await reloaded.GetItemsAsync(CollectionKey);

        Assert.Equal("true", items.Single(i => i.Values["name"] == "On").Values["enabled"]);
        Assert.Equal("false", items.Single(i => i.Values["name"] == "Off").Values["enabled"]);
    }

    [Fact]
    public async Task ValidationFailure_LeavesJsonUnchanged()
    {
        var plugin = new ScriptPlugin();
        plugin.SetDataDirectory(_tempDir);

        var ok = await plugin.SetItemsAsync(CollectionKey, [Item("Valid", "echo ok")]);
        Assert.True(ok.IsSuccess);
        var before = await File.ReadAllBytesAsync(ConfigPath);

        // Empty name -> validation failure.
        var bad = await plugin.SetItemsAsync(CollectionKey, [Item("", "echo bad")]);
        Assert.False(bad.IsSuccess);

        var after = await File.ReadAllBytesAsync(ConfigPath);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task ValidationFailure_EmptyCommand_Fails()
    {
        var plugin = new ScriptPlugin();
        plugin.SetDataDirectory(_tempDir);

        var bad = await plugin.SetItemsAsync(CollectionKey, [Item("HasName", "")]);
        Assert.False(bad.IsSuccess);
    }

    [Fact]
    public async Task ValidationFailure_UnknownShell_Fails()
    {
        var plugin = new ScriptPlugin();
        plugin.SetDataDirectory(_tempDir);

        var bad = await plugin.SetItemsAsync(CollectionKey, [Item("Name", "echo x", shell: "zsh")]);
        Assert.False(bad.IsSuccess);
    }

    [Fact]
    public async Task ValidationFailure_CmdShell_Fails()
    {
        var plugin = new ScriptPlugin();
        plugin.SetDataDirectory(_tempDir);

        var bad = await plugin.SetItemsAsync(CollectionKey, [Item("Name", "echo x", shell: "cmd")]);
        Assert.False(bad.IsSuccess);
    }

    [Fact]
    public async Task SetItems_PwshShell_Accepted()
    {
        var plugin = new ScriptPlugin();
        plugin.SetDataDirectory(_tempDir);

        var result = await plugin.SetItemsAsync(CollectionKey, [Item("PwshScript", "echo x", shell: "pwsh")]);
        Assert.True(result.IsSuccess);

        var items = await plugin.GetItemsAsync(CollectionKey);
        Assert.Single(items);
        Assert.Equal("pwsh", items[0].Values["shell"]);
    }

    [Fact]
    public async Task SetItems_WhenActivated_UpdatesLiveService()
    {
        var plugin = new ScriptPlugin();
        plugin.SetDataDirectory(_tempDir);
        await plugin.ActivateAsync(CreateHost(_tempDir));

        Assert.NotNull(plugin.Service);
        Assert.Empty(plugin.Service!.Scripts);

        await plugin.SetItemsAsync(CollectionKey,
        [
            Item("Live1", "echo 1"),
            Item("Live2", "echo 2"),
        ]);

        Assert.Equal(2, plugin.Service.Scripts.Count);
        Assert.Equal("Live1", plugin.Service.Scripts[0].Name);
        Assert.Equal("Live2", plugin.Service.Scripts[1].Name);
    }

    [Fact]
    public async Task GetItems_WhenActivated_ReadsLiveService()
    {
        var plugin = new ScriptPlugin();
        plugin.SetDataDirectory(_tempDir);
        await plugin.ActivateAsync(CreateHost(_tempDir));

        plugin.Service!.AddScript(new ScriptEntry { Name = "Direct", Command = "echo d" });

        var items = await plugin.GetItemsAsync(CollectionKey);
        Assert.Single(items);
        Assert.Equal("Direct", items[0].Values["name"]);
    }

    [Fact]
    public async Task GetItems_WithoutActivation_ReadsJsonFromDisk()
    {
        // Persist with one (activated) plugin instance.
        var writer = new ScriptPlugin();
        writer.SetDataDirectory(_tempDir);
        await writer.SetItemsAsync(CollectionKey, [Item("Persisted", "echo p")]);

        // A brand new, non-activated plugin instance reads the JSON.
        var reader = new ScriptPlugin();
        reader.SetDataDirectory(_tempDir);
        Assert.Null(reader.Service);

        var items = await reader.GetItemsAsync(CollectionKey);
        Assert.Single(items);
        Assert.Equal("Persisted", items[0].Values["name"]);
    }

    [Fact]
    public async Task GetItems_UnknownCollection_ReturnsEmpty()
    {
        var plugin = new ScriptPlugin();
        plugin.SetDataDirectory(_tempDir);
        var items = await plugin.GetItemsAsync("not-a-collection");
        Assert.Empty(items);
    }

    [Fact]
    public void GetCollectionDefinitions_ExposesScriptsCollection()
    {
        var plugin = new ScriptPlugin();
        var defs = plugin.GetCollectionDefinitions();
        var scripts = Assert.Single(defs);
        Assert.Equal(CollectionKey, scripts.Key);
        Assert.Equal("name", scripts.ItemLabelFieldKey);
        Assert.Contains(scripts.ItemFields, f => f.Key == "__id");
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
