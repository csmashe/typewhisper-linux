using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class HistoryInsightsServiceTests
{
    private readonly HistoryInsightsService _sut = new();

    [Fact]
    public void Build_ReturnsEmptyInsightsForNoRecords()
    {
        var result = _sut.Build([]);

        Assert.Equal(0, result.TotalRecords);
        Assert.Equal(0, result.TotalWords);
        Assert.Empty(result.TopApps);
    }

    [Fact]
    public void Build_ComputesAveragesAndTopApps()
    {
        var records = new[]
        {
            Record("one two three", "code", duration: 3),
            Record("one two", "code", duration: 5),
            Record("one two three four", "browser", duration: 4)
        };

        var result = _sut.Build(records);

        Assert.Equal(3, result.TotalRecords);
        Assert.Equal(9, result.TotalWords);
        Assert.Equal(3.0, result.AverageWordsPerDictation);
        Assert.Equal(4.0, result.AverageDurationSeconds);
        Assert.Equal("code", result.TopApps[0].AppProcessName);
        Assert.Equal(2, result.TopApps[0].RecordCount);
        Assert.Equal(5, result.TopApps[0].WordCount);
    }

    private static TranscriptionRecord Record(string finalText, string app, double duration) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            Timestamp = DateTime.UtcNow,
            RawText = finalText,
            FinalText = finalText,
            AppProcessName = app,
            DurationSeconds = duration
        };
}
