using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
///     Derives a learnable find/replace <see cref="CorrectionSuggestion" /> from a single user edit
///     by diffing the inserted and corrected text, but only for small, safe word-level changes.
/// </summary>
public sealed class CorrectionSuggestionService
{
    private const int MaxChangedWords = 5;

    public static IReadOnlyList<CorrectionSuggestion> GenerateSuggestions(
        string insertedText,
        string correctedText
    )
    {
        var originalTokens = Tokenize(insertedText);
        var correctedTokens = Tokenize(correctedText);

        if (originalTokens.Count == 0 || correctedTokens.Count == 0)
        {
            return [];
        }

        var prefixLength = CountCommonPrefix(originalTokens, correctedTokens);
        var suffixLength = CountCommonSuffix(originalTokens, correctedTokens, prefixLength);

        var originalChanged = originalTokens
            .Skip(prefixLength)
            .Take(originalTokens.Count - prefixLength - suffixLength)
            .ToList();
        var correctedChanged = correctedTokens
            .Skip(prefixLength)
            .Take(correctedTokens.Count - prefixLength - suffixLength)
            .ToList();

        if (
            !IsSuggestionSafe(
                originalTokens.Count,
                correctedTokens.Count,
                originalChanged,
                correctedChanged
            )
        )
        {
            return [];
        }

        var original = string.Join(' ', originalChanged.Select(token => token.Trimmed));
        var replacement = string.Join(' ', correctedChanged.Select(token => token.Trimmed));
        if (string.Equals(original, replacement, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var changedWordCount = Math.Max(originalChanged.Count, correctedChanged.Count);
        var totalWordCount = Math.Max(originalTokens.Count, correctedTokens.Count);
        // Confidence is the fraction of unchanged words, clamped so it is never zero or perfect.
        var confidence = Math.Clamp(1.0 - changedWordCount / (double)totalWordCount, 0.1, 0.95);

        return
        [
            new CorrectionSuggestion
            {
                Original = original, Replacement = replacement, Confidence = Math.Round(confidence, 2),
            },
        ];
    }

    private static bool IsSuggestionSafe(
        int originalTokenCount,
        int correctedTokenCount,
        IReadOnlyList<Token> originalChanged,
        IReadOnlyList<Token> correctedChanged
    )
    {
        if (originalChanged.Count == 0 || correctedChanged.Count == 0)
        {
            return false;
        }

        if (originalChanged.Count > MaxChangedWords || correctedChanged.Count > MaxChangedWords)
        {
            return false;
        }

        // If more than half the words changed, treat the edit as a full rewrite rather than a correction
        var maxTotal = Math.Max(originalTokenCount, correctedTokenCount);
        var maxChanged = Math.Max(originalChanged.Count, correctedChanged.Count);
        if (maxTotal > 3 && maxChanged > maxTotal / 2)
        {
            return false;
        }

        // Reject any changed token that straddles a line break. Tokenize only splits on
        // spaces, so a word adjacent to a newline gloms into one token (e.g. "foo\nbar");
        // a correction that embeds a line break is never a sensible word-level fix in
        // either the history or target-app flow.
        if (
            originalChanged
                .Concat(correctedChanged)
                .Any(token => token.Trimmed.Contains('\n') || token.Trimmed.Contains('\r'))
        )
        {
            return false;
        }

        // Avoid learning apostrophe-only churn from contractions or straight-vs-curly quote changes.
        return !originalChanged
            .Concat(correctedChanged)
            .Any(token => token.Trimmed.Contains('\'') || token.Trimmed.Contains('’'));
    }

    private static int CountCommonPrefix(
        IReadOnlyList<Token> original,
        IReadOnlyList<Token> corrected
    )
    {
        var count = 0;
        while (
            count < original.Count
            && count < corrected.Count
            && TokensEqual(original[count], corrected[count])
        )
        {
            count++;
        }

        return count;
    }

    private static int CountCommonSuffix(
        IReadOnlyList<Token> original,
        IReadOnlyList<Token> corrected,
        int prefixLength
    )
    {
        var count = 0;
        while (
            original.Count - count - 1 >= prefixLength
            && corrected.Count - count - 1 >= prefixLength
            && TokensEqual(
                original[original.Count - count - 1],
                corrected[corrected.Count - count - 1]
            )
        )
        {
            count++;
        }

        return count;
    }

    private static bool TokensEqual(Token left, Token right)
    {
        return string.Equals(left.Normalized, right.Normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static List<Token> Tokenize(string text)
    {
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token =>
            {
                var trimmed = token.Trim(
                    ' ',
                    '\t',
                    '\r',
                    '\n',
                    '.',
                    ',',
                    ';',
                    ':',
                    '!',
                    '?',
                    '"',
                    '(',
                    ')',
                    '[',
                    ']'
                );
                return new Token(trimmed, trimmed.ToUpperInvariant());
            })
            .Where(token => token.Trimmed.Length > 0)
            .ToList();
    }

    private sealed record Token(string Trimmed, string Normalized);
}