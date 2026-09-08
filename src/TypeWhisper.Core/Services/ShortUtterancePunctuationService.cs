using System.Text.RegularExpressions;

namespace TypeWhisper.Core.Services;

/// <summary>Strips model-added punctuation from one- or two-word utterances when the user turns it off.</summary>
public static partial class ShortUtterancePunctuationService
{
    private const int MaximumWordCount = 2;

    public static string NormalizeText(string text, bool punctuationEnabled)
    {
        return punctuationEnabled || string.IsNullOrWhiteSpace(text) || !IsShortUtterance(text)
            ? text
            : NormalizeShortUtterance(text);
    }

    private static bool IsShortUtterance(string text)
    {
        // Scripts written without spaces would count a whole sentence as one word, so every
        // such character counts as a word of its own instead.
        var wordCount = WordPattern().Count(text) + UnspacedScriptPattern().Count(text);
        return wordCount is >= 1 and <= MaximumWordCount;
    }

    private static string NormalizeShortUtterance(string text)
    {
        var normalized = LeadingInvertedPunctuationPattern().Replace(text, "${prefix}");
        normalized = GreetingCommaPattern().Replace(normalized, "${prefix}${greeting} ");
        normalized = TrailingGreetingCommaPattern().Replace(normalized, "${prefix}${greeting}");
        return TrailingSentencePunctuationPattern().Replace(normalized, string.Empty);
    }

    [GeneratedRegex(
        @"[\p{L}\p{M}\p{N}-[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}\p{IsHangulSyllables}]]+(?:['’\-][\p{L}\p{M}\p{N}]+)*",
        RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    [GeneratedRegex(
        @"[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}\p{IsHangulSyllables}]",
        RegexOptions.CultureInvariant)]
    private static partial Regex UnspacedScriptPattern();

    [GeneratedRegex(@"^(?<prefix>\s*)[¿¡]+\s*", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingInvertedPunctuationPattern();

    [GeneratedRegex(
        @"^(?<prefix>\s*)(?<greeting>Hallo|Hello|Hi|Hey|Moin|Servus|Bonjour|Salut|Hola|Ciao|Olá|Ola|Hoi|Cześć|Ahoj|Hej|Hei|Moi|Привет|Здравствуйте|你好)\s*[,，、]\s*(?=[\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GreetingCommaPattern();

    [GeneratedRegex(
        @"^(?<prefix>\s*)(?<greeting>Hallo|Hello|Hi|Hey|Moin|Servus|Bonjour|Salut|Hola|Ciao|Olá|Ola|Hoi|Cześć|Ahoj|Hej|Hei|Moi|Привет|Здравствуйте|你好)\s*[,，、]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingGreetingCommaPattern();

    [GeneratedRegex(@"\s*[.!?…。！？]+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingSentencePunctuationPattern();
}
