using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
///     File-backed <see cref="ISnippetService" />: persists snippets as JSON and expands their
///     triggers (with date/time/clipboard placeholders) within transcribed text.
/// </summary>
public sealed partial class SnippetService : ISnippetService
{
    private readonly string _filePath;
    private readonly Lock _gate = new();
    private List<Snippet> _cache = [];
    private bool _cacheLoaded;

    public SnippetService(string filePath)
    {
        _filePath = filePath;
    }

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
                    .SelectMany(s =>
                        s.Tags.Split(
                            ',',
                            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                        )
                    )
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
    }

    public event Action? SnippetsChanged;

    public void AddSnippet(Snippet snippet)
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            var next = new List<Snippet>(_cache) { snippet };
            SaveToDisk(next);
            _cache = next;
        }

        SnippetsChanged?.Invoke();
    }

    public void UpdateSnippet(Snippet snippet)
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            var idx = _cache.FindIndex(s => s.Id == snippet.Id);
            if (idx < 0 || _cache[idx] == snippet)
            {
                return;
            }

            var next = new List<Snippet>(_cache) { [idx] = snippet };
            SaveToDisk(next);
            _cache = next;
        }

        SnippetsChanged?.Invoke();
    }

    public void DeleteSnippet(string id)
    {
        EnsureCacheLoaded();
        lock (_gate)
        {
            var idx = _cache.FindIndex(s => s.Id == id);
            if (idx < 0)
            {
                return;
            }

            var next = new List<Snippet>(_cache);
            next.RemoveAt(idx);
            SaveToDisk(next);
            _cache = next;
        }

        SnippetsChanged?.Invoke();
    }

    public string ApplySnippets(
        string text,
        Func<string>? clipboardProvider = null,
        string? profileId = null
    )
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

        var usageIncrements = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var snippet in activeSnippets)
        {
            var comparison = snippet.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            if (!text.Contains(snippet.Trigger, comparison))
            {
                continue;
            }

            var expanded = ExpandPlaceholders(snippet.Replacement, clipboardProvider);
            var pattern = BuildTriggerPattern(snippet);
            var options = snippet.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            // Regex.Replace interprets "$" in the replacement as a backreference; escape it so
            // literal dollar signs in snippet text are preserved verbatim.
            var replaced = Regex.Replace(text, pattern, expanded.Replace("$", "$$"), options);
            if (string.Equals(replaced, text, StringComparison.Ordinal))
            {
                continue;
            }

            text = replaced;

            usageIncrements[snippet.Id] = usageIncrements.GetValueOrDefault(snippet.Id) + 1;
        }

        IncrementUsageCounts(usageIncrements);
        return text;
    }

    public string PreviewReplacement(string replacement, Func<string>? clipboardProvider = null)
    {
        return ExpandPlaceholders(replacement, clipboardProvider);
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
        if (string.IsNullOrWhiteSpace(json))
        {
            return 0;
        }

        var imported = JsonSerializer.Deserialize(json, SnippetJsonContext.Default.ListSnippet);
        if (imported is null or { Count: 0 })
        {
            return 0;
        }

        EnsureCacheLoaded();
        var count = 0;
        lock (_gate)
        {
            var next = new List<Snippet>(_cache);
            // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
            foreach (var snippet in imported)
            {
                if (next.Any(existing => SnippetIdentityEquals(existing, snippet)))
                {
                    continue;
                }

                var newSnippet = snippet with { Id = Guid.NewGuid().ToString() };
                next.Add(newSnippet);
                count++;
            }

            if (count > 0)
            {
                SaveToDisk(next);
                _cache = next;
            }
        }

        if (count > 0)
        {
            SnippetsChanged?.Invoke();
        }

        return count;
    }

    private static bool AppliesToProfile(Snippet snippet, string? profileId)
    {
        // JSON with explicit "profileIds": null defeats the [] default initializer
        // and would NRE on .Count below. Treat null/empty the same: "applies everywhere".
        if (snippet.ProfileIds is null || snippet.ProfileIds.Count == 0)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(profileId) && snippet.ProfileIds.Contains(profileId, StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildTriggerPattern(Snippet snippet)
    {
        var escaped = Regex.Escape(snippet.Trigger);
        return snippet.TriggerMode == SnippetTriggerMode.ExactPhrase
            ? @"^\s*" + escaped + @"[.!?]?\s*$"
            : escaped + "[.!?]?";
    }

    private static bool SnippetIdentityEquals(Snippet left, Snippet right)
    {
        return left.TriggerMode == right.TriggerMode
               && left.CaseSensitive == right.CaseSensitive
               && string.Equals(
                   left.Trigger,
                   right.Trigger,
                   left.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase
               )
               && (left.ProfileIds ?? []).Count == (right.ProfileIds ?? []).Count
               && (left.ProfileIds ?? [])
               .Order(StringComparer.OrdinalIgnoreCase)
               .SequenceEqual(
                   (right.ProfileIds ?? []).Order(StringComparer.OrdinalIgnoreCase),
                   StringComparer.OrdinalIgnoreCase
               );
    }

    private static string ExpandPlaceholders(string template, Func<string>? clipboardProvider)
    {
        var now = DateTime.Now;

        template = template
            .Replace("{day}", now.ToString("dddd"))
            .Replace("{year}", now.Year.ToString());

        template = PlaceholderRegex()
            .Replace(
                template,
                match =>
                {
                    var name = match.Groups[1].Value;
                    var format = match.Groups[2].Success ? match.Groups[2].Value : null;

                    return name switch
                    {
                        "date" => now.ToString(format ?? "yyyy-MM-dd"),
                        "time" => now.ToString(format ?? "HH:mm"),
                        "datetime" => now.ToString(format ?? "yyyy-MM-dd HH:mm"),
                        "clipboard" => clipboardProvider?.Invoke() ?? "",
                        _ => match.Value,
                    };
                }
            );

        return template;
    }

    [GeneratedRegex(@"\{(date|time|datetime|clipboard)(?::([^}]+))?\}")]
    private static partial Regex PlaceholderRegex();

    private void IncrementUsageCounts(Dictionary<string, int> increments)
    {
        if (increments.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            var next = new List<Snippet>(_cache);
            var changed = false;
            var now = DateTime.UtcNow;
            foreach (var (id, delta) in increments)
            {
                if (delta <= 0)
                {
                    continue;
                }

                var idx = next.FindIndex(s => s.Id == id);
                if (idx < 0)
                {
                    continue;
                }

                next[idx] = next[idx] with
                {
                    UsageCount = next[idx].UsageCount + delta,
                    LastUsedAt = now,
                };
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            // Deliberately the reverse of the mutating APIs, which persist before swapping the
            // cache: usage counts are best-effort telemetry on the dictation path, so a failed
            // write must not cost the in-memory increment too.
            _cache = next;
            try
            {
                SaveToDisk(next);
            }
            catch (Exception ex)
            {
                // Usage stats are best-effort because this runs on the dictation path.
                Trace.WriteLine(
                    $"[SnippetService] Could not persist usage counts to {_filePath}: {ex.Message}"
                );
            }
        }
    }

    private void EnsureCacheLoaded()
    {
        // Volatile.Read for the fast-path check avoids acquiring the lock on every call after load.
        // Volatile.Write inside the lock ensures the write is visible to all threads before they
        // exit the lock (double-checked locking pattern).
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

            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    _cache =
                        JsonSerializer.Deserialize(json, SnippetJsonContext.Default.ListSnippet)
                        ?? [];
                }
            }
            catch
            {
                PreserveBrokenFile(_filePath);
                _cache = [];
            }

            Volatile.Write(ref _cacheLoaded, true);
        }
    }

    private void SaveToDisk(IReadOnlyList<Snippet> snippets)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(snippets, SnippetJsonContext.Default.ListSnippet);
            AtomicFileWrite.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[SnippetService] Failed to save snippets to {_filePath}: {ex}"
            );
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
                $"[SnippetService] Preserved unreadable file as {brokenPath}"
            );
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[SnippetService] Could not preserve unreadable file: {ex.Message}"
            );
        }
    }
}

[JsonSerializable(typeof(List<Snippet>))]
internal partial class SnippetJsonContext : JsonSerializerContext;
