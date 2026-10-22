using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Collection templates and draft item tests: both work on the unsaved draft and never
///     reach <see cref="IPluginCollectionSettingsProvider.SetItemsAsync" /> on their own.
/// </summary>
public sealed class PluginCollectionItemTestViewModelTests : IDisposable
{
    private readonly string _tempDir = TestPaths.CreateTempDirectory("vm-collection-item-test");
    private PluginManager? _manager;

    public void Dispose()
    {
        TestPaths.DeleteDirectory(_tempDir);
    }

    [Fact]
    public async Task AddTemplate_CopiesValuesIntoAnUnsavedDraftWithAFreshId()
    {
        var (vm, row, plugin) = CreateSection(activated: true);
        await vm.ToggleExpandedCommand.ExecuteAsync(row);
        var collection = Assert.Single(row.Collections);
        Assert.True(collection.HasTemplates);
        Assert.Same(plugin.Template, collection.SelectedTemplate);
        Assert.False(row.HasUnsavedSettings);

        collection.AddTemplateCommand.Execute(null);

        var item = Assert.Single(collection.Items);
        Assert.Equal("Shout", Value(item, "name"));
        Assert.Equal("false", Value(item, "enabled"));
        Assert.Equal("upper", Value(item, "command"));
        Assert.NotEqual("template-id", item.HiddenId);
        Assert.True(Guid.TryParse(item.HiddenId, out _));
        Assert.True(row.HasUnsavedSettings);
        Assert.Null(plugin.LastSetItems);
    }

    [Fact]
    public async Task RunTest_SendsTheEditedDraftAndInput_WithoutSaving()
    {
        var (vm, row, plugin) = CreateSection(activated: true);
        plugin.Items.Add(Item("Saved", "saved-command"));
        await vm.ToggleExpandedCommand.ExecuteAsync(row);
        var item = Assert.Single(Assert.Single(row.Collections).Items);
        Assert.True(item.CanTest);
        Assert.Equal("sample input", item.TestInput);
        item.Fields.Single(field => field.Key == "command").Value = "draft-command";
        item.TestInput = "typed input";

        await vm.RunCollectionItemTestCommand.ExecuteAsync(item);

        Assert.Equal("draft-command", plugin.LastDraft!.Values["command"]);
        Assert.Equal("typed input", plugin.LastInput);
        Assert.True(item.TestSucceeded);
        Assert.Equal("passed", item.TestMessage);
        Assert.Equal("DRAFT-COMMAND", item.TestOutput);
        Assert.False(item.IsTestRunning);
        Assert.Null(plugin.LastSetItems);
        // The test result is not a settings change; the command edit still is.
        Assert.True(row.HasUnsavedSettings);
        item.Fields.Single(field => field.Key == "command").Value = "saved-command";
        Assert.False(row.HasUnsavedSettings);
    }

    [Fact]
    public async Task RunTest_UntouchedSecretIsSentAsNull()
    {
        var (vm, row, plugin) = CreateSection(activated: true);
        plugin.Items.Add(Item("Saved", "c"));
        await vm.ToggleExpandedCommand.ExecuteAsync(row);
        var item = Assert.Single(Assert.Single(row.Collections).Items);

        await vm.RunCollectionItemTestCommand.ExecuteAsync(item);

        Assert.Null(plugin.LastDraft!.Values["token"]);
    }

