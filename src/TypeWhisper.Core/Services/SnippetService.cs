using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
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
                .Where(s => s.IsEnabled && !string.IsNullOrEmpty(s.Trigger) && AppliesToProfile(s, profileId))
                .OrderByDescending(s => s.Trigger.Length)
                .ToList();
        if (activeSnippets.Count == 0)
        {
            return text;
        }

        // Match the original transcript so replacements never re-trigger; longer triggers claim spans first.
        var textElementStarts = StringInfo.ParseCombiningCharacters(text);
        var replacements = new List<(int Start, int End, string Text)>();
        var occupied = new bool[text.Length];
        var usageIncrements = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var snippet in activeSnippets)
        {
            var comparison = snippet.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            var matches = snippet.TriggerMode == SnippetTriggerMode.ExactPhrase
                ? FindExactPhraseMatch(text, snippet.Trigger, comparison, occupied)
                : FindStandaloneMatches(text, snippet.Trigger, comparison, textElementStarts, occupied);

            string? expanded = null;
            foreach (var (start, end) in matches)
            {
                // Lazy, so rejected matches never read the clipboard.
                expanded ??= ExpandPlaceholders(snippet.Replacement, clipboardProvider);
                occupied.AsSpan(start, end - start).Fill(true);
                replacements.Add((start, end, expanded));
            }

            if (expanded is not null)
            {
                usageIncrements[snippet.Id] = usageIncrements.GetValueOrDefault(snippet.Id) + 1;
            }
        }

        if (replacements.Count == 0)
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        var copiedThrough = 0;
        foreach (var (start, end, replacement) in replacements.OrderBy(r => r.Start))
        {
            result.Append(text, copiedThrough, start - copiedThrough).Append(replacement);
            copiedThrough = end;
        }

        result.Append(text, copiedThrough, text.Length - copiedThrough);
        IncrementUsageCounts(usageIncrements);
        return result.ToString();
    }

    /// <summary>
    ///     The whole, still unclaimed transcript when it is the trigger alone apart from surrounding
    ///     whitespace and one optional trailing <c>.</c>, <c>!</c> or <c>?</c>.
    /// </summary>
    private static IEnumerable<(int Start, int End)> FindExactPhraseMatch(
        string text,
        string trigger,
        StringComparison comparison,
        bool[] occupied
    )
    {
        if (occupied.Contains(true))
        {
            return [];
        }

        var phrase = text.AsSpan().Trim();
        if (phrase.Length == trigger.Length + 1 && phrase[^1] is '.' or '!' or '?')
        {
            phrase = phrase[..^1];
        }

        return phrase.Equals(trigger, comparison) ? [(0, text.Length)] : [];
    }

    /// <summary>
    ///     Unclaimed occurrences of the trigger that stand alone as complete words, each extended
    ///     over one directly following <c>.</c>, <c>!</c> or <c>?</c>. Triggers never split a grapheme.
    /// </summary>
    private static List<(int Start, int End)> FindStandaloneMatches(
        string text,
        string trigger,
        StringComparison comparison,
        int[] textElementStarts,
        bool[] occupied
    )
    {
        var matches = new List<(int Start, int End)>();
        var requiresLeftBoundary = !IsScriptWithoutWhitespaceBoundaries(trigger.EnumerateRunes().First());
        var requiresRightBoundary = !IsScriptWithoutWhitespaceBoundaries(trigger.EnumerateRunes().Last());
        var searchFrom = 0;
        while (searchFrom <= text.Length - trigger.Length)
        {
            var index = text.IndexOf(trigger, searchFrom, comparison);
            if (index < 0)
            {
                break;
            }

            var end = index + trigger.Length;
            searchFrom = index + 1;
            if (!IsTextElementBoundary(index)
                || !IsTextElementBoundary(end)
                || (requiresLeftBoundary && IsWordContinuation(text, index - 1, -1))
                || (requiresRightBoundary && IsWordContinuation(text, end, 1))
                || occupied.AsSpan(index, trigger.Length).Contains(true))
            {
                continue;
            }

            if (end < text.Length && !occupied[end] && text[end] is '.' or '!' or '?' && IsTextElementBoundary(end + 1))
            {
                end++;
            }

            matches.Add((index, end));
            searchFrom = end;
        }

        return matches;

        bool IsTextElementBoundary(int index) =>
            index == text.Length || Array.BinarySearch(textElementStarts, index) >= 0;
    }

    private static bool IsWordContinuation(string text, int index, int direction, bool includeWordPunctuation = true)
    {
        while (index >= 0 && index < text.Length)
        {
            // Decode the preceding scalar from its low surrogate when checking a left boundary.
            if (char.IsLowSurrogate(text[index]) && index > 0 && char.IsHighSurrogate(text[index - 1]))
            {
                index--;
            }

            if (!Rune.TryGetRuneAt(text, index, out var rune))
            {
                return false;
            }

            // Zero-width space separates words; other format controls do not create boundaries.
            if (rune.Value == 0x200B)
            {
                return false;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Format)
            {
                index += direction > 0 ? rune.Utf16SequenceLength : -1;
                continue;
            }

            // Unspaced scripts start a new word next to Latin text, as in "我的email是" (UAX #29).
            if (IsScriptWithoutWhitespaceBoundaries(rune))
            {
                return false;
            }

            // Internal apostrophes and Hebrew gershayim join words; surrounding quotes remain separators.
            if (includeWordPunctuation && rune.Value is '\'' or '\u2018' or '\u2019' or '\u05F4')
            {
                return IsWordContinuation(text, index - 1, -1, false)
                       && IsWordContinuation(text, index + 1, 1, false);
            }

            return rune.Value == 0x05F3 // Hebrew geresh is also word-internal at an abbreviation's end.
                   || Rune.IsLetter(rune)
                   || Rune.IsNumber(rune)
                   || category is UnicodeCategory.NonSpacingMark
                       or UnicodeCategory.SpacingCombiningMark
                       or UnicodeCategory.EnclosingMark
                       or UnicodeCategory.ConnectorPunctuation;
        }

        return false;
    }

    // Each trigger edge in an unspaced script needs no word boundary; digit sequences still do.
    // Blocks and South East Asian (SA) scripts: https://www.unicode.org/reports/tr14/#SA
    private static bool IsScriptWithoutWhitespaceBoundaries(Rune rune) =>
        (Rune.IsLetter(rune)
         || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
             or UnicodeCategory.SpacingCombiningMark
             or UnicodeCategory.EnclosingMark)
        && rune.Value is >= 0x0E00 and <= 0x0EFF // Thai and Lao
            or >= 0x1000 and <= 0x109F // Myanmar
            or >= 0x1100 and <= 0x11FF // Hangul Jamo
            or >= 0x1780 and <= 0x17FF // Khmer
            or >= 0x1950 and <= 0x19DF // Tai Le and New Tai Lue
            or >= 0x1A20 and <= 0x1AAF // Tai Tham
            or >= 0x3040 and <= 0x30FF // Hiragana and Katakana
            or >= 0x3130 and <= 0x318F // Hangul Compatibility Jamo
            or >= 0x31F0 and <= 0x31FF // Katakana Phonetic Extensions
            or >= 0x3400 and <= 0x4DBF // CJK Extension A
            or >= 0x4E00 and <= 0x9FFF // CJK ideographs
            or >= 0xA960 and <= 0xA97F // Hangul Jamo Extended-A
            or >= 0xA9E0 and <= 0xA9FF // Myanmar Extended-B
            or >= 0xAA60 and <= 0xAADF // Myanmar Extended-A and Tai Viet
            or >= 0xAC00 and <= 0xD7FF // Hangul syllables and Jamo Extended-B
            or >= 0xF900 and <= 0xFAFF // CJK Compatibility Ideographs
            or >= 0xFF66 and <= 0xFF9F // Halfwidth Katakana
            or >= 0x11700 and <= 0x1174F // Ahom
            or >= 0x1AFF0 and <= 0x1B16F // Supplementary Kana blocks
            or >= 0x20000 and <= 0x2A6DF // CJK Extension B
            or >= 0x2A700 and <= 0x2EE5F // CJK Extensions C-F and I
            or >= 0x2F800 and <= 0x2FA1F // CJK Compatibility Ideographs Supplement
            or >= 0x30000 and <= 0x3347F; // CJK Extensions G, H and J

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
