namespace TypeWhisper.Core.Models;

/// <summary>Per-application rollup (dictation and word counts) used for the "top apps" list in <see cref="HistoryInsights.TopApps" />.</summary>
public sealed record AppUsageInsight(string AppProcessName, int RecordCount, int WordCount);
