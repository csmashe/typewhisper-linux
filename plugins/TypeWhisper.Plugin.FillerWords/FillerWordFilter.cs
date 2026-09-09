using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace TypeWhisper.Plugin.FillerWords;

/// <summary>Removes configured filler words from transcribed text. Latin-script and Japanese-script words use separate matching rules because Japanese text has no whitespace word boundaries.</summary>
public static partial class FillerWordFilter
{
    private const int MatcherCacheLimit = 8;

    private static readonly ConcurrentDictionary<string, FillerWordMatcher> s_matcherCache = new(StringComparer.Ordinal);

    private static readonly Lock s_matcherCacheGate = new();

    private static readonly char[] s_wordSeparators = [',', ';'];

    // Defaults are scoped per spoken language because a filler in one language is a word in
    // another: "um" is a German preposition and "eh" means "anyway" in German.
    private static readonly IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> s_defaultsByLanguage =
    [
        new("en", ["ah", "ahh", "eh", "ehm", "hm", "hmm", "uh", "uhh", "um", "umm"]),
        new("de", ["äh", "ähm", "hm", "hmm"]),
        new(
            "ja",
            [
                "えっと",
                "えーっと",
                "ええと",
                "えーと",
                "えと",
                "なんか",
                "まぁ",
                "まあ",
                "あのー",
                "あのぉ",
                "そのー",
                "そのぉ",
                "うーん",
                "うーむ",
            ]
        ),
    ];

    /// <summary>Every default filler word across languages, for callers without a spoken language.</summary>
    public static IReadOnlyList<string> DefaultFillerWords { get; } =
        NormalizeWords(s_defaultsByLanguage.SelectMany(static pair => pair.Value).ToList());

    /// <summary>The default word list as the user sees it: one "language: words" line per language.</summary>
    public static string DefaultWordsText { get; } = string.Join(
        Environment.NewLine,
        s_defaultsByLanguage.Select(static pair => $"{pair.Key}: {string.Join(", ", pair.Value)}"));

    /// <summary>Default filler words for <paramref name="language"/>; empty when the language is unknown.</summary>
    public static IReadOnlyList<string> DefaultWordsFor(string? language) =>
        ParseWordList(DefaultWordsText).WordsFor(language);

    /// <summary>Removes the default filler words from <paramref name="text"/>.</summary>
    public static string Remove(string text) => Remove(text, DefaultFillerWords);

    /// <summary>Removes the given filler words from <paramref name="text"/>.</summary>
    public static string Remove(string text, IReadOnlyList<string> words)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var normalized = NormalizeWords(words);
        return normalized.Count == 0 ? text : GetMatcher(normalized).Apply(text);
    }

    /// <summary>Splits user-entered text into filler words, ignoring language scopes. Newlines, commas and semicolons all separate entries.</summary>
    public static IReadOnlyList<string> NormalizeWords(string text) => ParseWordList(text).AllWords;

    /// <summary>
    ///     Parses user-entered text into a scoped word list. A line starting with a language tag and a
    ///     colon ("de: äh, ähm") applies to that spoken language only; other lines apply everywhere.
    /// </summary>
    public static FillerWordList ParseWordList(string text)
    {
        var global = new List<string>();
        var scoped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var target = global;
            var separator = line.IndexOf(':');
            if (separator > 0 && LanguageTagPattern().IsMatch(line[..separator]))
            {
                var language = PrimarySubtag(line[..separator].Trim());
                if (!scoped.TryGetValue(language, out target))
                {
                    target = [];
                    scoped[language] = target;
                }

                line = line[(separator + 1)..];
            }

            target.AddRange(line.Split(s_wordSeparators, StringSplitOptions.RemoveEmptyEntries));
        }

        return new FillerWordList(
            NormalizeWords(global),
            scoped.ToDictionary(
                static pair => pair.Key,
                static pair => NormalizeWords(pair.Value),
                StringComparer.OrdinalIgnoreCase));
    }

    internal static string PrimarySubtag(string language) =>
        language.Split('-', '_')[0].Trim().ToLowerInvariant();

    /// <summary>Trims, lower-cases and de-duplicates the given words, ordering longest first so that longer fillers win over words that are a prefix of them.</summary>
    public static IReadOnlyList<string> NormalizeWords(IReadOnlyList<string> words)
    {
        var normalized = words
            .Select(static word => word.Trim().ToLowerInvariant())
            .Where(static cleaned => cleaned.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        normalized.Sort(static (left, right) =>
            left.Length != right.Length
                ? right.Length - left.Length
                : string.CompareOrdinal(left, right));

        return normalized;
    }

    /// <summary>Returns whether the word contains kana or CJK ideographs.</summary>
    internal static bool ContainsJapaneseScript(string word)
    {
        // Hiragana, Katakana (+ phonetic extensions), CJK unified ideographs (+ extension A).
        return word.Any(static c => c
            is >= '\u3040' and <= '\u309F'
            or >= '\u30A0' and <= '\u30FF'
            or >= '\u31F0' and <= '\u31FF'
            or >= '\u3400' and <= '\u4DBF'
            or >= '\u4E00' and <= '\u9FFF');
    }

    private static FillerWordMatcher GetMatcher(IReadOnlyList<string> normalizedWords)
    {
        // Newline-joined: words never contain one, whereas a space would alias
        // ["you know", "like"] with ["you know like"].
        var key = string.Join('\n', normalizedWords);
        // ReSharper disable once InconsistentlySynchronizedField -- lock-free read of a ConcurrentDictionary; the lock only serializes clear-and-insert.
        if (s_matcherCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        lock (s_matcherCacheGate)
        {
            if (s_matcherCache.TryGetValue(key, out cached))
            {
                return cached;
            }

            if (s_matcherCache.Count >= MatcherCacheLimit)
            {
                s_matcherCache.Clear();
            }

            var matcher = new FillerWordMatcher(normalizedWords);
            s_matcherCache[key] = matcher;
            return matcher;
        }
    }

    [GeneratedRegex(@"^\s*[A-Za-z]{2,3}(?:[-_][A-Za-z0-9]{2,8})*\s*$")]
    private static partial Regex LanguageTagPattern();
}

/// <summary>A parsed filler word list: words for every language plus per-language extras.</summary>
public sealed class FillerWordList(
    IReadOnlyList<string> globalWords,
    IReadOnlyDictionary<string, IReadOnlyList<string>> wordsByLanguage)
{
    /// <summary>Every configured word regardless of scope.</summary>
    public IReadOnlyList<string> AllWords { get; } =
        FillerWordFilter.NormalizeWords([.. globalWords, .. wordsByLanguage.Values.SelectMany(static words => words)]);

    /// <summary>The words that apply to <paramref name="language"/>: the unscoped ones plus its own.</summary>
    public IReadOnlyList<string> WordsFor(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)
            || !wordsByLanguage.TryGetValue(FillerWordFilter.PrimarySubtag(language), out var scoped))
        {
            return globalWords;
        }

        return FillerWordFilter.NormalizeWords([.. globalWords, .. scoped]);
    }
}
