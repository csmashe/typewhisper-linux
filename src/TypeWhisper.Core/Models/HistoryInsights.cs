namespace TypeWhisper.Core.Models;

public sealed record HistoryInsights
{
    public int TotalRecords { get; init; }
    public int TotalWords { get; init; }
    public double AverageWordsPerDictation { get; init; }
    public double AverageDurationSeconds { get; init; }
    public int PastedCount { get; init; }
    public int TypedCount { get; init; }
    public int CopiedToClipboardCount { get; init; }
    public int FailedInsertionCount { get; init; }
    public int SuccessfulInsertionCount { get; init; }
    public int InsertionAttemptCount { get; init; }
    public double InsertionSuccessRate { get; init; }
    public int CleanupAppliedCount { get; init; }
    public int SnippetAppliedCount { get; init; }
    public int DictionaryCorrectionAppliedCount { get; init; }
    public int PromptActionAppliedCount { get; init; }
    public int TranslationAppliedCount { get; init; }
    public IReadOnlyList<AppUsageInsight> TopApps { get; init; } = [];
}

public sealed record AppUsageInsight(
    string AppProcessName,
    int RecordCount,
    int WordCount);
