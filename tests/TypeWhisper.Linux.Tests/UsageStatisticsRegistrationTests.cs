using Microsoft.Extensions.DependencyInjection;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class UsageStatisticsRegistrationTests
{
    [Fact]
    public void UnavailableHistory_LeavesBackfillIncompleteSoLaterRecordsAreImported()
    {
        var directory = Path.Join(Path.GetTempPath(), $"tw_statistics_registration_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var history = new Mock<IHistoryService>(MockBehavior.Strict);
            history.SetupGet(service => service.Records).Returns([]);
            history.SetupGet(service => service.RecordsAvailable).Returns(false);
            var services = new ServiceCollection();
            services.AddSingleton(history.Object);
            ServiceRegistrations.RegisterUsageStatistics(services, directory);
            using var provider = services.BuildServiceProvider();
            var statistics = provider.GetRequiredService<IUsageStatisticsService>();

            Assert.Same(statistics, provider.GetRequiredService<IUsageStatisticsService>());
            Assert.False(statistics.HasAnyStatistics);
            Assert.Empty(statistics.Days);
            Assert.False(File.Exists(Path.Join(directory, "usage-statistics.json")));
            history.VerifyGet(service => service.Records, Times.Once);
            history.VerifyGet(service => service.RecordsAvailable, Times.Once);

            var record = new TranscriptionRecord
            {
                Id = "retry",
                Timestamp = DateTime.UtcNow,
                RawText = "hello world",
                FinalText = "hello world",
                DurationSeconds = 1,
            };
            statistics.RecordTranscription(record);
            Assert.False(statistics.HasAnyStatistics);

            statistics.BackfillFromHistoryIfNeeded([record, record with { Id = "second" }]);
            Assert.Equal(4, Assert.Single(statistics.Days).TotalWords);
            Assert.Equal(2, Assert.Single(statistics.Days).TranscriptionCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void HistoryBecomingAvailable_IsBackfilledBeforeTheFirstCountedRecord()
    {
        var directory = Path.Join(Path.GetTempPath(), $"tw_statistics_registration_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var older = new TranscriptionRecord
            {
                Id = "older",
                Timestamp = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc),
                RawText = "hello world",
                FinalText = "hello world",
                DurationSeconds = 1,
            };
            var history = new Mock<IHistoryService>(MockBehavior.Strict);
            // Startup and the first live record see unreadable history; the second retries successfully.
            history.SetupSequence(service => service.Records).Returns([]).Returns([]).Returns([older]);
            history.SetupSequence(service => service.RecordsAvailable).Returns(false).Returns(false).Returns(true);
            var services = new ServiceCollection();
            services.AddSingleton(history.Object);
            ServiceRegistrations.RegisterUsageStatistics(services, directory);
            using var provider = services.BuildServiceProvider();
            var statistics = provider.GetRequiredService<IUsageStatisticsService>();

            statistics.RecordTranscription(older with { Id = "first", Timestamp = older.Timestamp.AddMinutes(1) });
            Assert.False(statistics.HasAnyStatistics);

            statistics.RecordTranscription(older with { Id = "second", Timestamp = older.Timestamp.AddMinutes(2) });
            Assert.Equal(2, Assert.Single(statistics.Days).TranscriptionCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
