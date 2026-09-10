using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationOrchestratorStreamingInsertionTests
{
    [Theory]
    [InlineData(false, true, false, false, true)]
    // PA9: a partial first-chunk failure must block one-shot re-insertion of its delivered prefix.
    [InlineData(false, false, true, true, true)]
    [InlineData(false, false, false, true, false)]
    [InlineData(true, false, false, true, true)]
    [InlineData(true, true, false, false, true)]
    public void ApplyStreamChunkTypingOutcome_preserves_failure_and_delivery_state(
        bool typedAnythingSoFar,
        bool chunkSucceeded,
        bool chunkDeliveredPartialText,
        bool expectedTypingFailed,
        bool expectedTypedAnything
    )
    {
        var result = DictationOrchestrator.ApplyStreamChunkTypingOutcome(
            typedAnythingSoFar,
            chunkSucceeded,
            chunkDeliveredPartialText
        );

        Assert.Equal(expectedTypingFailed, result.TypingFailed);
        Assert.Equal(expectedTypedAnything, result.TypedAnything);
    }
}
