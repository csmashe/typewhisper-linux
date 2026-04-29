using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed class DictionaryService : IDictionaryService
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private List<DictionaryEntry> _cache = [];
    private bool _cacheLoaded;

    public IReadOnlyList<DictionaryEntry> Entries
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

    public event Action? EntriesChanged;

    public DictionaryService(string filePath)
    {
        _filePath = filePath;
    }

    public void AddEntry(DictionaryEntry entry)
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            _cache.Add(entry);
            SaveToDisk();
        }
        EntriesChanged?.Invoke();
    }

    public void AddEntries(IEnumerable<DictionaryEntry> entries)
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            _cache.AddRange(entries);
            SaveToDisk();
        }
        EntriesChanged?.Invoke();
    }

    public void UpdateEntry(DictionaryEntry entry)
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            var idx = _cache.FindIndex(e => e.Id == entry.Id);
            if (idx >= 0) _cache[idx] = entry;
            SaveToDisk();
        }
        EntriesChanged?.Invoke();
    }

    public void DeleteEntry(string id)
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            _cache.RemoveAll(e => e.Id == id);
            SaveToDisk();
        }
        EntriesChanged?.Invoke();
    }

    public void DeleteEntries(IEnumerable<string> ids)
    {
        EnsureCacheLoaded();
        var idSet = ids.ToHashSet();
        lock (_gate)
        {
            _cache.RemoveAll(e => idSet.Contains(e.Id));
            SaveToDisk();
        }
        EntriesChanged?.Invoke();
    }

    public string ApplyCorrections(string text)
    {
        EnsureCacheLoaded();
        List<DictionaryEntry> corrections;
        lock (_gate)
        {
            corrections = _cache
                .Where(e => e.IsEnabled && e.EntryType == DictionaryEntryType.Correction && e.Replacement is not null)
                .OrderByDescending(e => e.Priority)
                .ThenByDescending(e => e.IsStarred)
                .ThenByDescending(e => e.Original.Length)
                .ToList();
        }

        foreach (var entry in corrections)
        {
            var comparison = entry.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            if (text.Contains(entry.Original, comparison))
            {
                var pattern = Regex.Escape(entry.Original);
                var options = entry.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                var replaced = Regex.Replace(text, @"\b" + pattern + @"\b", entry.Replacement!, options);
                if (string.Equals(replaced, text, StringComparison.Ordinal))
                    continue;

                text = replaced;
                IncrementUsageCount(entry.Id);
            }
        }

        return text;
    }

    public string? GetTermsForPrompt()
    {
        EnsureCacheLoaded();
        var terms = GetEnabledTerms();

        if (terms.Count == 0) return null;
        return string.Join(", ", terms);
    }

    public IReadOnlyList<string> GetEnabledTerms()
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            return NormalizeTerms(_cache
                .Where(e => e.IsEnabled && e.EntryType == DictionaryEntryType.Term)
                .Select(e => e.Original));
        }
    }

    public void SetTerms(IEnumerable<string> terms, bool replaceExisting)
    {
        EnsureCacheLoaded();

        lock (_gate)
        {
            var normalized = NormalizeTerms(terms);
            var desiredByKey = normalized.ToDictionary(TermKey, term => term);
            var existingTerms = _cache.Where(e => e.EntryType == DictionaryEntryType.Term).ToList();

            foreach (var entry in existingTerms)
            {
                var key = TermKey(entry.Original);
                if (desiredByKey.ContainsKey(key))
                {
                    var idx = _cache.FindIndex(e => e.Id == entry.Id);
                    if (idx >= 0)
                        _cache[idx] = entry with { IsEnabled = true };
                }
                else if (replaceExisting)
                {
                    _cache.Remove(entry);
                }
            }

            var existingKeys = existingTerms.Select(e => TermKey(e.Original)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var term in normalized.Where(term => !existingKeys.Contains(TermKey(term))))
            {
                _cache.Add(new DictionaryEntry
                {
                    Id = Guid.NewGuid().ToString(),
                    EntryType = DictionaryEntryType.Term,
                    Original = term
                });
            }
            SaveToDisk();
        }
        EntriesChanged?.Invoke();
    }

    public void RemoveAllTerms()
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            _cache.RemoveAll(e => e.EntryType == DictionaryEntryType.Term);
            SaveToDisk();
        }
        EntriesChanged?.Invoke();
    }

    public void LearnCorrection(string original, string replacement)
    {
        EnsureCacheLoaded();

        lock (_gate)
        {
            var existing = _cache.FirstOrDefault(e =>
                e.EntryType == DictionaryEntryType.Correction &&
                e.Original.Equals(original, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                var idx = _cache.FindIndex(e => e.Id == existing.Id);
                if (idx >= 0)
                    _cache[idx] = existing with
                    {
                        Replacement = replacement,
                        UsageCount = existing.UsageCount + 1,
                        TimesCorrected = existing.TimesCorrected + 1,
                        LastCorrectedAt = DateTime.UtcNow
                    };
            }
            else
            {
                _cache.Add(new DictionaryEntry
                {
                    Id = Guid.NewGuid().ToString(),
                    EntryType = DictionaryEntryType.Correction,
                    Original = original,
                    Replacement = replacement,
                    TimesCorrected = 1,
                    LastCorrectedAt = DateTime.UtcNow,
                    Source = DictionaryEntrySource.CorrectionSuggestion
                });
            }

            SaveToDisk();
        }

        EntriesChanged?.Invoke();
    }

    public string ExportToCsv()
    {
        EnsureCacheLoaded();

        var sb = new StringBuilder();
        sb.AppendLine("EntryType,Original,Replacement,CaseSensitive,IsEnabled,IsStarred,Priority,Source");

        List<DictionaryEntry> entries;
        lock (_gate)
        {
            entries = _cache
                .OrderBy(e => e.EntryType)
                .ThenBy(e => e.Original, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        foreach (var entry in entries)
        {
            sb.Append(CsvEscape(entry.EntryType.ToString()));
            sb.Append(',');
            sb.Append(CsvEscape(entry.Original));
            sb.Append(',');
            sb.Append(CsvEscape(entry.Replacement ?? string.Empty));
            sb.Append(',');
            sb.Append(CsvEscape(entry.CaseSensitive.ToString()));
            sb.Append(',');
            sb.Append(CsvEscape(entry.IsEnabled.ToString()));
            sb.Append(',');
            sb.Append(CsvEscape(entry.IsStarred.ToString()));
            sb.Append(',');
            sb.Append(CsvEscape(entry.Priority.ToString()));
            sb.Append(',');
            sb.Append(CsvEscape(entry.Source.ToString()));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public int ImportFromCsv(string csv)
    {
        EnsureCacheLoaded();
        if (string.IsNullOrWhiteSpace(csv))
            return 0;

        var rows = ParseCsv(csv);
        if (rows.Count == 0)
            return 0;

        var imported = 0;

        lock (_gate)
        {
            var startIndex = LooksLikeHeader(rows[0]) ? 1 : 0;
            var existingKeys = _cache.Select(DictionaryEntryKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (var i = startIndex; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Count < 2)
                    continue;

                if (!Enum.TryParse<DictionaryEntryType>(row[0], ignoreCase: true, out var entryType))
                    continue;

                var original = row[1].Trim();
                if (string.IsNullOrWhiteSpace(original))
                    continue;

                var replacement = row.Count > 2 && !string.IsNullOrWhiteSpace(row[2])
                    ? row[2].Trim()
                    : null;

                if (entryType == DictionaryEntryType.Correction && string.IsNullOrWhiteSpace(replacement))
                    continue;

                var entry = new DictionaryEntry
                {
                    Id = Guid.NewGuid().ToString(),
                    EntryType = entryType,
                    Original = original,
                    Replacement = replacement,
                    CaseSensitive = ReadBool(row, 3),
                    IsEnabled = row.Count <= 4 || ReadBool(row, 4),
                    IsStarred = ReadBool(row, 5),
                    Priority = ReadInt(row, 6),
                    Source = ReadSource(row, 7)
                };

                if (!existingKeys.Add(DictionaryEntryKey(entry)))
                    continue;

                _cache.Add(entry);
                imported++;
            }

            if (imported > 0)
                SaveToDisk();
        }

        if (imported > 0)
            EntriesChanged?.Invoke();

        return imported;
    }

    public void ActivatePack(TermPack pack)
    {
        EnsureCacheLoaded();

        var changed = false;
        lock (_gate)
        {
            var existingPackIds = _cache
                .Where(e => e.EntryType == DictionaryEntryType.Term)
                .Select(e => e.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var newEntries = pack.Terms
                .Where(t => !existingPackIds.Contains($"pack:{pack.Id}:{t}"))
                .Select(t => new DictionaryEntry
                {
                    Id = $"pack:{pack.Id}:{t}",
                    EntryType = DictionaryEntryType.Term,
                    Original = t
                })
                .ToList();

            if (newEntries.Count > 0)
            {
                _cache.AddRange(newEntries);
                SaveToDisk();
                changed = true;
            }
        }

        if (changed)
            EntriesChanged?.Invoke();
    }

    public void DeactivatePack(string packId)
    {
        EnsureCacheLoaded();

        var changed = false;
        lock (_gate)
        {
            var prefix = $"pack:{packId}:";
            var removed = _cache.RemoveAll(e => e.Id.StartsWith(prefix, StringComparison.Ordinal));

            if (removed > 0)
            {
                SaveToDisk();
                changed = true;
            }
        }

        if (changed)
            EntriesChanged?.Invoke();
    }

    private void IncrementUsageCount(string id)
    {
        lock (_gate)
        {
            var idx = _cache.FindIndex(e => e.Id == id);
            if (idx >= 0)
            {
                _cache[idx] = _cache[idx] with
                {
                    UsageCount = _cache[idx].UsageCount + 1,
                    TimesApplied = _cache[idx].TimesApplied + 1,
                    LastUsedAt = DateTime.UtcNow
                };
                SaveToDisk();
            }
        }
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;

        lock (_gate)
        {
            if (_cacheLoaded) return;

            if (!File.Exists(_filePath))
            {
                _cacheLoaded = true;
                return;
            }

            string json;
            try
            {
                json = File.ReadAllText(_filePath);
            }
            catch (IOException ex)
            {
                System.Diagnostics.Trace.WriteLine($"[DictionaryService] Could not read cache file '{_filePath}': {ex.Message}");
                _cacheLoaded = true;
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Trace.WriteLine($"[DictionaryService] Could not read cache file '{_filePath}': {ex.Message}");
                _cacheLoaded = true;
                return;
            }

            try
            {
                _cache = JsonSerializer.Deserialize<List<DictionaryEntry>>(json) ?? [];
            }
            catch (JsonException)
            {
                PreserveBrokenFile(_filePath);
                _cache = [];
            }

            _cacheLoaded = true;
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

    private static void PreserveBrokenFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            var brokenPath = $"{path}.broken-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(path, brokenPath);
            System.Diagnostics.Trace.WriteLine($"[DictionaryService] Preserved unreadable file as {brokenPath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[DictionaryService] Could not preserve unreadable file: {ex.Message}");
        }
    }

    private static IReadOnlyList<string> NormalizeTerms(IEnumerable<string> terms)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();

        foreach (var rawTerm in terms)
        {
            var term = rawTerm.Trim();
            if (term.Length == 0)
                continue;

            if (seen.Add(TermKey(term)))
                normalized.Add(term);
        }

        return normalized;
    }

    private static string TermKey(string term) => term.Trim().ToUpperInvariant();

    private static string DictionaryEntryKey(DictionaryEntry entry) =>
        $"{entry.EntryType}|{entry.Original.Trim()}|{entry.Replacement?.Trim()}";

    private static bool LooksLikeHeader(IReadOnlyList<string> row) =>
        row.Count > 0 && string.Equals(row[0], "EntryType", StringComparison.OrdinalIgnoreCase);

    private static bool ReadBool(IReadOnlyList<string> row, int index) =>
        row.Count > index && bool.TryParse(row[index], out var value) && value;

    private static int ReadInt(IReadOnlyList<string> row, int index) =>
        row.Count > index && int.TryParse(row[index], out var value)
            ? Math.Clamp(value, 0, 999)
            : 0;

    private static DictionaryEntrySource ReadSource(IReadOnlyList<string> row, int index) =>
        row.Count > index && Enum.TryParse<DictionaryEntrySource>(row[index], ignoreCase: true, out var source)
            ? source
            : DictionaryEntrySource.Import;

    private static string CsvEscape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
            return value;

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static List<List<string>> ParseCsv(string csv)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];
            if (inQuotes)
            {
                if (ch == '"' && i + 1 < csv.Length && csv[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    field.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    if (row.Any(value => value.Length > 0))
                        rows.Add(row);
                    row = [];
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        row.Add(field.ToString());
        if (row.Any(value => value.Length > 0))
            rows.Add(row);

        return rows;
    }
}
