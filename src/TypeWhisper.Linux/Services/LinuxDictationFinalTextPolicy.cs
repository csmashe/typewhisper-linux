using System.Text;
using System.Text.RegularExpressions;

namespace TypeWhisper.Linux.Services;

internal static partial class LinuxDictationFinalTextPolicy
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

    // Rolling hashes use explicit, stable per-token hashes. Two independent
    // polynomial bases make accidental range collisions vanishingly unlikely;
    // matching ranges are still compared token-by-token before removal.
    private const ulong StableTokenHashOffset = 14_695_981_039_346_656_037UL;
    private const ulong StableTokenHashPrime = 1_099_511_628_211UL;
    private const ulong FirstRollingHashBase = 1_000_000_007UL;
    private const ulong SecondRollingHashBase = 1_000_000_009UL;

    [GeneratedRegex(@"\s*(?:\.{3,}|…)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex AutomaticEllipsisRegex();

    public static string SelectRawText(string? finalText)
    {
        return NormalizeDictationArtifacts(finalText?.Trim() ?? "");
    }

    private static string NormalizeDictationArtifacts(string text)
    {
        return RemoveAutomaticEllipses(ReduceAdjacentRepeatedPhrases(text));
    }

    private static string RemoveAutomaticEllipses(string text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? ""
            : AutomaticEllipsisRegex().Replace(text, " ").Trim();
    }

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
            if (tokens.Count < MinimumRepeatedPhraseWords * 2
                || !TryFindAdjacentRepeatedPhrase(reduced, tokens, out var removalStart, out var removalEnd))
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

        var characterPrefix = new int[tokens.Count + 1];
        var tokenHashes = new ulong[tokens.Count];
        var firstHashPrefix = new ulong[tokens.Count + 1];
        var secondHashPrefix = new ulong[tokens.Count + 1];
        var firstHashPowers = new ulong[tokens.Count + 1];
        var secondHashPowers = new ulong[tokens.Count + 1];
        firstHashPowers[0] = 1;
        secondHashPowers[0] = 1;

        for (var i = 0; i < tokens.Count; i++)
        {
            characterPrefix[i + 1] = characterPrefix[i] + tokens[i].Normalized.Length;
            tokenHashes[i] = ComputeStableTokenHash(tokens[i].Normalized);
            firstHashPrefix[i + 1] = unchecked(firstHashPrefix[i] * FirstRollingHashBase + tokenHashes[i]);
            secondHashPrefix[i + 1] = unchecked(secondHashPrefix[i] * SecondRollingHashBase + tokenHashes[i]);
            firstHashPowers[i + 1] = unchecked(firstHashPowers[i] * FirstRollingHashBase);
            secondHashPowers[i + 1] = unchecked(secondHashPowers[i] * SecondRollingHashBase);
        }

        for (var boundary = MinimumRepeatedPhraseWords;
             boundary <= tokens.Count - MinimumRepeatedPhraseWords;
             boundary++)
        {
            var maxLength = Math.Min(boundary, tokens.Count - boundary);
            for (var length = maxLength; length >= MinimumRepeatedPhraseWords; length--)
            {
                if (!HasMinimumRepeatedPhraseLength(characterPrefix, boundary, length)
                    || !TokensMatch(
                        tokens,
                        firstHashPrefix,
                        secondHashPrefix,
                        firstHashPowers,
                        secondHashPowers,
                        boundary - length,
                        boundary,
                        length))
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

    private static bool HasMinimumRepeatedPhraseLength(int[] characterPrefix, int boundary, int length)
    {
        var characterCount = characterPrefix[boundary + length] - characterPrefix[boundary];
        return characterCount >= MinimumRepeatedPhraseCharacters;
    }

    private static bool TokensMatch(
        IReadOnlyList<WordToken> tokens,
        ulong[] firstHashPrefix,
        ulong[] secondHashPrefix,
        ulong[] firstHashPowers,
        ulong[] secondHashPowers,
        int leftStart,
        int rightStart,
        int length)
    {
        if (GetRangeHash(firstHashPrefix, firstHashPowers, leftStart, length)
                != GetRangeHash(firstHashPrefix, firstHashPowers, rightStart, length)
            || GetRangeHash(secondHashPrefix, secondHashPowers, leftStart, length)
                != GetRangeHash(secondHashPrefix, secondHashPowers, rightStart, length))
        {
            return false;
        }

        // Hashes only reject non-matches. Exact comparison preserves behavior
        // even on a rolling-hash or per-token-hash collision.
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

    private static ulong GetRangeHash(
        ulong[] hashPrefix,
        ulong[] hashPowers,
        int start,
        int length)
    {
        return unchecked(hashPrefix[start + length] - hashPrefix[start] * hashPowers[length]);
    }

    private static ulong ComputeStableTokenHash(string normalized)
    {
        var hash = StableTokenHashOffset;
        // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator -- hot unchecked FNV accumulation; a LINQ Aggregate switches the char enumerator and is slower here.
        foreach (var ch in normalized)
        {
            hash = unchecked((hash ^ ch) * StableTokenHashPrime);
        }

        return hash;
    }

    private static bool RightMatchContinuesPhrase(string text, IReadOnlyList<WordToken> tokens, int boundary,
        int length)
    {
        var rightLastIndex = boundary + length - 1;
        if (rightLastIndex >= tokens.Count - 1)
        {
            return false;
        }

        var separator = text.AsSpan(tokens[rightLastIndex].End,
            tokens[rightLastIndex + 1].Start - tokens[rightLastIndex].End);
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
