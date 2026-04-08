using System.Text.Json;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed class DictionaryService : IDictionaryService
{
    private readonly string _filePath;
    private List<DictionaryEntry> _cache = [];
    private bool _cacheLoaded;

    public IReadOnlyList<DictionaryEntry> Entries
    {
        get
        {
            EnsureCacheLoaded();
            return _cache;
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
        _cache.Add(entry);
        SaveToDisk();
        EntriesChanged?.Invoke();
    }

    public void AddEntries(IEnumerable<DictionaryEntry> entries)
    {
        EnsureCacheLoaded();
        _cache.AddRange(entries);
        SaveToDisk();
        EntriesChanged?.Invoke();
    }

    public void UpdateEntry(DictionaryEntry entry)
    {
        EnsureCacheLoaded();
        var idx = _cache.FindIndex(e => e.Id == entry.Id);
        if (idx >= 0) _cache[idx] = entry;
        SaveToDisk();
        EntriesChanged?.Invoke();
    }

    public void DeleteEntry(string id)
    {
        EnsureCacheLoaded();
        _cache.RemoveAll(e => e.Id == id);
        SaveToDisk();
        EntriesChanged?.Invoke();
    }

    public void DeleteEntries(IEnumerable<string> ids)
    {
        EnsureCacheLoaded();
        var idSet = ids.ToHashSet();
        _cache.RemoveAll(e => idSet.Contains(e.Id));
        SaveToDisk();
        EntriesChanged?.Invoke();
    }

    public string ApplyCorrections(string text)
    {
        EnsureCacheLoaded();
        var corrections = _cache
            .Where(e => e.IsEnabled && e.EntryType == DictionaryEntryType.Correction && e.Replacement is not null)
            .OrderByDescending(e => e.Original.Length);

        foreach (var entry in corrections)
        {
            var comparison = entry.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            if (text.Contains(entry.Original, comparison))
            {
                var pattern = Regex.Escape(entry.Original);
                var options = entry.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
                text = Regex.Replace(text, @"\b" + pattern + @"\b", entry.Replacement!, options);

                IncrementUsageCount(entry.Id);
            }
        }

        return text;
    }

    public string? GetTermsForPrompt()
    {
        EnsureCacheLoaded();
        var terms = _cache
            .Where(e => e.IsEnabled && e.EntryType == DictionaryEntryType.Term)
            .Select(e => e.Original)
            .ToList();

        if (terms.Count == 0) return null;
        return string.Join(", ", terms);
    }

    public void LearnCorrection(string original, string replacement)
    {
        EnsureCacheLoaded();

        var existing = _cache.FirstOrDefault(e =>
            e.EntryType == DictionaryEntryType.Correction &&
            e.Original.Equals(original, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            UpdateEntry(existing with { Replacement = replacement, UsageCount = existing.UsageCount + 1 });
        }
        else
        {
            AddEntry(new DictionaryEntry
            {
                Id = Guid.NewGuid().ToString(),
                EntryType = DictionaryEntryType.Correction,
                Original = original,
                Replacement = replacement
            });
        }
    }

    public void ActivatePack(TermPack pack)
    {
        EnsureCacheLoaded();

        var existingOriginals = _cache
            .Where(e => e.EntryType == DictionaryEntryType.Term)
            .Select(e => e.Original)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newEntries = pack.Terms
            .Where(t => !existingOriginals.Contains(t))
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
            EntriesChanged?.Invoke();
        }
    }

    public void DeactivatePack(string packId)
    {
        EnsureCacheLoaded();

        var prefix = $"pack:{packId}:";
        var removed = _cache.RemoveAll(e => e.Id.StartsWith(prefix, StringComparison.Ordinal));

        if (removed > 0)
        {
            SaveToDisk();
            EntriesChanged?.Invoke();
        }
    }

    private void IncrementUsageCount(string id)
    {
        var idx = _cache.FindIndex(e => e.Id == id);
        if (idx >= 0)
        {
            _cache[idx] = _cache[idx] with { UsageCount = _cache[idx].UsageCount + 1 };
            SaveToDisk();
        }
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;

        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _cache = JsonSerializer.Deserialize<List<DictionaryEntry>>(json) ?? [];
            }
        }
        catch
        {
            _cache = [];
        }

        _cacheLoaded = true;
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
}
