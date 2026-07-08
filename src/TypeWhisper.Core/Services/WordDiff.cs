using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
///     Computes an inline, word-level diff between two strings using a
///     longest-common-subsequence (LCS) alignment. Pure and UI-independent so it
///     can be unit-tested and reused; the history Inspect panel renders the
///     resulting <see cref="DiffSegment" /> runs with add/remove coloring.
/// </summary>
public static class WordDiff
{
    /// <summary>
    ///     Aligns <paramref name="raw" /> against <paramref name="final" /> at the
    ///     word level and returns the merged runs. Consecutive words with the same
    ///     <see cref="DiffKind" /> are coalesced into a single segment (words joined
    ///     by a single space). Returns an empty list when both inputs are blank.
    /// </summary>
    // Guards the O(n*m) LCS table against exhausting memory on very long,
    // wholly-different transcripts. 2M cells ≈ 8 MB (int[,]); with the common
    // prefix/suffix trimmed first, only a wholesale rewrite of a ~1400+ word
    // dictation can exceed it, and that falls back to a coarse replacement diff.
    private const long MaxLcsCells = 2_000_000;

    public static IReadOnlyList<DiffSegment> Compute(string raw, string final)
    {
        var rawWords = Tokenize(raw);
        var finalWords = Tokenize(final);

        if (rawWords.Length == 0 && finalWords.Length == 0)
        {
            return [];
        }

        // Trim the common prefix/suffix first (O(n)): cleanup usually changes only
        // a handful of words, so this collapses the LCS table to the differing
        // middle and keeps the allocation small in the common case.
        var start = CommonPrefixLength(rawWords, finalWords);
        var (rawEnd, finalEnd) = CommonSuffixBounds(rawWords, finalWords, start);

        var ops = new List<DiffSegment>(rawWords.Length + finalWords.Length);
        AppendRun(ops, rawWords, 0, start, DiffKind.Unchanged);
        ops.AddRange(DiffMiddle(rawWords[start..rawEnd], finalWords[start..finalEnd]));
        AppendRun(ops, rawWords, rawEnd, rawWords.Length, DiffKind.Unchanged);

        return Coalesce(ops);
    }

    private static int CommonPrefixLength(string[] rawWords, string[] finalWords)
    {
        var max = Math.Min(rawWords.Length, finalWords.Length);
        var start = 0;
        while (start < max
               && string.Equals(rawWords[start], finalWords[start], StringComparison.Ordinal))
        {
            start++;
        }

        return start;
    }

    private static (int RawEnd, int FinalEnd) CommonSuffixBounds(
        string[] rawWords,
        string[] finalWords,
        int start
    )
    {
        var rawEnd = rawWords.Length;
        var finalEnd = finalWords.Length;
        while (rawEnd > start
               && finalEnd > start
               && string.Equals(rawWords[rawEnd - 1], finalWords[finalEnd - 1], StringComparison.Ordinal))
        {
            rawEnd--;
            finalEnd--;
        }

        return (rawEnd, finalEnd);
    }

    private static void AppendRun(
        List<DiffSegment> ops,
        string[] words,
        int from,
        int to,
        DiffKind kind
    )
    {
        for (var i = from; i < to; i++)
        {
            ops.Add(new DiffSegment(words[i], kind));
        }
    }

    // Word-level diff of the differing middle (common prefix/suffix already
    // removed). Above the cell budget it emits a coarse "all removed, then all
    // added" replacement instead of allocating the full LCS table.
    private static List<DiffSegment> DiffMiddle(string[] rawWords, string[] finalWords)
    {
        if (rawWords.Length == 0 && finalWords.Length == 0)
        {
            return [];
        }

        if ((long)rawWords.Length * finalWords.Length > MaxLcsCells)
        {
            return CoarseReplacement(rawWords, finalWords);
        }

        var lcs = BuildLcsTable(rawWords, finalWords);
        return Backtrack(lcs, rawWords, finalWords);
    }

    private static List<DiffSegment> CoarseReplacement(string[] rawWords, string[] finalWords)
    {
        var ops = new List<DiffSegment>(rawWords.Length + finalWords.Length);
        AppendRun(ops, rawWords, 0, rawWords.Length, DiffKind.Removed);
        AppendRun(ops, finalWords, 0, finalWords.Length, DiffKind.Added);
        return ops;
    }

    // Standard LCS DP table: lcs[i, j] = LCS length of the first i raw words and
    // the first j final words.
    private static int[,] BuildLcsTable(string[] rawWords, string[] finalWords)
    {
        var lcs = new int[rawWords.Length + 1, finalWords.Length + 1];
        for (var i = 1; i <= rawWords.Length; i++)
        {
            for (var j = 1; j <= finalWords.Length; j++)
            {
                lcs[i, j] = string.Equals(rawWords[i - 1], finalWords[j - 1], StringComparison.Ordinal)
                    ? lcs[i - 1, j - 1] + 1
                    : Math.Max(lcs[i - 1, j], lcs[i, j - 1]);
            }
        }

        return lcs;
    }

    // Backtrack the LCS table to a word-level op sequence (built in reverse). On
    // ties we favor "Added" (advance the final side) so a substitution reads as
    // the removed word struck through, immediately followed by its replacement.
    private static List<DiffSegment> Backtrack(int[,] lcs, string[] rawWords, string[] finalWords)
    {
        var ops = new List<DiffSegment>();
        var ri = rawWords.Length;
        var fi = finalWords.Length;
        while (ri > 0 && fi > 0)
        {
            if (string.Equals(rawWords[ri - 1], finalWords[fi - 1], StringComparison.Ordinal))
            {
                ops.Add(new DiffSegment(rawWords[ri - 1], DiffKind.Unchanged));
                ri--;
                fi--;
            }
            else if (lcs[ri - 1, fi] > lcs[ri, fi - 1])
            {
                ops.Add(new DiffSegment(rawWords[ri - 1], DiffKind.Removed));
                ri--;
            }
            else
            {
                ops.Add(new DiffSegment(finalWords[fi - 1], DiffKind.Added));
                fi--;
            }
        }

        while (ri > 0)
        {
            ops.Add(new DiffSegment(rawWords[ri - 1], DiffKind.Removed));
            ri--;
        }

        while (fi > 0)
        {
            ops.Add(new DiffSegment(finalWords[fi - 1], DiffKind.Added));
            fi--;
        }

        ops.Reverse();
        return ops;
    }

    private static string[] Tokenize(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    // Merge adjacent same-kind words into one segment so the UI renders fewer
    // runs; words within a segment are rejoined with a single space.
    private static List<DiffSegment> Coalesce(List<DiffSegment> ops)
    {
        var merged = new List<DiffSegment>();
        var i = 0;
        while (i < ops.Count)
        {
            var kind = ops[i].Kind;
            var words = new List<string> { ops[i].Text };
            var j = i + 1;
            while (j < ops.Count && ops[j].Kind == kind)
            {
                words.Add(ops[j].Text);
                j++;
            }

            merged.Add(new DiffSegment(string.Join(' ', words), kind));
            i = j;
        }

        return merged;
    }
}
