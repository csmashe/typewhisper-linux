namespace TypeWhisper.Linux.Services.SpokenCommand;

/// <summary>
///     Decides whether a spoken command edits the current selection ("shorten this", "translate to
///     spanish") rather than creating new text from scratch ("write a haiku about coffee"). Two
///     decisions ride on this:
///     <list type="bullet">
///         <item>whether we fire a synthesized Ctrl+C probe at the focused app to read its selection —
///             Ctrl+C is SIGINT in a terminal, so a false positive on a create command can interrupt a
///             running process;</item>
///         <item>when nothing is selected, whether the command errors with "Nothing highlighted"
///             instead of generating new text — so a false positive bounces a legitimate create
///             command with a bogus error.</item>
///     </list>
///     Because false positives are costly, referent pronouns only count when they appear early in the
///     command (where an edit target is normally named), while explicit selection phrases and a leading
///     transform verb are trusted anywhere / on their own.
/// </summary>
public static class SpokenCommandIntent
{
    // A referent pronoun only signals an edit when it names the target up front ("shorten this",
    // "make it formal"); the same word appearing late is usually incidental ("write a haiku ... and
    // make it rhyme"), so we only look inside the opening tokens.
    private const int ReferentTokenWindow = 4;

    private static readonly HashSet<string> s_selectionReferents = new(StringComparer.OrdinalIgnoreCase)
    {
        "this", "that", "it", "these", "those", "them", "selection", "highlighted", "selected"
    };

    private static readonly string[] s_selectionPhrases =
        ["the text", "the following", "the selection", "the highlighted", "this text"];

    // A command that opens with a transform verb edits existing text even without a pronoun
    // ("translate to spanish", "fix grammar"). Creation verbs (see s_leadingCreationVerbs) are
    // deliberately excluded. Matched against a whole normalized token, so "rewrite" is listed
    // explicitly and never matches via "write".
    private static readonly HashSet<string> s_leadingTransformVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "translate", "shorten", "lengthen", "summarize", "summarise", "rewrite", "rephrase", "reword",
        "reformat", "format", "fix", "correct", "proofread", "simplify", "condense", "expand",
        "capitalize", "capitalise", "uppercase", "lowercase", "bold", "italicize", "italicise",
        "punctuate"
    };

    // A command that opens with one of these asks for new text from scratch ("write an email",
    // "draft a reply"), not an edit of a selection — even when it happens to share words with a saved
    // prompt's name. Deliberately only the strong from-scratch verbs: weaker/ambiguous ones ("make",
    // "reply", "give", …) commonly lead a transform action's own name ("Make Formal", "Reply"), so
    // demoting those to create would hijack a legitimate invocation of that saved action.
    private static readonly HashSet<string> s_leadingCreationVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "write", "draft", "compose", "create", "generate"
    };

    // Politeness/filler that can precede the real verb ("please write…", "can you fix…"); skipped
    // when locating the leading creation OR transform verb so the filler doesn't hide it.
    private static readonly HashSet<string> s_leadingFillers = new(StringComparer.OrdinalIgnoreCase)
    {
        "please", "pls", "kindly", "just", "can", "could", "would", "you"
    };

    public static bool RefersToSelection(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var tokens = Tokenize(command).ToList();
        if (tokens.Count == 0)
        {
            return false;
        }

        // Match selection phrases as whole-word runs, not raw substrings, so "this textbook" doesn't
        // read as the phrase "this text" and misroute a create command onto the edit path.
        if (s_selectionPhrases.Any(phrase => ContainsWordRun(tokens, phrase)))
        {
            return true;
        }

        // Skip leading politeness/filler so "please fix grammar" / "can you make this formal" read the
        // same as the bare command, mirroring OpensWithCreationVerb.
        var meaningful = tokens.SkipWhile(s_leadingFillers.Contains).ToList();
        if (meaningful.Count == 0)
        {
            return false;
        }

        return s_leadingTransformVerbs.Contains(meaningful[0])
               || meaningful.Take(ReferentTokenWindow).Any(s_selectionReferents.Contains);
    }

    /// <summary>
    ///     True when the command opens with a creation verb ("write an email", "draft a reply"),
    ///     marking it as a from-scratch request rather than an edit of the current selection.
    /// </summary>
    public static bool OpensWithCreationVerb(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var tokens = Tokenize(command).ToList();
        var index = 0;
        while (index < tokens.Count && s_leadingFillers.Contains(tokens[index]))
        {
            index++;
        }

        return index < tokens.Count && s_leadingCreationVerbs.Contains(tokens[index]);
    }

    // True when the phrase's space-separated words appear as a contiguous run in tokens.
    private static bool ContainsWordRun(List<string> tokens, string phrase)
    {
        var parts = phrase.Split(' ');
        for (var start = 0; start + parts.Length <= tokens.Count; start++)
        {
            var matched = true;
            for (var offset = 0; offset < parts.Length; offset++)
            {
                if (!string.Equals(tokens[start + offset], parts[offset], StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        return text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()))
            .Where(token => token.Length > 0);
    }
}
