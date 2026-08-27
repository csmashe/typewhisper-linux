using System.Diagnostics;
using System.Reflection;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
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
    public async Task SaveSettings_SecretFields_DistinguishUntouchedClearedAndReplacement()
    {
        // Pre-fix, the host serialized both untouched and explicitly cleared
        // secrets as "", so the untouched-null assertion below failed and the
        // plugin could not distinguish those two user intents.
        var (vm, row, plugin) = CreateSectionWithCollectionPlugin();
        plugin.Items.Add(CreateItem("Untouched"));
        plugin.Items.Add(CreateItem("Cleared"));
        plugin.Items.Add(CreateItem("Replaced"));
        plugin.Items.Add(CreateItem("Non-secret"));
        await vm.ToggleExpandedCommand.ExecuteAsync(row);

        var items = Assert.Single(row.Collections).Items;
        var cleared = items[1].Fields.Single(field => field.Key == "api-key");
        cleared.Value = "temporary";
        cleared.Value = "";
        items[2].Fields.Single(field => field.Key == "api-key").Value = "replacement";
        items[3].Fields.Single(field => field.Key == "name").Value = "Edited name";

        await vm.SaveSettingsCommand.ExecuteAsync(row);

        var forwarded = Assert.IsType<IReadOnlyList<PluginCollectionItem>>(
            plugin.LastSetItems,
            exactMatch: false
        );
        Assert.Null(forwarded[0].Values["api-key"]);
        Assert.Equal("", forwarded[1].Values["api-key"]);
        Assert.Equal("replacement", forwarded[2].Values["api-key"]);
        Assert.Equal("Untouched", forwarded[0].Values["name"]);
        Assert.Equal("Edited name", forwarded[3].Values["name"]);
    }

    [Fact]
    public async Task AmbientRefresh_SecretEditedBackToEmpty_PreservesModifiedStateUntilSave()
    {
        // Pre-fix, returning the visible value to its empty baseline made the
        // row look clean, so ambient refresh recreated its field VMs and lost
        // the only state that can identify an explicit clear.
        var (vm, row, plugin) = CreateSectionWithCollectionPlugin();
        plugin.Items.Add(CreateItem("Profile"));
        await vm.ToggleExpandedCommand.ExecuteAsync(row);
        var secret = Assert
            .Single(row.Collections)
            .Items.Single()
            .Fields.Single(field => field.Key == "api-key");

        secret.Value = "temporary";
        secret.Value = "";

        Assert.True(secret.IsUserModified);
        Assert.True(row.HasUnsavedSettings);

        InvokeRefresh(vm);

        var visibleRow = vm.PluginGroups.SelectMany(group => group.Plugins).Single();
        Assert.Same(row, visibleRow);
        Assert.Same(
            secret,
            visibleRow
                .Collections.Single()
                .Items.Single()
                .Fields.Single(field => field.Key == "api-key")
        );

        await vm.SaveSettingsCommand.ExecuteAsync(visibleRow);

        Assert.Equal("", Assert.Single(plugin.LastSetItems!).Values["api-key"]);
        Assert.False(
            visibleRow
                .Collections.Single()
                .Items.Single()
                .Fields.Single(field => field.Key == "api-key")
                .IsUserModified
        );
    }

    [Fact]
    public async Task SaveSettings_DropdownSentinel_SubmitsDisplayedValueThenDropsAfterRealSelection()
    {
        var plugin = new FakeDropdownSettingsPlugin("com.test.dropdown-save");
        var loaded = TestPluginManagerFactory.CreateLoadedPlugin(
            _tempDir,
            plugin.PluginId,
            plugin
        );
        var manager = TestPluginManagerFactory.Create(loadedPlugins: [loaded]);
        var vm = new PluginsSectionViewModel(manager);
        var row = vm.PluginGroups.SelectMany(group => group.Plugins).Single();
        await vm.ToggleExpandedCommand.ExecuteAsync(row);

        var field = row.SettingFields.Single(candidate => candidate.Key == "model");
        var sentinel = Assert.IsType<PluginSettingOption>(field.SelectedOption);
        Assert.Equal(plugin.ModelValue, sentinel.Label);
        Assert.Equal(sentinel.Value, field.Value);

        await vm.SaveSettingsCommand.ExecuteAsync(row);

        Assert.Equal(["retired-model"], plugin.SavedModelValues);
        field = row.SettingFields.Single(candidate => candidate.Key == "model");
        var advertisedOption = field.Options.Single(option => option.Value == "current-b");
        field.SelectedOption = advertisedOption;

        Assert.Equal(advertisedOption.Value, field.Value);
        Assert.DoesNotContain(field.Options, option => option.Value == "retired-model");
        Assert.True(row.HasUnsavedSettings);

        await vm.SaveSettingsCommand.ExecuteAsync(row);

        Assert.Equal(["retired-model", "current-b"], plugin.SavedModelValues);
        field = row.SettingFields.Single(candidate => candidate.Key == "model");
        Assert.Equal("current-b", field.Value);
        Assert.Equal("current-b", field.SelectedOption?.Value);
        Assert.DoesNotContain(field.Options, option => option.Value == "retired-model");
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
    public void Descriptor_DrivesLocationBadgeAndEveryCategoryGroup()
    {
        var plugin = new FakeCollectionPlugin();
        var loaded = TestPluginManagerFactory.CreateLoadedPlugin(
            _tempDir,
            plugin.PluginId,
            plugin,
            PluginNetworkAccess.Mixed,
            [PluginCategory.Tts, PluginCategory.Integration]
        );
        var manager = TestPluginManagerFactory.Create(loadedPlugins: [loaded]);

        var vm = new PluginsSectionViewModel(manager);

        Assert.Equal(
            ["Text-to-Speech", "Integrations"],
            vm.PluginGroups.Select(group => group.Title).ToArray()
        );
        var rows = vm.PluginGroups.SelectMany(group => group.Plugins).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Same(rows[0], rows[1]);
        Assert.Equal(PluginNetworkAccess.Mixed, rows[0].NetworkAccess);
        Assert.True(
            rows[0].Categories.SetEquals(
                [PluginCategory.Tts, PluginCategory.Integration]
            )
        );
        Assert.Equal("Mixed", rows[0].LocationBadge);
        Assert.False(rows[0].RanLocally);
    }

    [Fact]
    public void TtsDescriptor_RendersSupertonicUnderTtsInsteadOfUtility()
    {
        var plugin = new FakeSettingsPlugin("com.typewhisper.supertonic-tts");
        var loaded = TestPluginManagerFactory.CreateLoadedPlugin(
            _tempDir,
            plugin.PluginId,
            plugin,
            PluginNetworkAccess.Local,
            [PluginCategory.Tts]
        );
        var manager = TestPluginManagerFactory.Create(loadedPlugins: [loaded]);

        var vm = new PluginsSectionViewModel(manager);

        var group = Assert.Single(vm.PluginGroups);
        Assert.Equal("Text-to-Speech", group.Title);
        var row = Assert.Single(group.Plugins);
        Assert.Equal("tts", row.CategoryKey);
        Assert.Equal("Local", row.LocationBadge);
        Assert.True(row.RanLocally);
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

    [Fact]
    public async Task PluginStateChanged_SameInstance_PreservesDirtyFlatAndCollectionDrafts_ButRecreatesCleanSibling()
    {
        var plugin = new FakeEditablePlugin("com.test.draft-preservation");
        plugin.Items.Add(CreateItem("A"));
        plugin.Items.Add(CreateItem("B"));
        var sibling = new FakeSettingsPlugin("com.test.clean-sibling");
        var loadedPlugin = TestPluginManagerFactory.CreateLoadedPlugin(
            _tempDir,
            plugin.PluginId,
            plugin
        );
        var loadedSibling = TestPluginManagerFactory.CreateLoadedPlugin(
            _tempDir,
            sibling.PluginId,
            sibling
        );
        var manager = TestPluginManagerFactory.Create(
            loadedPlugins: [loadedPlugin, loadedSibling]
        );
        var vm = new PluginsSectionViewModel(manager);
        var row = vm.PluginGroups
            .SelectMany(group => group.Plugins)
            .Single(candidate => candidate.Id == plugin.PluginId);
        var siblingRow = vm.PluginGroups
            .SelectMany(group => group.Plugins)
            .Single(candidate => candidate.Id == sibling.PluginId);
        await vm.ToggleExpandedCommand.ExecuteAsync(row);

        row.SettingFields.Single().Value = "flat draft";
        var collection = Assert.Single(row.Collections);
        collection.AddItemCommand.Execute(null);
        var added = collection.Items[^1];
        added.Fields.Single(field => field.Key == "name").Value = "C";
        collection.MoveUpCommand.Execute(added);

        InvokeRefresh(vm);

        var visibleRows = vm.PluginGroups.SelectMany(group => group.Plugins).ToList();
        var visibleRow = visibleRows.Single(candidate => candidate.Id == plugin.PluginId);
        var visibleSibling = visibleRows.Single(candidate => candidate.Id == sibling.PluginId);
        Assert.Same(row, visibleRow);
        Assert.Equal("flat draft", visibleRow.SettingFields.Single().Value);
        Assert.Equal(
            ["A", "C", "B"],
            visibleRow
                .Collections.Single()
                .Items.Select(item => item.Fields.Single(field => field.Key == "name").Value)
                .ToArray()
        );
        Assert.NotSame(siblingRow, visibleSibling);
    }

    [Fact]
    public async Task PluginStateChanged_DirtyRowWithDropdownSentinel_PreservesCoherentDraft()
    {
        var plugin = new FakeDropdownSettingsPlugin("com.test.dropdown-draft");
        var loaded = TestPluginManagerFactory.CreateLoadedPlugin(
            _tempDir,
            plugin.PluginId,
            plugin
        );
        var manager = TestPluginManagerFactory.Create(loadedPlugins: [loaded]);
        var vm = new PluginsSectionViewModel(manager);
        var row = vm.PluginGroups.SelectMany(group => group.Plugins).Single();
        await vm.ToggleExpandedCommand.ExecuteAsync(row);
        row.SettingFields.Single(field => field.Key == "notes").Value = "draft notes";

        InvokeRefresh(vm);

        var visibleRow = vm.PluginGroups.SelectMany(group => group.Plugins).Single();
        Assert.Same(row, visibleRow);
        var dropdown = visibleRow.SettingFields.Single(field => field.Key == "model");
        var sentinel = Assert.IsType<PluginSettingOption>(dropdown.SelectedOption);
        Assert.Equal("retired-model", sentinel.Label);
        Assert.Equal(sentinel.Value, dropdown.Value);
        Assert.Contains(dropdown.Options, option => ReferenceEquals(option, sentinel));
        Assert.Equal(
            "draft notes",
            visibleRow.SettingFields.Single(field => field.Key == "notes").Value
        );
        Assert.True(visibleRow.HasUnsavedSettings);
    }

    [Fact]
    public async Task PluginStateChanged_NewInstanceWithSameId_DropsDraftAndRecreatesRow()
    {
        var originalPlugin = new FakeEditablePlugin("com.test.instance-replacement")
        {
            SettingValue = "original",
        };
        var originalLoaded = TestPluginManagerFactory.CreateLoadedPlugin(
            _tempDir,
            originalPlugin.PluginId,
            originalPlugin
        );
        var manager = TestPluginManagerFactory.Create(loadedPlugins: [originalLoaded]);
        var vm = new PluginsSectionViewModel(manager);
        var originalRow = vm.PluginGroups.SelectMany(group => group.Plugins).Single();
        await vm.ToggleExpandedCommand.ExecuteAsync(originalRow);
        originalRow.SettingFields.Single().Value = "draft";

        var replacementPlugin = new FakeEditablePlugin(originalPlugin.PluginId)
        {
            SettingValue = "replacement",
        };
        var replacementLoaded = TestPluginManagerFactory.CreateLoadedPlugin(
            _tempDir,
            replacementPlugin.PluginId,
            replacementPlugin
        );
        ReplaceLoadedPlugins(manager, replacementLoaded);

        InvokeRefresh(vm);

        var replacementRow = vm.PluginGroups.SelectMany(group => group.Plugins).Single();
        Assert.NotSame(originalRow, replacementRow);
        await WaitForAsync(
            () => replacementRow.SettingFields.Count == 1,
            TimeSpan.FromSeconds(2)
        );
        Assert.Equal("replacement", replacementRow.SettingFields.Single().Value);
    }

    [Fact]
    public async Task SaveSuccess_ResetsBaseline_SoAmbientRefreshRecreatesRowWithSavedValues()
    {
        var plugin = new FakeEditablePlugin("com.test.clean-after-save");
        plugin.Items.Add(CreateItem("Before"));
        var loaded = TestPluginManagerFactory.CreateLoadedPlugin(
            _tempDir,
            plugin.PluginId,
            plugin
        );
        var manager = TestPluginManagerFactory.Create(loadedPlugins: [loaded]);
        var vm = new PluginsSectionViewModel(manager);
        var row = vm.PluginGroups.SelectMany(group => group.Plugins).Single();
        await vm.ToggleExpandedCommand.ExecuteAsync(row);
        row.SettingFields.Single().Value = "saved flat";
        row.Collections
            .Single()
            .Items.Single()
            .Fields.Single(field => field.Key == "name")
            .Value = "Saved item";

        await vm.SaveSettingsCommand.ExecuteAsync(row);

        Assert.Equal(2, plugin.GetSettingValueCallCount);
        Assert.Equal("saved flat", row.SettingFields.Single().Value);
        Assert.Equal(
            "Saved item",
            row.Collections
                .Single()
                .Items.Single()
                .Fields.Single(field => field.Key == "name")
                .Value
        );

        InvokeRefresh(vm);

        var refreshedRow = vm.PluginGroups.SelectMany(group => group.Plugins).Single();
        Assert.NotSame(row, refreshedRow);
        await WaitForAsync(
            () => refreshedRow.SettingFields.Count == 1 && refreshedRow.Collections.Count == 1,
            TimeSpan.FromSeconds(2)
        );
        Assert.Equal("saved flat", refreshedRow.SettingFields.Single().Value);
        Assert.Equal(
            "Saved item",
            refreshedRow
                .Collections.Single()
                .Items.Single()
                .Fields.Single(field => field.Key == "name")
                .Value
        );
    }

    [Fact]
    public async Task AmbientRefresh_DuringInFlightSave_PostSaveReloadTargetsCurrentVisibleRow()
    {
        var plugin = new FakeEditablePlugin("com.test.save-refresh-race")
        {
            NormalizeSettingOnSave = true,
            SetSettingStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            ),
            ContinueSetSetting = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            ),
        };
        var loaded = TestPluginManagerFactory.CreateLoadedPlugin(
            _tempDir,
            plugin.PluginId,
            plugin
        );
        var manager = TestPluginManagerFactory.Create(loadedPlugins: [loaded]);
        var vm = new PluginsSectionViewModel(manager);
        var commandRow = vm.PluginGroups.SelectMany(group => group.Plugins).Single();
        await vm.ToggleExpandedCommand.ExecuteAsync(commandRow);

        var saveTask = vm.SaveSettingsCommand.ExecuteAsync(commandRow);
        await plugin.SetSettingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        InvokeRefresh(vm);

        var visibleRow = vm.PluginGroups.SelectMany(group => group.Plugins).Single();
        Assert.NotSame(commandRow, visibleRow);
        await WaitForAsync(
            () => visibleRow.SettingFields.Count == 1,
            TimeSpan.FromSeconds(2)
        );
        Assert.Equal("initial", visibleRow.SettingFields.Single().Value);

        plugin.ContinueSetSetting.TrySetResult();
        await saveTask;

        Assert.Same(
            visibleRow,
            vm.PluginGroups.SelectMany(group => group.Plugins).Single()
        );
        Assert.Equal("initial-saved", visibleRow.SettingFields.Single().Value);
        Assert.True(plugin.GetSettingValueCallCount >= 3);
    }

    // ---- PluginSettingFieldRow direct unit tests --------------------------

    [Fact]
    public void FieldRow_UnknownPersistedDropdownValue_SelectsSentinelWithoutDirtyingBaseline()
    {
        var field = new PluginSettingFieldRow(
            "model",
            "Model",
            "",
            "",
            [
                new PluginSettingOption("current-a", "Current A"),
                new PluginSettingOption("current-b", "Current B"),
            ],
            false,
            PluginSettingKind.Dropdown,
            "retired-model"
        );
        var row = new PluginRow(
            null,
            "p",
            "P",
            "1",
            "",
            new PluginMetadataDescriptor(
                PluginNetworkAccess.Local,
                [PluginCategory.Utility]
            ),
            true,
            true
        );
        row.SettingFields.Add(field);
        row.CaptureSettingsBaseline();

        var sentinel = Assert.IsType<PluginSettingOption>(field.SelectedOption);
        Assert.Equal("retired-model", sentinel.Value);
        Assert.Equal("retired-model", sentinel.Label);
        Assert.Equal(sentinel.Value, field.Value);
        Assert.Contains(field.Options, option => ReferenceEquals(option, sentinel));
        Assert.False(row.HasUnsavedSettings);

        field.SelectedOption = field.Options.Single(option => option.Value == "current-b");

        Assert.Equal("current-b", field.Value);
        Assert.DoesNotContain(field.Options, option => option.Value == "retired-model");
        Assert.True(row.HasUnsavedSettings);
    }

    [Fact]
    public void FieldRow_DropdownConstruction_KeepsUnknownKnownAndEmptyValuesCoherent()
    {
        PluginSettingOption[] options =
        [
            new("current-a", "Current A"),
            new("current-b", "Current B"),
        ];

        var unknown = new PluginSettingFieldRow(
            "unknown",
            "Unknown",
            "",
            "",
            options,
            false,
            PluginSettingKind.Dropdown,
            "retired-model"
        );
        var known = new PluginSettingFieldRow(
            "known",
            "Known",
            "",
            "",
            options,
            false,
            PluginSettingKind.Dropdown,
            "current-b"
        );
        var empty = new PluginSettingFieldRow(
            "empty",
            "Empty",
            "",
            "",
            options,
            false,
            PluginSettingKind.Dropdown,
            ""
        );

        Assert.Equal("retired-model", unknown.SelectedOption?.Value);
        Assert.Equal(unknown.SelectedOption?.Value, unknown.Value);
        Assert.Same(options[1], known.SelectedOption);
        Assert.Equal("current-b", known.Value);
        Assert.Same(options[0], empty.SelectedOption);
        Assert.Equal("current-a", empty.Value);
    }

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
    public void FieldRow_ExplicitMultiline_MasksOnlyWhenSecret()
    {
        var secret = new PluginSettingFieldRow(
            "secret-notes",
            "Secret notes",
            "",
            "",
            [],
            true,
            PluginSettingKind.Multiline,
            ""
        );
        var plain = new PluginSettingFieldRow(
            "notes",
            "Notes",
            "",
            "",
            [],
            false,
            PluginSettingKind.Multiline,
            ""
        );

        Assert.Equal(PluginSettingKind.Multiline, secret.Kind);
        Assert.Equal(PluginSettingKind.Multiline, plain.Kind);
        Assert.Equal('•', secret.MultilinePasswordChar);
        Assert.Equal('\0', plain.MultilinePasswordChar);
        Assert.True(secret.IsSecretMultiline);
        Assert.False(plain.IsSecretMultiline);
        Assert.False(secret.RevealSecretMultiline);
        Assert.False(secret.IsSecretKind);
    }

    [Fact]
    public void CollectionItemRow_SecretMultiline_PropagatesMask()
    {
        var item = new PluginCollectionItemRow(
            [
                new PluginSettingDefinition(
                    "headers",
                    "Headers",
                    IsSecret: true,
                    Kind: PluginSettingKind.Multiline
                ),
            ],
            null,
            null
        );

        var field = item.Fields.Single(candidate => candidate.Key == "headers");
        Assert.Equal(PluginSettingKind.Multiline, field.Kind);
        Assert.Equal('•', field.MultilinePasswordChar);
        Assert.True(field.IsSecretMultiline);
        Assert.False(field.RevealSecretMultiline);
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

    private static void ReplaceLoadedPlugins(
        PluginManager manager,
        params LoadedPlugin[] loadedPlugins
    )
    {
        var field =
            typeof(PluginManager).GetField(
                "_allPlugins",
                BindingFlags.Instance | BindingFlags.NonPublic
            )
            ?? throw new MissingFieldException(typeof(PluginManager).FullName, "_allPlugins");
        var current = Assert.IsType<List<LoadedPlugin>>(field.GetValue(manager));
        current.Clear();
        current.AddRange(loadedPlugins);
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
                new PluginSettingDefinition(
                    "api-key",
                    "API key",
                    IsSecret: true,
                    Kind: PluginSettingKind.Secret
                ),
                new PluginSettingDefinition("__id", "__id", Kind: PluginSettingKind.Text),
            ],
            "name",
            "Add thing"
        );
    }

    private static PluginCollectionItem CreateItem(string name)
    {
        return new PluginCollectionItem(
            new Dictionary<string, string?>
            {
                ["name"] = name,
                ["enabled"] = "true",
                ["__id"] = Guid.NewGuid().ToString("D"),
            }
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
            new PluginMetadataDescriptor(
                PluginNetworkAccess.Local,
                [PluginCategory.Utility]
            ),
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
            // ReSharper disable once ConvertIfStatementToReturnStatement -- fault-injection guard; the suggested ternary-throw buries the throw.
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
            // ReSharper disable once ConvertIfStatementToReturnStatement -- fault-injection guard; the suggested ternary-throw buries the throw.
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

    private sealed class FakeDropdownSettingsPlugin
        : ITypeWhisperPlugin,
            IPluginSettingsProvider
    {
        private static readonly PluginSettingDefinition s_modelDefinition = new(
            "model",
            "Model",
            Options:
            [
                new PluginSettingOption("current-a", "Current A"),
                new PluginSettingOption("current-b", "Current B"),
            ],
            Kind: PluginSettingKind.Dropdown
        );
        private static readonly PluginSettingDefinition s_notesDefinition = new(
            "notes",
            "Notes",
            Kind: PluginSettingKind.Text
        );

        public FakeDropdownSettingsPlugin(string pluginId)
        {
            PluginId = pluginId;
        }

        public List<string?> SavedModelValues { get; } = [];
        public string ModelValue { get; private set; } = "retired-model";
        private string NotesValue { get; set; } = "initial notes";
        public string PluginId { get; }
        public string PluginName => $"Dropdown {PluginId}";
        public string PluginVersion => "1.0.0";

        public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions()
        {
            return [s_modelDefinition, s_notesDefinition];
        }

        public Task<string?> GetSettingValueAsync(
            string key,
            CancellationToken ct = default
        )
        {
            return Task.FromResult(
                key switch
                {
                    "model" => ModelValue,
                    "notes" => NotesValue,
                    _ => null,
                }
            );
        }

        public Task SetSettingValueAsync(
            string key,
            string? value,
            CancellationToken ct = default
        )
        {
            switch (key)
            {
                case "model":
                    SavedModelValues.Add(value);
                    ModelValue = value ?? string.Empty;
                    break;
                case "notes":
                    NotesValue = value ?? string.Empty;
                    break;
            }

            return Task.CompletedTask;
        }

        public Task<PluginSettingsValidationResult?> ValidateAsync(
            CancellationToken ct = default
        )
        {
            return Task.FromResult<PluginSettingsValidationResult?>(null);
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

    private sealed class FakeEditablePlugin
        : ITypeWhisperPlugin,
            IPluginSettingsProvider,
            IPluginCollectionSettingsProvider
    {
        private static readonly PluginSettingDefinition s_settingDefinition = new(
            "value",
            "Value",
            Kind: PluginSettingKind.Text
        );

        public FakeEditablePlugin(string pluginId)
        {
            PluginId = pluginId;
        }

        public List<PluginCollectionItem> Items { get; } = [];
        public string SettingValue { get; set; } = "initial";
        public int GetSettingValueCallCount { get; private set; }
        public bool NormalizeSettingOnSave { get; init; }
        public TaskCompletionSource? SetSettingStarted { get; init; }
        public TaskCompletionSource? ContinueSetSetting { get; init; }
        public string PluginId { get; }
        public string PluginName => $"Editable {PluginId}";
        public string PluginVersion => "1.0.0";

        public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions()
        {
            return [s_settingDefinition];
        }

        public Task<string?> GetSettingValueAsync(
            string key,
            CancellationToken ct = default
        )
        {
            GetSettingValueCallCount++;
            return Task.FromResult<string?>(SettingValue);
        }

        public async Task SetSettingValueAsync(
            string key,
            string? value,
            CancellationToken ct = default
        )
        {
            SetSettingStarted?.TrySetResult();
            if (ContinueSetSetting is not null)
            {
                await ContinueSetSetting.Task.ConfigureAwait(false);
            }

            SettingValue = NormalizeSettingOnSave ? $"{value}-saved" : value ?? string.Empty;
        }

        public Task<PluginSettingsValidationResult?> ValidateAsync(
            CancellationToken ct = default
        )
        {
            return Task.FromResult<PluginSettingsValidationResult?>(null);
        }

        public IReadOnlyList<PluginCollectionDefinition> GetCollectionDefinitions()
        {
            return [ThingsDefinition()];
        }

        public Task<IReadOnlyList<PluginCollectionItem>> GetItemsAsync(
            string collectionKey,
            CancellationToken ct = default
        )
        {
            return Task.FromResult<IReadOnlyList<PluginCollectionItem>>(
                Items.Select(CloneItem).ToList()
            );
        }

        public Task<PluginSettingsValidationResult> SetItemsAsync(
            string collectionKey,
            IReadOnlyList<PluginCollectionItem> items,
            CancellationToken ct = default
        )
        {
            Items.Clear();
            Items.AddRange(items.Select(CloneItem));
            return Task.FromResult(new PluginSettingsValidationResult(true, "ok"));
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

        private static PluginCollectionItem CloneItem(PluginCollectionItem item)
        {
            return new PluginCollectionItem(
                new Dictionary<string, string?>(item.Values, StringComparer.Ordinal)
            );
        }
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
