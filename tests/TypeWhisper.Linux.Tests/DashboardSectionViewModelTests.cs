using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DashboardSectionViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public DashboardSectionViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TypeWhisper.Dashboard.Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void Refresh_BuildsHistoryInsightsForSelectedRange()
    {
        var history = new HistoryService(Path.Combine(_tempDir, "history.json"));
        history.AddRecord(CreateRecord("one two three four", "code", 8, DateTime.UtcNow, TextInsertionStatus.Pasted, cleanupApplied: true));
        history.AddRecord(CreateRecord("one two", "code", 4, DateTime.UtcNow, TextInsertionStatus.Typed, snippetApplied: true));
        history.AddRecord(CreateRecord("one two three four five six", "browser", 12, DateTime.UtcNow, TextInsertionStatus.CopiedToClipboard, dictionaryApplied: true, promptApplied: true, translationApplied: true));
        var settings = new SettingsService(Path.Combine(_tempDir, "settings.json"));
        var sut = new DashboardSectionViewModel(history, settings, new HistoryInsightsService());

        sut.SelectedRange = DashboardSectionViewModel.TimeRange.AllTime;

        Assert.Equal("4", sut.AverageWordsPerDictationLabel);
        Assert.Equal("8s", sut.AverageDurationLabel);
        Assert.True(sut.HasTopApps);
        Assert.Equal("code", sut.TopApps[0].AppProcessName);
        Assert.Equal("2 dictations · 6 words", sut.TopApps[0].Summary);
        Assert.Equal("66.7%", sut.InsertionSuccessRateLabel);
        Assert.Equal("1", sut.PastedCountLabel);
        Assert.Equal("1", sut.TypedCountLabel);
        Assert.Equal("1 pasted / 1 typed", sut.InsertedBreakdownLabel);
        Assert.Equal("1", sut.ClipboardFallbackCountLabel);
        Assert.Equal("0", sut.FailedInsertionCountLabel);
        Assert.Equal("1", sut.CleanupAppliedCountLabel);
        Assert.Equal("1", sut.SnippetAppliedCountLabel);
        Assert.Equal("1", sut.DictionaryCorrectionAppliedCountLabel);
        Assert.Equal("1", sut.PromptActionAppliedCountLabel);
        Assert.Equal("1", sut.TranslationAppliedCountLabel);
    }

    [Fact]
    public void Refresh_CalculatesTimeSavedFromManualTypingBaseline()
    {
        var history = new HistoryService(Path.Combine(_tempDir, "history.json"));
        history.AddRecord(CreateRecord("one two three four five six seven eight", "code", 4, DateTime.UtcNow));
        var settings = new SettingsService(Path.Combine(_tempDir, "settings.json"));
        var sut = new DashboardSectionViewModel(history, settings, new HistoryInsightsService());

        sut.SelectedRange = DashboardSectionViewModel.TimeRange.AllTime;

        Assert.Equal("8s", sut.TimeSavedLabel);
    }

    [Fact]
    public void Refresh_CountsWordsSeparatedByNewlines()
    {
        var history = new HistoryService(Path.Combine(_tempDir, "history.json"));
        history.AddRecord(CreateRecord("Hi Ryan,\n\nThis has spacing.", "browser", 1, DateTime.UtcNow));
        var settings = new SettingsService(Path.Combine(_tempDir, "settings.json"));
        var sut = new DashboardSectionViewModel(history, settings, new HistoryInsightsService());

        sut.SelectedRange = DashboardSectionViewModel.TimeRange.AllTime;

        Assert.Equal(5, sut.WordCount);
    }

    private static TranscriptionRecord CreateRecord(
        string finalText,
        string appProcessName,
        double durationSeconds,
        DateTime timestamp,
        TextInsertionStatus insertionStatus = TextInsertionStatus.Unknown,
        bool cleanupApplied = false,
        bool snippetApplied = false,
        bool dictionaryApplied = false,
        bool promptApplied = false,
        bool translationApplied = false) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = timestamp,
            RawText = finalText,
            FinalText = finalText,
            AppProcessName = appProcessName,
            DurationSeconds = durationSeconds,
            InsertionStatus = insertionStatus,
            CleanupApplied = cleanupApplied,
            SnippetApplied = snippetApplied,
            DictionaryCorrectionApplied = dictionaryApplied,
            PromptActionApplied = promptApplied,
            TranslationApplied = translationApplied
        };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
        }
    }
}
