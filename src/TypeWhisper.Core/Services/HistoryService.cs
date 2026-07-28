using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
///     File-backed <see cref="IHistoryService" />: persists transcription records as JSON, caches
///     them with running totals, and deletes the associated audio files when records are removed.
/// </summary>
public sealed partial class HistoryService : IHistoryService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly string? _audioDirectory;
    private readonly AtomicJsonStore<ImmutableArray<TranscriptionRecord>> _store;

    public HistoryService(string filePath, string? audioDirectory = null)
    {
        _audioDirectory = audioDirectory;
        _store = new AtomicJsonStore<ImmutableArray<TranscriptionRecord>>(
            filePath,
            static () => [],
            new AtomicJsonStoreOptions<ImmutableArray<TranscriptionRecord>>
            {
                JsonOptions = s_jsonOptions,
                CorruptFilePolicy = AtomicJsonCorruptFilePolicy.PreserveAndReset,
                Deserialize = json =>
                {
                    var records = JsonSerializer.Deserialize<ImmutableArray<TranscriptionRecord>>(
                        json,
                        s_jsonOptions
                    );
                    return records.IsDefault
                        ? throw new JsonException("History JSON deserialized to null.")
                        : records;
                },
                Diagnostic = diagnostic =>
                    Trace.WriteLine(
                        $"[HistoryService] {diagnostic.Kind} at '{diagnostic.Path}'."
                    ),
            }
        );
    }

    public IReadOnlyList<TranscriptionRecord> Records => _store.Current.ToArray();

    public event Action? RecordsChanged;

    public int TotalRecords => _store.Current.Length;
    public int TotalWords => _store.Current.Sum(r => r.WordCount);
    public double TotalDuration => _store.Current.Sum(r => r.DurationSeconds);

    public async Task EnsureLoadedAsync()
    {
        _ = await Task.Run(() => _store.Current).ConfigureAwait(false);
    }

    public IReadOnlyList<string> GetDistinctApps()
    {
        return _store.Current
            .Select(r => r.AppProcessName)
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    public void AddRecord(TranscriptionRecord record)
    {
        _store.Update(current => current.Insert(0, record));
        RecordsChanged?.Invoke();
    }

    public void UpdateRecord(string id, string finalText)
    {
        var changed = false;
        _store.Update(
            current =>
            {
                var idx = FindIndex(current, r => r.Id == id);
            if (idx < 0)
            {
                    return current;
            }

                changed = true;
                var old = current[idx];
            var updated = old with { FinalText = finalText };
                return current.SetItem(idx, updated);
            }
        );

        if (changed)
        {
            RecordsChanged?.Invoke();
        }
    }

    public void SetPendingCorrectionSuggestions(
        string id,
        IReadOnlyList<CorrectionSuggestion> suggestions
    )
    {
        var changed = false;
        _store.Update(
            current =>
            {
                var idx = FindIndex(current, r => r.Id == id);
                if (idx < 0)
                {
                    return current;
                }

                changed = true;
                return current.SetItem(
                    idx,
                    current[idx] with { PendingCorrectionSuggestions = suggestions.ToList() }
                );
            }
        );

        if (changed)
        {
            RecordsChanged?.Invoke();
        }
    }

    public void DeleteRecord(string id)
    {
        string? removedAudioFileName = null;
        var changed = false;
        _store.Update(
            current =>
            {
                var idx = FindIndex(current, r => r.Id == id);
                if (idx < 0)
                {
                    return current;
                }

                changed = true;
                removedAudioFileName = current[idx].AudioFileName;
                return current.RemoveAt(idx);
            }
        );

        if (!changed)
        {
            return;
        }

        DeleteAudioFile(removedAudioFileName);
        RecordsChanged?.Invoke();
    }

    public void ClearAll()
    {
        List<string?> audioFiles = [];
        var changed = false;
        _store.Update(
            current =>
            {
                if (current.IsEmpty)
                {
                    return current;
                }

                changed = true;
                audioFiles = current.Select(r => r.AudioFileName).ToList();
                return [];
            }
        );

        if (!changed)
        {
            return;
        }

        DeleteAudioFiles(audioFiles);
        RecordsChanged?.Invoke();
    }

    public IReadOnlyList<TranscriptionRecord> Search(string query)
    {
        var records = _store.Current;
        if (string.IsNullOrWhiteSpace(query))
        {
            return records.ToArray();
        }

        return records
            .Where(r =>
                r.FinalText.Contains(query, StringComparison.OrdinalIgnoreCase)
                || r.RawText.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (r.AppName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            )
            .ToList();
    }

    public void PurgeOldRecords(TimeSpan? retention)
    {
        if (retention is null)
        {
            return;
        }

        var cutoff = DateTime.UtcNow - retention.Value;
        List<string?> removedAudioFiles = [];
        var changed = false;
        _store.Update(
            current =>
            {
                removedAudioFiles = current
                    .Where(r => r.CreatedAt < cutoff)
                    .Select(r => r.AudioFileName)
                    .ToList();
                if (removedAudioFiles.Count == 0)
                {
                    return current;
                }

                changed = true;
                return [.. current.Where(r => r.CreatedAt >= cutoff)];
            }
        );

        if (!changed)
        {
            return;
        }

        DeleteAudioFiles(removedAudioFiles);
        RecordsChanged?.Invoke();
    }

    private static int FindIndex(
        ImmutableArray<TranscriptionRecord> records,
        Func<TranscriptionRecord, bool> predicate
    )
    {
        for (var index = 0; index < records.Length; index++)
        {
            if (predicate(records[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private void DeleteAudioFile(string? audioFileName)
    {
        if (string.IsNullOrEmpty(audioFileName) || string.IsNullOrEmpty(_audioDirectory))
        {
            return;
        }

        try
        {
            // history.json can be hand-edited, so treat audioFileName as untrusted:
            // strip directory components and confirm the resolved path stays inside
            // _audioDirectory before deleting.
            var safeName = Path.GetFileName(audioFileName);
            if (string.IsNullOrEmpty(safeName) || Path.IsPathRooted(audioFileName))
            {
                return;
            }

            var directoryRoot = Path.GetFullPath(_audioDirectory);
            var separator = Path.DirectorySeparatorChar;
            if (!directoryRoot.EndsWith(separator))
            {
                directoryRoot += separator;
            }

            var candidate = Path.GetFullPath(Path.Join(_audioDirectory, safeName));
            if (!candidate.StartsWith(directoryRoot, StringComparison.Ordinal))
            {
                return;
            }

            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
        catch (Exception ex)
        {
            // Best-effort cleanup: a locked file or permission error shouldn't fail the
            // record delete, but log it so a recurring problem (e.g. orphaned audio) is visible.
            Trace.WriteLine(
                $"[HistoryService] Could not delete audio file '{audioFileName}': {ex.Message}"
            );
        }
    }

    private void DeleteAudioFiles(IEnumerable<string?> audioFileNames)
    {
        foreach (var audioFileName in audioFileNames)
        {
            DeleteAudioFile(audioFileName);
        }
    }
}
