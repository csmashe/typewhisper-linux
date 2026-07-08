using System.Text;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Linux.Services.SpokenCommand;

/// <summary>
///     Deterministically matches a spoken command to a saved prompt action by name, or returns null
///     so the caller falls back to an ad-hoc transform. "clean up email" runs the saved "Clean up
///     email" prompt; "shorten this" matches nothing.
/// </summary>
public static class SpokenCommandActionMatcher
{
    // A 1–3 letter action name would match far too much.
    private const int MinNameLength = 4;

    public static PromptAction? Match(string command, IReadOnlyList<PromptAction> actions)
    {
        if (string.IsNullOrWhiteSpace(command) || actions.Count == 0)
        {
            return null;
        }

        var commandTokens = Tokenize(command);
        if (commandTokens.Count == 0)
        {
            return null;
        }

        var commandCompact = Normalize(command);

        PromptAction? best = null;
        var bestScore = 0;
        foreach (var action in actions)
        {
            var nameCompact = Normalize(action.Name);
            if (nameCompact.Length < MinNameLength)
            {
                continue;
            }

            var nameTokens = Tokenize(action.Name);
            if (nameTokens.Count == 0)
            {
                continue;
            }

            var allTokensPresent = nameTokens.All(nameToken =>
                commandTokens.Any(commandToken => TokensSimilar(nameToken, commandToken)));

            // A lone-word name matched anywhere is too weak: a create command that merely mentions the
            // word ("draft an email to Bob") would hijack an "Email" action and force the edit branch.
            // Require a single-word name to lead the command, where a named invocation puts it; a
            // multi-word name needing every word present is already specific enough to match anywhere.
            if (nameTokens.Count == 1 && !TokensSimilar(nameTokens[0], commandTokens[0]))
            {
                allTokensPresent = false;
            }

            // Multi-word names only, so STT word-joins ("cleanup email") still match; a single short
            // word appearing anywhere in a long command is too weak a signal.
            var compactContained = nameTokens.Count >= 2
                                   && commandCompact.Contains(nameCompact, StringComparison.Ordinal);

            if (!allTokensPresent && !compactContained)
            {
                continue;
            }

            // Prefer the most specific (longest-name) match.
            if (nameCompact.Length <= bestScore)
            {
                continue;
            }

            bestScore = nameCompact.Length;
            best = action;
        }

        return best;
    }

    private static bool TokensSimilar(string a, string b)
    {
        return a == b
               || (a.Length >= 4 && b.Length >= 4 && StringDistance.Levenshtein(a, b) <= 1);
    }

    private static List<string> Tokenize(string text)
    {
        return text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(token => token.Length > 0)
            .ToList();
    }

    // Lowercased, alphanumerics only — collapses "Clean up email" to "cleanupemail".
    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        // Side-effecting StringBuilder accumulation; a LINQ rewrite would swap the char enumerator
        // and read worse than the plain loop.
        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }
}
