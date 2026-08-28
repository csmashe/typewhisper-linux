using System.Collections.Immutable;
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
    private readonly AtomicJsonStore<ImmutableArray<Snippet>> _store;
    private readonly TimeProvider _timeProvider;

    public SnippetService(string filePath, TimeProvider? timeProvider = null)
    {
        _filePath = Path.GetFullPath(filePath);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _store = new AtomicJsonStore<ImmutableArray<Snippet>>(
            _filePath,
            static () => [],
            new AtomicJsonStoreOptions<ImmutableArray<Snippet>>
            {
                CorruptFilePolicy = AtomicJsonCorruptFilePolicy.PreserveAndReset,
                Deserialize = json =>
                    [
                        .. JsonSerializer.Deserialize(
                            json,
                            SnippetJsonContext.Default.ListSnippet
                        ) ?? throw new JsonException("Snippet JSON deserialized to null."),
                    ],
                Serialize = snippets =>
                    JsonSerializer.Serialize(
                        snippets.ToList(),
                        SnippetJsonContext.Default.ListSnippet
                    ),
            }
        );
    }

    public IReadOnlyList<Snippet> Snippets => _store.Current.ToArray();

    public IReadOnlyList<string> AllTags
    {
        get
        {
            return _store.Current
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

    public event Action? SnippetsChanged;

    public void AddSnippet(Snippet snippet)
    {
        Commit(snippets =>
        {
            snippets.Add(snippet);
            return true;
        });
    }

    public void UpdateSnippet(Snippet snippet)
    {
        Commit(snippets =>
        {
            var idx = snippets.FindIndex(s => s.Id == snippet.Id);
            if (idx < 0 || snippets[idx] == snippet)
            {
                return false;
            }

            snippets[idx] = snippet;
            return true;
        });
    }

    public void DeleteSnippet(string id)
    {
        Commit(snippets =>
        {
            var idx = snippets.FindIndex(s => s.Id == id);
            if (idx < 0)
            {
                return false;
            }

            snippets.RemoveAt(idx);
            return true;
        });
    }

    public string ApplySnippets(
        string text,
        Func<string>? clipboardProvider = null,
        string? profileId = null
    )
    {
        var activeSnippets = _store.Current
                .Where(s => s.IsEnabled && AppliesToProfile(s, profileId))
                .OrderByDescending(s => s.Trigger.Length)
                .ToList();

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
        return JsonSerializer.Serialize(
            _store.Current.ToList(),
            SnippetJsonContext.Default.ListSnippet
        );
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

        var count = 0;
        Commit(next =>
        {
            count = 0;
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

            return count > 0;
        });

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

    private string ExpandPlaceholders(string template, Func<string>? clipboardProvider)
    {
        var now = _timeProvider.GetLocalNow();

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
                        "date" => Format(now, format ?? "yyyy-MM-dd"),
                        "time" => Format(now, format ?? "HH:mm"),
                        "datetime" => Format(now, format ?? "yyyy-MM-dd HH:mm"),
                        "clipboard" => clipboardProvider?.Invoke() ?? "",
                        _ => match.Value,
                    };
                }
            );

        return template;

        // DateTimeOffset rejects "U" and converts "u"/"R"/"r" to UTC, where DateTime formats the
        // local wall-clock fields. Keep the output these specifiers had before the injected clock.
        static string Format(DateTimeOffset value, string format) =>
            format switch
            {
                "U" => value.UtcDateTime.ToString(format),
                "u" or "R" or "r" => value.DateTime.ToString(format),
                _ => value.ToString(format),
            };
    }

    [GeneratedRegex(@"\{(date|time|datetime|clipboard)(?::([^}]+))?\}")]
    private static partial Regex PlaceholderRegex();

    private void IncrementUsageCounts(Dictionary<string, int> increments)
    {
        if (increments.Count == 0)
        {
            return;
        }

        try
        {
            Commit(
                snippets =>
                {
                    var changed = false;
                    var now = _timeProvider.GetUtcNow().UtcDateTime;
                    foreach (var (id, delta) in increments)
                    {
                        if (delta <= 0)
                        {
                            continue;
                        }

                        var idx = snippets.FindIndex(s => s.Id == id);
                        if (idx < 0)
                        {
                            continue;
                        }

                        snippets[idx] = snippets[idx] with
                        {
                            UsageCount = snippets[idx].UsageCount + delta,
                            LastUsedAt = now,
                        };
                        changed = true;
                    }

                    return changed;
                },
                raiseEvent: false
            );
        }
        catch (Exception ex)
        {
            // Usage stats are best-effort because this runs on the dictation path. The store
            // retains the prior snapshot when persistence fails.
            Trace.WriteLine(
                $"[SnippetService] Could not persist usage counts to {_filePath}: {ex.Message}"
            );
        }
    }

    private void Commit(
        Func<List<Snippet>, bool> update,
        bool raiseEvent = true
    )
    {
        var changed = false;
        try
        {
            _store.Update(
                current =>
                {
                    var next = current.ToList();
                    changed = update(next);
                    return changed ? [.. next] : current;
                }
            );
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[SnippetService] Failed to save snippets to {_filePath}: {ex}"
            );
            throw;
        }

        if (changed && raiseEvent)
        {
            SnippetsChanged?.Invoke();
        }
    }
}

[JsonSerializable(typeof(List<Snippet>))]
internal partial class SnippetJsonContext : JsonSerializerContext;
