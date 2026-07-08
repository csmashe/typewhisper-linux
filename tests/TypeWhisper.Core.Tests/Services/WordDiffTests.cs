using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class WordDiffTests
{
    [Fact]
    public void Compute_IdenticalStrings_ReturnsSingleUnchangedSegment()
    {
        var segments = WordDiff.Compute("the quick brown fox", "the quick brown fox");

        var only = Assert.Single(segments);
        Assert.Equal(DiffKind.Unchanged, only.Kind);
        Assert.Equal("the quick brown fox", only.Text);
        Assert.All(segments, segment => Assert.Equal(DiffKind.Unchanged, segment.Kind));
    }

    [Fact]
    public void Compute_Insertion_MarksInsertedWordAdded()
    {
        var segments = WordDiff.Compute("hello world", "hello brave world");

        Assert.Collection(
            segments,
            s => AssertSegment(s, DiffKind.Unchanged, "hello"),
            s => AssertSegment(s, DiffKind.Added, "brave"),
            s => AssertSegment(s, DiffKind.Unchanged, "world")
        );
    }

    [Fact]
    public void Compute_Deletion_MarksRemovedWordRemoved()
    {
        var segments = WordDiff.Compute("hello brave world", "hello world");

        Assert.Collection(
            segments,
            s => AssertSegment(s, DiffKind.Unchanged, "hello"),
            s => AssertSegment(s, DiffKind.Removed, "brave"),
            s => AssertSegment(s, DiffKind.Unchanged, "world")
        );
    }

    [Fact]
    public void Compute_Substitution_ProducesRemovedThenAdded()
    {
        var segments = WordDiff.Compute("hello world", "hello there");

        Assert.Collection(
            segments,
            s => AssertSegment(s, DiffKind.Unchanged, "hello"),
            s => AssertSegment(s, DiffKind.Removed, "world"),
            s => AssertSegment(s, DiffKind.Added, "there")
        );
    }

    [Fact]
    public void Compute_CoalescesConsecutiveSameKindWords()
    {
        var segments = WordDiff.Compute("keep this", "keep one two three");

        Assert.Collection(
            segments,
            s => AssertSegment(s, DiffKind.Unchanged, "keep"),
            s => AssertSegment(s, DiffKind.Removed, "this"),
            s => AssertSegment(s, DiffKind.Added, "one two three")
        );
    }

    [Fact]
    public void Compute_TrimsCommonPrefixAndSuffix_AroundMiddleChange()
    {
        var segments = WordDiff.Compute(
            "the quick brown fox jumps",
            "the quick red fox jumps"
        );

        Assert.Collection(
            segments,
            s => AssertSegment(s, DiffKind.Unchanged, "the quick"),
            s => AssertSegment(s, DiffKind.Removed, "brown"),
            s => AssertSegment(s, DiffKind.Added, "red"),
            s => AssertSegment(s, DiffKind.Unchanged, "fox jumps")
        );
    }

    [Fact]
    public void Compute_LargeWhollyDifferentInputs_FallsBackToCoarseReplacement()
    {
        // 2000×2000 words with no common prefix/suffix exceeds the LCS cell cap,
        // so the diff degrades to a coarse whole-text replacement instead of
        // allocating (and hanging the UI on) the full O(n*m) table.
        var raw = string.Join(' ', Enumerable.Range(0, 2000).Select(i => $"a{i}"));
        var final = string.Join(' ', Enumerable.Range(0, 2000).Select(i => $"b{i}"));

        var segments = WordDiff.Compute(raw, final);

        Assert.Collection(
            segments,
            s => AssertSegment(s, DiffKind.Removed, raw),
            s => AssertSegment(s, DiffKind.Added, final)
        );
    }

    [Fact]
    public void Compute_BothEmpty_ReturnsNoSegments()
    {
        Assert.Empty(WordDiff.Compute("", ""));
        Assert.Empty(WordDiff.Compute("   ", "\t"));
    }

    [Fact]
    public void Compute_EmptyRaw_MarksEverythingAdded()
    {
        var segments = WordDiff.Compute("", "brand new text");

        var only = Assert.Single(segments);
        AssertSegment(only, DiffKind.Added, "brand new text");
    }

    [Fact]
    public void Compute_EmptyFinal_MarksEverythingRemoved()
    {
        var segments = WordDiff.Compute("all gone now", "");

        var only = Assert.Single(segments);
        AssertSegment(only, DiffKind.Removed, "all gone now");
    }

    private static void AssertSegment(DiffSegment segment, DiffKind kind, string text)
    {
        Assert.Equal(kind, segment.Kind);
        Assert.Equal(text, segment.Text);
    }
}
