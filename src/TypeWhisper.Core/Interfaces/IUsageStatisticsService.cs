using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
/// Maintains usage aggregates independently of history retention, grouped by local calendar day.
/// </summary>
public interface IUsageStatisticsService
{
    /// <summary>
    /// Gets snapshots ordered by calendar day, including days whose history entries have expired.
    /// Persisted days retain their original calendar dates when the local time zone changes.
    /// </summary>
    IReadOnlyList<UsageStatisticsDaySnapshot> Days { get; }

    /// <summary>
    /// Gets whether any day contains recorded transcriptions, words, or duration.
    /// </summary>
    bool HasAnyStatistics { get; }

    /// <summary>
    /// Fires after a store update changes persisted state, including initial backfill completion
    /// with no records. Ignored records and repeated backfill attempts do not raise this event.
    /// </summary>
    event Action? StatisticsChanged;

    /// <summary>
    /// Records a transcription in its local calendar day and hour. Records without words or with
    /// negative or non-finite durations are ignored. Until backfill completes successfully, live
    /// records are ignored because they are already in history and will be imported by backfill.
    /// </summary>
    void RecordTranscription(TranscriptionRecord record);

    /// <summary>
    /// Imports valid history records once and persists completion, even for empty history.
    /// Live records are ignored until completion, including after skipped or failed backfill;
    /// a later successful backfill imports them from history. Run before history retention
    /// prunes records to avoid losing usage. Later calls leave the aggregates unchanged.
    /// </summary>
    void BackfillFromHistoryIfNeeded(IEnumerable<TranscriptionRecord> records);
}