    [Fact]
    public async Task CancelTest_StopsTheRunAndReportsCancellation()
    {
        var (vm, row, plugin) = CreateSection(activated: true);
        plugin.Items.Add(Item("Saved", "c"));
        plugin.Block = true;
        await vm.ToggleExpandedCommand.ExecuteAsync(row);
        var item = Assert.Single(Assert.Single(row.Collections).Items);

        var run = vm.RunCollectionItemTestCommand.ExecuteAsync(item);
        await plugin.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(item.IsTestRunning);
        vm.CancelCollectionItemTestCommand.Execute(item);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(item.IsTestRunning);
        Assert.False(item.TestSucceeded);
        Assert.Equal("Test cancelled.", item.TestMessage);
        Assert.Null(item.TestOutput);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelTest_ResultReturnedAfterCancellation_IsDiscarded(bool returnNull)
    {
        var (vm, row, plugin) = CreateSection(activated: true);
        plugin.Items.Add(Item("Saved", "c"));
        plugin.Block = true;
        plugin.SucceedWhenCancelled = true;
        plugin.ReturnNullWhenCancelled = returnNull;
        await vm.ToggleExpandedCommand.ExecuteAsync(row);
        var item = Assert.Single(Assert.Single(row.Collections).Items);

        var run = vm.RunCollectionItemTestCommand.ExecuteAsync(item);
        await plugin.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        vm.CancelCollectionItemTestCommand.Execute(item);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(plugin.ObservedCancellation);
        Assert.False(item.TestSucceeded);
        Assert.Equal("Test cancelled.", item.TestMessage);
        Assert.Null(item.TestOutput);
    }

    [Fact]
    public async Task RemovingARunningItem_CancelsItsTest()
    {
        var (vm, row, plugin) = CreateSection(activated: true);
        plugin.Items.Add(Item("Saved", "c"));
        plugin.Block = true;
        await vm.ToggleExpandedCommand.ExecuteAsync(row);
        var collection = Assert.Single(row.Collections);
        var item = Assert.Single(collection.Items);

        var run = vm.RunCollectionItemTestCommand.ExecuteAsync(item);
        await plugin.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        collection.RemoveItemCommand.Execute(item);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(plugin.ObservedCancellation);
    }

    [Fact]
    public async Task DisablingThePlugin_CancelsATestOnARowKeptForUnsavedEdits()
    {
        var (vm, row, plugin) = CreateSection(activated: true);
        plugin.Items.Add(Item("Saved", "c"));
        plugin.Block = true;
        await vm.ToggleExpandedCommand.ExecuteAsync(row);
        var item = Assert.Single(Assert.Single(row.Collections).Items);
        item.Fields.Single(field => field.Key == "command").Value = "edited";
        Assert.True(row.HasUnsavedSettings);

        var run = vm.RunCollectionItemTestCommand.ExecuteAsync(item);
        await plugin.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await _manager!.DisablePluginAsync(plugin.PluginId);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(row, vm.DisabledGroups.SelectMany(group => group.Plugins));
        Assert.True(plugin.ObservedCancellation);
        Assert.Equal("Test cancelled.", item.TestMessage);
    }

    [Fact]
    public async Task RunTest_DisabledPlugin_DoesNotCallThePlugin()
    {
        var (vm, row, plugin) = CreateSection(activated: false);
        plugin.Items.Add(Item("Saved", "c"));
        await vm.ToggleExpandedCommand.ExecuteAsync(row);
        var item = Assert.Single(Assert.Single(row.Collections).Items);

        await vm.RunCollectionItemTestCommand.ExecuteAsync(item);

        Assert.Null(plugin.LastDraft);
        Assert.Equal("Enable the plugin to run a test.", item.TestMessage);
    }

    [Fact]
    public async Task RunTest_PluginThrows_ReportsFailureAndStopsRunning()
    {
        var (vm, row, plugin) = CreateSection(activated: true);
        plugin.Items.Add(Item("Saved", "c"));
        plugin.Throw = true;
        await vm.ToggleExpandedCommand.ExecuteAsync(row);
        var item = Assert.Single(Assert.Single(row.Collections).Items);

        await vm.RunCollectionItemTestCommand.ExecuteAsync(item);

        Assert.False(item.IsTestRunning);
        Assert.False(item.TestSucceeded);
        Assert.Equal("The test could not run.", item.TestMessage);
    }

    [Fact]
    public void DefinitionWithoutTestOrTemplates_HidesBothControls()
    {
        var ownerRow = new PluginRow(
            null,
            "p",
            "P",
            "1",
            "",
            new PluginMetadataDescriptor(PluginNetworkAccess.Local, [PluginCategory.Utility]),
            true,
            true
        );
        var collection = new PluginCollectionRow(
            Definition() with { Templates = [], ItemTest = null },
            ownerRow,
            [Item("Saved", "c")]
        );

        Assert.False(collection.HasTemplates);
        Assert.False(collection.AddTemplateCommand.CanExecute(null));
        Assert.False(Assert.Single(collection.Items).CanTest);
    }

    private (PluginsSectionViewModel Vm, PluginRow Row, FakeTestablePlugin Plugin) CreateSection(
        bool activated
    )
    {
        var plugin = new FakeTestablePlugin();
        var loaded = TestPluginManagerFactory.CreateLoadedPlugin(_tempDir, plugin.PluginId, plugin);
        var manager = TestPluginManagerFactory.Create(
            loadedPlugins: [loaded],
            activatedPluginIds: activated ? [plugin.PluginId] : []
        );
        _manager = manager;
        // Synchronous post so a plugin state change rebuilds the rows inside the test.
        var vm = new PluginsSectionViewModel(manager, null, TimeSpan.FromSeconds(5), postToUi: action => action());
        var row = vm.EnabledGroups.Concat(vm.DisabledGroups)
            .SelectMany(group => group.Plugins)
            .Single(candidate => candidate.Id == plugin.PluginId);
        return (vm, row, plugin);
    }

    private static string Value(PluginCollectionItemRow item, string key) =>
        item.Fields.Single(field => field.Key == key).Value;

    private static PluginCollectionDefinition Definition() =>
        new(
            "things",
            "Things",
            null,
            [
                new PluginSettingDefinition("name", "Name", Kind: PluginSettingKind.Text),
                new PluginSettingDefinition("command", "Command", Kind: PluginSettingKind.Text),
                new PluginSettingDefinition("enabled", "Enabled", Kind: PluginSettingKind.Boolean),
                new PluginSettingDefinition(
                    "token",
                    "Token",
                    IsSecret: true,
                    Kind: PluginSettingKind.Secret
                ),
                new PluginSettingDefinition("__id", "__id", Kind: PluginSettingKind.Text),
            ],
            "name"
        );

    private static PluginCollectionItem Item(string name, string command) =>
        new(
            new Dictionary<string, string?>
            {
                ["name"] = name,
                ["command"] = command,
                ["enabled"] = "true",
                ["token"] = "stored-secret",
                ["__id"] = Guid.NewGuid().ToString("D"),
            }
        );

    private sealed class FakeTestablePlugin
        : ITypeWhisperPlugin,
            IPluginCollectionSettingsProvider,
            IPluginCollectionItemTester
    {
        public PluginCollectionTemplate Template { get; } =
            new(
                "shout",
                "Shout",
                "Uppercases text.",
                new Dictionary<string, string?>
                {
                    ["name"] = "Shout",
                    ["command"] = "upper",
                    ["enabled"] = "false",
                    ["__id"] = "template-id",
                    ["unknown"] = "ignored",
                }
            );

        public List<PluginCollectionItem> Items { get; } = [];
        public IReadOnlyList<PluginCollectionItem>? LastSetItems { get; private set; }
        public PluginCollectionItem? LastDraft { get; private set; }
        public string? LastInput { get; private set; }
        public bool Block { get; set; }
        public bool Throw { get; set; }
        public bool SucceedWhenCancelled { get; set; }
        public bool ReturnNullWhenCancelled { get; set; }
        public bool ObservedCancellation { get; private set; }

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<PluginCollectionDefinition> GetCollectionDefinitions() =>
        [
            Definition() with
            {
                Templates = [Template],
                ItemTest = new PluginCollectionItemTest("Test", "sample input"),
            },
        ];

        public Task<IReadOnlyList<PluginCollectionItem>> GetItemsAsync(
            string collectionKey,
            CancellationToken ct = default
        ) => Task.FromResult<IReadOnlyList<PluginCollectionItem>>(Items.ToList());

        public Task<PluginSettingsValidationResult> SetItemsAsync(
            string collectionKey,
            IReadOnlyList<PluginCollectionItem> items,
            CancellationToken ct = default
        )
        {
            LastSetItems = items;
            return Task.FromResult(new PluginSettingsValidationResult(true, "ok"));
        }

        public async Task<PluginCollectionItemTestResult> TestItemAsync(
            string collectionKey,
            PluginCollectionItem draft,
            string input,
            CancellationToken ct
        )
        {
            LastDraft = draft;
            LastInput = input;
            Started.TrySetResult();
            if (Throw)
                throw new InvalidOperationException("broken tester");

            if (!Block)
            {
                return new PluginCollectionItemTestResult(
                    true,
                    "passed",
                    draft.Values["command"]?.ToUpperInvariant()
                );
            }

            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = true;
                if (SucceedWhenCancelled)
                {
                    return ReturnNullWhenCancelled
                        ? null!
                        : new PluginCollectionItemTestResult(true, "passed late", "late output");
                }

                throw;
            }

            throw new InvalidOperationException("An infinite delay completed without cancellation.");
        }

        public string PluginId => "com.test.fake-testable";
        public string PluginName => "Fake Testable";
        public string PluginVersion => "1.0.0";

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;

        public Task DeactivateAsync() => Task.CompletedTask;

        public void Dispose() { }
    }
}
