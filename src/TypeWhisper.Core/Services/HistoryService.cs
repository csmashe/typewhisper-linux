using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed class HistoryService : IHistoryService
{
    private readonly string? _audioDirectory;
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private List<TranscriptionRecord> _cache = [];

    private bool _cacheLoaded;

    // Set when the on-disk history file existed but couldn't be read.
    // SaveToDisk refuses to write while this is set so a transient read error
    // doesn't cause the next AddRecord call to replace the user's history
    // with a one-entry file.
    private bool _cacheLoadFailed;
    private List<string> _distinctApps = [];
    private double _totalDuration;

    private int _totalRecords;
    private int _totalWords;

    public HistoryService(string filePath, string? audioDirectory = null)
    {
        _filePath = filePath;
        _audioDirectory = audioDirectory;
    }

    public IReadOnlyList<TranscriptionRecord> Records
    {
        get
        {
            EnsureCacheLoaded();
            lock (_gate)
            {
                return _cache.ToList();
            }
        }
    }

    public event Action? RecordsChanged;

    // Fast path when already loaded; the fallback triggers a synchronous load via Records.
    public int TotalRecords => _cacheLoaded ? _totalRecords : Records.Count;
    public int TotalWords => _cacheLoaded ? _totalWords : Records.Sum(r => r.WordCount);

    public double TotalDuration =>
        _cacheLoaded ? _totalDuration : Records.Sum(r => r.DurationSeconds);

    public async Task EnsureLoadedAsync()
    {
        if (_cacheLoaded)
        {
            return;
        }

        await _loadLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cacheLoaded)
            {
                return;
            }

            var records = await Task.Run(LoadFromDisk).ConfigureAwait(false);
            lock (_gate)
            {
                _cache = records;
                RebuildStats();
                _cacheLoaded = true;
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public IReadOnlyList<string> GetDistinctApps()
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            return _distinctApps.ToList();
        }
    }

    public void AddRecord(TranscriptionRecord record)
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            // Stage on a copy and persist before mutating _cache/stats so a save
            // failure can't leave the in-memory state ahead of disk.
            var newCache = new List<TranscriptionRecord>(_cache.Count + 1) { record };
            newCache.AddRange(_cache);
            SaveToDisk(newCache);

            _cache = newCache;
            _totalRecords++;
            _totalWords += record.WordCount;
            _totalDuration += record.DurationSeconds;
            if (
                !string.IsNullOrEmpty(record.AppProcessName)
                && !_distinctApps.Contains(record.AppProcessName, StringComparer.OrdinalIgnoreCase)
            )
            {
                _distinctApps.Add(record.AppProcessName);
                _distinctApps.Sort(StringComparer.OrdinalIgnoreCase);
            }
        }

        RecordsChanged?.Invoke();
    }

    public void UpdateRecord(string id, string finalText)
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            var idx = _cache.FindIndex(r => r.Id == id);
            if (idx < 0)
            {
                return;
            }

            var old = _cache[idx];
            var updated = old with { FinalText = finalText };
            var newCache = new List<TranscriptionRecord>(_cache);
            newCache[idx] = updated;
            SaveToDisk(newCache);

            _cache = newCache;
            _totalWords += updated.WordCount - old.WordCount;
        }

        RecordsChanged?.Invoke();
    }

    public void SetPendingCorrectionSuggestions(
        string id,
        IReadOnlyList<CorrectionSuggestion> suggestions
    )
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            var idx = _cache.FindIndex(r => r.Id == id);
            if (idx < 0)
            {
                return;
            }

            var newCache = new List<TranscriptionRecord>(_cache);
            newCache[idx] = newCache[idx] with
            {
                PendingCorrectionSuggestions = suggestions.ToList()
            };
            SaveToDisk(newCache);

            _cache = newCache;
        }

        RecordsChanged?.Invoke();
    }

    public void DeleteRecord(string id)
    {
        EnsureCacheLoaded();
        string? removedAudioFileName;
        lock (_gate)
        {
            var idx = _cache.FindIndex(r => r.Id == id);
            if (idx < 0)
            {
                return;
            }

            var removed = _cache[idx];
            var newCache = new List<TranscriptionRecord>(_cache);
            newCache.RemoveAt(idx);
            SaveToDisk(newCache);

            _cache = newCache;
            _totalRecords--;
            _totalWords -= removed.WordCount;
            _totalDuration -= removed.DurationSeconds;
            removedAudioFileName = removed.AudioFileName;

            RebuildDistinctApps();
        }

        DeleteAudioFile(removedAudioFileName);
        RecordsChanged?.Invoke();
    }

    public void ClearAll()
    {
        EnsureCacheLoaded();
        List<string?> audioFiles;
        lock (_gate)
        {
            // Collect audio file names from current cache, then persist the
            // empty list. Only commit the in-memory clear if save succeeds —
            // otherwise the file deletes below would orphan records that are
            // still in history.json.
            audioFiles = _cache.Select(r => r.AudioFileName).ToList();
            SaveToDisk([]);

            _cache.Clear();
            _totalRecords = 0;
            _totalWords = 0;
            _totalDuration = 0;
            _distinctApps.Clear();
        }

        DeleteAudioFiles(audioFiles);
        RecordsChanged?.Invoke();
    }

    public IReadOnlyList<TranscriptionRecord> Search(string query)
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return _cache.ToList();
            }

            return _cache
                .Where(r =>
                    r.FinalText.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || r.RawText.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || (r.AppName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                )
                .ToList();
        }
    }

    public void PurgeOldRecords(TimeSpan? retention)
    {
        if (retention is null)
        {
            return;
        }

        EnsureCacheLoaded();
        var cutoff = DateTime.UtcNow - retention.Value;
        List<string?> removedAudioFiles;

        lock (_gate)
        {
            removedAudioFiles = _cache
                .Where(r => r.CreatedAt < cutoff)
                .Select(r => r.AudioFileName)
                .ToList();

            if (removedAudioFiles.Count == 0)
            {
                return;
            }

            var newCache = _cache.Where(r => r.CreatedAt >= cutoff).ToList();
            SaveToDisk(newCache);

            _cache = newCache;
            RebuildStats();
        }

        DeleteAudioFiles(removedAudioFiles);
        RecordsChanged?.Invoke();
    }

    public string ExportToText(
        IReadOnlyList<TranscriptionRecord> records,
        ExportLabels? labels = null
    )
    {
        var l = labels ?? ExportLabels.Default;
        var sb = new StringBuilder();
        sb.AppendLine(l.Header);
        sb.AppendLine($"{l.Exported}: {DateTime.Now:dd.MM.yyyy HH:mm}");
        sb.AppendLine($"{l.Entries}: {records.Count}");
        sb.AppendLine(new string('─', 60));
        sb.AppendLine();

        foreach (var r in records)
        {
            sb.AppendLine(
                $"[{r.Timestamp:dd.MM.yyyy HH:mm}] {r.AppProcessName ?? "–"} ({r.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture)}s)"
            );
            sb.AppendLine(r.FinalText);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string ExportToCsv(
        IReadOnlyList<TranscriptionRecord> records,
        ExportLabels? labels = null
    )
    {
        var l = labels ?? ExportLabels.Default;
        var sb = new StringBuilder();
        sb.AppendLine(
            string.Join(
                ',',
                CsvEscape(l.Timestamp),
                CsvEscape(l.App),
                CsvEscape(l.Text),
                CsvEscape(l.Duration),
                CsvEscape(l.Words),
                CsvEscape(l.Language)
            )
        );

        foreach (var r in records)
        {
            sb.AppendLine(
                string.Join(
                    ',',
                    CsvEscape(
                        r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    ),
                    CsvEscape(r.AppProcessName ?? ""),
                    CsvEscape(r.FinalText),
                    CsvEscape(r.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture)),
                    CsvEscape(r.WordCount.ToString(CultureInfo.InvariantCulture)),
                    CsvEscape(r.Language ?? "")
                )
            );
        }

        return sb.ToString();
    }

    public string ExportToMarkdown(
        IReadOnlyList<TranscriptionRecord> records,
        ExportLabels? labels = null
    )
    {
        var l = labels ?? ExportLabels.Default;
        var sb = new StringBuilder();
        sb.AppendLine($"# {l.Header}");
        sb.AppendLine();
        sb.AppendLine($"- **{l.Exported}:** {DateTime.Now:dd.MM.yyyy HH:mm}");
        sb.AppendLine($"- **{l.Entries}:** {records.Count}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var r in records)
        {
            sb.AppendLine($"## {r.Timestamp:dd.MM.yyyy HH:mm}");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(r.AppProcessName))
            {
                sb.AppendLine($"- **{l.App}:** {r.AppProcessName}");
            }

            sb.AppendLine(
                $"- **{l.Duration}:** {r.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture)}s"
            );
            if (!string.IsNullOrEmpty(r.Language))
            {
                sb.AppendLine($"- **{l.Language}:** {r.Language}");
            }

            sb.AppendLine();
            sb.AppendLine(r.FinalText);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string ExportToJson(IReadOnlyList<TranscriptionRecord> records)
    {
        var data = records.Select(r => new
        {
            id = r.Id,
            timestamp = r.Timestamp.ToString("o"),
            text = r.FinalText,
            raw_text = r.RawText,
            app = r.AppProcessName,
            duration_seconds = r.DurationSeconds,
            language = r.Language,
            engine = r.EngineUsed,
            model = r.ModelUsed,
            profile = r.ProfileName,
            insertion_status = r.InsertionStatus.ToString(),
            insertion_failure_reason = r.InsertionFailureReason,
            words = r.WordCount
        });

        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }

    // Synchronous fallback for callers that cannot await EnsureLoadedAsync.
    // Do not call this from a thread that may already hold _loadLock — it will deadlock.
    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded)
        {
            return;
        }

        _loadLock.Wait();
        try
        {
            if (_cacheLoaded)
            {
                return;
            }

            var records = LoadFromDisk();
            lock (_gate)
            {
                _cache = records;
                RebuildStats();
                _cacheLoaded = true;
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private List<TranscriptionRecord> LoadFromDisk()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        string json;
        try
        {
            json = File.ReadAllText(_filePath);
        }
        catch (Exception ex)
        {
            // File exists but is unreadable (transient IO / permission). Set
            // _cacheLoadFailed so SaveToDisk refuses to overwrite — otherwise
            // the next AddRecord would replace the user's whole history with
            // a single record.
            Trace.WriteLine($"[HistoryService] Failed to read history from '{_filePath}': {ex}");
            _cacheLoadFailed = true;
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<TranscriptionRecord>>(json) ?? [];
        }
        catch (JsonException ex)
        {
            Trace.WriteLine($"[HistoryService] Failed to parse history from '{_filePath}': {ex}");
            PreserveBrokenFile(_filePath);
            return [];
        }
    }

    private void SaveToDisk(IReadOnlyList<TranscriptionRecord> records)
    {
        if (_cacheLoadFailed)
        {
            Trace.WriteLine(
                $"[HistoryService] Skipping save to '{_filePath}': previous load failed and overwriting would discard existing data."
            );
            throw new IOException(
                $"Refusing to overwrite '{_filePath}' because the previous load failed."
            );
        }

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(
            records,
            new JsonSerializerOptions { WriteIndented = true }
        );

        // Write to a sibling temp file and atomically replace, so a crash
        // mid-write can't leave history truncated.
        var tempPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            if (File.Exists(_filePath))
                File.Replace(tempPath, _filePath, destinationBackupFileName: null);
            else
                File.Move(tempPath, _filePath);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { /* best effort */ }
            }
            throw;
        }
    }

    private static string CsvEscape(string value)
    {
        if (
            !value.Contains(',')
            && !value.Contains('"')
            && !value.Contains('\n')
            && !value.Contains('\r')
        )
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static void PreserveBrokenFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var brokenPath = $"{path}.broken-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(path, brokenPath);
            Trace.WriteLine(
                $"[HistoryService] Preserved unreadable file as {brokenPath}"
            );
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[HistoryService] Could not preserve unreadable file: {ex.Message}"
            );
        }
    }

    private void RebuildStats()
    {
        _totalRecords = _cache.Count;
        _totalWords = _cache.Sum(r => r.WordCount);
        _totalDuration = _cache.Sum(r => r.DurationSeconds);
        RebuildDistinctApps();
    }

    private void RebuildDistinctApps()
    {
        _distinctApps = _cache
            .Select(r => r.AppProcessName)
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order()
            .ToList()!;
    }

    private void DeleteAudioFile(string? audioFileName)
    {
        if (string.IsNullOrEmpty(audioFileName) || string.IsNullOrEmpty(_audioDirectory))
        {
            return;
        }

        try
        {
            // Producers (DictationOrchestrator) write Path.GetFileName(...) so
            // this is normally just a bare filename. But history.json can be
            // hand-edited, so treat audioFileName as untrusted: strip any
            // directory components and confirm the resolved path stays inside
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

            var candidate = Path.GetFullPath(Path.Combine(_audioDirectory, safeName));
            if (!candidate.StartsWith(directoryRoot, StringComparison.Ordinal))
            {
                return;
            }

            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
        catch { }
    }

    private void DeleteAudioFiles(IEnumerable<string?> audioFileNames)
    {
        foreach (var audioFileName in audioFileNames)
        {
            DeleteAudioFile(audioFileName);
        }
    }
}