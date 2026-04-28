using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed class HistoryInsightsService
{
    public HistoryInsights Build(IReadOnlyList<TranscriptionRecord> records, int topAppCount = 5)
    {
        if (records.Count == 0)
            return new HistoryInsights();

        var totalWords = records.Sum(record => record.WordCount);
        var totalDuration = records.Sum(record => record.DurationSeconds);
        var topApps = records
            .Where(record => !string.IsNullOrWhiteSpace(record.AppProcessName))
            .GroupBy(record => record.AppProcessName!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new AppUsageInsight(
                AppProcessName: group.Key,
                RecordCount: group.Count(),
                WordCount: group.Sum(record => record.WordCount)))
            .OrderByDescending(app => app.RecordCount)
            .ThenByDescending(app => app.WordCount)
            .ThenBy(app => app.AppProcessName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, topAppCount))
            .ToList();

        return new HistoryInsights
        {
            TotalRecords = records.Count,
            TotalWords = totalWords,
            AverageWordsPerDictation = Math.Round(totalWords / (double)records.Count, 1),
            AverageDurationSeconds = Math.Round(totalDuration / records.Count, 1),
            TopApps = topApps
        };
    }
}
