using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class UsageStatisticsServiceTests : IDisposable
{
    private readonly string _directory = Path.Join(
        Path.GetTempPath(),
        "TypeWhisper-UsageStatisticsTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void RecordTranscription_WaitsForBackfillThenAggregatesImmediately()
    {
        var service = new UsageStatisticsService(Path.Join(_directory, "statistics.json"));
        var record = CreateRecord("one", DateTime.UtcNow, "hello world");
        var events = 0;
        service.StatisticsChanged += () => events++;

        service.RecordTranscription(record);
        Assert.False(service.HasAnyStatistics);
        Assert.Empty(service.Days);
        Assert.Equal(0, events);

        service.BackfillFromHistoryIfNeeded([record]);
        Assert.Equal(1, Assert.Single(service.Days).TranscriptionCount);
        Assert.Equal(2, Assert.Single(service.Days).TotalWords);
        Assert.Equal(1, events);

        service.RecordTranscription(record with { Id = "two" });
        Assert.Equal(2, Assert.Single(service.Days).TranscriptionCount);
        Assert.Equal(4, Assert.Single(service.Days).TotalWords);
        Assert.Equal(2, events);
    }

    [Fact]
    public void UnwritableParent_DropsRecordsAndBackfillWithoutEventsAndAllowsRetry()
    {
        Directory.CreateDirectory(_directory);
        var parent = Path.Join(_directory, "blocked");
        File.WriteAllText(parent, "regular file");
        var path = Path.Join(parent, "statistics.json");
        var service = new UsageStatisticsService(path);
        var events = 0;
        service.StatisticsChanged += () => events++;
        var record = CreateRecord("one", DateTime.UtcNow, "hello world");

        service.RecordTranscription(record);
        service.BackfillFromHistoryIfNeeded([record]);

        Assert.Empty(service.Days);
        Assert.False(service.HasAnyStatistics);
        Assert.Equal(0, events);
        Assert.False(File.Exists(path));
        File.Delete(parent);
        service.RecordTranscription(record);
        Assert.False(service.HasAnyStatistics);
        Assert.Equal(0, events);
        service.BackfillFromHistoryIfNeeded([record]);
        Assert.Equal(1, Assert.Single(service.Days).TranscriptionCount);
        Assert.Equal(1, events);
        Assert.Equal(1, Assert.Single(new UsageStatisticsService(path).Days).TranscriptionCount);
    }

    [Fact]
    public void UnreadableStore_ReturnsEmptyStatisticsWithoutThrowing()
    {
        var path = Path.Join(_directory, "statistics.json");
        Directory.CreateDirectory(path);
        var service = new UsageStatisticsService(path);
        var events = 0;
        service.StatisticsChanged += () => events++;

        Assert.Empty(service.Days);
        Assert.False(service.HasAnyStatistics);
        service.RecordTranscription(CreateRecord("one", DateTime.UtcNow, "hello"));
        service.BackfillFromHistoryIfNeeded([]);
        Assert.Equal(0, events);
    }

    [Fact]
    public void RecordTranscription_AggregatesDayAppModelAndHourAndPersists()
    {
        var path = Path.Join(_directory, "usage-statistics.json");
        var timestamp = DateTime.SpecifyKind(new DateTime(2026, 8, 8, 14, 30, 0), DateTimeKind.Utc);
        var service = new UsageStatisticsService(path, new FixedTimeProvider(TimeZoneInfo.Utc));
        service.BackfillFromHistoryIfNeeded([]);

        service.RecordTranscription(CreateRecord("one", timestamp, string.Join(" ", Enumerable.Repeat("word", 120))) with { DurationSeconds = 60 });
        service.RecordTranscription(CreateRecord("two", timestamp.AddMinutes(15), string.Join(" ", Enumerable.Repeat("word", 30))) with { DurationSeconds = 15 });

        var day = Assert.Single(service.Days);
        Assert.Equal(DateOnly.FromDateTime(timestamp), day.Day);
        Assert.Equal(2, day.TranscriptionCount);
        Assert.Equal(150, day.TotalWords);
        Assert.Equal(75, day.TotalDurationSeconds);
        Assert.Equal(2, day.AppCounts["notepad"]);
        Assert.Equal(2, day.ModelCounts[UsageStatisticsService.BuildModelKey("whisper", "large-v3")]);
        Assert.Equal(2, day.HourCounts[14]);

        var reloaded = new UsageStatisticsService(path);
        var reloadedDay = Assert.Single(reloaded.Days);
        Assert.Equal(day.Day, reloadedDay.Day);
        Assert.Equal(day.TranscriptionCount, reloadedDay.TranscriptionCount);
        Assert.Equal(day.TotalWords, reloadedDay.TotalWords);
        Assert.Equal(day.AppCounts, reloadedDay.AppCounts);
        Assert.Equal(day.ModelCounts, reloadedDay.ModelCounts);
        Assert.Equal(day.HourCounts, reloadedDay.HourCounts);
    }

    [Fact]
    public void BackfillFromHistoryIfNeeded_ImportsOnlyOnceAndSurvivesHistoryChanges()
    {
        var path = Path.Join(_directory, "usage-statistics.json");
        var timestamp = DateTime.SpecifyKind(new DateTime(2026, 8, 7, 9, 0, 0), DateTimeKind.Utc);
        var record = CreateRecord("one", timestamp, "hello persistent statistics");
        var service = new UsageStatisticsService(path, new FixedTimeProvider(TimeZoneInfo.Utc));

        service.BackfillFromHistoryIfNeeded([record]);
        service.BackfillFromHistoryIfNeeded([record]);

        var day = Assert.Single(service.Days);
        Assert.Equal(1, day.TranscriptionCount);
        Assert.Equal(3, day.TotalWords);

        var reloaded = new UsageStatisticsService(path);
        reloaded.BackfillFromHistoryIfNeeded([record]);
        var reloadedDay = Assert.Single(reloaded.Days);
        Assert.Equal(day.Day, reloadedDay.Day);
        Assert.Equal(day.TranscriptionCount, reloadedDay.TranscriptionCount);
        Assert.Equal(day.TotalWords, reloadedDay.TotalWords);
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public void RecordTranscription_UsesProviderLocalDayAndHour(DateTimeKind kind)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");
        var service = new UsageStatisticsService(Path.Join(_directory, "local.json"), new FixedTimeProvider(zone));
        service.BackfillFromHistoryIfNeeded([]);
        service.RecordTranscription(CreateRecord("one", new DateTime(2026, 8, 8, 23, 30, 0, kind), "hello world"));
        var day = Assert.Single(service.Days);
        Assert.Equal(new DateOnly(2026, 8, 9), day.Day);
        Assert.Equal(1, day.HourCounts[2]);
    }

    [Fact]
    public void Persistence_PreservesCalendarDayAndHoursAcrossTimeZones()
    {
        var path = Path.Join(_directory, "zones.json");
        var east = TimeZoneInfo.CreateCustomTimeZone("UTC+3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");
        var west = TimeZoneInfo.CreateCustomTimeZone("UTC-8", TimeSpan.FromHours(-8), "UTC-8", "UTC-8");
        var service = new UsageStatisticsService(path, new FixedTimeProvider(east));
        service.BackfillFromHistoryIfNeeded([]);
        service.RecordTranscription(CreateRecord("one", new DateTime(2026, 9, 8, 23, 30, 0, DateTimeKind.Utc), "hello"));
        service.RecordTranscription(CreateRecord("two", new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc), "world"));

        var original = Assert.Single(service.Days);
        var reloaded = Assert.Single(new UsageStatisticsService(path, new FixedTimeProvider(west)).Days);
        Assert.Equal(new DateOnly(2026, 9, 9), reloaded.Day);
        Assert.Equal(original.Day, reloaded.Day);
        Assert.Equal(original.HourCounts, reloaded.HourCounts);
        Assert.Equal(1, reloaded.HourCounts[2]);
        Assert.Equal(1, reloaded.HourCounts[15]);
        Assert.Contains("\"day\": \"2026-09-09\"", File.ReadAllText(path));
    }

    [Fact]
    public void Deserialize_LegacyTimestampPreservesWrittenCalendarDay()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Join(_directory, "legacy.json");
        File.WriteAllText(path, """
            {"version":1,"days":[{"day":"2026-09-09T00:00:00+03:00","transcriptionCount":1,"totalWords":2}]}
            """);
        var service = new UsageStatisticsService(path, new FixedTimeProvider(TimeZoneInfo.Utc));

        var day = Assert.Single(service.Days);
        Assert.Equal(new DateOnly(2026, 9, 9), day.Day);
        Assert.Equal(2, day.TotalWords);
    }

    [Fact]
    public void RecordTranscription_NormalizesLanguageAndAppFallbacksAfterReload()
    {
        var path = Path.Join(_directory, "fallbacks.json");
        var service = new UsageStatisticsService(path);
        service.BackfillFromHistoryIfNeeded([]);
        var record = CreateRecord("one", DateTime.UtcNow, "hello world");
        service.RecordTranscription(record with { Language = " EN-us ", AppProcessName = "  ", AppName = " Notes " });
        service = new UsageStatisticsService(path);
        service.RecordTranscription(record with { Language = "en-GB", AppProcessName = "NOTES" });
        service.RecordTranscription(record with { Language = null, AppProcessName = null, AppName = null });
        var day = Assert.Single(service.Days);
        Assert.Equal(2, day.LanguageCounts["EN"]);
        Assert.Equal(1, day.LanguageCounts["unknown"]);
        Assert.Equal(2, day.AppCounts["notes"]);
        Assert.Single(day.AppCounts);
        Assert.Equal(("unknown", (string?)null), UsageStatisticsService.ParseModelKey(UsageStatisticsService.BuildModelKey(null, null)));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"days\":[null]}")]
    [InlineData("{\"days\":[{}]}")]
    [InlineData("{\"days\":[{\"day\":null}]}")]
    [InlineData("{\"days\":[{\"day\":\"not-a-date\"}]}")]
    [InlineData("{\"days\":[{\"day\":123}]}")]
    [InlineData("{\"days\":[42]}")]
    public void CorruptFile_IsPreservedAndReset(string json)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Join(_directory, "corrupt.json");
        File.WriteAllText(path, json);
        var service = new UsageStatisticsService(path);
        Assert.False(service.HasAnyStatistics);
        Assert.Empty(service.Days);
        service.BackfillFromHistoryIfNeeded([]);
        var preserved = Assert.Single(Directory.GetFiles(_directory, "*.broken-*"));
        Assert.Equal(json, File.ReadAllText(preserved));
        Assert.Empty(new UsageStatisticsService(path).Days);
        Assert.Contains("\"historyBackfillCompleted\": true", File.ReadAllText(path));
    }

    [Fact]
    public void IgnoredRecords_DoNotRaiseEvents()
    {
        var service = new UsageStatisticsService(Path.Join(_directory, "ignored.json"));
        var events = 0;
        service.StatisticsChanged += () => events++;
        var record = CreateRecord("one", DateTime.UtcNow, "hello");
        service.RecordTranscription(record with { FinalText = "" });
        double[] invalidDurations = [-1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity];
        foreach (var duration in invalidDurations)
            service.RecordTranscription(record with { DurationSeconds = duration });
        Assert.Empty(service.Days);
        Assert.Equal(0, events);
        service.BackfillFromHistoryIfNeeded([]);
        Assert.Equal(1, events);
        service.BackfillFromHistoryIfNeeded([record]);
        Assert.Equal(1, events);
        service.RecordTranscription(record);
        Assert.Equal(2, events);
    }

    private sealed class FixedTimeProvider(TimeZoneInfo zone) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => zone;
    }

    private static TranscriptionRecord CreateRecord(string id, DateTime timestamp, string text) => new()
    {
        Id = id,
        Timestamp = timestamp,
        RawText = text,
        FinalText = text,
        AppProcessName = "notepad",
        DurationSeconds = 30,
        EngineUsed = "whisper",
        ModelUsed = "large-v3",
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}