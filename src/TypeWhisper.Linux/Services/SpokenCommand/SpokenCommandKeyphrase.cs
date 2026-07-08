using System.Text;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Linux.Services.SpokenCommand;

/// <summary>
///     Detects and strips a spoken command keyphrase (default "TypeWhisper") from
///     the start of a raw transcript. Matching is deliberately forgiving: speech-to-text
///     routinely splits the product name into two words ("type whisper") or fuzzes it
///     ("typewhisperer"), so the leading token(s) are normalized (lowercased, punctuation
///     and spaces removed) and compared to the normalized keyphrase with a small
///     Levenshtein tolerance. The remainder after the keyphrase is returned as the command.
/// </summary>
public static class SpokenCommandKeyphrase
{
    // Punctuation the keyphrase or STT can leave leading the command ("TypeWhisper, …",
    // "TypeWhisper: …", "TypeWhisper - …"). Stripped before returning the remainder.
    private static readonly char[] s_leadingSeparators =
        [',', '.', ':', ';', '-', '–', '—', '!', '?', '…'];

    /// <summary>
    ///     If <paramref name="rawText" /> begins with the (fuzzy) keyphrase, sets
    ///     <paramref name="command" /> to the trimmed remainder and returns true.
    ///     Returns false when the keyphrase is absent, when the keyphrase is spoken
    ///     bare (no command follows), or when either input is blank.
    /// </summary>
    public static bool TryStrip(string rawText, string keyphrase, out string command)
    {
        command = string.Empty;
        if (string.IsNullOrWhiteSpace(rawText) || string.IsNullOrWhiteSpace(keyphrase))
        {
            return false;
        }

        var normalizedKeyphrase = Normalize(keyphrase);
        if (normalizedKeyphrase.Length == 0)
        {
            return false;
        }

        // Scale the allowed edit distance to the keyphrase length so a short custom
        // keyphrase can't over-match common words, while the long default name still
        // tolerates STT fuzzing ("typewhisperer" is distance 2 from "typewhisper").
        var maxDistance = normalizedKeyphrase.Length switch
        {
            <= 3 => 0,
            <= 6 => 1,
            _ => 2
        };

        var tokens = Tokenize(rawText);
        if (tokens.Count == 0)
        {
            return false;
        }

        // STT can split a one-word keyphrase into a couple of fragments, so try folding
        // up to the keyphrase's own word count plus a small slack of leading tokens.
        var keyphraseWordCount = KeyphraseWordCount(keyphrase);
        var maxTokens = Math.Min(tokens.Count, keyphraseWordCount + 2);
        var accumulated = new StringBuilder();
        for (var count = 1; count <= maxTokens; count++)
        {
            accumulated.Append(tokens[count - 1].Normalized);
            if (accumulated.Length == 0)
            {
                continue;
            }

            // Give the canonical one-extra-token split ("type whisperer") the full edit budget, but
            // charge a rising penalty for folding 2+ extra tokens so unrelated dictated words
            // ("Type this per …") can't concatenate their way into a match.
            var extraFolds = Math.Max(0, count - keyphraseWordCount);
            var foldPenalty = extraFolds <= 1 ? 0 : extraFolds;
            var allowedDistance = Math.Max(0, maxDistance - foldPenalty);
            if (StringDistance.Levenshtein(accumulated.ToString(), normalizedKeyphrase) > allowedDistance)
            {
                continue;
            }

            var remainder = rawText[tokens[count - 1].EndOffset..];
            command = remainder.Trim().TrimStart(s_leadingSeparators).Trim();
            return command.Length > 0;
        }

        return false;
    }

    private static int KeyphraseWordCount(string keyphrase)
    {
        return keyphrase.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        ).Length;
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator -- the loop mutates a StringBuilder; a LINQ rewrite would switch enumerators and be less clear.
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    // Splits on whitespace, returning each token's normalized form and the offset in the
    // original string just past the token — used to slice the command remainder with its
    // original spacing/punctuation intact.
    private static List<(string Normalized, int EndOffset)> Tokenize(string text)
    {
        var tokens = new List<(string, int)>();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length)
            {
                break;
            }

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            tokens.Add((Normalize(text[start..i]), i));
        }

        return tokens;
    }
}
