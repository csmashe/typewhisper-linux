using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.UiTests;

[Collection("ui")]
public sealed class ComputedLocalizationTests(UiSession ui)
{
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
