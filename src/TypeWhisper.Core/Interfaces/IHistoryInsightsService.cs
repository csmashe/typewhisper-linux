using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
///     Computes aggregate statistics (totals, top apps, trends) over transcription
///     history for the dashboard.
/// </summary>
public interface IHistoryInsightsService
{
    /// <summary>
    ///     Builds dashboard insights from <paramref name="records" />, limiting the
    ///     per-app breakdown to the top <paramref name="topAppCount" /> apps.
    /// </summary>
    HistoryInsights Build(IReadOnlyList<TranscriptionRecord> records, int topAppCount = 5);
}
