using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed class DictionaryService : IDictionaryService
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private List<DictionaryEntry> _cache = [];

    private bool _cacheLoaded;

    // Set when the cache file exists but couldn't be read (IO / permission error).
    // SaveToDisk refuses to write while this is set so we don't replace the
    // user's on-disk dictionary with an empty list we got from a failed load.
    private bool _cacheLoadFailed;

    public DictionaryService(string filePath)
    {
        _filePath = filePath;
    }

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

    public void AddEntry(DictionaryEntry entry)
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            var newCache = new List<DictionaryEntry>(_cache) { entry };
            SaveToDisk(newCache);
            _cache = newCache;
        }

        EntriesChanged?.Invoke();
    }

    public void AddEntries(IEnumerable<DictionaryEntry> entries)
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            var newCache = new List<DictionaryEntry>(_cache);
            newCache.AddRange(entries);
            SaveToDisk(newCache);
            _cache = newCache;
        }

        EntriesChanged?.Invoke();
    }

    public void UpdateEntry(DictionaryEntry entry)
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            var newCache = new List<DictionaryEntry>(_cache);
            var idx = newCache.FindIndex(e => e.Id == entry.Id);
            if (idx >= 0)
            {
                newCache[idx] = entry;
            }

            SaveToDisk(newCache);
            _cache = newCache;
        }

        EntriesChanged?.Invoke();
    }

    public void DeleteEntry(string id)
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            var newCache = new List<DictionaryEntry>(_cache);
            newCache.RemoveAll(e => e.Id == id);
            SaveToDisk(newCache);
            _cache = newCache;
        }

        EntriesChanged?.Invoke();
    }

    public void DeleteEntries(IEnumerable<string> ids)
    {
        EnsureCacheLoaded();
        var idSet = ids.ToHashSet();
        lock (_gate)
        {
            var newCache = new List<DictionaryEntry>(_cache);
            newCache.RemoveAll(e => idSet.Contains(e.Id));
            SaveToDisk(newCache);
            _cache = newCache;
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
                .Where(e =>
                    e.IsEnabled
                    && e.EntryType == DictionaryEntryType.Correction
                    && !string.IsNullOrEmpty(e.Original)
                    && e.Replacement is not null
                )
                .OrderByDescending(e => e.Priority)
                .ThenByDescending(e => e.IsStarred)
                .ThenByDescending(e => e.Original.Length)
                .ToList();
        }

        // Accumulate usage increments and persist once at the end. A per-match
        // SaveToDisk would mean N file writes for a transcript with N
        // corrections, which adds noticeable I/O to every dictation.
        var usedIds = new List<string>();

        foreach (var entry in corrections)
        {
            var comparison = entry.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            if (text.Contains(entry.Original, comparison))
            {
                var pattern = Regex.Escape(entry.Original);
                var options = entry.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                // \b only anchors between a word char and a non-word char, so a
                // bare \b on each side silently fails for originals like "C#" or
                // ".NET" whose ends are non-word. Anchor each side based on what
                // the original actually starts/ends with: \b on word ends, and a
                // string/non-word lookaround on symbol ends.
                var prefix = char.IsLetterOrDigit(entry.Original[0]) || entry.Original[0] == '_'
                    ? @"\b"
                    : @"(?<=\W|^)";
                var lastChar = entry.Original[^1];
                var suffix = char.IsLetterOrDigit(lastChar) || lastChar == '_'
                    ? @"\b"
                    : @"(?=\W|$)";
                // Use the MatchEvaluator overload so dollar sequences in
                // user-supplied replacements (e.g. "$1", "$&") are inserted
                // verbatim rather than interpreted as regex substitution tokens.
                var replacement = entry.Replacement!;
                var replaced = Regex.Replace(
                    text,
                    prefix + pattern + suffix,
                    _ => replacement,
                    options
                );
                if (string.Equals(replaced, text, StringComparison.Ordinal))
                {
                    continue;
                }

                text = replaced;
                usedIds.Add(entry.Id);
            }
        }

        if (usedIds.Count > 0)
        {
            IncrementUsageCounts(usedIds);
        }

        return text;
    }

    public string? GetTermsForPrompt()
    {
        EnsureCacheLoaded();
        var terms = GetEnabledTerms();

        if (terms.Count == 0)
        {
            return null;
        }

        return string.Join(", ", terms);
    }

    public IReadOnlyList<string> GetEnabledTerms()
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            return NormalizeTerms(
                _cache
                    .Where(e => e.IsEnabled && e.EntryType == DictionaryEntryType.Term)
                    .Select(e => e.Original)
            );
        }
    }

    public void SetTerms(IEnumerable<string> terms, bool replaceExisting)
    {
        EnsureCacheLoaded();

        lock (_gate)
        {
            var newCache = new List<DictionaryEntry>(_cache);
            var normalized = NormalizeTerms(terms);
            var desiredByKey = normalized.ToDictionary(TermKey, term => term);
            var existingTerms = newCache.Where(e => e.EntryType == DictionaryEntryType.Term).ToList();

            foreach (var entry in existingTerms)
            {
                var key = TermKey(entry.Original);
                if (desiredByKey.ContainsKey(key))
                {
                    var idx = newCache.FindIndex(e => e.Id == entry.Id);
                    if (idx >= 0)
                    {
                        newCache[idx] = entry with { IsEnabled = true };
                    }
                }
                else if (replaceExisting)
                {
                    newCache.Remove(entry);
                }
            }

            var existingKeys = existingTerms
                .Select(e => TermKey(e.Original))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var term in normalized.Where(term => !existingKeys.Contains(TermKey(term))))
            {
                newCache.Add(
                    new DictionaryEntry
                    {
                        Id = Guid.NewGuid().ToString(),
                        EntryType = DictionaryEntryType.Term,
                        Original = term
                    }
                );
            }

            SaveToDisk(newCache);
            _cache = newCache;
        }

        EntriesChanged?.Invoke();
    }

    public void RemoveAllTerms()
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            var newCache = _cache.Where(e => e.EntryType != DictionaryEntryType.Term).ToList();
            SaveToDisk(newCache);
            _cache = newCache;
        }

        EntriesChanged?.Invoke();
    }

    public bool DeleteTerm(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        EnsureCacheLoaded();
        bool removed;
        lock (_gate)
        {
            var newCache = _cache.Where(e =>
                e.EntryType != DictionaryEntryType.Term
                || !e.Original.Trim().Equals(term.Trim(), StringComparison.OrdinalIgnoreCase)
            ).ToList();
            removed = newCache.Count != _cache.Count;

            if (removed)
            {
                SaveToDisk(newCache);
                _cache = newCache;
            }
        }

        if (removed)
        {
            EntriesChanged?.Invoke();
        }

        return removed;
    }

    public IReadOnlyList<DictionaryCorrection> GetCorrections()
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            return _cache
                .Where(e =>
                    e.IsEnabled
                    && e.EntryType == DictionaryEntryType.Correction
                    && e.Replacement is not null
                )
                .Select(e => new DictionaryCorrection(e.Original, e.Replacement!, e.CaseSensitive))
                .ToList();
        }
    }

    public DictionaryCorrection UpsertCorrection(
        string original,
        string replacement,
        bool caseSensitive
    )
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            throw new ArgumentException("Original must not be empty.", nameof(original));
        }

        if (replacement is null)
        {
            throw new ArgumentNullException(nameof(replacement));
        }

        EnsureCacheLoaded();

        lock (_gate)
        {
            var newCache = _cache.ToList();
            var existing = newCache.FirstOrDefault(e =>
                e.EntryType == DictionaryEntryType.Correction
                && e.Original.Equals(original, StringComparison.OrdinalIgnoreCase)
            );

            if (existing is not null)
            {
                var idx = newCache.FindIndex(e => e.Id == existing.Id);
                if (idx >= 0)
                {
                    newCache[idx] = existing with
                    {
                        Replacement = replacement,
                        CaseSensitive = caseSensitive,
                        IsEnabled = true
                    };
                }
            }
            else
            {
                newCache.Add(
                    new DictionaryEntry
                    {
                        Id = Guid.NewGuid().ToString(),
                        EntryType = DictionaryEntryType.Correction,
                        Original = original,
                        Replacement = replacement,
                        CaseSensitive = caseSensitive,
                        Source = DictionaryEntrySource.Manual
                    }
                );
            }

            SaveToDisk(newCache);
            _cache = newCache;
        }

        EntriesChanged?.Invoke();
        return new DictionaryCorrection(original, replacement, caseSensitive);
    }

    public bool DeleteCorrection(string original)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return false;
        }

        EnsureCacheLoaded();
        bool removed;
        lock (_gate)
        {
            var newCache = _cache.Where(e =>
                e.EntryType != DictionaryEntryType.Correction
                || !e.Original.Equals(original, StringComparison.OrdinalIgnoreCase)
            ).ToList();
            removed = newCache.Count != _cache.Count;

            if (removed)
            {
                SaveToDisk(newCache);
                _cache = newCache;
            }
        }

        if (removed)
        {
            EntriesChanged?.Invoke();
        }

        return removed;
    }

    public void LearnCorrection(string original, string replacement)
    {
        EnsureCacheLoaded();

        lock (_gate)
        {
            var newCache = new List<DictionaryEntry>(_cache);
            var existing = newCache.FirstOrDefault(e =>
                e.EntryType == DictionaryEntryType.Correction
                && e.Original.Equals(original, StringComparison.OrdinalIgnoreCase)
            );

            if (existing is not null)
            {
                var idx = newCache.FindIndex(e => e.Id == existing.Id);
                if (idx >= 0)
                {
                    newCache[idx] = existing with
                    {
                        Replacement = replacement,
                        UsageCount = existing.UsageCount + 1,
                        TimesCorrected = existing.TimesCorrected + 1,
                        LastCorrectedAt = DateTime.UtcNow
                    };
                }
            }
            else
            {
                newCache.Add(
                    new DictionaryEntry
                    {
                        Id = Guid.NewGuid().ToString(),
                        EntryType = DictionaryEntryType.Correction,
                        Original = original,
                        Replacement = replacement,
                        TimesCorrected = 1,
                        LastCorrectedAt = DateTime.UtcNow,
                        Source = DictionaryEntrySource.CorrectionSuggestion
                    }
                );
            }

            SaveToDisk(newCache);
            _cache = newCache;
        }

        EntriesChanged?.Invoke();
    }

    public string ExportToCsv()
    {
        EnsureCacheLoaded();

        var sb = new StringBuilder();
        sb.AppendLine(
            "EntryType,Original,Replacement,CaseSensitive,IsEnabled,IsStarred,Priority,Source"
        );

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
        {
            return 0;
        }

        var rows = ParseCsv(csv);
        if (rows.Count == 0)
        {
            return 0;
        }

        var imported = 0;

        lock (_gate)
        {
            var newCache = new List<DictionaryEntry>(_cache);
            var startIndex = LooksLikeHeader(rows[0]) ? 1 : 0;
            var existingKeys = newCache
                .Select(DictionaryEntryKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (var i = startIndex; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Count < 2)
                {
                    continue;
                }

                if (
                    !Enum.TryParse<DictionaryEntryType>(row[0], true, out var entryType)
                )
                {
                    continue;
                }

                var original = row[1].Trim();
                if (string.IsNullOrWhiteSpace(original))
                {
                    continue;
                }

                var replacement =
                    row.Count > 2 && !string.IsNullOrWhiteSpace(row[2]) ? row[2].Trim() : null;

                if (
                    entryType == DictionaryEntryType.Correction
                    && string.IsNullOrWhiteSpace(replacement)
                )
                {
                    continue;
                }

                // Term rows must not carry a Replacement: it's meaningless for
                // terms and a hand-edited CSV with a stray value would break
                // DictionaryEntryKey-based de-duplication on re-import.
                if (entryType != DictionaryEntryType.Correction)
                {
                    replacement = null;
                }

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
                {
                    continue;
                }

                newCache.Add(entry);
                imported++;
            }

            if (imported > 0)
            {
                SaveToDisk(newCache);
                _cache = newCache;
            }
        }

        if (imported > 0)
        {
            EntriesChanged?.Invoke();
        }

        return imported;
    }

    public void ActivatePack(TermPack pack)
    {
        EnsureCacheLoaded();

        var changed = false;
        lock (_gate)
        {
            // Pack term entries use a deterministic ID "pack:<packId>:<term>" so we can
            // detect duplicates without a full text scan and cleanly remove them in DeactivatePack.
            var existingPackIds = _cache
                .Where(e => e.EntryType == DictionaryEntryType.Term)
                .Select(e => e.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var newEntries = pack
                .Terms.Where(t => !existingPackIds.Contains($"pack:{pack.Id}:{t}"))
                .Select(t => new DictionaryEntry
                {
                    Id = $"pack:{pack.Id}:{t}",
                    EntryType = DictionaryEntryType.Term,
                    Original = t
                })
                .ToList();

            if (newEntries.Count > 0)
            {
                var newCache = new List<DictionaryEntry>(_cache);
                newCache.AddRange(newEntries);
                SaveToDisk(newCache);
                _cache = newCache;
                changed = true;
            }
        }

        if (changed)
        {
            EntriesChanged?.Invoke();
        }
    }

    public void DeactivatePack(string packId)
    {
        EnsureCacheLoaded();

        var changed = false;
        lock (_gate)
        {
            var prefix = $"pack:{packId}:";
            var newCache = _cache
                .Where(e => !e.Id.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();

            if (newCache.Count != _cache.Count)
            {
                SaveToDisk(newCache);
                _cache = newCache;
                changed = true;
            }
        }

        if (changed)
        {
            EntriesChanged?.Invoke();
        }
    }

    private void IncrementUsageCounts(IReadOnlyList<string> ids)
    {
        lock (_gate)
        {
            var newCache = new List<DictionaryEntry>(_cache);
            var now = DateTime.UtcNow;
            var changed = false;

            foreach (var id in ids)
            {
                var idx = newCache.FindIndex(e => e.Id == id);
                if (idx < 0)
                {
                    continue;
                }

                newCache[idx] = newCache[idx] with
                {
                    UsageCount = newCache[idx].UsageCount + 1,
                    TimesApplied = newCache[idx].TimesApplied + 1,
                    LastUsedAt = now
                };
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            try
            {
                SaveToDisk(newCache);
                _cache = newCache;
            }
            catch (Exception ex)
            {
                // Usage tracking is best-effort. ApplyCorrections has already
                // produced corrected text by the time this runs; if SaveToDisk
                // throws (load-failed guard, disk full, permission), the
                // post-processing pipeline would otherwise treat the whole
                // correction step as failed and return the pre-correction text.
                // Drop the increments so memory stays consistent with disk and
                // swallow the exception.
                Trace.WriteLine(
                    $"[DictionaryService] Could not persist usage counts for {ids.Count} entries: {ex.Message}"
                );
            }
        }
    }

    private void EnsureCacheLoaded()
    {
        // Volatile reads/writes pair the unsynchronized fast-path check with the
        // in-lock state mutation: without them, a reader could see
        // _cacheLoaded == true while _cache hasn't been published yet on weaker
        // memory architectures (ARM/AArch64).
        if (Volatile.Read(ref _cacheLoaded))
        {
            return;
        }

        lock (_gate)
        {
            if (Volatile.Read(ref _cacheLoaded))
            {
                return;
            }

            if (!File.Exists(_filePath))
            {
                Volatile.Write(ref _cacheLoaded, true);
                return;
            }

            string json;
            try
            {
                json = File.ReadAllText(_filePath);
            }
            catch (IOException ex)
            {
                Trace.WriteLine(
                    $"[DictionaryService] Could not read cache file '{_filePath}': {ex.Message}"
                );
                _cacheLoadFailed = true;
                Volatile.Write(ref _cacheLoaded, true);
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                Trace.WriteLine(
                    $"[DictionaryService] Could not read cache file '{_filePath}': {ex.Message}"
                );
                _cacheLoadFailed = true;
                Volatile.Write(ref _cacheLoaded, true);
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

            Volatile.Write(ref _cacheLoaded, true);
        }
    }

    private void SaveToDisk(IReadOnlyList<DictionaryEntry> entries)
    {
        // If the on-disk file existed but we couldn't read it, don't overwrite
        // it with whatever's currently in _cache — we'd silently destroy the
        // user's dictionary.
        if (_cacheLoadFailed)
        {
            Trace.WriteLine(
                $"[DictionaryService] Skipping save to '{_filePath}': previous load failed and overwriting would discard existing data."
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
            entries,
            new JsonSerializerOptions { WriteIndented = true }
        );

        // Write to a sibling temp file and atomically replace, so a crash
        // mid-write can't leave the user's dictionary truncated.
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
                $"[DictionaryService] Preserved unreadable file as {brokenPath}"
            );
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[DictionaryService] Could not preserve unreadable file: {ex.Message}"
            );
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
            {
                continue;
            }

            if (seen.Add(TermKey(term)))
            {
                normalized.Add(term);
            }
        }

        return normalized;
    }

    private static string TermKey(string term)
    {
        return term.Trim().ToUpperInvariant();
    }

    private static string DictionaryEntryKey(DictionaryEntry entry)
    {
        return $"{entry.EntryType}|{entry.Original.Trim()}|{entry.Replacement?.Trim()}";
    }

    private static bool LooksLikeHeader(IReadOnlyList<string> row)
    {
        return row.Count > 0 && string.Equals(row[0], "EntryType", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReadBool(IReadOnlyList<string> row, int index)
    {
        return row.Count > index && bool.TryParse(row[index], out var value) && value;
    }

    private static int ReadInt(IReadOnlyList<string> row, int index)
    {
        return row.Count > index && int.TryParse(row[index], out var value)
            ? Math.Clamp(value, 0, 999)
            : 0;
    }

    private static DictionaryEntrySource ReadSource(IReadOnlyList<string> row, int index)
    {
        return row.Count > index
               && Enum.TryParse<DictionaryEntrySource>(row[index], true, out var source)
            ? source
            : DictionaryEntrySource.Import;
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
                    {
                        rows.Add(row);
                    }

                    row = [];
                    break;
                default:
                    field.Append(ch);
                    break;
            }
        }

        row.Add(field.ToString());
        if (row.Any(value => value.Length > 0))
        {
            rows.Add(row);
        }

        return rows;
    }
}