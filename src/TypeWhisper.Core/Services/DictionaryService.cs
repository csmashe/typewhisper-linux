using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
///     File-backed <see cref="IDictionaryService" />: persists terms and corrections as JSON,
///     lazily loaded and cached, with atomic writes that refuse to overwrite a file that failed
///     to load so a transient IO error can't discard the user's dictionary.
/// </summary>
public sealed partial class DictionaryService : IDictionaryService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly Lock _gate = new();
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
                    e is { IsEnabled: true, EntryType: DictionaryEntryType.Correction }
                    && !string.IsNullOrEmpty(e.Original)
                    && e.Replacement is not null
                )
                .OrderByDescending(e => e.Priority)
                .ThenByDescending(e => e.IsStarred)
                .ThenByDescending(e => e.Original.Length)
                .ToList();
        }

        // Accumulate usage increments and persist once at the end to avoid N file writes per dictation.
        var usedCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var entry in corrections)
        {
            var comparison = entry.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            if (!text.Contains(entry.Original, comparison))
            {
                continue;
            }

            var pattern = Regex.Escape(entry.Original);
            var options = entry.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            // \b silently fails for originals like "C#" or ".NET" whose ends are non-word chars.
            // Anchor each side based on what the original starts/ends with: \b on word-chars,
            // lookaround on symbol-chars.
            var prefix = char.IsLetterOrDigit(entry.Original[0]) || entry.Original[0] == '_'
                ? @"\b"
                : @"(?<=\W|^)";
            var lastChar = entry.Original[^1];
            var suffix = char.IsLetterOrDigit(lastChar) || lastChar == '_'
                ? @"\b"
                : @"(?=\W|$)";
            // MatchEvaluator overload: prevents "$1"/"$&" in user replacements from being
            // interpreted as regex substitution tokens; also counts each match individually.
            var replacement = entry.Replacement!;
            var matchCount = 0;
            var replaced = Regex.Replace(
                text,
                prefix + pattern + suffix,
                _ =>
                {
                    matchCount++;
                    return replacement;
                },
                options
            );
            if (matchCount == 0 || string.Equals(replaced, text, StringComparison.Ordinal))
            {
                continue;
            }

            text = replaced;
            usedCounts[entry.Id] = usedCounts.TryGetValue(entry.Id, out var prior)
                ? prior + matchCount
                : matchCount;
        }

        if (usedCounts.Count > 0)
        {
            IncrementUsageCounts(usedCounts);
        }

        return text;
    }

    public string? GetTermsForPrompt()
    {
        EnsureCacheLoaded();
        var terms = GetEnabledTerms();

        return terms.Count == 0 ? null : string.Join(", ", terms);
    }

    public IReadOnlyList<string> GetEnabledTerms()
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            return NormalizeTerms(
                _cache
                    .Where(e => e is { IsEnabled: true, EntryType: DictionaryEntryType.Term })
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
            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var term in normalized.Where(term => !existingKeys.Contains(TermKey(term))))
            {
                newCache.Add(
                    new DictionaryEntry
                    {
                        Id = Guid.NewGuid().ToString(), EntryType = DictionaryEntryType.Term, Original = term
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
                    e is { IsEnabled: true, EntryType: DictionaryEntryType.Correction, Replacement: not null }
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

        ArgumentNullException.ThrowIfNull(replacement);

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
                        Replacement = replacement, CaseSensitive = caseSensitive, IsEnabled = true
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

    public void ActivatePack(TermPack pack)
    {
        EnsureCacheLoaded();

        var changed = false;
        lock (_gate)
        {
            // Deterministic IDs "pack:<packId>:<term>" enable duplicate detection and clean removal in DeactivatePack.
            var existingPackIds = _cache
                .Where(e => e.EntryType == DictionaryEntryType.Term)
                .Select(e => e.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var newEntries = pack
                .Terms.Where(t => !existingPackIds.Contains($"pack:{pack.Id}:{t}"))
                .Select(t => new DictionaryEntry
                {
                    Id = $"pack:{pack.Id}:{t}", EntryType = DictionaryEntryType.Term, Original = t
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

    public void ApplyIndustryPreset(string presetId)
    {
        var preset = IndustryPreset.All.FirstOrDefault(p =>
            string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase)
        );
        if (preset?.TermPackId is not { } packId)
        {
            return;
        }

        var pack = TermPack.FindById(packId);
        if (pack is not null)
        {
            ActivatePack(pack);
        }
    }

    private void IncrementUsageCounts(Dictionary<string, int> deltas)
    {
        lock (_gate)
        {
            var newCache = new List<DictionaryEntry>(_cache);
            var now = DateTime.UtcNow;
            var changed = false;

            foreach (var (id, delta) in deltas)
            {
                if (delta <= 0)
                {
                    continue;
                }

                var idx = newCache.FindIndex(e => e.Id == id);
                if (idx < 0)
                {
                    continue;
                }

                newCache[idx] = newCache[idx] with
                {
                    UsageCount = newCache[idx].UsageCount + delta,
                    TimesApplied = newCache[idx].TimesApplied + delta,
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
                // Usage tracking is best-effort — the corrected text is already produced.
                // Swallow so a disk/permission error doesn't roll back the correction result.
                Trace.WriteLine(
                    $"[DictionaryService] Could not persist usage counts for {deltas.Count} entries: {ex.Message}"
                );
            }
        }
    }

    private void EnsureCacheLoaded()
    {
        // Volatile read pairs with the in-lock write: without it, ARM/AArch64 could observe
        // _cacheLoaded == true before _cache is published.
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
        // Refuse to overwrite when the previous load failed — would silently destroy the user's data.
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

        var json = JsonSerializer.Serialize(entries, s_jsonOptions);

        // Atomic write via temp file + replace so a mid-write crash can't truncate the dictionary.
        var tempPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            if (File.Exists(_filePath))
            {
                File.Replace(tempPath, _filePath, null);
            }
            else
            {
                File.Move(tempPath, _filePath);
            }
        }
        catch
        {
            if (!File.Exists(tempPath))
            {
                throw;
            }

            try { File.Delete(tempPath); }
            catch
            {
                /* best effort */
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

    private static List<string> NormalizeTerms(IEnumerable<string> terms)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();

        // ReSharper disable once LoopCanBeConvertedToQuery
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
}