using System.IO;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
/// Tests for the collection-settings view-model layer: the full
/// <see cref="PluginsSectionViewModel"/> flow driven by a fake
/// <see cref="IPluginCollectionSettingsProvider"/>, plus direct unit tests of
/// <see cref="PluginCollectionRow"/>, <see cref="PluginCollectionItemRow"/> and
/// <see cref="PluginSettingFieldRow"/>.
/// </summary>
public sealed class PluginCollectionSettingsViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public PluginCollectionSettingsViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tw-vm-collection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    // ---- Full PluginsSectionViewModel flow --------------------------------

    private (PluginsSectionViewModel Vm, PluginRow Row, FakeCollectionPlugin Plugin) CreateSectionWithCollectionPlugin()
    {
        var plugin = new FakeCollectionPlugin();
        var loaded = TestPluginManagerFactory.CreateLoadedPlugin(_tempDir, plugin.PluginId, plugin);
        var manager = TestPluginManagerFactory.Create(loadedPlugins: [loaded]);
        var vm = new PluginsSectionViewModel(manager);
        var row = vm.PluginGroups.SelectMany(g => g.Plugins).Single(p => p.Id == plugin.PluginId);
        return (vm, row, plugin);
    }

    [Fact]
    public async Task ToggleExpanded_PopulatesCollectionsFromProvider()
    {
        var (vm, row, plugin) = CreateSectionWithCollectionPlugin();
        plugin.Items.Add(new PluginCollectionItem(new Dictionary<string, string?>
        {
            ["name"] = "Existing",
            ["enabled"] = "true",
        }));

        await vm.ToggleExpandedCommand.ExecuteAsync(row);

        var collection = Assert.Single(row.Collections);
        Assert.Equal("things", collection.Key);
        var item = Assert.Single(collection.Items);
        Assert.Equal("Existing", item.Fields.Single(f => f.Key == "name").Value);
        Assert.True(row.CanEditSettings);
    }

    [Fact]
    public async Task SaveSettings_ForwardsEditedItemsToProvider()
    {
        var (vm, row, plugin) = CreateSectionWithCollectionPlugin();
        await vm.ToggleExpandedCommand.ExecuteAsync(row);

        var collection = Assert.Single(row.Collections);
        collection.AddItemCommand.Execute(null);
        var newRow = Assert.Single(collection.Items);
        newRow.Fields.Single(f => f.Key == "name").Value = "Saved Item";

        await vm.SaveSettingsCommand.ExecuteAsync(row);

        Assert.Equal("Settings saved.", row.Status);
        var forwarded = Assert.Single(plugin.LastSetItems!);
        Assert.Equal("Saved Item", forwarded.Values["name"]);
        Assert.True(Guid.TryParse(forwarded.Values["__id"], out _));
    }

    [Fact]
    public async Task SaveSettings_FailureResultSurfacesInRowStatus()
    {
        var (vm, row, plugin) = CreateSectionWithCollectionPlugin();
        plugin.FailWith = "Thing 'X': name is required.";
        await vm.ToggleExpandedCommand.ExecuteAsync(row);

        await vm.SaveSettingsCommand.ExecuteAsync(row);

        Assert.Equal("Thing 'X': name is required.", row.Status);
    }

    [Fact]
    public void HasExpandableSettings_TrueForCollectionOnlyPlugin()
    {
        var (_, row, _) = CreateSectionWithCollectionPlugin();

        // The fake implements IPluginCollectionSettingsProvider but NOT
        // IPluginSettingsProvider — HasExpandableSettings must still be true.
        Assert.True(row.HasExpandableSettings);
    }

    // ---- PluginSettingFieldRow direct unit tests --------------------------

    [Fact]
    public void FieldRow_AutoKind_WithOptions_ResolvesToDropdown()
    {
        var field = new PluginSettingFieldRow(
            "shell", "Shell", "", "",
            [new PluginSettingOption("bash", "bash")],
            isSecret: false, PluginSettingKind.Auto, "bash");

        Assert.Equal(PluginSettingKind.Dropdown, field.Kind);
        Assert.True(field.IsDropdownKind);
    }

    [Fact]
    public void FieldRow_AutoKind_Secret_ResolvesToSecret()
    {
        var field = new PluginSettingFieldRow(
            "key", "Key", "", "", [],
            isSecret: true, PluginSettingKind.Auto, "");

        Assert.Equal(PluginSettingKind.Secret, field.Kind);
        Assert.True(field.IsSecretKind);
    }

    [Fact]
    public void FieldRow_AutoKind_Plain_ResolvesToText()
    {
        var field = new PluginSettingFieldRow(
            "name", "Name", "", "", [],
            isSecret: false, PluginSettingKind.Auto, "");

        Assert.Equal(PluginSettingKind.Text, field.Kind);
        Assert.True(field.IsTextKind);
    }

    [Fact]
    public void FieldRow_ExplicitKinds_ArePreserved()
    {
        var multiline = new PluginSettingFieldRow(
            "cmd", "Command", "", "", [],
            isSecret: false, PluginSettingKind.Multiline, "");
        Assert.Equal(PluginSettingKind.Multiline, multiline.Kind);
        Assert.True(multiline.IsMultilineKind);

        var boolean = new PluginSettingFieldRow(
            "enabled", "Enabled", "", "", [],
            isSecret: false, PluginSettingKind.Boolean, "true");
        Assert.Equal(PluginSettingKind.Boolean, boolean.Kind);
        Assert.True(boolean.IsBooleanKind);
    }

    [Fact]
    public void FieldRow_HiddenKey_IsHidden()
    {
        var field = new PluginSettingFieldRow(
            "__id", "Id", "", "", [],
            isSecret: false, PluginSettingKind.Text, "");
        Assert.True(field.IsHidden);

        var visible = new PluginSettingFieldRow(
            "name", "Name", "", "", [],
            isSecret: false, PluginSettingKind.Text, "");
        Assert.False(visible.IsHidden);
    }

    [Fact]
    public void FieldRow_BoolValueSync_ValueToBool()
    {
        var field = new PluginSettingFieldRow(
            "enabled", "Enabled", "", "", [],
            isSecret: false, PluginSettingKind.Boolean, "false");
        Assert.False(field.BoolValue);

        field.Value = "true";
        Assert.True(field.BoolValue);

        field.Value = "false";
        Assert.False(field.BoolValue);
    }

    [Fact]
    public void FieldRow_BoolValueSync_BoolToValue()
    {
        var field = new PluginSettingFieldRow(
            "enabled", "Enabled", "", "", [],
            isSecret: false, PluginSettingKind.Boolean, "false");

        field.BoolValue = true;
        Assert.Equal("true", field.Value);

        field.BoolValue = false;
        Assert.Equal("false", field.Value);
    }

    // ---- PluginCollectionRow / PluginCollectionItemRow direct tests -------

    private static PluginCollectionDefinition ThingsDefinition() =>
        new(
            Key: "things",
            Label: "Things",
            Description: "Some things.",
            ItemFields:
            [
                new PluginSettingDefinition("name", "Name", Kind: PluginSettingKind.Text),
                new PluginSettingDefinition("enabled", "Enabled", Kind: PluginSettingKind.Boolean),
                new PluginSettingDefinition("__id", "__id", Kind: PluginSettingKind.Text),
            ],
            ItemLabelFieldKey: "name",
            AddButtonLabel: "Add thing");

    private static PluginCollectionRow CreateCollectionRow(params PluginCollectionItem[] items)
    {
        var ownerRow = new PluginRow(
            owner: null, id: "p", name: "P", version: "1", author: "", description: "",
            category: "utility", isLocal: true, hasExpandableSettings: true, isEnabled: true);
        return new PluginCollectionRow(ThingsDefinition(), ownerRow, items);
    }

    [Fact]
    public void CollectionRow_AddItem_GeneratesGuidId()
    {
        var collection = CreateCollectionRow();
        collection.AddItemCommand.Execute(null);

        var item = Assert.Single(collection.Items);
        Assert.True(Guid.TryParse(item.HiddenId, out var parsed));
        Assert.NotEqual(Guid.Empty, parsed);
        // Boolean field seeded "true".
        Assert.Equal("true", item.Fields.Single(f => f.Key == "enabled").Value);
    }

    [Fact]
    public void CollectionRow_RemoveItem_RemovesFromCollection()
    {
        var collection = CreateCollectionRow();
        collection.AddItemCommand.Execute(null);
        collection.AddItemCommand.Execute(null);
        Assert.Equal(2, collection.Items.Count);

        collection.RemoveItemCommand.Execute(collection.Items[0]);
        Assert.Single(collection.Items);
    }

    [Fact]
    public void CollectionRow_MoveUpAndDown_ReordersItems()
    {
        var first = new PluginCollectionItem(new Dictionary<string, string?> { ["name"] = "A" });
        var second = new PluginCollectionItem(new Dictionary<string, string?> { ["name"] = "B" });
        var collection = CreateCollectionRow(first, second);

        var bRow = collection.Items[1];
        collection.MoveUpCommand.Execute(bRow);
        Assert.Same(bRow, collection.Items[0]);

        collection.MoveDownCommand.Execute(bRow);
        Assert.Same(bRow, collection.Items[1]);
    }

    [Fact]
    public void ItemRow_HeaderText_UpdatesWhenLabelFieldChanges()
    {
        var collection = CreateCollectionRow(
            new PluginCollectionItem(new Dictionary<string, string?> { ["name"] = "Original" }));
        var item = collection.Items[0];
        Assert.Equal("Original", item.HeaderText);

        string? observed = null;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PluginCollectionItemRow.HeaderText))
                observed = item.HeaderText;
        };

        item.Fields.Single(f => f.Key == "name").Value = "Renamed";

        Assert.Equal("Renamed", item.HeaderText);
        Assert.Equal("Renamed", observed);
    }

    [Fact]
    public void ItemRow_HeaderText_BlankLabel_ShowsUnnamed()
    {
        var collection = CreateCollectionRow(
            new PluginCollectionItem(new Dictionary<string, string?> { ["name"] = "" }));
        Assert.Equal("(unnamed)", collection.Items[0].HeaderText);
    }

    [Fact]
    public void ItemRow_PreservesProvidedId()
    {
        var knownId = Guid.NewGuid().ToString("D");
        var collection = CreateCollectionRow(
            new PluginCollectionItem(new Dictionary<string, string?>
            {
                ["name"] = "X",
                ["__id"] = knownId,
            }));

        Assert.Equal(knownId, collection.Items[0].HiddenId);
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

    /// <summary>
    /// Minimal plugin exposing only <see cref="IPluginCollectionSettingsProvider"/>
    /// (no <see cref="IPluginSettingsProvider"/>) for view-model tests.
    /// </summary>
    private sealed class FakeCollectionPlugin : ITypeWhisperPlugin, IPluginCollectionSettingsProvider
    {
        public string PluginId => "com.test.fake-collection";
        public string PluginName => "Fake Collection";
        public string PluginVersion => "1.0.0";

        public List<PluginCollectionItem> Items { get; } = [];
        public IReadOnlyList<PluginCollectionItem>? LastSetItems { get; private set; }
        public string? FailWith { get; set; }

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public void Dispose() { }

        public IReadOnlyList<PluginCollectionDefinition> GetCollectionDefinitions() =>
            [ThingsDefinition()];

        public Task<IReadOnlyList<PluginCollectionItem>> GetItemsAsync(
            string collectionKey, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PluginCollectionItem>>(Items.ToList());

        public Task<PluginSettingsValidationResult> SetItemsAsync(
            string collectionKey, IReadOnlyList<PluginCollectionItem> items, CancellationToken ct = default)
        {
            LastSetItems = items;
            return Task.FromResult(FailWith is null
                ? new PluginSettingsValidationResult(true, "ok")
                : new PluginSettingsValidationResult(false, FailWith));
        }
    }
}
