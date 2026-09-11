using System.Text.RegularExpressions;

namespace TypeWhisper.Core.Services;

/// <summary>Respells whitespace-delimited prose tokens while leaving literal tokens untouched.</summary>
internal static partial class OutputLiteralTokens
{
    private static readonly char[] s_trailingProsePunctuation = [';', ':', ',', '.', '!', '?', ')', ']', '"', '\'', '…'];

    // URLs, e-mail addresses, paths and identifiers are literals: rewriting a word inside them
    // breaks the destination, so only prose tokens are handed to the respeller.
    public static string RespellProse(string text, Func<string, string> respellToken)
    {
        return Token().Replace(
            text,
            token => LiteralMarker().IsMatch(token.Value.TrimEnd(s_trailingProsePunctuation))
                ? token.Value
                : respellToken(token.Value));
    }

    [GeneratedRegex(@"\S+", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex(@"[/\\@_=:;{}<>`#$\d]|\w\.\w", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralMarker();
}
