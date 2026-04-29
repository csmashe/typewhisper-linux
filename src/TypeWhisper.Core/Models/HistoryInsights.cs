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
    public IReadOnlyList<AppUsageInsight> TopApps { get; init; } = [];
}

public sealed record AppUsageInsight(
    string AppProcessName,
    int RecordCount,
    int WordCount);
