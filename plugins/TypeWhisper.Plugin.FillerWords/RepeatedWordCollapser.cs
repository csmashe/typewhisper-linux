using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TypeWhisper.Plugin.FillerWords;

/// <summary>
///     Collapses a word spoken three or more times in a row, such as a stuttered "I I I", to its
///     first occurrence. Two in a row stay because they are often meant ("very very good").
/// </summary>
public static class RepeatedWordCollapser
{
    // Repeated digits are a value, not a stutter: "zero point zero zero zero five" and "nine nine
    // nine" must reach number normalization intact. Spelled without diacritics, as the number
    // parsers compare them, so "fünf", "funf" and "fuenf" all match.
    private static readonly HashSet<string> s_digitWords = new(
        [
            "zero", "oh", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
            "null", "eins", "zwei", "drei", "vier", "funf", "fuenf", "sechs", "sieben", "acht", "neun",
            "cero", "uno", "dos", "tres", "cuatro", "cinco", "seis", "siete", "ocho", "nueve",
            "ноль", "нуль", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Replaces each run of three or more identical words with its first word.</summary>
    public static string Collapse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        try
        {
            return s_repetition.Replace(text, static match =>
            {
                var word = match.Groups["word"].Value;
                return s_digitWords.Contains(StripDiacritics(word)) ? match.Value : word;
            });
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological input must not hold up the rest of post-processing.
            return text;
        }
    }

    private static string StripDiacritics(string word)
    {
        var decomposed = word.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator -- keeps string's struct enumerator, as NumberWordNormalizer.NormalizeWord does.
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    // Repetitions are separated by spaces, optionally after a comma (Parakeet writes "ich, ich,
    // ich"); other punctuation or a line break means they were meant. Inner apostrophes, dashes,
    // combining marks, dots and @ belong to the word, so "I I I'm", "test test test-case",
    // "कर कर करो" and "example example example.com" have only two. Not [GeneratedRegex]: it cannot
    // emit a case-insensitive backreference (SYSLIB1044).
    private static readonly Regex s_repetition = new(
        @"(?<![\p{L}\p{M}\p{N}_\p{Pd}]|[\p{L}\p{M}\p{N}]['’]|[.@][\p{L}\p{M}\p{N}]*)(?<word>\p{L}[\p{L}\p{M}]*(?:['’]\p{L}[\p{L}\p{M}]*)*)(?:,?[ \t　]+\k<word>(?![\p{L}\p{M}\p{N}_\p{Pd}])(?![.@'’][\p{L}\p{N}])){2,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));
}
