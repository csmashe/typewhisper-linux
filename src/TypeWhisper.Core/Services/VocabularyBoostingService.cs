using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
///     <see cref="IVocabularyBoostingService" /> that fuzzy-matches a sliding window of transcribed
///     words against a normalized catalog of dictionary terms and replaces near-misses, rebuilding
///     the catalog whenever the dictionary changes.
/// </summary>
public sealed class VocabularyBoostingService : IVocabularyBoostingService
{
    private const int MaxWindowTokens = 4; // sliding window of up to 4 words checked against each term
    private const int MaxReplacements = 10; // safety cap to avoid runaway substitutions on long transcripts
    private const double AmbiguityMargin = 0.08; // discard best candidate when the runner-up is within this score gap

    private readonly IDictionaryService _dictionary;
    private readonly Lock _sync = new();
    private TermCatalog _catalog = TermCatalog.s_empty;

    public VocabularyBoostingService(IDictionaryService dictionary)
    {
        _dictionary = dictionary;
        _dictionary.EntriesChanged += RebuildCatalog;
        RebuildCatalog();
    }

    public string Apply(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return rawText;
        }

        TermCatalog catalog;
        lock (_sync)
        {
            catalog = _catalog;
        }

        if (catalog.Count == 0)
        {
            Debug.WriteLine("VocabularyBoosting: candidates=0 replacements=0");
            return rawText;
        }

