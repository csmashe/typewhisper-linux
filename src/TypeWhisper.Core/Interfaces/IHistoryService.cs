using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
///     Stores the transcription history, exposes running totals, and exports records
///     in several formats. Records are lazily loaded from disk on first use.
/// </summary>
public interface IHistoryService
{
    IReadOnlyList<TranscriptionRecord> Records { get; }

    int TotalRecords { get; }
    int TotalWords { get; }

    /// <summary>Total recorded audio duration across all records, in seconds.</summary>
    double TotalDuration { get; }

    void AddRecord(TranscriptionRecord record);

    /// <summary>Replaces the final text of a record (e.g. after the user edits it inline).</summary>
    void UpdateRecord(string id, string finalText);

    /// <summary>Attaches not-yet-applied correction suggestions to a record for later review.</summary>
    void SetPendingCorrectionSuggestions(
        string id,
        IReadOnlyList<CorrectionSuggestion> suggestions
    );

    void DeleteRecord(string id);
    void ClearAll();

    /// <summary>Case-insensitive search over record text.</summary>
    IReadOnlyList<TranscriptionRecord> Search(string query);

    /// <summary>Deletes records (and their audio) older than <paramref name="retention" />; a <c>null</c> retention keeps everything.</summary>
    void PurgeOldRecords(TimeSpan? retention);

    /// <summary>Lazily loads history from disk on first use; cheap and safe to call repeatedly.</summary>
    Task EnsureLoadedAsync();

    /// <summary>Distinct app names that appear in the history.</summary>
    IReadOnlyList<string> GetDistinctApps();

    string ExportToText(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null);
    string ExportToCsv(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null);

    string ExportToMarkdown(
        IReadOnlyList<TranscriptionRecord> records,
        ExportLabels? labels = null
    );

    string ExportToJson(IReadOnlyList<TranscriptionRecord> records);
    event Action? RecordsChanged;
}
