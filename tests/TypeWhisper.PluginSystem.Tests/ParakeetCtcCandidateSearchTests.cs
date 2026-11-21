using System.Text.RegularExpressions;
using TypeWhisper.Plugin.ParakeetCtc;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public sealed partial class ParakeetCtcCandidateSearchTests
{
    private static readonly string[] s_words =
    [
        "the", "a", "Postgres", "SQL", "cluster", "in", "Kubanesis", "Type", "Visper", "uses",
        "open", "AI", "GitHub", "actions", "re-sharper", "it's", "Grafana", "dashboards",
        "Kestrel", "hosts", "über", "Straße", "42", "v2", "TypeWhisper", "x", "configuration",
    ];

    private static readonly string[] s_terms =
    [
        "PostgreSQL", "Kubernetes", "TypeWhisper", "OpenAI", "GitHub Actions", "Grafana",
        "Postgres SQL", "ReSharper", "Kestrel", "Straße", "a", "Über-Straße", "K8s", "x",
        "Configuration Manager", "  ", "",
    ];

    [Fact]
    public void PreparedWindowsMatchThePerTermSearch()
    {
        var random = new Random(746);
        for (var round = 0; round < 3000; round++)
        {
            var text = RandomText(random);
            var aligned = Align(text, random);
            var terms = RandomTerms(random);

            var expected = ReferenceFindCandidates(text, aligned, terms);
            var actual = ParakeetCtcPlugin.FindCandidates(text, aligned, terms, CancellationToken.None);

            Assert.Equal(expected.Candidates, actual.Candidates);
            Assert.Equal(expected.SimilarityRejected, actual.SimilarityRejected);
            Assert.Equal(expected.TimingRejected, actual.TimingRejected);
            Assert.Equal(expected.BestSimilarity, actual.BestSimilarity);
        }
    }

    [Fact]
    public void WindowAtExactlyTheFloatThresholdIsKept()
    {
        // "a" vs "abcde" is 1 - 4/5, just below .2f in double but equal to it as a float.
        // The exact "abcde" match first raises the best similarity to 1, so the length
        // prefilter would reject "a" too if it compared in double.
        const string text = "abcde a";
        var aligned = Align(text, new Random(0), dropRate: 0);

        var result = ParakeetCtcPlugin.FindCandidates(
            text,
            aligned,
            [new VocabularyTermHint("abcde", .2f)],
            CancellationToken.None
        );

        Assert.Equal("a", Assert.Single(result.Candidates).Original);
    }

    private static string RandomText(Random random)
    {
        var count = random.Next(0, 18);
        var separators = new[] { " ", " ", " ", ", ", ". ", " - ", "  " };
        return string.Concat(
            Enumerable
                .Range(0, count)
                .Select(i =>
                    (i == 0 ? "" : separators[random.Next(separators.Length)])
                    + s_words[random.Next(s_words.Length)]
                )
        );
    }

    private static List<VocabularyTermHint> RandomTerms(Random random)
    {
        var count = random.Next(0, 300);
        var thresholds = new float?[] { null, null, null, .5f, .6f, .75f, .875f, 0f, 1f, 1.5f, float.NaN };
        return Enumerable
            .Range(0, count)
            .Select(_ => new VocabularyTermHint(
                s_terms[random.Next(s_terms.Length)],
                thresholds[random.Next(thresholds.Length)]
            ))
            .ToList();
    }

    // Token positions as Rescore builds them; some tokens are dropped to exercise the timing check.
    private static List<(int Start, int End, double From, double To)> Align(
        string text,
        Random random,
        double dropRate = .1
    )
    {
        var aligned = new List<(int Start, int End, double From, double To)>();
        var time = 0d;
        foreach (Match match in TokenRegex().Matches(text))
        {
            time += .1;
            if (random.NextDouble() < dropRate)
                continue;
            aligned.Add((match.Index, match.Index + match.Length, time, time + .05));
        }
        return aligned;
    }

    [GeneratedRegex(@"[^\s]+")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’-][\p{L}\p{N}]+)*")]
    private static partial Regex WordRegex();

    // The search as it was before windows were prepared once per rescoring.
    private static (
        List<ParakeetCtcPlugin.Candidate> Candidates,
        int SimilarityRejected,
        int TimingRejected,
        double BestSimilarity
    ) ReferenceFindCandidates(
        string text,
        List<(int Start, int End, double From, double To)> aligned,
        List<VocabularyTermHint> terms
    )
    {
        var words = WordRegex().Matches(text).ToArray();
        var candidates = new List<ParakeetCtcPlugin.Candidate>();
        var similarityRejected = 0;
        var timingRejected = 0;
        double bestSimilarity = 0;
        foreach (var term in terms.Take(256).DistinctBy(t => t.Text, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(term.Text) || term.Text.Length > 160)
                continue;
            var threshold =
                term.MinimumSimilarity ?? CtcBiasPolicy.MinimumSimilarity(Math.Min(terms.Count, 256));
            if (!float.IsFinite(threshold) || threshold is < 0 or > 1)
                continue;
            for (var first = 0; first < words.Length; first++)
            for (var count = 1; count <= 3 && first + count <= words.Length; count++)
            {
                var start = words[first].Index;
                var end = words[first + count - 1].Index + words[first + count - 1].Length;
                var original = text[start..end];
                var similarity = ReferenceSimilarity(original, term.Text);
                bestSimilarity = Math.Max(bestSimilarity, similarity);
                // ReSharper disable once DuplicatedSequentialIfBodies -- kept as written in the plugin before the change, so it stays a faithful reference.
                if (original == term.Text || (float)similarity < threshold)
                {
                    similarityRejected++;
                    continue;
                }
                if (ParakeetCtcPlugin.WindowAlreadyContainsTerm(original, term.Text))
                {
                    similarityRejected++;
                    continue;
                }
                var times = aligned.Where(t => t.Start < end && t.End > start).ToArray();
                if (times.Length == 0 || times[0].Start > start || times[^1].End < end)
                {
                    timingRejected++;
                    continue;
                }
                candidates.Add(
                    new ParakeetCtcPlugin.Candidate(
                        start,
                        end - start,
                        term.Text,
                        original,
                        times[0].From,
                        times[^1].To,
                        similarity
                    )
                );
            }
        }
        return (candidates, similarityRejected, timingRejected, bestSimilarity);
    }

    private static double ReferenceSimilarity(string a, string b)
    {
        a = string.Concat(a.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        b = string.Concat(b.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        if (a.Length == 0 || b.Length == 0 || a.Length > 160)
            return 0;
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var previous = row[j];
                row[j] = Math.Min(
                    Math.Min(row[j] + 1, row[j - 1] + 1),
                    diagonal + (a[i - 1] == b[j - 1] ? 0 : 1)
                );
                diagonal = previous;
            }
        }
        return 1 - row[^1] / (double)Math.Max(a.Length, b.Length);
    }
}
