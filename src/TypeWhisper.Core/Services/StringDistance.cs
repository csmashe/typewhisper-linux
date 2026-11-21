namespace TypeWhisper.Core.Services;

/// <summary>
///     Shared string-similarity helpers. Extracted so the vocabulary booster and
///     the spoken-command keyphrase matcher share one Levenshtein implementation
///     instead of each keeping a private copy.
/// </summary>
public static class StringDistance
{
    private const int StackRowLimit = 128;

    /// <summary>
    ///     Classic two-row Levenshtein (insert/delete/substitute cost 1). Returns the
    ///     minimum number of single-character edits to turn <paramref name="source" />
    ///     into <paramref name="target" />.
    /// </summary>
    public static int Levenshtein(string source, string target)
    {
        if (source.Length == 0)
        {
            return target.Length;
        }

        if (target.Length == 0)
        {
            return source.Length;
        }

        // Stack rows for short strings: the vocabulary booster calls this thousands of times per dictation.
        var rowLength = target.Length + 1;
        var rows = rowLength <= StackRowLimit ? stackalloc int[rowLength * 2] : new int[rowLength * 2];
        var previous = rows[..rowLength];
        var current = rows[rowLength..];

        for (var j = 0; j <= target.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= source.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= target.Length; j++)
            {
                var substitutionCost = source[i - 1] == target[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + substitutionCost
                );
            }

            var swap = previous;
            previous = current;
            current = swap;
        }

        return previous[target.Length];
    }
}
