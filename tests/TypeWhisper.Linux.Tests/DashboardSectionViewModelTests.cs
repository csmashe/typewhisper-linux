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
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "TypeWhisper.Dashboard.Tests_" + Guid.NewGuid().ToString("N")
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
    public void Refresh_BuildsHistoryInsightsForSelectedRange()
    {
        var history = new HistoryService(Path.Join(_tempDir, "history.json"));
        history.AddRecord(
            CreateRecord(
                "one two three four",
                "code",
                8,
                DateTime.UtcNow,
                TextInsertionStatus.Pasted,
                true
            )
        );
        history.AddRecord(
            CreateRecord(
                "one two",
                "code",
                4,
                DateTime.UtcNow,
                TextInsertionStatus.Typed,
                snippetApplied: true
            )
        );
        history.AddRecord(
            CreateRecord(
                "one two three four five six",
                "browser",
                12,
                DateTime.UtcNow,
                TextInsertionStatus.CopiedToClipboard,
                dictionaryApplied: true,
                promptApplied: true,
                translationApplied: true
            )
        );
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
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
        var history = new HistoryService(Path.Join(_tempDir, "history.json"));
        history.AddRecord(
            CreateRecord("one two three four five six seven eight", "code", 4, DateTime.UtcNow)
        );
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        var sut = new DashboardSectionViewModel(history, settings, new HistoryInsightsService());

        sut.SelectedRange = DashboardSectionViewModel.TimeRange.AllTime;

        Assert.Equal("8s", sut.TimeSavedLabel);
    }

    [Fact]
    public void Refresh_CountsWordsSeparatedByNewlines()
    {
        var history = new HistoryService(Path.Join(_tempDir, "history.json"));
        history.AddRecord(
            CreateRecord("Hi Ryan,\n\nThis has spacing.", "browser", 1, DateTime.UtcNow)
        );
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        var sut = new DashboardSectionViewModel(history, settings, new HistoryInsightsService());

        sut.SelectedRange = DashboardSectionViewModel.TimeRange.AllTime;

        Assert.Equal(5, sut.WordCount);
    }

    [Fact]
    public void RecentActivity_ExposesLocalPresentationTimestamp()
    {
        var timeZone = TimeZoneInfo.CreateCustomTimeZone(
            "Dashboard tests UTC+13",
            TimeSpan.FromHours(13),
            "Dashboard tests UTC+13",
            "Dashboard tests UTC+13"
        );
        var history = new HistoryService(Path.Join(_tempDir, "history.json"));
        var timestamp = new DateTime(2030, 1, 2, 23, 30, 0, DateTimeKind.Utc);
        history.AddRecord(CreateRecord("recent words", "code", 2, timestamp));
        var settings = new SettingsService(Path.Join(_tempDir, "settings.json"));
        var sut = new DashboardSectionViewModel(
            history,
            settings,
            new HistoryInsightsService(),
            timeZone
        );

        sut.SelectedRange = DashboardSectionViewModel.TimeRange.AllTime;

        var activity = Assert.Single(sut.RecentActivity);
        Assert.Same(history.Records[0], activity.Record);
        Assert.Equal(new DateTime(2030, 1, 3, 12, 30, 0), activity.LocalTimestamp);
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
        bool translationApplied = false
    )
    {
        return new TranscriptionRecord
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
            TranslationApplied = translationApplied,
        };
    }
}
