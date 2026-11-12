using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Linux.Views.Sections;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.UiTests;

[Collection("ui")]
public sealed class PluginDraftLocalizationTests(UiSession ui)
{
    [Theory]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("ru")]
    [InlineData("ja")]
    [InlineData("zh")]
    public async Task LanguageChange_RelabelsDirtyCardAndConfirmationWithoutChangingDraft(string language)
    {
        await ui.Session.Dispatch(() =>
        {
            var oldLanguage = Loc.Instance.CurrentLanguage;
            var manager = ui.Services.GetRequiredService<PluginManager>();
            var oldPlugins = manager.AllPlugins.ToArray();
            var enabledIds = oldPlugins.Where(plugin => manager.IsEnabled(plugin.Manifest.Id)).Select(plugin => plugin.Manifest.Id).ToArray();
            var plugin = new LocalizedSettingsPlugin();
            var directory = Path.Join(UiEnvironment.Root, plugin.PluginId);
            Directory.CreateDirectory(directory);
            var manifest = new PluginManifest
            {
                Id = plugin.PluginId,
                Name = plugin.PluginName,
                Version = plugin.PluginVersion,
                AssemblyName = "localized-settings.dll",
                PluginClass = typeof(LocalizedSettingsPlugin).FullName!,
                NetworkAccess = PluginNetworkAccess.Network,
                Categories = [PluginCategory.Utility],
            };
            var context = new PluginAssemblyLoadContext(directory);
            var vm = ui.Services.GetRequiredService<PluginsSectionViewModel>();
            Window? window = null;
            try
            {
                Loc.Instance.CurrentLanguage = "en";
                PluginManagerTestAccess.SetLoadedPlugins(manager,
                    [new LoadedPlugin(manifest, plugin, context, directory, PluginLoader.ResolveMetadata(manifest))],
                    [plugin.PluginId]);
                MainWindowRenderTests.Settle();
                window = new Window { Width = 1200, Height = 900, Content = new PluginsSection { DataContext = vm } };
                window.Show();
                Pump(vm.RevealPluginAsync(plugin.PluginId, Task.CompletedTask, Task.CompletedTask));
                var row = vm.EnabledGroups.SelectMany(group => group.Plugins).Single();
                Pump(row.ObservedSettingsLoad);
                var mode = row.SettingFields.Single();
                var collection = row.Collections.Single();
                var item = collection.Items.Single();
                var itemMode = item.Fields.Single(field => field.Key == "mode");
                mode.SelectedOption = mode.Options.Single(option => option.Value == "b");
                itemMode.SelectedOption = itemMode.Options.Single(option => option.Value == "b");
                Pump(vm.RunSettingsActionCommand.ExecuteAsync(row.Actions.Single()));
                Assert.True(row.HasUnsavedSettings);
                var oldExpanderName = row.ExpanderAccessibleName;

                Loc.Instance.CurrentLanguage = language;
                MainWindowRenderTests.Settle();

                Assert.Same(row, vm.EnabledGroups.SelectMany(group => group.Plugins).Single());
                Assert.Equal("b", mode.Value);
                Assert.Equal("b", itemMode.Value);
                Assert.Equal("sample", collection.SelectedTemplate!.Id);
                Assert.Equal("remove", row.PendingAction!.Id);
                Assert.Equal(Loc.Instance["Common.Delete"], row.PendingAction.ConfirmationMessage);
                Assert.Equal(Loc.Instance["Common.Copy"], collection.SelectedTemplateDescription);
                Assert.True(row.HasUnsavedSettings);
                var visibleText = window.GetVisualDescendants().OfType<TextBlock>()
                    .Where(text => text.IsEffectivelyVisible).Select(text => text.Text).ToArray();
                Assert.Contains(Loc.Instance["Plugins.BadgeCloud"], visibleText);
                Assert.Contains(Loc.Instance["Plugins.BadgeEnabled"], visibleText);
                var accessibleNames = window.GetVisualDescendants().OfType<Control>()
                    .Select(AutomationProperties.GetName).ToArray();
                Assert.Contains(row.ExpanderAccessibleName, accessibleNames);
                Assert.DoesNotContain(oldExpanderName, accessibleNames);

                // Restoring the original values after relabeling must leave no phantom draft.
                mode.SelectedOption = mode.Options.Single(option => option.Value == "a");
                itemMode.SelectedOption = itemMode.Options.Single(option => option.Value == "a");
                MainWindowRenderTests.Settle();
                Assert.False(row.HasUnsavedSettings);
            }
            finally
            {
                window?.Close();
                PluginManagerTestAccess.SetLoadedPlugins(manager, oldPlugins, enabledIds);
                Loc.Instance.CurrentLanguage = oldLanguage;
                MainWindowRenderTests.Settle();
                context.Unload();
            }
        }, CancellationToken.None);
    }

    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(task.IsCompleted, "Timed out loading plugin editor.");
        task.GetAwaiter().GetResult();
        MainWindowRenderTests.Settle();
    }

    private sealed class LocalizedSettingsPlugin : ITypeWhisperPlugin, IPluginSettingsProvider,
        IPluginCollectionSettingsProvider, IPluginSettingsActions
    {
        public string PluginId => "com.typewhisper.localized-draft-test";
        public string PluginName => "Sample";
        public string PluginVersion => "1.0.0";
        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public void Dispose() { }

        private static PluginSettingDefinition Mode() => new("mode", Loc.Instance["Common.Apply"],
            Options: [new("a", Loc.Instance["Common.Copy"]), new("b", Loc.Instance["Common.Delete"])],
            Kind: PluginSettingKind.Dropdown);

        public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() => [Mode()];
        public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>("a");
        public Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default) => throw new InvalidOperationException("The test never saves.");
        public IReadOnlyList<PluginCollectionDefinition> GetCollectionDefinitions() =>
        [
            new("items", Loc.Instance["Common.New"], null,
                [new("name", Loc.Instance["Common.Edit"], Kind: PluginSettingKind.Text), Mode()],
                ItemLabelFieldKey: "name")
            {
                Templates = [new("sample", Loc.Instance["Common.Add"], Loc.Instance["Common.Copy"], new Dictionary<string, string?>())],
            },
        ];
        public Task<IReadOnlyList<PluginCollectionItem>> GetItemsAsync(string collectionKey, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PluginCollectionItem>>([new(new Dictionary<string, string?> { ["__id"] = "item-1", ["name"] = "", ["mode"] = "a" })]);
        public Task<PluginSettingsValidationResult> SetItemsAsync(string collectionKey, IReadOnlyList<PluginCollectionItem> items, CancellationToken ct = default) =>
            throw new InvalidOperationException("The test never saves.");
        public IReadOnlyList<PluginSettingsAction> GetSettingsActions() =>
            [new("remove", Loc.Instance["Common.Remove"], ConfirmationMessage: Loc.Instance["Common.Delete"])];
        public Task<PluginSettingsValidationResult> ExecuteSettingsActionAsync(string actionId, CancellationToken ct) =>
            throw new InvalidOperationException("The test never confirms the action.");
    }
}
