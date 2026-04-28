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
        var numbered = TryFormatSpokenNumberedList(text);
        return numbered ?? text;
    }

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

    [GeneratedRegex(@"^[\s,.;:!?-]+")]
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
}
