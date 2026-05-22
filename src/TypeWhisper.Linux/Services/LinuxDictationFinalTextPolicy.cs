using System.Text;

namespace TypeWhisper.Linux.Services;

internal static class LinuxDictationFinalTextPolicy
{
    // Adjacent-repeat thresholds: a candidate phrase must be at least this many
    // words and this many normalized characters before it is collapsed. Short
    // (post #117) values catch brief stutters like "and set the and set the"
    // while still preserving intentional short repeats ("yes yes").
    private const int MinimumRepeatedPhraseWords = 3;
    private const int MinimumRepeatedPhraseCharacters = 8;

    // Removing one repeated phrase can expose another; cap the rescan loop so a
    // pathological transcript can never spin indefinitely.
    private const int MaximumRepeatReductionPasses = 8;

    public static string SelectRawText(string? finalText) =>
        ReduceAdjacentRepeatedPhrases(finalText?.Trim() ?? "");

    private static string ReduceAdjacentRepeatedPhrases(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var reduced = text;
        for (var pass = 0; pass < MaximumRepeatReductionPasses; pass++)
        {
            var tokens = TokenizeWords(reduced);
            if (tokens.Count < MinimumRepeatedPhraseWords * 2)
            {
                return reduced.Trim();
            }

            if (!TryFindAdjacentRepeatedPhrase(reduced, tokens, out var removalStart, out var removalEnd))
            {
                return reduced.Trim();
            }

            reduced = string.Concat(reduced.AsSpan(0, removalStart), reduced.AsSpan(removalEnd)).Trim();
        }

        return reduced.Trim();
    }

    private static bool TryFindAdjacentRepeatedPhrase(
        string text,
        IReadOnlyList<WordToken> tokens,
        out int removalStart,
        out int removalEnd)
    {
        removalStart = 0;
        removalEnd = 0;

        for (var boundary = MinimumRepeatedPhraseWords; boundary <= tokens.Count - MinimumRepeatedPhraseWords; boundary++)
        {
            var maxLength = Math.Min(boundary, tokens.Count - boundary);
            for (var length = maxLength; length >= MinimumRepeatedPhraseWords; length--)
            {
                if (!HasMinimumRepeatedPhraseLength(tokens, boundary, length)
                    || !TokensMatch(tokens, boundary - length, boundary, length))
                {
                    continue;
                }

                if (RightMatchContinuesPhrase(text, tokens, boundary, length))
                {
                    removalStart = tokens[boundary - length].Start;
                    removalEnd = tokens[boundary].Start;
                }
                else
                {
                    removalStart = tokens[boundary].Start;
                    removalEnd = boundary + length < tokens.Count
                        ? tokens[boundary + length].Start
                        : text.Length;
                }

                return true;
            }
        }

        return false;
    }

    private static bool HasMinimumRepeatedPhraseLength(IReadOnlyList<WordToken> tokens, int boundary, int length)
    {
        var characterCount = 0;
        for (var i = boundary; i < boundary + length; i++)
        {
            characterCount += tokens[i].Normalized.Length;
        }

        return characterCount >= MinimumRepeatedPhraseCharacters;
    }

    private static bool TokensMatch(IReadOnlyList<WordToken> tokens, int leftStart, int rightStart, int length)
    {
        for (var offset = 0; offset < length; offset++)
        {
            if (!string.Equals(
                    tokens[leftStart + offset].Normalized,
                    tokens[rightStart + offset].Normalized,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RightMatchContinuesPhrase(string text, IReadOnlyList<WordToken> tokens, int boundary, int length)
    {
        var rightLastIndex = boundary + length - 1;
        if (rightLastIndex >= tokens.Count - 1)
        {
            return false;
        }

        var separator = text.AsSpan(tokens[rightLastIndex].End, tokens[rightLastIndex + 1].Start - tokens[rightLastIndex].End);
        foreach (var ch in separator)
        {
            if (ch is '.' or '!' or '?' or '\r' or '\n')
            {
                return false;
            }
        }

        return true;
    }

    private static List<WordToken> TokenizeWords(string text)
    {
        var tokens = new List<WordToken>();
        var index = 0;

        while (index < text.Length)
        {
            while (index < text.Length && !char.IsLetterOrDigit(text[index]))
            {
                index++;
            }

            var start = index;
            while (index < text.Length && char.IsLetterOrDigit(text[index]))
            {
                index++;
            }

            if (start == index)
            {
                continue;
            }

            tokens.Add(new WordToken(start, index, NormalizeWord(text[start..index])));
        }

        return tokens;
    }

    private static string NormalizeWord(string word)
    {
        var builder = new StringBuilder(word.Length);
        foreach (var ch in word)
        {
            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    private readonly record struct WordToken(int Start, int End, string Normalized);
}
