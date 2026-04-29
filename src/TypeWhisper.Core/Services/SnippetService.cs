using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed partial class SnippetService : ISnippetService
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private List<Snippet> _cache = [];
    private bool _cacheLoaded;

    public IReadOnlyList<Snippet> Snippets
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

    public IReadOnlyList<string> AllTags
    {
        get
        {
            EnsureCacheLoaded();
            lock (_gate)
            {
                return _cache
                    .SelectMany(s => s.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
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
        lock (_gate)
        {
            _cache.Add(snippet);
            SaveToDisk();
        }
        SnippetsChanged?.Invoke();
    }

    public void UpdateSnippet(Snippet snippet)
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            var idx = _cache.FindIndex(s => s.Id == snippet.Id);
            if (idx >= 0) _cache[idx] = snippet;
            SaveToDisk();
        }
        SnippetsChanged?.Invoke();
    }

    public void DeleteSnippet(string id)
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            _cache.RemoveAll(s => s.Id == id);
            SaveToDisk();
        }
        SnippetsChanged?.Invoke();
    }

    public string ApplySnippets(string text, Func<string>? clipboardProvider = null, string? profileId = null)
    {
        EnsureCacheLoaded();
        List<Snippet> activeSnippets;
        lock (_gate)
        {
            activeSnippets = _cache
                .Where(s => s.IsEnabled && AppliesToProfile(s, profileId))
                .OrderByDescending(s => s.Trigger.Length)
                .ToList();
        }

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

    private static bool AppliesToProfile(Snippet snippet, string? profileId)
    {
        if (snippet.ProfileIds.Count == 0)
            return true;

        if (string.IsNullOrWhiteSpace(profileId))
            return false;

        return snippet.ProfileIds.Contains(profileId, StringComparer.OrdinalIgnoreCase);
    }

    public string PreviewReplacement(string replacement, Func<string>? clipboardProvider = null) =>
        ExpandPlaceholders(replacement, clipboardProvider);

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
        lock (_gate)
        {
            return JsonSerializer.Serialize(_cache, SnippetJsonContext.Default.ListSnippet);
        }
    }

    public int ImportFromJson(string json)
    {
        var imported = JsonSerializer.Deserialize(json, SnippetJsonContext.Default.ListSnippet);
        if (imported is null or { Count: 0 }) return 0;

        EnsureCacheLoaded();
        var count = 0;
        lock (_gate)
        {
            foreach (var snippet in imported)
            {
                if (_cache.Any(existing => SnippetIdentityEquals(existing, snippet)))
                    continue;

                var newSnippet = snippet with { Id = Guid.NewGuid().ToString() };
                _cache.Add(newSnippet);
                count++;
            }

            if (count > 0)
                SaveToDisk();
        }

        if (count > 0)
            SnippetsChanged?.Invoke();

        return count;
    }

    private static bool SnippetIdentityEquals(Snippet left, Snippet right) =>
        string.Equals(left.Trigger, right.Trigger, StringComparison.OrdinalIgnoreCase) &&
        left.TriggerMode == right.TriggerMode &&
        left.CaseSensitive == right.CaseSensitive &&
        (left.ProfileIds ?? []).Count == (right.ProfileIds ?? []).Count &&
        (left.ProfileIds ?? []).Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual((right.ProfileIds ?? []).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

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
        lock (_gate)
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
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;

        lock (_gate)
        {
            if (_cacheLoaded) return;

            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    _cache = JsonSerializer.Deserialize(json, SnippetJsonContext.Default.ListSnippet) ?? [];
                }
            }
            catch
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

            var json = JsonSerializer.Serialize(_cache, SnippetJsonContext.Default.ListSnippet);
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
            System.Diagnostics.Trace.WriteLine($"[SnippetService] Preserved unreadable file as {brokenPath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[SnippetService] Could not preserve unreadable file: {ex.Message}");
        }
    }
}

[JsonSerializable(typeof(List<Snippet>))]
internal partial class SnippetJsonContext : JsonSerializerContext;