        try
        {
            var tokens = Tokenize(rawText);
            if (tokens.Count == 0)
            {
                Debug.WriteLine($"VocabularyBoosting: candidates={catalog.Count} replacements=0");
                return rawText;
            }

            var proposals = FindProposals(rawText, tokens, catalog);
            if (proposals.Count == 0)
            {
                Debug.WriteLine($"VocabularyBoosting: candidates={catalog.Count} replacements=0");
                return rawText;
            }

            proposals.Sort(static (left, right) =>
                {
                    var byTokenCount = right.Term.TokenCount.CompareTo(left.Term.TokenCount);
                    if (byTokenCount != 0)
                    {
                        return byTokenCount;
                    }

                    var byLength = right.Term.Normalized.Length.CompareTo(
                        left.Term.Normalized.Length
                    );
                    if (byLength != 0)
                    {
                        return byLength;
                    }

                    var byManual = left.Term.IsPack.CompareTo(right.Term.IsPack);
                    if (byManual != 0)
                    {
                        return byManual;
                    }

                    var byScore = right.Score.CompareTo(left.Score);
                    return byScore != 0 ? byScore : left.Start.CompareTo(right.Start);
                }
            );

            var accepted = new List<Replacement>(Math.Min(MaxReplacements, proposals.Count));
            // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
            foreach (var proposal in proposals)
            {
                if (accepted.Count >= MaxReplacements)
                {
                    break;
                }

                if (accepted.Any(existing => Overlaps(existing, proposal)))
                {
                    continue;
                }

                accepted.Add(proposal);
            }

            if (accepted.Count == 0)
            {
                Debug.WriteLine($"VocabularyBoosting: candidates={catalog.Count} replacements=0");
                return rawText;
            }

            var rewritten = ApplyReplacements(rawText, accepted);
            Debug.WriteLine(
                $"VocabularyBoosting: candidates={catalog.Count} replacements={accepted.Count}"
            );
            return rewritten;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"VocabularyBoosting failed: {ex.Message}");
            return rawText;
        }
    }

    private void RebuildCatalog()
    {
        try
        {
            var terms = _dictionary
                .Entries.Where(entry =>
                    entry is { IsEnabled: true, EntryType: DictionaryEntryType.Term }
                    && !string.IsNullOrWhiteSpace(entry.Original)
                )
                .SelectMany(CreateNormalizedTerms)
                .GroupBy(term => term.Normalized, StringComparer.Ordinal)
                .Select(group =>
                    group
                        .OrderBy(term => term.IsPack)
                        .ThenByDescending(term => term.TokenCount)
                        .ThenByDescending(term => term.Normalized.Length)
                        .First()
                )
                .OrderByDescending(term => term.TokenCount)
                .ThenByDescending(term => term.Normalized.Length)
                .ThenBy(term => term.IsPack)
                .ToArray();
            var catalog = new TermCatalog(terms);

            lock (_sync)
            {
                _catalog = catalog;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"VocabularyBoosting catalog rebuild failed: {ex.Message}");
            lock (_sync)
            {
                _catalog = TermCatalog.s_empty;
            }
        }
    }

    private static List<Replacement> FindProposals(
        string rawText,
        IReadOnlyList<TokenSpan> tokens,
        TermCatalog catalog
    )
    {
        var proposals = new List<Replacement>();

        for (var startIndex = 0; startIndex < tokens.Count; startIndex++)
        {
            var maxWindowLength = Math.Min(MaxWindowTokens, tokens.Count - startIndex);
            for (var windowLength = 1; windowLength <= maxWindowLength; windowLength++)
            {
                var endIndex = startIndex + windowLength - 1;
                var spanStart = tokens[startIndex].Start;
                var spanEnd = tokens[endIndex].End;
                var rawSpan = rawText[spanStart..spanEnd];
                var trimmed = TrimWindow(rawSpan);
                if (trimmed.CoreLength <= 0)
                {
                    continue;
                }

                var coreText = rawSpan.Substring(trimmed.CoreStartOffset, trimmed.CoreLength);
                var normalizedWindow = Normalize(coreText);
                if (string.IsNullOrEmpty(normalizedWindow))
                {
                    continue;
                }

                var window = new Window(
                    coreText,
                    normalizedWindow,
                    windowLength,
                    GetFirstAlphaNumeric(normalizedWindow),
                    GetLastAlphaNumeric(normalizedWindow),
                    CharacterPresence(normalizedWindow)
                );

                // Only the catalog's candidate groups are scored; every skipped term would fail
                // IsCompatibleWindow or ComputeScore anyway, so the outcome is unchanged.
                var ranking = new CandidateRanking();
                if (windowLength <= 2)
                {
                    foreach (var term in catalog.SingleWords(window.FirstAlphaNumeric, window.LastAlphaNumeric))
                    {
                        Consider(term, window, ref ranking);
                    }
                }

                for (var tokenCount = windowLength - 1; tokenCount <= windowLength + 1; tokenCount++)
                {
                    foreach (var term in catalog.Phrases(tokenCount, normalizedWindow.Length))
                    {
                        Consider(term, window, ref ranking);
                    }
                }

                if (ranking.Best is not { } best)
                {
                    continue;
                }

                if (ranking.Count > 1 && ranking.BestScore - ranking.SecondScore < AmbiguityMargin)
                {
                    continue;
                }

                proposals.Add(
                    new Replacement(
                        spanStart + trimmed.CoreStartOffset,
                        spanStart + trimmed.CoreStartOffset + trimmed.CoreLength,
                        best.OutputText,
                        ranking.BestScore,
                        best
                    )
                );
            }
        }

        return proposals;
    }

    private static void Consider(NormalizedTerm term, in Window window, ref CandidateRanking ranking)
    {
        if (!IsCompatibleWindow(term, window.Normalized, window.TokenCount))
        {
            return;
        }

        if (string.Equals(window.CoreText, term.OutputText, StringComparison.Ordinal))
        {
            return;
        }

        var score = ComputeScore(term, window);
        if (score is not null)
        {
            ranking.Add(term, score.Value);
        }
    }

    private static bool IsCompatibleWindow(
        NormalizedTerm term,
        string normalizedWindow,
        int windowTokenCount
    )
    {
        if (term.TokenCount > MaxWindowTokens)
        {
            return false;
        }

        if (Math.Abs(term.TokenCount - windowTokenCount) > 1)
        {
            return false;
        }

        var lengthDifference = Math.Abs(term.Normalized.Length - normalizedWindow.Length);
        if (term.TokenCount == 1)
        {
            return lengthDifference <= 2;
        }

        var maxAllowedDifference = Math.Max(3, term.Normalized.Length / 3);
        return lengthDifference <= maxAllowedDifference;
    }

    private static double? ComputeScore(NormalizedTerm term, in Window window)
    {
        var maxLength = Math.Max(term.Normalized.Length, window.Normalized.Length);
        if (maxLength == 0)
        {
            return null;
        }

        var lengthDifference = Math.Abs(term.Normalized.Length - window.Normalized.Length);
        var sameFirst = term.FirstAlphaNumeric == window.FirstAlphaNumeric;
        var sameLast = term.LastAlphaNumeric == window.LastAlphaNumeric;

        // Single-token terms get stricter matching: first and last characters must agree, and
        // the edit distance must be small relative to length. This avoids spurious replacements
        // on short common words that happen to resemble a technical term.
        if (term.TokenCount == 1 && (!sameFirst || !sameLast))
        {
            return null;
        }

        // Each edit changes at most two characters of a string's character set, so the distance is at
        // least half the characters in only one of the two. Failing on this lower bound means the exact
        // check below would fail too.
        var presenceBound = (BitOperations.PopCount(term.Characters ^ window.Characters) + 1) / 2;
        var lowerBound = Math.Max(lengthDifference, presenceBound);
        if (1d - (double)lowerBound / maxLength < (term.TokenCount == 1 ? 0.86d : 0.80d))
        {
            return null;
        }

        var distance = StringDistance.Levenshtein(term.Normalized, window.Normalized);
        var charSimilarity = 1d - (double)distance / maxLength;

        if (term.TokenCount == 1)
        {
            if (lengthDifference > 2 || charSimilarity < 0.86d)
            {
                return null;
            }
        }
        else
        {
            // Multi-token terms tolerate a 1-token count difference (e.g. one word dropped/merged by Whisper)
            if (Math.Abs(term.TokenCount - window.TokenCount) > 1 || charSimilarity < 0.80d)
            {
                return null;
            }
        }

        // Boost when anchoring characters and token count agree; penalize large length gaps
        var score = charSimilarity;
        if (sameFirst)
        {
            score += 0.02d;
        }

        if (sameLast)
        {
            score += 0.02d;
        }

        if (term.TokenCount == window.TokenCount)
        {
            score += 0.03d;
        }

        if (lengthDifference >= 3)
        {
            score -= 0.03d;
        }

        return score;
    }

    private static string ApplyReplacements(string rawText, IReadOnlyList<Replacement> replacements)
    {
        var ordered = replacements.OrderByDescending(replacement => replacement.Start);
        var builder = new StringBuilder(rawText);

        foreach (var replacement in ordered)
        {
            builder.Remove(replacement.Start, replacement.End - replacement.Start);
            builder.Insert(replacement.Start, replacement.ReplacementText);
        }

        return builder.ToString();
    }

    private static bool Overlaps(Replacement left, Replacement right)
    {
        return left.Start < right.End && right.Start < left.End;
    }

    private static IEnumerable<NormalizedTerm> CreateNormalizedTerms(DictionaryEntry entry)
    {
        var outputText = string.IsNullOrWhiteSpace(entry.Replacement)
            ? entry.Original.Trim()
            : entry.Replacement.Trim();

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entry.Original.Trim() };

        if (!string.IsNullOrWhiteSpace(entry.Replacement))
        {
            aliases.Add(entry.Replacement.Trim());
        }

        var isPack = entry.Id.StartsWith("pack:", StringComparison.Ordinal);
        foreach (var alias in aliases)
        {
            var normalized = Normalize(alias);
            if (string.IsNullOrEmpty(normalized))
            {
                continue;
            }

            var tokenCount = CountTokens(normalized);
            if (tokenCount == 0)
            {
                continue;
            }

            yield return new NormalizedTerm(
                outputText,
                normalized,
                tokenCount,
                isPack,
                GetFirstAlphaNumeric(normalized),
                GetLastAlphaNumeric(normalized),
                CharacterPresence(normalized)
            );
        }
    }

    // One bit per character class (code unit modulo 64); merging classes keeps the distance bound valid.
    private static ulong CharacterPresence(string text)
    {
        var presence = 0UL;
        // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator -- an Aggregate over the string is slower and harder to read on this hot path.
        foreach (var ch in text)
        {
            presence |= 1UL << (ch & 63);
        }

        return presence;
    }

    private static List<TokenSpan> Tokenize(string text)
    {
        var tokens = new List<TokenSpan>();
        var index = 0;

        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index >= text.Length)
            {
                break;
            }

            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            tokens.Add(new TokenSpan(start, index));
        }

        return tokens;
    }

    private static WindowTrim TrimWindow(string rawSpan)
    {
        var start = 0;
        var end = rawSpan.Length - 1;

        while (start <= end && !char.IsLetterOrDigit(rawSpan[start]))
        {
            start++;
        }

        while (end >= start && !char.IsLetterOrDigit(rawSpan[end]))
        {
            end--;
        }

        return end < start ? new WindowTrim(0, 0) : new WindowTrim(start, end - start + 1);
    }

    private static int CountTokens(string normalized)
    {
        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;

        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(ch) || ch is '-' or '_' or '/')
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToLowerInvariant(ch));
                pendingSpace = false;
                continue;
            }

            if (builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(ch);
            }
        }

        var normalized = CollapseSpaces(builder.ToString());
        return TrimNonAlphaNumericEdges(normalized);
    }

    private static string CollapseSpaces(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
            }
            else
            {
                builder.Append(ch);
                lastWasSpace = false;
            }
        }

        return builder.ToString().Trim();
    }

    private static string TrimNonAlphaNumericEdges(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var start = 0;
        var end = value.Length - 1;

        while (start <= end && !char.IsLetterOrDigit(value[start]))
        {
            start++;
        }

        while (end >= start && !char.IsLetterOrDigit(value[end]))
        {
            end--;
        }

        return end < start ? string.Empty : value[start..(end + 1)];
    }

    private static char? GetFirstAlphaNumeric(string text)
    {
        return text.FirstOrDefault(char.IsLetterOrDigit) is var ch && ch != 0 ? ch : null;
    }

    private static char? GetLastAlphaNumeric(string text)
    {
        return text.LastOrDefault(char.IsLetterOrDigit) is var ch && ch != 0 ? ch : null;
    }

    private sealed record NormalizedTerm(
        string OutputText,
        string Normalized,
        int TokenCount,
        bool IsPack,
        char? FirstAlphaNumeric,
        char? LastAlphaNumeric,
        ulong Characters
    );

    private sealed record Replacement(
        int Start,
        int End,
        string ReplacementText,
        double Score,
        NormalizedTerm Term
    );

    private readonly record struct TokenSpan(int Start, int End);

    private readonly record struct WindowTrim(int CoreStartOffset, int CoreLength);

    private readonly record struct Window(
        string CoreText,
        string Normalized,
        int TokenCount,
        char? FirstAlphaNumeric,
        char? LastAlphaNumeric,
        ulong Characters
    );

    // Groups terms by what a window can match: single words by first and last alphanumeric character,
    // phrases by token count and length. Terms longer than MaxWindowTokens never match and only count.
    private sealed class TermCatalog
    {
        internal static readonly TermCatalog s_empty = new([]);

        private readonly Dictionary<(char First, char Last), NormalizedTerm[]> _singleWords;
        private readonly NormalizedTerm[][] _phrasesByTokenCount;
        private readonly int[][] _phraseLengthsByTokenCount;

        internal TermCatalog(NormalizedTerm[] terms)
        {
            Count = terms.Length;
            _singleWords = terms
                .Where(term => term is { TokenCount: 1, FirstAlphaNumeric: not null, LastAlphaNumeric: not null })
                .GroupBy(term => (term.FirstAlphaNumeric!.Value, term.LastAlphaNumeric!.Value))
                .ToDictionary(group => group.Key, group => group.ToArray());
            _phrasesByTokenCount = new NormalizedTerm[MaxWindowTokens + 1][];
            _phraseLengthsByTokenCount = new int[MaxWindowTokens + 1][];
            for (var tokenCount = 0; tokenCount <= MaxWindowTokens; tokenCount++)
            {
                var phrases = tokenCount < 2
                    ? []
                    : terms
                        .Where(term => term.TokenCount == tokenCount)
                        .OrderBy(term => term.Normalized.Length)
                        .ToArray();
                _phrasesByTokenCount[tokenCount] = phrases;
                _phraseLengthsByTokenCount[tokenCount] = phrases.Select(term => term.Normalized.Length).ToArray();
            }
        }

        internal int Count { get; }

        internal NormalizedTerm[] SingleWords(char? first, char? last)
        {
            return first is { } firstCharacter
                   && last is { } lastCharacter
                   && _singleWords.TryGetValue((firstCharacter, lastCharacter), out var words)
                ? words
                : [];
        }

        internal ReadOnlySpan<NormalizedTerm> Phrases(int tokenCount, int windowCharacters)
        {
            if (tokenCount is < 2 or > MaxWindowTokens)
            {
                return [];
            }

            // A 0.80 similarity keeps a phrase between 4/5 and 5/4 of the window's length; the band is
            // one wider on each side so integer rounding never drops a phrase the exact check would keep.
            var lengths = _phraseLengthsByTokenCount[tokenCount];
            var start = FirstIndexAtLeast(lengths, windowCharacters * 4 / 5 - 1);
            var end = FirstIndexAtLeast(lengths, (windowCharacters * 5 + 3) / 4 + 2);
            return _phrasesByTokenCount[tokenCount].AsSpan(start, end - start);
        }

        private static int FirstIndexAtLeast(int[] sorted, int value)
        {
            var low = 0;
            var high = sorted.Length;
            while (low < high)
            {
                var middle = (low + high) >> 1;
                if (sorted[middle] < value)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }
    }

    // Tracks a window's best candidate and runner-up score. A proposal needs the best to lead by
    // AmbiguityMargin, which makes it the unique maximum, so the order terms are scanned in is irrelevant.
    private struct CandidateRanking
    {
        internal NormalizedTerm? Best { get; private set; }
        internal double BestScore { get; private set; }
        internal double SecondScore { get; private set; }
        internal int Count { get; private set; }

        internal void Add(NormalizedTerm term, double score)
        {
            Count++;
            if (Best is null || score > BestScore)
            {
                SecondScore = Best is null ? double.NegativeInfinity : BestScore;
                Best = term;
                BestScore = score;
            }
            else if (score > SecondScore)
            {
                SecondScore = score;
            }
        }
    }
}