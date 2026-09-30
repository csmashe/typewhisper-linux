using System.Text.Json;
using TypeWhisper.Plugin.ParakeetCtc;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class ParakeetCtcScoringTests
{
    private static readonly string[] s_merges = ["b c", "a b"];
    private static readonly int[] s_boundaryEncoding = [0, 1, 4];
    private static readonly int[] s_unboundedEncoding = [1, 4];

    [Fact]
    public void AcousticEvidencePrefersMatchingLabel()
    {
        var emission = new CtcEmission([-0.1f, -8f, -5f], 1, 3, .08);
        Assert.True(
            CtcVocabularyScorer.Score(emission, [0], 2, 0, 1, CancellationToken.None)
                > CtcVocabularyScorer.Score(emission, [1], 2, 0, 1, CancellationToken.None)
        );
    }

    [Fact]
    public void RepeatedLabelsRequireBlankBetweenThem()
    {
        var twoFrames = new CtcEmission([0, -9, 0, -9], 2, 2, .08);
        Assert.Equal(
            double.NegativeInfinity,
            CtcVocabularyScorer.Score(twoFrames, [0, 0], 1, 0, 2, CancellationToken.None)
        );
        var separated = new CtcEmission([0, -9, -9, 0, 0, -9], 3, 2, .08);
        Assert.Equal(
            0,
            CtcVocabularyScorer.Score(separated, [0, 0], 1, 0, 3, CancellationToken.None)
        );
    }

    [Fact]
    public void WindowExcludesBetterEvidenceOutsideIt()
    {
        var emission = new CtcEmission([0, -9, -8, 0], 2, 2, .08);
        Assert.Equal(-8, CtcVocabularyScorer.Score(emission, [0], 1, 1, 2, CancellationToken.None));
        Assert.Equal(
            double.NegativeInfinity,
            CtcVocabularyScorer.Score(emission, [1], 1, 0, 2, CancellationToken.None)
        );
    }

    [Fact]
    public void ScoringHonorsCancellation()
    {
        var emission = new CtcEmission([0, -9], 1, 2, .08);
        Assert.Throws<OperationCanceledException>(() =>
            CtcVocabularyScorer.Score(emission, [0], 1, 0, 1, new CancellationToken(true))
        );
    }

    [Fact]
    public void DefaultsAndAdaptiveBonus()
    {
        Assert.Equal(.60f, CtcBiasPolicy.MinimumSimilarity(5));
        Assert.Equal(.60f, CtcBiasPolicy.MinimumSimilarity(50));
        Assert.Equal(.60f, CtcBiasPolicy.MinimumSimilarity(500));
        Assert.Equal(4.5, CtcBiasPolicy.Bonus(3));
        Assert.Equal(5.85, CtcBiasPolicy.Bonus(6), 8);
        Assert.True(CtcBiasPolicy.Accept(-6, -8, 3));
        Assert.False(CtcBiasPolicy.Accept(-1, -20, 6));
        Assert.False(CtcBiasPolicy.Accept(double.NegativeInfinity, -1, 3));
    }

    [Fact]
    public void ScoresAreNormalizedByTokenCount()
    {
        var emission = new CtcEmission([-2, -90, -90, -90, -2, -90], 2, 3, .08);
        Assert.Equal(
            -2,
            CtcVocabularyScorer.Score(emission, [0, 1], 2, 0, 2, CancellationToken.None)
        );
    }

    [Fact]
    public void TokenizerUsesMergeRankNormalizationAndBoundaryVariants()
    {
        var folder = Path.Join(Path.GetTempPath(), "ctc-bpe-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var vocab = new Dictionary<string, int>
            {
                ["▁"] = 0,
                ["a"] = 1,
                ["b"] = 2,
                ["c"] = 3,
                ["bc"] = 4,
                ["ab"] = 5,
            };
            File.WriteAllLines(
                Path.Join(folder, "tokens.txt"),
                vocab.Select(p => $"{p.Key} {p.Value}").Append("<blk> 6")
            );
            File.WriteAllText(
                Path.Join(folder, "tokenizer.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        model = new
                        {
                            type = "BPE",
                            vocab,
                            merges = s_merges,
                        },
                    }
                )
            );
            var tokenizer = new CtcTokenizer(Path.Join(folder, "tokens.txt"));
            Assert.Equal(s_boundaryEncoding, tokenizer.Encode("ＡBC"));
            Assert.Equal(s_unboundedEncoding, tokenizer.Encode("ABC", false));
            Assert.Empty(tokenizer.Encode("unknown"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
