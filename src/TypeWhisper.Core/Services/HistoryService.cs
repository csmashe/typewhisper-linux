using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed class HistoryService : IHistoryService
{
    private readonly string _filePath;
    private List<TranscriptionRecord> _cache = [];
    private bool _cacheLoaded;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    private int _totalRecords;
    private int _totalWords;
    private double _totalDuration;
    private List<string> _distinctApps = [];

    public IReadOnlyList<TranscriptionRecord> Records
    {
        get
        {
            EnsureCacheLoaded();
            return _cache;
        }
    }

    public event Action? RecordsChanged;

    public int TotalRecords => _cacheLoaded ? _totalRecords : Records.Count;
    public int TotalWords => _cacheLoaded ? _totalWords : Records.Sum(r => r.WordCount);
    public double TotalDuration => _cacheLoaded ? _totalDuration : Records.Sum(r => r.DurationSeconds);

    public HistoryService(string filePath)
    {
        _filePath = filePath;
    }

    public async Task EnsureLoadedAsync()
    {
        if (_cacheLoaded) return;

        await _loadLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cacheLoaded) return;
            var records = await Task.Run(LoadFromDisk).ConfigureAwait(false);
            _cache = records;
            RebuildStats();
            _cacheLoaded = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public IReadOnlyList<string> GetDistinctApps()
    {
        EnsureCacheLoaded();
        return _distinctApps;
    }

    public void AddRecord(TranscriptionRecord record)
    {
        EnsureCacheLoaded();
        _cache.Insert(0, record);

        _totalRecords++;
        _totalWords += record.WordCount;
        _totalDuration += record.DurationSeconds;
        if (!string.IsNullOrEmpty(record.AppProcessName) &&
            !_distinctApps.Contains(record.AppProcessName, StringComparer.OrdinalIgnoreCase))
        {
            _distinctApps.Add(record.AppProcessName);
            _distinctApps.Sort(StringComparer.OrdinalIgnoreCase);
        }

        SaveToDisk();
        RecordsChanged?.Invoke();
    }

    public void UpdateRecord(string id, string finalText)
    {
        EnsureCacheLoaded();
        var idx = _cache.FindIndex(r => r.Id == id);
        if (idx >= 0)
        {
            var old = _cache[idx];
            var updated = old with { FinalText = finalText };
            _cache[idx] = updated;
            _totalWords += updated.WordCount - old.WordCount;
        }

        SaveToDisk();
        RecordsChanged?.Invoke();
    }

    public void DeleteRecord(string id)
    {
        EnsureCacheLoaded();
        var idx = _cache.FindIndex(r => r.Id == id);
        if (idx >= 0)
        {
            var removed = _cache[idx];
            _cache.RemoveAt(idx);
            _totalRecords--;
            _totalWords -= removed.WordCount;
            _totalDuration -= removed.DurationSeconds;
        }

        RebuildDistinctApps();
        SaveToDisk();
        RecordsChanged?.Invoke();
    }

    public void ClearAll()
    {
        _cache.Clear();
        _totalRecords = 0;
        _totalWords = 0;
        _totalDuration = 0;
        _distinctApps.Clear();
        SaveToDisk();
        RecordsChanged?.Invoke();
    }

    public IReadOnlyList<TranscriptionRecord> Search(string query)
    {
        EnsureCacheLoaded();
        if (string.IsNullOrWhiteSpace(query)) return _cache;

        return _cache.Where(r =>
            r.FinalText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            r.RawText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (r.AppName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
        ).ToList();
    }

    public void PurgeOldRecords(int retentionDays)
    {
        EnsureCacheLoaded();
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var removed = _cache.RemoveAll(r => r.CreatedAt < cutoff);

        if (removed > 0)
        {
            RebuildStats();
            SaveToDisk();
            RecordsChanged?.Invoke();
        }
    }

    public string ExportToText(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null)
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
            sb.AppendLine($"[{r.Timestamp:dd.MM.yyyy HH:mm}] {r.AppProcessName ?? "–"} ({r.DurationSeconds:F1}s)");
            sb.AppendLine(r.FinalText);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string ExportToCsv(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null)
    {
        var l = labels ?? ExportLabels.Default;
        var sb = new StringBuilder();
        sb.AppendLine($"{l.Timestamp},{l.App},{l.Text},{l.Duration},{l.Words},{l.Language}");

        foreach (var r in records)
        {
            var text = "\"" + r.FinalText.Replace("\"", "\"\"") + "\"";
            sb.AppendLine($"{r.Timestamp:yyyy-MM-dd HH:mm:ss},{r.AppProcessName ?? ""},{text},{r.DurationSeconds:F1},{r.WordCount},{r.Language ?? ""}");
        }

        return sb.ToString();
    }

    public string ExportToMarkdown(IReadOnlyList<TranscriptionRecord> records, ExportLabels? labels = null)
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
                sb.AppendLine($"- **{l.App}:** {r.AppProcessName}");
            sb.AppendLine($"- **{l.Duration}:** {r.DurationSeconds:F1}s");
            if (!string.IsNullOrEmpty(r.Language))
                sb.AppendLine($"- **{l.Language}:** {r.Language}");
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
            words = r.WordCount
        });

        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;

        _loadLock.Wait();
        try
        {
            if (_cacheLoaded) return;
            _cache = LoadFromDisk();
            RebuildStats();
            _cacheLoaded = true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private List<TranscriptionRecord> LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_filePath)) return [];

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<TranscriptionRecord>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveToDisk()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch { }
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
}
