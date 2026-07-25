using System.Diagnostics;
using System.Reflection;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Tests for the collection-settings view-model layer: the full
///     <see cref="PluginsSectionViewModel" /> flow driven by a fake
///     <see cref="IPluginCollectionSettingsProvider" />, plus direct unit tests of
///     <see cref="PluginCollectionRow" />, <see cref="PluginCollectionItemRow" /> and
///     <see cref="PluginSettingFieldRow" />.
/// </summary>
public sealed class PluginCollectionSettingsViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public PluginCollectionSettingsViewModelTests()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "tw-vm-collection-" + Guid.NewGuid().ToString("N")
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
            // Best-effort cleanup for temp test directories.
        }
    }

    [Fact]
    public async Task ToggleExpanded_PopulatesCollectionsFromProvider()
    {
        var (vm, row, plugin) = CreateSectionWithCollectionPlugin();
        plugin.Items.Add(
            new PluginCollectionItem(
                new Dictionary<string, string?> { ["name"] = "Existing", ["enabled"] = "true" }
            )
        );

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

    [Fact]
    public async Task Refresh_DefinitionThrow_MarksOnlyThrowingPluginFailed_AndKeepsOtherPluginFunctional()
    {
        var throwing = new FakeSettingsPlugin("com.test.throwing-definitions")
        {
            DefinitionFactory = () => throw new InvalidOperationException("definitions exploded"),
        };
        var healthy = new FakeCollectionPlugin();
        var errorLog = new ErrorLogService(_tempDir);

        var vm = CreateSection(
            [throwing, healthy],
            TimeSpan.FromMilliseconds(40),
            errorLog
        );

        var rows = vm.PluginGroups.SelectMany(group => group.Plugins).ToList();
        var throwingRow = Assert.Single(rows, row => row.Id == throwing.PluginId);
        var healthyRow = Assert.Single(rows, row => row.Id == healthy.PluginId);
        Assert.Equal("Unable to load plugin settings.", throwingRow.Status);
        Assert.True(throwingRow.HasExpandableSettings);

        await vm.ToggleExpandedCommand.ExecuteAsync(healthyRow);

        Assert.Single(healthyRow.Collections);
        Assert.True(healthyRow.CanEditSettings);
        Assert.Contains(
            errorLog.Entries,
            entry =>
                entry.Message.Contains(throwing.PluginName, StringComparison.Ordinal)
                && entry.Message.Contains("read setting definitions", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task Refresh_HungDefinitions_TimesOutAndObservesLateCompletion_WithoutBlockingOtherPlugins()
    {
        using var releaseDefinitions = new ManualResetEventSlim();
        var lateCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var hung = new FakeSettingsPlugin("com.test.hung-definitions")
        {
            DefinitionFactory = () =>
            {
                // ReSharper disable once AccessToDisposedClosure -- test awaits lateCompletion before the using disposes this event.
                releaseDefinitions.Wait();
                lateCompletion.TrySetResult();
                return [FakeSettingsPlugin.SettingDefinition];
            },
        };
        var healthy = new FakeCollectionPlugin();
        var errorLog = new ErrorLogService(_tempDir);
        var releaseTask = Task.Run(async () =>
        {
            await Task.Delay(750);
            // ReSharper disable once AccessToDisposedClosure -- test awaits releaseTask before the using disposes this event.
            releaseDefinitions.Set();
        });
        var stopwatch = Stopwatch.StartNew();

        var vm = CreateSection(
            [hung, healthy],
            TimeSpan.FromMilliseconds(40),
            errorLog
        );

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
            $"Refresh took {stopwatch.Elapsed.TotalMilliseconds:0} ms."
        );
        var rows = vm.PluginGroups.SelectMany(group => group.Plugins).ToList();
        var hungRow = Assert.Single(rows, row => row.Id == hung.PluginId);
        var healthyRow = Assert.Single(rows, row => row.Id == healthy.PluginId);
        Assert.Equal("Unable to load plugin settings.", hungRow.Status);

        await vm.ToggleExpandedCommand.ExecuteAsync(healthyRow);
        Assert.Single(healthyRow.Collections);

        await releaseTask;
        await lateCompletion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(
            errorLog.Entries,
            entry =>
                entry.Message.Contains(hung.PluginName, StringComparison.Ordinal)
                && entry.Message.Contains("timed out", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task SaveSettings_SetterAndRecoveryReloadThrow_AreContainedToCard()
    {
        var plugin = new FakeSettingsPlugin("com.test.throwing-save");
        var errorLog = new ErrorLogService(_tempDir);
        var vm = CreateSection(
            [plugin],
            TimeSpan.FromMilliseconds(40),
            errorLog
        );
        var row = vm.PluginGroups.SelectMany(group => group.Plugins).Single();
        await vm.ToggleExpandedCommand.ExecuteAsync(row);
        plugin.ThrowOnSetValue = true;
        plugin.ThrowOnGetValue = true;

        var exception = await Record.ExceptionAsync(
            () => vm.SaveSettingsCommand.ExecuteAsync(row)
        );

        Assert.Null(exception);
        Assert.Equal(
            "Settings could not be saved. See the error log for details.",
            row.Status
        );
        Assert.Empty(row.SettingFields);
        Assert.False(row.CanEditSettings);
        Assert.Contains(
            errorLog.Entries,
            entry => entry.Message.Contains("save setting", StringComparison.Ordinal)
        );
        Assert.Contains(
            errorLog.Entries,
            entry => entry.Message.Contains("read setting", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task Refresh_ExpandedRestorationThrow_IsObservedAndMarksRestoredCardFailed()
    {
        var plugin = new FakeSettingsPlugin("com.test.throwing-restoration");
        var errorLog = new ErrorLogService(_tempDir);
        var vm = CreateSection(
            [plugin],
            TimeSpan.FromMilliseconds(40),
            errorLog
        );
        var originalRow = vm.PluginGroups.SelectMany(group => group.Plugins).Single();
        await vm.ToggleExpandedCommand.ExecuteAsync(originalRow);
        plugin.ThrowOnGetValue = true;

        InvokeRefresh(vm);

        var restoredRow = vm.PluginGroups.SelectMany(group => group.Plugins).Single();
        Assert.True(restoredRow.IsExpanded);
        await WaitForAsync(
            () => restoredRow.Status == "Unable to load plugin settings.",
            TimeSpan.FromSeconds(2)
        );
        Assert.Equal("Unable to load plugin settings.", restoredRow.Status);
        Assert.Contains(
            errorLog.Entries,
            entry =>
                entry.Message.Contains(plugin.PluginName, StringComparison.Ordinal)
                && entry.Message.Contains("read setting", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task ValidateSettings_SlowValidateAsync_UsesLongerValidationTimeout()
    {
        var plugin = new FakeSettingsPlugin("com.test.slow-validate")
        {
            // Validation legitimately runs longer than the short boundary timeout
            // (e.g. SupertonicTts downloading a model on demand).
            ValidateDelay = TimeSpan.FromMilliseconds(150),
            ValidationResult = new PluginSettingsValidationResult(true, "Validated OK."),
        };
        var errorLog = new ErrorLogService(_tempDir);
        var vm = CreateSection(
            [plugin],
            TimeSpan.FromMilliseconds(40),
            errorLog,
            TimeSpan.FromSeconds(5)
        );
        var row = vm.PluginGroups.SelectMany(group => group.Plugins).Single();
        await vm.ToggleExpandedCommand.ExecuteAsync(row);

        await vm.ValidateSettingsCommand.ExecuteAsync(row);

        Assert.Equal("Validated OK.", row.Status);
    }

    // ---- PluginSettingFieldRow direct unit tests --------------------------

    [Fact]
    public void FieldRow_AutoKind_WithOptions_ResolvesToDropdown()
    {
        var field = new PluginSettingFieldRow(
            "shell",
            "Shell",
            "",
            "",
            [new PluginSettingOption("bash", "bash")],
            false,
            PluginSettingKind.Auto,
            "bash"
        );

        Assert.Equal(PluginSettingKind.Dropdown, field.Kind);
        Assert.True(field.IsDropdownKind);
    }

    [Fact]
    public void FieldRow_AutoKind_Secret_ResolvesToSecret()
    {
        var field = new PluginSettingFieldRow(
            "key",
            "Key",
            "",
            "",
            [],
            true,
            PluginSettingKind.Auto,
            ""
        );

        Assert.Equal(PluginSettingKind.Secret, field.Kind);
        Assert.True(field.IsSecretKind);
    }

    [Fact]
    public void FieldRow_AutoKind_Plain_ResolvesToText()
    {
        var field = new PluginSettingFieldRow(
            "name",
            "Name",
            "",
            "",
            [],
            false,
            PluginSettingKind.Auto,
            ""
        );

        Assert.Equal(PluginSettingKind.Text, field.Kind);
        Assert.True(field.IsTextKind);
    }

    [Fact]
    public void FieldRow_ExplicitKinds_ArePreserved()
    {
        var multiline = new PluginSettingFieldRow(
            "cmd",
            "Command",
            "",
            "",
            [],
            false,
            PluginSettingKind.Multiline,
            ""
        );
        Assert.Equal(PluginSettingKind.Multiline, multiline.Kind);
        Assert.True(multiline.IsMultilineKind);

        var boolean = new PluginSettingFieldRow(
            "enabled",
            "Enabled",
            "",
            "",
            [],
            false,
            PluginSettingKind.Boolean,
            "true"
        );
        Assert.Equal(PluginSettingKind.Boolean, boolean.Kind);
        Assert.True(boolean.IsBooleanKind);
    }

    [Fact]
    public void FieldRow_HiddenKey_IsHidden()
    {
        var field = new PluginSettingFieldRow(
            "__id",
            "Id",
            "",
            "",
            [],
            false,
            PluginSettingKind.Text,
            ""
        );
        Assert.True(field.IsHidden);

        var visible = new PluginSettingFieldRow(
            "name",
            "Name",
            "",
            "",
            [],
            false,
            PluginSettingKind.Text,
            ""
        );
        Assert.False(visible.IsHidden);
    }

    [Fact]
    public void FieldRow_BoolValueSync_ValueToBool()
    {
        var field = new PluginSettingFieldRow(
            "enabled",
            "Enabled",
            "",
            "",
            [],
            false,
            PluginSettingKind.Boolean,
            "false"
        );
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
            "enabled",
            "Enabled",
            "",
            "",
            [],
            false,
            PluginSettingKind.Boolean,
            "false"
        ) { BoolValue = true };

        Assert.Equal("true", field.Value);

        field.BoolValue = false;
        Assert.Equal("false", field.Value);
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
            new PluginCollectionItem(new Dictionary<string, string?> { ["name"] = "Original" })
        );
        var item = collection.Items[0];
        Assert.Equal("Original", item.HeaderText);

        string? observed = null;
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PluginCollectionItemRow.HeaderText))
            {
                observed = item.HeaderText;
            }
        };

        item.Fields.Single(f => f.Key == "name").Value = "Renamed";

        Assert.Equal("Renamed", item.HeaderText);
        Assert.Equal("Renamed", observed);
    }

    [Fact]
    public void ItemRow_HeaderText_BlankLabel_ShowsUnnamed()
    {
        var collection = CreateCollectionRow(
            new PluginCollectionItem(new Dictionary<string, string?> { ["name"] = "" })
        );
        Assert.Equal("(unnamed)", collection.Items[0].HeaderText);
    }

    [Fact]
    public void ItemRow_PreservesProvidedId()
    {
        var knownId = Guid.NewGuid().ToString("D");
        var collection = CreateCollectionRow(
            new PluginCollectionItem(
                new Dictionary<string, string?> { ["name"] = "X", ["__id"] = knownId }
            )
        );

        Assert.Equal(knownId, collection.Items[0].HiddenId);
    }

    // ---- Full PluginsSectionViewModel flow --------------------------------

    private (
        PluginsSectionViewModel Vm,
        PluginRow Row,
        FakeCollectionPlugin Plugin
        ) CreateSectionWithCollectionPlugin()
    {
        var plugin = new FakeCollectionPlugin();
        var loaded = TestPluginManagerFactory.CreateLoadedPlugin(_tempDir, plugin.PluginId, plugin);
        var manager = TestPluginManagerFactory.Create(loadedPlugins: [loaded]);
        var vm = new PluginsSectionViewModel(manager);
        var row = vm.PluginGroups.SelectMany(g => g.Plugins).Single(p => p.Id == plugin.PluginId);
        return (vm, row, plugin);
    }

    private PluginsSectionViewModel CreateSection(
        IReadOnlyList<ITypeWhisperPlugin> plugins,
        TimeSpan pluginBoundaryTimeout,
        ErrorLogService errorLog,
        TimeSpan? pluginValidationTimeout = null
    )
    {
        var loadedPlugins = plugins
            .Select(plugin =>
                TestPluginManagerFactory.CreateLoadedPlugin(
                    _tempDir,
                    plugin.PluginId,
                    plugin
                )
            )
            .ToList();
        var manager = TestPluginManagerFactory.Create(loadedPlugins: loadedPlugins);
        return new PluginsSectionViewModel(
            manager,
            errorLog,
            pluginBoundaryTimeout,
            pluginValidationTimeout
        );
    }

    private static void InvokeRefresh(PluginsSectionViewModel vm)
    {
        var refresh =
            typeof(PluginsSectionViewModel).GetMethod(
                "Refresh",
                BindingFlags.Instance | BindingFlags.NonPublic
            )
            ?? throw new MissingMethodException(
                typeof(PluginsSectionViewModel).FullName,
                "Refresh"
            );
        refresh.Invoke(vm, null);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < timeout)
        {
            await Task.Delay(10);
        }
    }

    // ---- PluginCollectionRow / PluginCollectionItemRow direct tests -------

    private static PluginCollectionDefinition ThingsDefinition()
    {
        return new PluginCollectionDefinition(
            "things",
            "Things",
            "Some things.",
            [
                new PluginSettingDefinition("name", "Name", Kind: PluginSettingKind.Text),
                new PluginSettingDefinition("enabled", "Enabled", Kind: PluginSettingKind.Boolean),
                new PluginSettingDefinition("__id", "__id", Kind: PluginSettingKind.Text),
            ],
            "name",
            "Add thing"
        );
    }

    private static PluginCollectionRow CreateCollectionRow(params PluginCollectionItem[] items)
    {
        var ownerRow = new PluginRow(
            null,
            "p",
            "P",
            "1",
            "",
            "utility",
            true,
            true,
            true
        );
        return new PluginCollectionRow(ThingsDefinition(), ownerRow, items);
    }

    private sealed class FakeSettingsPlugin : ITypeWhisperPlugin, IPluginSettingsProvider
    {
        public static readonly PluginSettingDefinition SettingDefinition = new(
            "value",
            "Value",
            Kind: PluginSettingKind.Text
        );

        public FakeSettingsPlugin(string pluginId)
        {
            PluginId = pluginId;
        }

        public Func<IReadOnlyList<PluginSettingDefinition>>? DefinitionFactory { get; init; }
        public bool ThrowOnGetValue { get; set; }
        public bool ThrowOnSetValue { get; set; }
        public TimeSpan ValidateDelay { get; init; }
        public PluginSettingsValidationResult? ValidationResult { get; init; }
        public string PluginId { get; }
        public string PluginName => $"Settings {PluginId}";
        public string PluginVersion => "1.0.0";

        public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions()
        {
            return DefinitionFactory?.Invoke() ?? [SettingDefinition];
        }

        public Task<string?> GetSettingValueAsync(
            string key,
            CancellationToken ct = default
        )
        {
            if (ThrowOnGetValue)
            {
                throw new InvalidOperationException("setting getter exploded");
            }

            return Task.FromResult<string?>("initial");
        }

        public Task SetSettingValueAsync(
            string key,
            string? value,
            CancellationToken ct = default
        )
        {
            if (ThrowOnSetValue)
            {
                throw new InvalidOperationException("setting setter exploded");
            }

            return Task.CompletedTask;
        }

        public async Task<PluginSettingsValidationResult?> ValidateAsync(
            CancellationToken ct = default
        )
        {
            if (ValidateDelay > TimeSpan.Zero)
            {
                await Task.Delay(ValidateDelay, ct).ConfigureAwait(false);
            }

            return ValidationResult;
        }

        public Task ActivateAsync(IPluginHostServices host)
        {
            return Task.CompletedTask;
        }

        public Task DeactivateAsync()
        {
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    /// <summary>
    ///     Minimal plugin exposing only <see cref="IPluginCollectionSettingsProvider" />
    ///     (no <see cref="IPluginSettingsProvider" />) for view-model tests.
    /// </summary>
    private sealed class FakeCollectionPlugin
        : ITypeWhisperPlugin,
            IPluginCollectionSettingsProvider
    {
        public List<PluginCollectionItem> Items { get; } = [];
        public IReadOnlyList<PluginCollectionItem>? LastSetItems { get; private set; }
        public string? FailWith { get; set; }

        public IReadOnlyList<PluginCollectionDefinition> GetCollectionDefinitions()
        {
            return [ThingsDefinition()];
        }

        public Task<IReadOnlyList<PluginCollectionItem>> GetItemsAsync(
            string collectionKey,
            CancellationToken ct = default
        )
        {
            return Task.FromResult<IReadOnlyList<PluginCollectionItem>>(Items.ToList());
        }

        public Task<PluginSettingsValidationResult> SetItemsAsync(
            string collectionKey,
            IReadOnlyList<PluginCollectionItem> items,
            CancellationToken ct = default
        )
        {
            LastSetItems = items;
            return Task.FromResult(
                FailWith is null
                    ? new PluginSettingsValidationResult(true, "ok")
                    : new PluginSettingsValidationResult(false, FailWith)
            );
        }

        public string PluginId => "com.test.fake-collection";
        public string PluginName => "Fake Collection";
        public string PluginVersion => "1.0.0";

        public Task ActivateAsync(IPluginHostServices host)
        {
            return Task.CompletedTask;
        }

        public Task DeactivateAsync()
        {
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }
}
