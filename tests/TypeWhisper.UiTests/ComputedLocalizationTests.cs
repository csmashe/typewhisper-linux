using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Tests;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.UiTests;

[Collection("ui")]
public sealed class ComputedLocalizationTests(UiSession ui)
{
    [Fact]
    public async Task LanguageChange_RefreshesLocalModelProviderLabelsAndKeepsSelection()
    {
        await ui.Session.Dispatch(() =>
        {
            var language = Loc.Instance.CurrentLanguage;
            var manager = ui.Services.GetRequiredService<PluginManager>();
            var oldEngines = manager.TranscriptionEngines.ToArray();
            var settings = ui.Services.GetRequiredService<ISettingsService>();
            var oldSelectedId = settings.Current.SelectedModelId;
            var dictation = ui.Services.GetRequiredService<DictationSectionViewModel>();
            void NotifyPlugins() => PluginManagerTestAccess.SetLoadedPlugins(manager, manager.AllPlugins,
                manager.AllPlugins.Where(plugin => manager.IsEnabled(plugin.Manifest.Id)).Select(plugin => plugin.Manifest.Id).ToArray());
            try
            {
                Loc.Instance.CurrentLanguage = "en";
                PluginManagerTestAccess.SetTranscriptionEngines(manager,
                    [new UiLocalModelEngine(() => Loc.Instance.CurrentLanguage == "en" ? "Local engine" : "Lokale Engine")]);
                NotifyPlugins();
                MainWindowRenderTests.Settle();
                var model = dictation.ModelOptions.First();
                settings.Update(current => current with { SelectedModelId = model.ModelId });
                dictation.SelectedModel = model;
                MainWindowRenderTests.Settle();
                Loc.Instance.CurrentLanguage = "de";
                MainWindowRenderTests.Settle();

                Assert.Equal(model.ModelId, dictation.SelectedModel?.ModelId);
                Assert.Equal(model.ModelId, settings.Current.SelectedModelId);
                Assert.Equal("Lokale Engine", dictation.EngineName);
                Assert.All(dictation.ModelOptions, option => Assert.Equal("Lokale Engine", option.EngineName));
                Assert.All(dictation.LocalModelCards, card => Assert.Contains("Lokale Engine", card.DetailText));
            }
            finally
            {
                PluginManagerTestAccess.SetTranscriptionEngines(manager, oldEngines);
                settings.Update(current => current with { SelectedModelId = oldSelectedId });
                NotifyPlugins();
                Loc.Instance.CurrentLanguage = language;
                MainWindowRenderTests.Settle();
                dictation.SelectedModel = dictation.ModelOptions.FirstOrDefault(option => option.ModelId == oldSelectedId);
                MainWindowRenderTests.Settle();
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("de", true)]
    [InlineData("ja", false)]
    [InlineData("ru", true)]
    public async Task LocalizingSeededNames_PreservesOpenEditorDrafts(string language, bool renameDraft)
    {
        await ui.Session.Dispatch(() =>
        {
            var originalLanguage = Loc.Instance.CurrentLanguage;
            Loc.Instance.CurrentLanguage = "en";
            var profileService = ui.Services.GetRequiredService<IProfileService>();
            var promptService = ui.Services.GetRequiredService<IPromptActionService>();
            var profiles = ui.Services.GetRequiredService<ProfilesSectionViewModel>();
            var prompts = ui.Services.GetRequiredService<PromptsSectionViewModel>();
            var main = ui.Services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow(main) { Width = 1200, Height = 900 };
            var profile = FirstRunDefaults.CreateAutoFormatProfile();
            var action = FirstRunDefaults.CreateAutoCleanupAction();
            profileService.AddProfile(profile);
            promptService.AddAction(action);
            window.Show();
            MainWindowRenderTests.Settle();
            try
            {
                main.SelectedItem = main.NavItems.Single(item => item.Content == profiles);
                profiles.SelectedProfile = profiles.Profiles.Single(p => p.Id == profile.Id);
                MainWindowRenderTests.Settle();
                profiles.EditPriority = 42;
                profiles.ProcessNameChips.Add("audit-editor");
                profiles.ProcessNameInput = "uncommitted-input";
                if (renameDraft) profiles.EditName = "My profile draft";
                Loc.Instance.CurrentLanguage = language;
                profileService.LocalizeDefaultNames();
                MainWindowRenderTests.Settle();

                Assert.Equal(42, profiles.EditPriority);
                Assert.Contains("audit-editor", profiles.ProcessNameChips);
                Assert.Equal("uncommitted-input", profiles.ProcessNameInput);
                Assert.Equal(renameDraft ? "My profile draft" : Loc.Instance[DefaultItemNames.AutoFormatProfile], profiles.EditName);
                profiles.SaveProfileCommand.Execute(null);
                Assert.Equal(42, profileService.Profiles.Single(p => p.Id == profile.Id).Priority);

                Loc.Instance.CurrentLanguage = "en";
                main.SelectedItem = main.NavItems.Single(item => item.Content == prompts);
                prompts.SelectedAction = prompts.Actions.Single(p => p.Id == action.Id);
                MainWindowRenderTests.Settle();
                prompts.EditSystemPrompt = "Keep this unsaved prompt body.";
                prompts.EditIsManualOnly = true;
                if (renameDraft) prompts.EditName = "My prompt draft";
                Loc.Instance.CurrentLanguage = language;
                promptService.LocalizeDefaultNames();
                MainWindowRenderTests.Settle();

                Assert.Equal("Keep this unsaved prompt body.", prompts.EditSystemPrompt);
                Assert.True(prompts.EditIsManualOnly);
                Assert.Equal(renameDraft ? "My prompt draft" : Loc.Instance[DefaultItemNames.AutoCleanupAction], prompts.EditName);
                window.Width = 960;
                window.Height = 640;
                MainWindowRenderTests.Settle();
                Assert.DoesNotContain(UiInspector.Inspect(window, new HashSet<string>()), finding => finding.Kind == UiInspector.ClippedText);
                prompts.SaveActionCommand.Execute(null);
                Assert.Equal("Keep this unsaved prompt body.", promptService.Actions.Single(p => p.Id == action.Id).SystemPrompt);
            }
            finally
            {
                window.Close();
                prompts.SelectedAction = null;
                prompts.ShowEditor = false;
                profileService.DeleteProfile(profile.Id);
                promptService.DeleteAction(action.Id);
                Loc.Instance.CurrentLanguage = originalLanguage;
                MainWindowRenderTests.Settle();
            }
        }, CancellationToken.None);
    }

    private static readonly string[] s_promptProperties = ["Summary", "PromptsHint", "ProviderWarningText", "EditorTitle"];
    private static readonly string[] s_snippetEditorProperties = ["EditorTitle", "EditorSaveText", "ConflictWarningText"];

    [Fact]
    public async Task LanguageChange_RefreshesPromptPropertiesAndSnippetModesWithoutLosingDraft()
    {
        await ui.Session.Dispatch(() =>
        {
            var prompts = ui.Services.GetRequiredService<PromptsSectionViewModel>();
            var snippets = ui.Services.GetRequiredService<SnippetsSectionViewModel>();
            var changed = new HashSet<string?>();
            var snippetChanged = new HashSet<string?>();
            prompts.PropertyChanged += OnChanged;
            snippets.PropertyChanged += OnSnippetChanged;
            var language = Loc.Instance.CurrentLanguage;
            try
            {
                snippets.BeginCreateCommand.Execute(null);
                snippets.NewTrigger = "draft";
                snippets.SelectedTriggerMode = SnippetTriggerMode.ExactPhrase;
                snippetChanged.Clear();
                Loc.Instance.CurrentLanguage = language == "de" ? "en" : "de";
                Dispatcher.UIThread.RunJobs();
                foreach (var property in s_promptProperties)
                    Assert.Contains(property, changed);
                Assert.Equal(Loc.Instance["Snippets.TriggerModeExactPhrase"],
                    snippets.TriggerModeOptions.Single(o => o.Value == SnippetTriggerMode.ExactPhrase).Label);
                Assert.Equal(SnippetTriggerMode.ExactPhrase, snippets.SelectedTriggerMode);
                Assert.Equal("draft", snippets.NewTrigger);
                foreach (var property in s_snippetEditorProperties)
                    Assert.Contains(property, snippetChanged);
                Assert.Equal(Loc.Instance["Snippets.NewSnippet"], snippets.EditorTitle);
                Assert.Null(snippets.SelectedTagFilter?.Tag);
                Assert.Equal(Loc.Instance["Snippets.AllTags"], snippets.SelectedTagFilter?.Label);
            }
            finally
            {
                snippets.CancelEditCommand.Execute(null);
                prompts.PropertyChanged -= OnChanged;
                snippets.PropertyChanged -= OnSnippetChanged;
                Loc.Instance.CurrentLanguage = language;
                Dispatcher.UIThread.RunJobs();
            }

            return;

            void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => changed.Add(e.PropertyName);
            void OnSnippetChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => snippetChanged.Add(e.PropertyName);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LanguageChange_RecomputesGeneralStateTextAndPluginSummary()
    {
        await ui.Session.Dispatch(() =>
        {
            var general = ui.Services.GetRequiredService<GeneralSectionViewModel>();
            var plugins = ui.Services.GetRequiredService<PluginsSectionViewModel>();
            var api = ui.Services.GetRequiredService<HttpApiService>();
            var language = Loc.Instance.CurrentLanguage;
            try
            {
                Loc.Instance.CurrentLanguage = "en";
                Dispatcher.UIThread.RunJobs();
                var cliPath = general.CliBundledPathText;
                var cliStatus = general.CliStatusText;
                var apiStatus = general.ApiStatusText;
                var header = plugins.HeaderSummary;

                Loc.Instance.CurrentLanguage = "de";
                Dispatcher.UIThread.RunJobs();

                Assert.NotEqual(cliPath, general.CliBundledPathText);
                Assert.NotEqual(cliStatus, general.CliStatusText);
                Assert.NotEqual(apiStatus, general.ApiStatusText);
                Assert.Equal(api.StatusText, general.ApiStatusText);
                Assert.NotEqual(header, plugins.HeaderSummary);
            }
            finally
            {
                Loc.Instance.CurrentLanguage = language;
                Dispatcher.UIThread.RunJobs();
            }
        }, CancellationToken.None);
    }
}
