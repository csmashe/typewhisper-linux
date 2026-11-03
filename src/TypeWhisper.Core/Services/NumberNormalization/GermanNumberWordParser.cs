using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace TypeWhisper.Core.Services.NumberNormalization;

// The IReadOnlyList<string> parser contract is intentional: it keeps a uniform,
// upstream-parity signature across all language parsers. CA1859's array micro-optimization
// is irrelevant for these tiny (<10-element) word spans, so it is suppressed here.
[SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Uniform IReadOnlyList parser contract; negligible perf impact on tiny word spans.")]
internal static class GermanNumberWordParser
{
    private static readonly Dictionary<string, int> s_units = new(StringComparer.Ordinal)
    {
        ["null"] = 0, ["eins"] = 1, ["ein"] = 1, ["eine"] = 1, ["einen"] = 1, ["einem"] = 1, ["einer"] = 1,
        ["zwei"] = 2, ["drei"] = 3, ["vier"] = 4, ["funf"] = 5, ["fuenf"] = 5,
        ["sechs"] = 6, ["sieben"] = 7, ["acht"] = 8, ["neun"] = 9,
    };

    private static readonly Dictionary<string, int> s_teens = new(StringComparer.Ordinal)
    {
        ["zehn"] = 10, ["elf"] = 11, ["zwolf"] = 12, ["zwoelf"] = 12, ["dreizehn"] = 13, ["vierzehn"] = 14,
        ["funfzehn"] = 15, ["fuenfzehn"] = 15, ["sechzehn"] = 16, ["siebzehn"] = 17,
        ["achtzehn"] = 18, ["neunzehn"] = 19,
    };

    private static readonly Dictionary<string, int> s_tens = new(StringComparer.Ordinal)
    {
        ["zwanzig"] = 20, ["dreissig"] = 30, ["dreizig"] = 30, ["vierzig"] = 40,
        ["funfzig"] = 50, ["fuenfzig"] = 50, ["sechzig"] = 60, ["siebzig"] = 70,
        ["achtzig"] = 80, ["neunzig"] = 90,
    };

    public static NumberWordNormalizer.ParsedWords? Parse(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
            return null;

        var normalizedWords = words.Select(NormalizeWord).ToArray();
        var index = 0;
        var isNegative = false;

        if (normalizedWords[index] == "minus")
        {
            isNegative = true;
            index++;
            if (index >= normalizedWords.Length)
                return null;
        }

        var integer = ParseInteger(normalizedWords, index);
        if (integer is null)
            return null;

        index = integer.Value.NextIndex;
        var replacement = integer.Value.Value.ToString(CultureInfo.InvariantCulture);

        if (index < normalizedWords.Length && normalizedWords[index] == "komma")
        {
            var decimalPart = ParseDecimalDigits(normalizedWords, index + 1);
            if (decimalPart.Digits.Length > 0)
            {
                replacement += "," + decimalPart.Digits;
                index = decimalPart.NextIndex;
            }
        }

        if (isNegative)
            replacement = "-" + replacement;

        return new NumberWordNormalizer.ParsedWords(replacement, index);
    }

    private static (int Value, int NextIndex)? ParseInteger(IReadOnlyList<string> words, int startIndex)
    {
        if (startIndex >= words.Count)
            return null;

        var total = 0;
        var current = 0;
        var index = startIndex;
        var consumed = false;
        var lastWasPlainSmallNumber = false;

        while (index < words.Count)
        {
            var word = words[index];

            // ReSharper disable once ConvertIfStatementToSwitchStatement -- the branches guard on
            // more than `word`, and each one `continue`s the enclosing while; a switch would need
            // when-clauses and read worse.
            if (word == "und" &&
                current is > 0 and < 10 &&
                index + 1 < words.Count &&
                s_tens.TryGetValue(words[index + 1], out var tenValue))
            {
                current += tenValue;
                index += 2;
                consumed = true;
                lastWasPlainSmallNumber = false;
                continue;
            }

            if (word == "hundert")
            {
                current = Math.Max(current, 1) * 100;
                index++;
                consumed = true;
                lastWasPlainSmallNumber = false;
                continue;
            }

            if (word is "tausend" or "million" or "millionen")
            {
                // Without a leading count these are nouns, not numbers ("Millionen von
                // Menschen", "eine halbe Million"). "eine Million" still converts:
                // AllowsArticleOne consumes the article as 1 before this branch is reached.
                if (word is "million" or "millionen" && current == 0)
                    break;

                var scale = word == "tausend" ? 1_000 : 1_000_000;
                total += Math.Max(current, 1) * scale;
                current = 0;
                index++;
                consumed = true;
                lastWasPlainSmallNumber = false;
                continue;
            }

            var allowsArticleOne = AllowsArticleOne(index, words);
            var value = ParseCompound(word, allowsArticleOne);
            if (value is null)
                break;

            if (lastWasPlainSmallNumber && value.Value < 10)
                break;

            current += value.Value;
            index++;
            consumed = true;
            lastWasPlainSmallNumber = value.Value < 10 && !allowsArticleOne;
        }

        return consumed ? (total + current, index) : null;
    }

    private static (string Digits, int NextIndex) ParseDecimalDigits(IReadOnlyList<string> words, int startIndex)
    {
        var digits = new StringBuilder();
        var index = startIndex;

        while (index < words.Count && DigitValue(words[index]) is { } digit)
        {
            digits.Append(digit.ToString(CultureInfo.InvariantCulture));
            index++;
        }

        return (digits.ToString(), index);
    }

    private static int? ParseCompound(string word, bool allowArticleOne)
    {
        if (DirectValue(word, allowArticleOne) is { } direct)
            return direct;

        var thousandIndex = word.IndexOf("tausend", StringComparison.Ordinal);
        if (thousandIndex >= 0)
        {
            var prefix = word[..thousandIndex];
            var suffix = word[(thousandIndex + "tausend".Length)..];
            var prefixValue = prefix.Length == 0 ? 1 : ParseCompound(prefix, true);
            if (prefixValue is null)
                return null;
            var suffixValue = suffix.Length == 0 ? 0 : ParseCompound(suffix, true);
            return suffixValue is null ? null : prefixValue.Value * 1_000 + suffixValue.Value;
        }

        var hundredIndex = word.IndexOf("hundert", StringComparison.Ordinal);
        // ReSharper disable once InvertIf -- mirrors the "tausend" branch above; inverting would
        // hoist prefix/suffix to method scope and collide with that branch's locals (CS0136).
        if (hundredIndex >= 0)
        {
            var prefix = word[..hundredIndex];
            var suffix = word[(hundredIndex + "hundert".Length)..];
            var prefixValue = prefix.Length == 0 ? 1 : ParseUnderHundred(prefix, true);
            if (prefixValue is null)
                return null;
            var suffixValue = suffix.Length == 0 ? 0 : ParseUnderHundred(suffix, true);
            return suffixValue is null ? null : prefixValue.Value * 100 + suffixValue.Value;
        }

        return ParseUnderHundred(word, allowArticleOne);
    }

    private static int? ParseUnderHundred(string word, bool allowArticleOne)
    {
        if (DirectValue(word, allowArticleOne) is { } direct)
            return direct;

        var undIndex = word.IndexOf("und", StringComparison.Ordinal);
        if (undIndex < 0)
            return null;

        var prefix = word[..undIndex];
        var suffix = word[(undIndex + "und".Length)..];
        return DirectUnitValue(prefix, true) is { } unit and > 0 and < 10 &&
               s_tens.TryGetValue(suffix, out var tenValue)
            ? unit + tenValue
            : null;
    }

    private static int? DirectValue(string word, bool allowArticleOne) =>
        DirectUnitValue(word, allowArticleOne)
        ?? (s_teens.TryGetValue(word, out var teen)
            ? teen
            : s_tens.TryGetValue(word, out var ten)
                ? ten
                : null);

    private static int? DirectUnitValue(string word, bool allowArticleOne)
    {
        if (!s_units.TryGetValue(word, out var value))
            return null;

        if (value == 1 && word != "eins" && !allowArticleOne)
            return null;

        return value;
    }

    private static int? DigitValue(string word) => DirectUnitValue(word, false);

    private static bool AllowsArticleOne(int index, IReadOnlyList<string> words) =>
        index + 1 < words.Count && words[index + 1] is "hundert" or "tausend" or "million" or "millionen";

    private static string NormalizeWord(string word) =>
        NumberWordNormalizer.NormalizeWord(word).Replace("ß", "ss", StringComparison.Ordinal);
}
