using System.Globalization;
using System.Text;

namespace TypeWhisper.Core.Services.NumberNormalization;

public static class NumberWordNormalizer
{
    // fr/zh/ja parsers from upstream are intentionally not ported on Linux (locales are en/de/es/ru),
    // so those language codes are omitted from the supported set.
    private static readonly HashSet<string> s_supportedLanguageCodes = ["en", "de", "es"];

    public static string Normalize(string text, string? language)
    {
        var languageCode = NormalizeLanguageCode(language);
        if (languageCode is null || !s_supportedLanguageCodes.Contains(languageCode) || string.IsNullOrEmpty(text))
            return text;

        var tokens = Tokenize(text);
        if (!tokens.Any(static token => token.IsWord))
            return text;

        var result = new StringBuilder();
        var index = 0;
        while (index < tokens.Count)
        {
            // Don't convert a spoken number that immediately follows a digit (e.g. "2 Millionen",
            // "2 mil"): the digit already carries the count, so treating the trailing scale word as
            // a standalone number would corrupt already-digit text into "2 1000000".
            if (tokens[index].IsWord &&
                !FollowsDigit(index, tokens) &&
                ParseNumber(index, tokens, languageCode) is { } parsed)
            {
                result.Append(parsed.Replacement);
                index = parsed.EndIndex;
            }
            else
            {
                result.Append(tokens[index].Text);
                index++;
            }
        }

        return result.ToString();
    }

    internal static string? NormalizeLanguageCode(string? language)
    {
        var trimmed = language?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        var separatorIndex = trimmed.IndexOfAny(['-', '_']);
        var primary = separatorIndex >= 0 ? trimmed[..separatorIndex] : trimmed;
        var normalized = primary.Trim().ToLowerInvariant();
        return normalized.Length == 0 ? null : normalized;
    }

    internal sealed record ParsedWords(string Value, int ConsumedWords);

    internal static string NormalizeWord(string word)
    {
        var normalized = word.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized.Where(static c =>
                     CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
            builder.Append(c);

        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    private static ParsedNumber? ParseNumber(int index, IReadOnlyList<Token> tokens, string languageCode)
    {
        var words = WordCandidates(index, tokens);
        if (words.Count == 0)
            return null;

        var wordTexts = words.Select(static word => word.Text).ToArray();
        var parsed = languageCode switch
        {
            "en" => EnglishNumberWordParser.Parse(wordTexts),
            "de" => GermanNumberWordParser.Parse(wordTexts),
            "es" => SpanishNumberWordParser.Parse(wordTexts),
            _ => null,
        };

        if (parsed is null || parsed.ConsumedWords <= 0 || parsed.ConsumedWords > words.Count)
            return null;

        var finalTokenIndex = words[parsed.ConsumedWords - 1].TokenIndex;
        return new ParsedNumber(parsed.Value, finalTokenIndex + 1);
    }

    private static bool FollowsDigit(int index, IReadOnlyList<Token> tokens)
    {
        // Skip a single connector separator (whitespace/hyphen) between the digit and the word run.
        var previous = index - 1;
        if (previous >= 0 && !tokens[previous].IsWord && IsWordConnector(tokens[previous].Text))
            previous--;

        if (previous < 0 || tokens[previous].IsWord)
            return false;

        // Tokenize groups a digit and its trailing whitespace into one Other token (e.g. "2 "),
        // so the final char is whitespace. Scan back past trailing whitespace to the last
        // meaningful character before deciding whether a digit immediately precedes the word.
        var text = tokens[previous].Text;
        var lastNonWhitespace = text.Length - 1;
        while (lastNonWhitespace >= 0 && char.IsWhiteSpace(text[lastNonWhitespace]))
            lastNonWhitespace--;

        return lastNonWhitespace >= 0 && char.IsDigit(text[lastNonWhitespace]);
    }

    private static List<WordCandidate> WordCandidates(int index, IReadOnlyList<Token> tokens)
    {
        var words = new List<WordCandidate>();
        var current = index;

        while (current < tokens.Count && tokens[current].IsWord)
        {
            words.Add(new WordCandidate(current, tokens[current].Text));

            var separatorIndex = current + 1;
            var nextWordIndex = current + 2;
            if (separatorIndex >= tokens.Count ||
                nextWordIndex >= tokens.Count ||
                !tokens[nextWordIndex].IsWord ||
                !IsWordConnector(tokens[separatorIndex].Text))
            {
                break;
            }

            current = nextWordIndex;
        }

        return words;
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var current = new StringBuilder();
        TokenKind? currentKind = null;

        foreach (var character in text)
        {
            var kind = TokenKindFor(character);
            if (currentKind == kind)
            {
                current.Append(character);
                continue;
            }

            if (current.Length > 0 && currentKind is { } previousKind)
                tokens.Add(new Token(current.ToString(), previousKind));

            current.Clear();
            current.Append(character);
            currentKind = kind;
        }

        if (current.Length > 0 && currentKind is { } finalKind)
            tokens.Add(new Token(current.ToString(), finalKind));

        return tokens;
    }

    private static TokenKind TokenKindFor(char character) =>
        char.IsLetter(character) ? TokenKind.Word : TokenKind.Other;

    private static bool IsWordConnector(string text) =>
        text.Length > 0 && text.All(static c => char.IsWhiteSpace(c) || c == '-' || c == '‑');

    private enum TokenKind
    {
        Word,
        Other,
    }

    private sealed record Token(string Text, TokenKind Kind)
    {
        public bool IsWord => Kind is TokenKind.Word;
    }

    private sealed record ParsedNumber(string Replacement, int EndIndex);
    private sealed record WordCandidate(int TokenIndex, string Text);
}
