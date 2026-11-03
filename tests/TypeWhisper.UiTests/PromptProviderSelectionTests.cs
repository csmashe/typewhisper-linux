using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.UiTests;

[Collection("ui")]
public sealed class PromptProviderSelectionTests(UiSession ui)
{
    // Picking a new provider for a prompt whose saved provider is gone drops the editor-only
    // "unavailable" row. Doing that while the ComboBox is still committing the pointer
    // selection threw ArgumentOutOfRangeException from Avalonia's SelectionModel.
    [Theory]
    [InlineData(null)]
    [InlineData("plugin:compatible:gemma4-e2b-it-q4")]
    public async Task MissingProvider_PointerSelectionCommitsAndSaves(string? targetValue)
    {
        await ui.Session.Dispatch(() =>
        {
            var pluginManager = ui.Services.GetRequiredService<PluginManager>();
            var llmProviders = typeof(PluginManager).GetField("_llmProviders", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var originalProviders = llmProviders.GetValue(pluginManager);
            llmProviders.SetValue(pluginManager, new List<ILlmProviderRole> { new CompatibleProvider() });
            var path = Path.Join(UiEnvironment.Root, $"prompts-{Guid.NewGuid():N}.json");
            var prompts = new PromptActionService(path);
            var missing = new PromptAction
            {
                Id = "missing-provider", Name = "Missing provider", SystemPrompt = "x",
                ProviderOverride = "plugin:disabled.gemma:gemma",
            };
            prompts.AddAction(missing);
            var vm = new PromptsSectionViewModel(prompts,
                ui.Services.GetRequiredService<IProfileService>(),
                ui.Services.GetRequiredService<HotkeyService>(),
                pluginManager,
                ui.Services.GetRequiredService<ISettingsService>())
            {
                SelectedAction = missing,
            };
            var combo = new ComboBox { DataContext = vm, Width = 300 };
            combo.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.AvailableProviders)));
            combo.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(vm.SelectedEditProvider)) { Mode = BindingMode.TwoWay });
            var window = new Window { Width = 400, Height = 200, Content = combo };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var synthesized = Assert.IsType<ProviderOption>(combo.SelectedItem);
                Assert.Equal("plugin:disabled.gemma:gemma", synthesized.Value);
                Assert.True(synthesized.IsUnavailable);
                combo.IsDropDownOpen = true;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                var target = vm.AvailableProviders.Single(option => option.Value == targetValue);
                var row = combo.ContainerFromIndex(vm.AvailableProviders.IndexOf(target))!;
                var popup = TopLevel.GetTopLevel(row)!;
                popup.UpdateLayout();
                var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), popup)!.Value;
                popup.MouseDown(point, MouseButton.Left);
                popup.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                Assert.False(combo.IsDropDownOpen);
                Assert.Same(target, combo.SelectedItem);
                Assert.Equal(targetValue, vm.EditProviderOverride);
                Assert.DoesNotContain(vm.AvailableProviders, option => option.Value == "plugin:disabled.gemma:gemma");
                vm.SaveActionCommand.Execute(null);
                var saved = new PromptActionService(path);
                Assert.Equal(targetValue, saved.Actions.Single(a => a.Id == missing.Id).ProviderOverride);
            }
            finally
            {
                window.Close();
                llmProviders.SetValue(pluginManager, originalProviders);
                Dispatcher.UIThread.RunJobs();
            }
        }, CancellationToken.None);
    }

    private sealed class CompatibleProvider : ILlmProviderRole
    {
        public string PluginId => "compatible";
        public string ProviderName => "OpenAI Compatible";
        public bool IsAvailable => true;
        public IReadOnlyList<PluginModelInfo> SupportedModels { get; } = [new("gemma4-e2b-it-q4", "gemma4-e2b-it-q4")];

        public Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct) =>
            Task.FromResult(userText);
    }
}
