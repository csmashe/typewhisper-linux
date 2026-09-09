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
}
