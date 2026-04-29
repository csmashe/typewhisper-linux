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
        history.AddRecord(CreateRecord("one two three four", "code", 8, DateTime.UtcNow));
        history.AddRecord(CreateRecord("one two", "code", 4, DateTime.UtcNow));
        history.AddRecord(CreateRecord("one two three four five six", "browser", 12, DateTime.UtcNow));
        var settings = new SettingsService(Path.Combine(_tempDir, "settings.json"));
        var sut = new DashboardSectionViewModel(history, settings, new HistoryInsightsService());

        sut.SelectedRange = DashboardSectionViewModel.TimeRange.AllTime;

        Assert.Equal("4", sut.AverageWordsPerDictationLabel);
        Assert.Equal("8s", sut.AverageDurationLabel);
        Assert.True(sut.HasTopApps);
        Assert.Equal("code", sut.TopApps[0].AppProcessName);
        Assert.Equal("2 dictations · 6 words", sut.TopApps[0].Summary);
    }

    private static TranscriptionRecord CreateRecord(
        string finalText,
        string appProcessName,
        double durationSeconds,
        DateTime timestamp) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = timestamp,
            RawText = finalText,
            FinalText = finalText,
            AppProcessName = appProcessName,
            DurationSeconds = durationSeconds
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
