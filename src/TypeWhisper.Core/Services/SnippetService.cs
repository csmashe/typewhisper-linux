using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed partial class SnippetService : ISnippetService
{
    private readonly string _filePath;
    private List<Snippet> _cache = [];
    private bool _cacheLoaded;

    public IReadOnlyList<Snippet> Snippets
    {
        get
        {
            EnsureCacheLoaded();
            return _cache;
        }
    }

    public IReadOnlyList<string> AllTags
    {
        get
        {
            EnsureCacheLoaded();
            return _cache
                .SelectMany(s => s.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public event Action? SnippetsChanged;

    public SnippetService(string filePath)
    {
        _filePath = filePath;
    }

    public void AddSnippet(Snippet snippet)
    {
        EnsureCacheLoaded();
        _cache.Add(snippet);
        SaveToDisk();
        SnippetsChanged?.Invoke();
    }

    public void UpdateSnippet(Snippet snippet)
    {
        EnsureCacheLoaded();
        var idx = _cache.FindIndex(s => s.Id == snippet.Id);
        if (idx >= 0) _cache[idx] = snippet;
        SaveToDisk();
        SnippetsChanged?.Invoke();
    }

    public void DeleteSnippet(string id)
    {
        EnsureCacheLoaded();
        _cache.RemoveAll(s => s.Id == id);
        SaveToDisk();
        SnippetsChanged?.Invoke();
    }

    public string ApplySnippets(string text, Func<string>? clipboardProvider = null)
    {
        EnsureCacheLoaded();
        var activeSnippets = _cache
            .Where(s => s.IsEnabled)
            .OrderByDescending(s => s.Trigger.Length);

        foreach (var snippet in activeSnippets)
        {
            var comparison = snippet.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            if (!text.Contains(snippet.Trigger, comparison)) continue;

            var expanded = ExpandPlaceholders(snippet.Replacement, clipboardProvider);
            var pattern = BuildTriggerPattern(snippet);
            var options = snippet.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            var replaced = Regex.Replace(text, pattern, expanded.Replace("$", "$$"), options);
            if (string.Equals(replaced, text, StringComparison.Ordinal))
                continue;

            text = replaced;

            IncrementUsageCount(snippet.Id);
        }

        return text;
    }

    private static string BuildTriggerPattern(Snippet snippet)
    {
        var escaped = Regex.Escape(snippet.Trigger);
        return snippet.TriggerMode == SnippetTriggerMode.ExactPhrase
            ? @"^\s*" + escaped + @"[.!?]?\s*$"
            : escaped + @"[.!?]?";
    }

    public string ExportToJson()
    {
        EnsureCacheLoaded();
        return JsonSerializer.Serialize(_cache, SnippetJsonContext.Default.ListSnippet);
    }

    public int ImportFromJson(string json)
    {
        var imported = JsonSerializer.Deserialize(json, SnippetJsonContext.Default.ListSnippet);
        if (imported is null or { Count: 0 }) return 0;

        EnsureCacheLoaded();
        var existingTriggers = _cache.Select(s => s.Trigger).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var count = 0;
        foreach (var snippet in imported)
        {
            if (existingTriggers.Contains(snippet.Trigger)) continue;

            var newSnippet = snippet with { Id = Guid.NewGuid().ToString() };
            _cache.Add(newSnippet);
            existingTriggers.Add(newSnippet.Trigger);
            count++;
        }

        if (count > 0)
        {
            SaveToDisk();
            SnippetsChanged?.Invoke();
        }

        return count;
    }

    private static string ExpandPlaceholders(string template, Func<string>? clipboardProvider)
    {
        var now = DateTime.Now;

        template = template
            .Replace("{day}", now.ToString("dddd"))
            .Replace("{year}", now.Year.ToString());

        template = PlaceholderRegex().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            var format = match.Groups[2].Success ? match.Groups[2].Value : null;

            return name switch
            {
                "date" => now.ToString(format ?? "yyyy-MM-dd"),
                "time" => now.ToString(format ?? "HH:mm"),
                "datetime" => now.ToString(format ?? "yyyy-MM-dd HH:mm"),
                "clipboard" => clipboardProvider?.Invoke() ?? "",
                _ => match.Value
            };
        });

        return template;
    }

    [GeneratedRegex(@"\{(date|time|datetime|clipboard)(?::([^}]+))?\}")]
    private static partial Regex PlaceholderRegex();

    private void IncrementUsageCount(string id)
    {
        var idx = _cache.FindIndex(s => s.Id == id);
        if (idx >= 0)
        {
            _cache[idx] = _cache[idx] with
            {
                UsageCount = _cache[idx].UsageCount + 1,
                LastUsedAt = DateTime.UtcNow
            };
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
                _cache = JsonSerializer.Deserialize<List<Snippet>>(json) ?? [];
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

[JsonSerializable(typeof(List<Snippet>))]
internal partial class SnippetJsonContext : JsonSerializerContext;
