using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed class HistoryInsightsService : IHistoryInsightsService
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

        var pastedCount = records.Count(record => record.InsertionStatus is TextInsertionStatus.Pasted);
        var typedCount = records.Count(record => record.InsertionStatus is TextInsertionStatus.Typed);
        var copiedToClipboardCount = records.Count(record => record.InsertionStatus is TextInsertionStatus.CopiedToClipboard);
        var failedInsertionCount = records.Count(record => record.InsertionStatus
            is TextInsertionStatus.Failed
            or TextInsertionStatus.ActionFailed
            or TextInsertionStatus.MissingClipboardTool
            or TextInsertionStatus.MissingPasteTool);
        var successfulInsertionCount = pastedCount + typedCount;
        var insertionAttemptCount = successfulInsertionCount + copiedToClipboardCount + failedInsertionCount;

        return new HistoryInsights
        {
            TotalRecords = records.Count,
            TotalWords = totalWords,
            AverageWordsPerDictation = Math.Round(totalWords / (double)records.Count, 1),
            AverageDurationSeconds = Math.Round(totalDuration / records.Count, 1),
            PastedCount = pastedCount,
            TypedCount = typedCount,
            CopiedToClipboardCount = copiedToClipboardCount,
            FailedInsertionCount = failedInsertionCount,
            SuccessfulInsertionCount = successfulInsertionCount,
            InsertionAttemptCount = insertionAttemptCount,
            InsertionSuccessRate = insertionAttemptCount > 0
                ? Math.Round(successfulInsertionCount / (double)insertionAttemptCount * 100, 1)
                : 0,
            CleanupAppliedCount = records.Count(record => record.CleanupApplied),
            SnippetAppliedCount = records.Count(record => record.SnippetApplied),
            DictionaryCorrectionAppliedCount = records.Count(record => record.DictionaryCorrectionApplied),
            PromptActionAppliedCount = records.Count(record => record.PromptActionApplied),
            TranslationAppliedCount = records.Count(record => record.TranslationApplied),
            TopApps = topApps
        };
    }
}
