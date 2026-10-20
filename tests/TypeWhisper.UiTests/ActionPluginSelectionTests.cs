using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.UiTests;

[Collection("ui")]
public sealed class ActionPluginSelectionTests(UiSession ui)
{
    [Theory]
    [InlineData(false, "none")]
    [InlineData(true, "none")]
    [InlineData(false, "other prompt")]
    [InlineData(false, "reopen prompt")]
    [InlineData(false, "language")]
    public async Task MissingPlugin_PointerSelectionCommitsAndSaves(bool installed, string transition)
    {
        await ui.Session.Dispatch(() =>
        {
            var path = Path.Join(UiEnvironment.Root, $"prompts-{Guid.NewGuid():N}.json");
            var prompts = new PromptActionService(path);
            var missing = new PromptAction { Id = "missing", Name = "Missing", SystemPrompt = "x", TargetActionPluginId = "missing.plugin" };
            prompts.AddAction(missing);
            var vm = new PromptsSectionViewModel(prompts,
                ui.Services.GetRequiredService<IProfileService>(),
                ui.Services.GetRequiredService<HotkeyService>(),
                ui.Services.GetRequiredService<PluginManager>(),
                ui.Services.GetRequiredService<ISettingsService>());
            var installedOption = new ActionPluginOption("installed.plugin", "Installed action");
            vm.ActionPluginOptions.Add(installedOption);
            vm.SelectedAction = missing;
            var combo = new ComboBox { DataContext = vm, Width = 300 };
            combo.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.ActionPluginOptions)));
            combo.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(vm.SelectedActionPlugin)) { Mode = BindingMode.TwoWay });
            var window = new Window { Width = 400, Height = 200, Content = combo };
            window.Show();
            var language = Loc.Instance.CurrentLanguage;
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.Equal("missing.plugin", Assert.IsType<ActionPluginOption>(combo.SelectedItem).Value);
                // A transient empty selection from a list rebuild must not overwrite the draft.
                combo.SelectedItem = null;
                Assert.Equal("missing.plugin", vm.EditTargetActionPluginId);
                combo.SelectedItem = vm.SelectedActionPlugin;
                combo.IsDropDownOpen = true;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var target = installed ? installedOption : vm.ActionPluginOptions.Single(o => o.Value is null);
                var changedDraft = new PromptAction { Id = "other", Name = "Other", SystemPrompt = "y", TargetActionPluginId = "other.plugin" };
                prompts.AddAction(changedDraft);
                // Exercise navigation and a queued language rebuild before deferred cleanup runs.
                var transitioned = false;
                combo.SelectionChanged += (_, _) =>
                {
                    if (transitioned || !ReferenceEquals(combo.SelectedItem, target))
                        return;
                    transitioned = true;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (transition == "other prompt" || transition == "reopen prompt")
                        {
                            vm.SelectedAction = changedDraft;
                            if (transition == "reopen prompt")
                                vm.SelectedAction = missing;
                        }
                        else if (transition == "language")
                            Loc.Instance.CurrentLanguage = language == "de" ? "en" : "de";
                    });
                };
                var row = combo.ContainerFromIndex(vm.ActionPluginOptions.IndexOf(target))!;
                var popup = TopLevel.GetTopLevel(row)!;
                popup.UpdateLayout();
                var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), popup)!.Value;
                popup.MouseDown(point, MouseButton.Left);
                popup.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                Assert.False(combo.IsDropDownOpen);
                var expected = transition == "other prompt" ? "other.plugin"
                    : transition == "reopen prompt" ? "missing.plugin" : target.Value;
                Assert.Equal(expected, Assert.IsType<ActionPluginOption>(combo.SelectedItem).Value);
                Assert.Equal(expected, vm.EditTargetActionPluginId);
                Assert.Equal(transition == "reopen prompt", vm.ActionPluginOptions.Any(o => o.Value == "missing.plugin"));
                Assert.Equal(transition == "other prompt", vm.ActionPluginOptions.Any(o => o.Value == "other.plugin"));
                if (transition == "language")
                    Assert.Equal(Loc.Instance["Prompts.InsertTextNormally"], Assert.IsType<ActionPluginOption>(combo.SelectedItem).Label);
                vm.SaveActionCommand.Execute(null);
                var saved = new PromptActionService(path);
                Assert.Equal(expected, saved.Actions.Single(a => a.Id == vm.SelectedAction!.Id).TargetActionPluginId);
            }
            finally
            {
                window.Close();
                Loc.Instance.CurrentLanguage = language;
                Dispatcher.UIThread.RunJobs();
            }
        }, CancellationToken.None);
    }
}
