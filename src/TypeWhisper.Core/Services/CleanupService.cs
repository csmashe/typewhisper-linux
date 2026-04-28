using System.Text.RegularExpressions;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public sealed partial class CleanupService
{
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

    private static string CleanLight(string text)
    {
        var cleaned = text.Trim();
        cleaned = LeadingNoiseRegex().Replace(cleaned, "");
        cleaned = TrailingNoiseRegex().Replace(cleaned, "");
        cleaned = StandaloneFillerRegex().Replace(cleaned, " ");
        cleaned = LeadingNoiseRegex().Replace(cleaned, "");
        cleaned = TrailingNoiseRegex().Replace(cleaned, "");
        cleaned = DuplicateCommaRegex().Replace(cleaned, ",");
        cleaned = WhitespaceBeforePunctuationRegex().Replace(cleaned, "$1");
        cleaned = DuplicateCommaRegex().Replace(cleaned, ",");
        cleaned = RepeatedWhitespaceRegex().Replace(cleaned, " ").Trim();
        return ApplyBasicSentenceCasing(cleaned);
    }

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

    [GeneratedRegex(@"\s+")]
    private static partial Regex RepeatedWhitespaceRegex();

    [GeneratedRegex(@"\s+([,.;:!?])")]
    private static partial Regex WhitespaceBeforePunctuationRegex();

    [GeneratedRegex(@",{2,}")]
    private static partial Regex DuplicateCommaRegex();

    [GeneratedRegex(@"^[\s,.;:!?-]+")]
    private static partial Regex LeadingNoiseRegex();

    [GeneratedRegex(@"[\s,;:-]+$")]
    private static partial Regex TrailingNoiseRegex();
}
