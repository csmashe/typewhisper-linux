using System.Text.RegularExpressions;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed partial class CleanupService
{
    public const string MediumSystemPrompt =
        "Improve readability while preserving meaning, facts, tone, and terminology. Do not add new information. Return only the cleaned text.";

    public const string HighSystemPrompt =
        "Rewrite as concise polished prose while preserving meaning, facts, tone, and terminology. Do not add new information. Return only the rewritten text.";

    public string Clean(string text, CleanupLevel level)
    {
        if (level == CleanupLevel.None || string.IsNullOrWhiteSpace(text))
            return text;

        return level switch
        {
            CleanupLevel.Light => CleanLight(text),
            // Medium/High LLM cleanup is intentionally not wired yet. Until a
            // provider-backed pass exists, degrade to deterministic cleanup.
            CleanupLevel.Medium or CleanupLevel.High => CleanLight(text),
            _ => text
        };
    }

    public static string GetLlmSystemPrompt(CleanupLevel level) =>
        level switch
        {
            CleanupLevel.Medium => MediumSystemPrompt,
            CleanupLevel.High => HighSystemPrompt,
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Only Medium and High cleanup use LLM prompts.")
        };

    private static string CleanLight(string text)
    {
        var cleaned = text.Trim();
        cleaned = LeadingNoiseRegex().Replace(cleaned, "");
        cleaned = TrailingNoiseRegex().Replace(cleaned, "");
        cleaned = StandaloneFillerRegex().Replace(cleaned, " ");
        cleaned = ApplyBacktrack(cleaned);
        cleaned = ApplySpokenPunctuation(cleaned);
        cleaned = ApplySmartFormatting(cleaned);
        cleaned = LeadingNoiseRegex().Replace(cleaned, "");
        cleaned = TrailingNoiseRegex().Replace(cleaned, "");
        cleaned = DuplicateCommaRegex().Replace(cleaned, ",");
        cleaned = WhitespaceBeforeNewlineRegex().Replace(cleaned, "\n");
        cleaned = WhitespaceBeforePunctuationRegex().Replace(cleaned, "$1");
        cleaned = DuplicateCommaRegex().Replace(cleaned, ",");
        cleaned = RepeatedInlineWhitespaceRegex().Replace(cleaned, " ").Trim();
        return ApplyBasicSentenceCasing(cleaned);
    }

    private static string ApplyBacktrack(string text)
    {
        var cleaned = ScratchThatWholeUtteranceRegex().Replace(text, "");
        cleaned = ScratchThatReplacementRegex().Replace(cleaned, "${replacement}");
        cleaned = OneWordCorrectionRegex().Replace(cleaned, "${prefix}${replacement}${suffix}");
        return cleaned;
    }

    private static string ApplySmartFormatting(string text)
    {
        var bullets = TryFormatSpokenBulletList(text);
        if (bullets is not null)
            return bullets;

        var numbered = TryFormatSpokenNumberedList(text);
        return numbered ?? text;
    }

    private static string ApplySpokenPunctuation(string text)
    {
        return SpokenPunctuationRegex().Replace(text, match =>
        {
            var mark = match.Groups["mark"].Value.ToLowerInvariant();
            if (!ShouldApplySpokenPunctuation(text, match, mark))
                return match.Value;

            return mark switch
            {
                "comma" => ",",
                "period" or "full stop" => ".",
                "question mark" => "?",
                "exclamation mark" or "exclamation point" => "!",
                "colon" => ":",
                "semicolon" => ";",
                _ => match.Value
            };
        });
    }

    private static bool ShouldApplySpokenPunctuation(string text, Match match, string mark)
    {
        var previousWordCount = CountWordsBefore(text, match.Index);
        var hasWordAfter = HasWordAfter(text, match.Index + match.Length);

        return mark switch
        {
            "period" or "full stop" => previousWordCount >= 2 && !hasWordAfter,
            "comma" or "colon" or "semicolon" => previousWordCount >= 1 && hasWordAfter,
            "question mark" or "exclamation mark" or "exclamation point" => previousWordCount >= 1 && !hasWordAfter,
            _ => false
        };
    }

    private static int CountWordsBefore(string text, int endIndex)
    {
        var prefix = text[..endIndex];
        var boundary = Math.Max(
            Math.Max(prefix.LastIndexOf('.'), prefix.LastIndexOf('?')),
            Math.Max(prefix.LastIndexOf('!'), prefix.LastIndexOf('\n')));
        var phrase = prefix[(boundary + 1)..];
        return WordRegex().Matches(phrase).Count;
    }

    private static bool HasWordAfter(string text, int startIndex) =>
        WordRegex().IsMatch(text[startIndex..]);

    private static string? TryFormatSpokenBulletList(string text)
    {
        var match = BulletListTriggerRegex().Match(text);
        if (!match.Success)
            return null;

        var body = match.Groups["items"].Value.Trim(' ', '\t', ',', ';', ':', '.', '-', '\r', '\n');
        if (body.Length == 0)
            return null;

        var items = SplitBulletItems(body);
        return items.Count < 2
            ? null
            : string.Join('\n', items.Select(item => $"- {item}"));
    }

    private static IReadOnlyList<string> SplitBulletItems(string body)
    {
        var explicitItems = ExplicitBulletSeparatorRegex()
            .Split(body)
            .Select(CleanListItem)
            .Where(item => item.Length > 0)
            .ToList();
        if (explicitItems.Count >= 2)
            return explicitItems;

        var punctuatedItems = BulletPunctuationSeparatorRegex()
            .Split(body)
            .Select(CleanListItem)
            .Where(item => item.Length > 0)
            .ToList();
        if (punctuatedItems.Count >= 2)
            return punctuatedItems;

        var words = body
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CleanListItem)
            .Where(item => item.Length > 0)
            .ToList();

        return IsConservativeSingleWordBulletList(words) ? words : [];
    }

    private static bool IsConservativeSingleWordBulletList(IReadOnlyList<string> words)
    {
        if (words.Count is < 2 or > 9)
            return false;

        return words.All(word =>
            SingleWordListItemRegex().IsMatch(word)
            && !BulletListStopWords().Contains(word, StringComparer.OrdinalIgnoreCase));
    }

    private static string CleanListItem(string item) =>
        item.Trim(' ', '\t', ',', ';', ':', '.', '-', '\r', '\n');

    private static IReadOnlySet<string> BulletListStopWords() =>
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a",
            "an",
            "and",
            "for",
            "need",
            "of",
            "or",
            "the",
            "things",
            "to",
            "we",
            "with"
        };

    private static string? TryFormatSpokenNumberedList(string text)
    {
        var matches = NumberedListMarkerRegex().Matches(text);
        if (matches.Count < 2 || matches[0].Index != 0)
            return null;

        var expected = 1;
        var items = new List<string>();

        for (var i = 0; i < matches.Count; i++)
        {
            var number = SpokenNumberToInt(matches[i].Value);
            if (number != expected)
                return null;

            var itemStart = matches[i].Index + matches[i].Length;
            var itemEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var item = text[itemStart..itemEnd].Trim(' ', '\t', ',', ';', ':', '.', '-', '\r', '\n');
            if (item.Length == 0)
                return null;

            items.Add(item);
            expected++;
        }

        if (items.Count < 2)
            return null;

        return string.Join('\n', items.Select((item, index) => $"{index + 1}. {item}"));
    }

    private static int SpokenNumberToInt(string number) =>
        number.ToLowerInvariant() switch
        {
            "one" => 1,
            "two" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            _ => 0
        };

    private static string ApplyBasicSentenceCasing(string text)
    {
        if (text.Length == 0)
            return text;

        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsLetter(text[i]))
                continue;

            if (char.IsUpper(text[i]))
                return text;

            return text[..i] + char.ToUpperInvariant(text[i]) + text[(i + 1)..];
        }

        return text;
    }

    [GeneratedRegex(@"(?i)(^|[\s,.;:!?-])(?:um+|uh+|er+|ah+|you know)(?=$|[\s,.;:!?-])")]
    private static partial Regex StandaloneFillerRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex RepeatedInlineWhitespaceRegex();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex WhitespaceBeforeNewlineRegex();

    [GeneratedRegex(@"\s+([,.;:!?])")]
    private static partial Regex WhitespaceBeforePunctuationRegex();

    [GeneratedRegex(@",{2,}")]
    private static partial Regex DuplicateCommaRegex();

    [GeneratedRegex(@"^[\s,.;:!?]+")]
    private static partial Regex LeadingNoiseRegex();

    [GeneratedRegex(@"[\s,;:-]+$")]
    private static partial Regex TrailingNoiseRegex();

    [GeneratedRegex(@"(?i)^\s*scratch\s+that\s*$")]
    private static partial Regex ScratchThatWholeUtteranceRegex();

    [GeneratedRegex(@"(?is)^.+?\bscratch\s+that\b[\s,.;:!?-]+(?<replacement>(?:i|we|let's|lets|please|the|a|an|this|that)\b.+)$")]
    private static partial Regex ScratchThatReplacementRegex();

    [GeneratedRegex(@"(?is)\b(?<prefix>.*\s)(?<original>[A-Za-z][A-Za-z'-]*)\s+(?:actually|i\s+mean)\s+(?<replacement>[A-Za-z][A-Za-z'-]*)(?<suffix>[.?!]?)$")]
    private static partial Regex OneWordCorrectionRegex();

    [GeneratedRegex(@"(?i)\b(?:one|two|three|four|five|six|seven|eight|nine)\b")]
    private static partial Regex NumberedListMarkerRegex();

    [GeneratedRegex(@"(?i)\b(?<mark>comma|period|full\s+stop|question\s+mark|exclamation\s+(?:mark|point)|colon|semicolon)\b")]
    private static partial Regex SpokenPunctuationRegex();

    [GeneratedRegex(@"(?is)^\s*bullet\s+list\s+(?<items>.+)$")]
    private static partial Regex BulletListTriggerRegex();

    [GeneratedRegex(@"(?i)\s+(?:next\s+bullet|new\s+bullet)\s+")]
    private static partial Regex ExplicitBulletSeparatorRegex();

    [GeneratedRegex(@"\s*[,;]\s*")]
    private static partial Regex BulletPunctuationSeparatorRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z'-]*$")]
    private static partial Regex SingleWordListItemRegex();

    [GeneratedRegex(@"[A-Za-z][A-Za-z'-]*")]
    private static partial Regex WordRegex();
}
