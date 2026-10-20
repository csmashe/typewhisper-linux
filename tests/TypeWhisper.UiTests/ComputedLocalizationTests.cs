using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.UiTests;

[Collection("ui")]
public sealed class ComputedLocalizationTests(UiSession ui)
{
    [Fact]
    public async Task LanguageChange_RefreshesPromptPropertiesAndSnippetModesWithoutLosingDraft()
    {
        await ui.Session.Dispatch(() =>
        {
            var prompts = ui.Services.GetRequiredService<PromptsSectionViewModel>();
            var snippets = ui.Services.GetRequiredService<SnippetsSectionViewModel>();
            var changed = new HashSet<string?>();
            void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => changed.Add(e.PropertyName);
            prompts.PropertyChanged += OnChanged;
            var language = Loc.Instance.CurrentLanguage;
            try
            {
                snippets.BeginCreateCommand.Execute(null);
                snippets.NewTrigger = "draft";
                snippets.SelectedTriggerMode = SnippetTriggerMode.ExactPhrase;
                Loc.Instance.CurrentLanguage = language == "de" ? "en" : "de";
                Dispatcher.UIThread.RunJobs();
                foreach (var property in new[] { "Summary", "PromptsHint", "ProviderWarningText", "EditorTitle" })
                    Assert.Contains(property, changed);
                Assert.Equal(Loc.Instance["Snippets.TriggerModeExactPhrase"],
                    snippets.TriggerModeOptions.Single(o => o.Value == SnippetTriggerMode.ExactPhrase).Label);
                Assert.Equal(SnippetTriggerMode.ExactPhrase, snippets.SelectedTriggerMode);
                Assert.Equal("draft", snippets.NewTrigger);
            }
            finally
            {
                snippets.CancelEditCommand.Execute(null);
                prompts.PropertyChanged -= OnChanged;
                Loc.Instance.CurrentLanguage = language;
                Dispatcher.UIThread.RunJobs();
            }
        }, CancellationToken.None);
    }
}
