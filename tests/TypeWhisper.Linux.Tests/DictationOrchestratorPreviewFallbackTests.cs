using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Covers the long-dictation preview fallback ported from upstream
///     <c>3a43766</c>: when the batch transcription returns empty/whitespace
///     but the streaming overlay has accumulated text during the session,
///     <see cref="DictationOrchestrator.SelectRawTextWithPreviewFallback" />
///     substitutes the recovered preview so the pipeline still emits the
///     user's words instead of publishing a <c>discarded</c> terminal.
/// </summary>
public sealed class DictationOrchestratorPreviewFallbackTests
{
    [Fact]
    public void Returns_BatchText_WhenBatchHasContent()
    {
        var result = DictationOrchestrator.SelectRawTextWithPreviewFallback(
            "actual transcription",
            "stale preview",
            out var usedFallback
        );

        Assert.Equal("actual transcription", result);
        Assert.False(usedFallback);
    }

    [Fact]
    public void Returns_RecoveredPreview_WhenBatchIsEmpty()
    {
        var result = DictationOrchestrator.SelectRawTextWithPreviewFallback(
            "",
            "hello world",
            out var usedFallback
        );

        Assert.Equal("hello world", result);
        Assert.True(usedFallback);
    }

    [Fact]
    public void Returns_RecoveredPreview_WhenBatchIsNull()
    {
        var result = DictationOrchestrator.SelectRawTextWithPreviewFallback(
            null,
            "hello world",
            out var usedFallback
        );

        Assert.Equal("hello world", result);
        Assert.True(usedFallback);
    }

    [Fact]
    public void Returns_RecoveredPreview_WhenBatchIsWhitespace()
    {
        var result = DictationOrchestrator.SelectRawTextWithPreviewFallback(
            "   ",
            "hello world",
            out var usedFallback
        );

        Assert.Equal("hello world", result);
        Assert.True(usedFallback);
    }

    [Fact]
    public void Returns_Empty_WhenBothEmpty()
    {
        var result = DictationOrchestrator.SelectRawTextWithPreviewFallback(
            "",
            "",
            out var usedFallback
        );

        Assert.Equal("", result);
        Assert.False(usedFallback);
    }

    [Fact]
    public void Returns_Empty_WhenBatchEmptyAndPreviewWhitespace()
    {
        var result = DictationOrchestrator.SelectRawTextWithPreviewFallback(
            "",
            "   ",
            out var usedFallback
        );

        Assert.Equal("", result);
        Assert.False(usedFallback);
    }

    [Fact]
    public void Normalizes_RecoveredPreview_ThroughFinalTextPolicy()
    {
        // The fallback must run through the same dedupe/ellipsis pipeline
        // as the batch result so the inserted text is consistent regardless
        // of which path produced it.
        var result = DictationOrchestrator.SelectRawTextWithPreviewFallback(
            null,
            "and set the and set the timer for noon",
            out var usedFallback
        );

        Assert.True(usedFallback);
        Assert.Equal("and set the timer for noon", result);
    }
}
