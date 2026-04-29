using System.Text.RegularExpressions;

namespace TypeWhisper.Core.Services;

public sealed partial class DeveloperFormattingService
{
    private static readonly (Regex Pattern, string Replacement)[] SymbolReplacements =
    [
        (DashDashRegex(), "--"),
        (BackslashRegex(), "\\"),
        (SlashRegex(), "/"),
        (PipeRegex(), "|"),
        (OpenParenRegex(), "("),
        (CloseParenRegex(), ")"),
        (OpenBracketRegex(), "["),
        (CloseBracketRegex(), "]"),
        (OpenBraceRegex(), "{"),
        (CloseBraceRegex(), "}"),
        (DoubleQuoteRegex(), "\""),
        (SingleQuoteRegex(), "'"),
        (AtSignRegex(), "@"),
        (HashRegex(), "#"),
        (DollarRegex(), "$"),
        (AmpersandRegex(), "&"),
        (PlusRegex(), "+"),
        (MinusRegex(), "-"),
        (StarRegex(), "*"),
        (ColonRegex(), ":"),
        (SemicolonRegex(), ";"),
        (CommaRegex(), ","),
        (UnderscoreRegex(), "_"),
        (EqualsRegex(), "="),
        (DotRegex(), ".")
    ];

    public string Format(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var casing = TryFormatCasingCommand(text);
        if (casing is not null)
            return casing;

        var formatted = text;
        foreach (var (pattern, replacement) in SymbolReplacements)
            formatted = pattern.Replace(formatted, replacement);

        formatted = FlagWhitespaceRegex().Replace(formatted, "--");
        formatted = SpaceAroundSymbolsRegex().Replace(formatted, "$1");
        return RepeatedWhitespaceRegex().Replace(formatted, " ").Trim();
    }

    private static string? TryFormatCasingCommand(string text)
    {
        var match = CasingCommandRegex().Match(text.Trim());
        if (!match.Success)
            return null;

        var words = WordRegex().Matches(match.Groups["text"].Value)
            .Select(word => word.Value.ToLowerInvariant())
            .ToList();
        if (words.Count == 0)
            return "";

        return match.Groups["mode"].Value.ToLowerInvariant() switch
        {
            "camel" => words[0] + string.Concat(words.Skip(1).Select(ToTitleInvariant)),
            "snake" => string.Join('_', words),
            "kebab" => string.Join('-', words),
            _ => null
        };
    }

    private static string ToTitleInvariant(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    [GeneratedRegex(@"\bdash\s+dash\b", RegexOptions.IgnoreCase)]
    private static partial Regex DashDashRegex();

    [GeneratedRegex(@"\bback\s*slash\b", RegexOptions.IgnoreCase)]
    private static partial Regex BackslashRegex();

    [GeneratedRegex(@"\bslash\b", RegexOptions.IgnoreCase)]
    private static partial Regex SlashRegex();

    [GeneratedRegex(@"\bpipe\b", RegexOptions.IgnoreCase)]
    private static partial Regex PipeRegex();

    [GeneratedRegex(@"\bunderscore\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnderscoreRegex();

    [GeneratedRegex(@"\bequals\b", RegexOptions.IgnoreCase)]
    private static partial Regex EqualsRegex();

    [GeneratedRegex(@"\bdot\b", RegexOptions.IgnoreCase)]
    private static partial Regex DotRegex();

    [GeneratedRegex(@"\b(?:open|left)\s+paren(?:thesis)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex OpenParenRegex();

    [GeneratedRegex(@"\b(?:close|right)\s+paren(?:thesis)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex CloseParenRegex();

    [GeneratedRegex(@"\b(?:open|left)\s+bracket\b", RegexOptions.IgnoreCase)]
    private static partial Regex OpenBracketRegex();

    [GeneratedRegex(@"\b(?:close|right)\s+bracket\b", RegexOptions.IgnoreCase)]
    private static partial Regex CloseBracketRegex();

    [GeneratedRegex(@"\b(?:open|left)\s+brace\b", RegexOptions.IgnoreCase)]
    private static partial Regex OpenBraceRegex();

    [GeneratedRegex(@"\b(?:close|right)\s+brace\b", RegexOptions.IgnoreCase)]
    private static partial Regex CloseBraceRegex();

    [GeneratedRegex(@"\b(?:double\s+quote|quote)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DoubleQuoteRegex();

    [GeneratedRegex(@"\b(?:single\s+quote|apostrophe)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SingleQuoteRegex();

    [GeneratedRegex(@"\b(?:at\s+sign|at)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AtSignRegex();

    [GeneratedRegex(@"\b(?:hash|pound\s+sign)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HashRegex();

    [GeneratedRegex(@"\bdollar(?:\s+sign)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex DollarRegex();

    [GeneratedRegex(@"\b(?:ampersand|and\s+sign)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AmpersandRegex();

    [GeneratedRegex(@"\bplus\b", RegexOptions.IgnoreCase)]
    private static partial Regex PlusRegex();

    [GeneratedRegex(@"\bminus\b", RegexOptions.IgnoreCase)]
    private static partial Regex MinusRegex();

    [GeneratedRegex(@"\b(?:star|asterisk)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StarRegex();

    [GeneratedRegex(@"\bcolon\b", RegexOptions.IgnoreCase)]
    private static partial Regex ColonRegex();

    [GeneratedRegex(@"\bsemicolon\b", RegexOptions.IgnoreCase)]
    private static partial Regex SemicolonRegex();

    [GeneratedRegex(@"\bcomma\b", RegexOptions.IgnoreCase)]
    private static partial Regex CommaRegex();

    [GeneratedRegex(@"--\s+")]
    private static partial Regex FlagWhitespaceRegex();

    [GeneratedRegex(@"\s*([|/\\=._()[\]{}""'@#$&+*:;,])\s*")]
    private static partial Regex SpaceAroundSymbolsRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex RepeatedWhitespaceRegex();

    [GeneratedRegex(@"^(?<mode>camel|snake|kebab)\s+case\s+(?<text>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex CasingCommandRegex();

    [GeneratedRegex(@"[A-Za-z0-9]+")]
    private static partial Regex WordRegex();
}
