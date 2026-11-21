using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.UiTests;

/// <summary>An expanded History entry shows its engine, transcription time, LLM and LLM time, unclipped.</summary>
[Collection("ui")]
public sealed class HistoryProcessingDetailsTests(UiSession ui)
{
    private const string RecordId = "ui-history-processing-details";

    public static TheoryData<string> Languages => MainWindowRenderTests.Languages;

    [Theory]
    [MemberData(nameof(Languages))]
    public async Task ExpandedEntry_ShowsProcessingDetails(string language)
    {
        var findings = await ui.Session.Dispatch(() => Render(language), CancellationToken.None);

        Assert.True(findings.Count == 0, string.Join("\n", findings.Distinct()));
    }

    private List<string> Render(string language)
    {
        Loc.Instance.CurrentLanguage = language;
        var history = ui.Services.GetRequiredService<IHistoryService>();
        try
        {
            var record = new TranscriptionRecord
            {
                Id = RecordId,
                Timestamp = DateTime.UtcNow,
                RawText = "hello world",
                FinalText = "Hello world.",
                EngineUsed = "whisper",
                ModelUsed = "base",
                TranscriptionSeconds = 1.24,
                LlmProvider = "OpenAI",
                LlmModel = "gpt-4o",
                LlmSeconds = 0.82,
            };
            history.AddRecord(record);
            var viewModel = ui.Services.GetRequiredService<MainWindowViewModel>();
            var findings = new List<string>();
            foreach (var (sizeName, width, height) in MainWindowRenderTests.s_sizes)
            {
                var window = new MainWindow(viewModel) { Width = width, Height = height };
                window.Show();
                try
                {
                    var item = viewModel.NavItems.Single(nav => nav.Content is HistorySectionViewModel);
                    viewModel.SelectedItem = item;
                    MainWindowRenderTests.Settle();
                    var row = ((HistorySectionViewModel)item.Content!).Groups
                        .SelectMany(group => group.Entries)
                        .Single(entry => entry.Record.Id == RecordId);
                    row.IsExpanded = true;
                    MainWindowRenderTests.Settle();

                    var shown = window.GetVisualDescendants().OfType<TextBlock>()
                        .Where(block => block.IsEffectivelyVisible)
                        .Select(block => block.Inlines?.Text ?? block.Text ?? "")
                        .ToList();
                    var details = HistoryRecordRow.BuildProcessingDetails(record);
                    Assert.Equal(4, details.Count);
                    findings.AddRange(details
                        .Where(detail => !shown.Any(text => text.Contains(detail.Label, StringComparison.Ordinal)
                                                            && text.Contains(detail.Value, StringComparison.Ordinal)))
                        .Select(detail => $"{sizeName}: missing row {detail.Label} {detail.Value}"));

                    window.CaptureRenderedFrame()?.Save(
                        Path.Join(UiEnvironment.SnapshotDir, $"HistoryDetails-{language}-{sizeName}.png"),
                        PngBitmapEncoderOptions.Default);
                    // Only the detail rows this test is about.
                    var labels = details.Select(detail => detail.Label).ToList();
                    findings.AddRange(UiInspector.Inspect(window, new HashSet<string>())
                        .Where(finding => finding.Kind == UiInspector.ClippedText
                                          && labels.Any(label => finding.Detail.Contains(label, StringComparison.Ordinal)))
                        .Select(finding => $"{sizeName}: {finding.Control} {finding.Detail} at {finding.Location}"));
                }
                finally
                {
                    window.Close();
                }
            }

            return findings;
        }
        finally
        {
            try
            {
                history.DeleteRecord(RecordId);
                MainWindowRenderTests.Settle();
            }
            finally
            {
                Loc.Instance.CurrentLanguage = "en";
            }
        }
    }
}
