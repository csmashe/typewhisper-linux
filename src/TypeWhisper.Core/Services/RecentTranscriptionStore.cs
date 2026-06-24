using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
///     In-memory, capped store of the current session's transcriptions, and merges them with
///     persisted history records (session entries winning ties) for the recent-transcriptions palette.
/// </summary>
public sealed class RecentTranscriptionStore
{
    private readonly Lock _gate = new();
    private readonly int _maxSessionEntries;
    private readonly List<RecentTranscriptionEntry> _sessionEntries = [];

    public RecentTranscriptionStore(int maxSessionEntries = 20)
    {
        _maxSessionEntries = Math.Max(1, maxSessionEntries);
    }

    public IReadOnlyList<RecentTranscriptionEntry> SessionEntries
    {
        get
        {
            lock (_gate)
            {
                return _sessionEntries.ToList();
            }
        }
    }

    public void RecordTranscription(
        string id,
        string finalText,
        DateTime timestamp,
        string? appName,
        string? appProcessName
    )
    {
        var trimmedText = finalText.Trim();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(trimmedText))
        {
            return;
        }

        lock (_gate)
        {
            _sessionEntries.RemoveAll(entry =>
                string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase)
            );
            _sessionEntries.Insert(
                0,
                new RecentTranscriptionEntry(
                    id,
                    trimmedText,
                    timestamp,
                    appName,
                    appProcessName,
                    RecentTranscriptionSource.Session
                )
            );

            if (_sessionEntries.Count > _maxSessionEntries)
            {
                _sessionEntries.RemoveRange(
                    _maxSessionEntries,
                    _sessionEntries.Count - _maxSessionEntries
                );
            }
        }
    }

    /// <summary>
    ///     Returns the most-recent transcriptions, merging in-memory session entries with
    ///     persisted history records. Session entries win on tie-breaks (same timestamp) so
    ///     the most up-to-date version of a record is always shown. Duplicate IDs are dropped.
    /// </summary>
    public IReadOnlyList<RecentTranscriptionEntry> MergedEntries(
        IReadOnlyList<TranscriptionRecord> historyRecords,
        int limit = 12
    )
    {
        if (limit <= 0)
        {
            return [];
        }

        List<RecentTranscriptionEntry> sessionSnapshot;
        lock (_gate)
        {
            sessionSnapshot = _sessionEntries.ToList();
        }

        var historyEntries = historyRecords
            .Where(record => !string.IsNullOrWhiteSpace(record.FinalText))
            .Select(record => new RecentTranscriptionEntry(
                record.Id,
                record.FinalText.Trim(),
                record.Timestamp,
                record.AppName,
                record.AppProcessName,
                RecentTranscriptionSource.History
            ));

        var merged = sessionSnapshot
            .Concat(historyEntries)
            .OrderByDescending(entry => entry.Timestamp)
            .ThenBy(entry => entry.Source == RecentTranscriptionSource.Session ? 0 : 1)
            .ToList();

        // seen.Add returns false for subsequent encounters of the same ID, effectively keeping
        // only the first (highest-priority) occurrence in the sorted list.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return merged.Where(entry => seen.Add(entry.Id)).Take(limit).ToList();
    }

    public RecentTranscriptionEntry? LatestEntry(
        IReadOnlyList<TranscriptionRecord> historyRecords
    )
    {
        var merged = MergedEntries(historyRecords, 1);
        return merged.Count > 0 ? merged[0] : null;
    }
}