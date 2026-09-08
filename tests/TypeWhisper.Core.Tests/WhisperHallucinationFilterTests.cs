using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests;

public sealed class WhisperHallucinationFilterTests
{
    [Theory]
    [InlineData("Thank you.")]
    [InlineData("thank you")]
    [InlineData("  Thank you!  ")]
    [InlineData("Thanks for watching!")]
    [InlineData("Thank you for watching.")]
    [InlineData("Please subscribe")]
    [InlineData("Bye.")]
    public void IsLikelyHallucination_TrueForStockPhraseOnShortClip(string transcript)
    {
        Assert.True(
            WhisperHallucinationFilter.IsLikelyHallucination(
                transcript,
                durationSeconds: 1.0,
                noSpeechProbability: null));
    }

    [Fact]
    public void IsLikelyHallucination_FalseWhenClipIsLongEnoughToBeRealSpeech()
    {
        Assert.False(
            WhisperHallucinationFilter.IsLikelyHallucination(
                "Thank you.",
                durationSeconds: 4.0,
                noSpeechProbability: null));
    }

    [Theory]
    [InlineData("Thank you for the quick turnaround on this.")]
    [InlineData("format this email")]
    [InlineData("subscribe me to the newsletter")]
    [InlineData("")]
    [InlineData("   ")]
    public void IsLikelyHallucination_FalseWhenTranscriptIsRealDictation(string transcript)
    {
        Assert.False(
            WhisperHallucinationFilter.IsLikelyHallucination(
                transcript,
                durationSeconds: 1.0,
                noSpeechProbability: null));
    }

    [Fact]
    public void IsLikelyHallucination_FalseForNull()
    {
        Assert.False(
            WhisperHallucinationFilter.IsLikelyHallucination(
                null,
                durationSeconds: 1.0,
                noSpeechProbability: null));
    }

    [Fact]
    public void IsLikelyHallucination_FalseWhenEngineIsConfidentSpeechWasPresent()
    {
        // Low no-speech probability = the user confidently dictated "Thank you." — keep it.
        Assert.False(
            WhisperHallucinationFilter.IsLikelyHallucination(
                "Thank you.",
                durationSeconds: 1.0,
                noSpeechProbability: 0.1f));
    }

    [Fact]
    public void IsLikelyHallucination_TrueWhenNoSpeechProbabilityIsHigh()
    {
        Assert.True(
            WhisperHallucinationFilter.IsLikelyHallucination(
                "Thank you.",
                durationSeconds: 1.0,
                noSpeechProbability: 0.5f));
    }

    [Fact]
    public void IsLikelyHallucination_TrueWhenNoSpeechProbabilityIsNull()
    {
        Assert.True(
            WhisperHallucinationFilter.IsLikelyHallucination(
                "Thank you.",
                durationSeconds: 1.0,
                noSpeechProbability: null));
    }

    [Theory]
    // Duration comparison is `> 2.5`, so exactly 2.5 is still "short enough" (hallucination), and
    // anything above is treated as real speech.
    [InlineData(2.5, true)]
    [InlineData(2.5001, false)]
    public void IsLikelyHallucination_HonorsDurationThresholdBoundary(
        double durationSeconds,
        bool expected)
    {
        Assert.Equal(
            expected,
            WhisperHallucinationFilter.IsLikelyHallucination(
                "Thank you.",
                durationSeconds,
                noSpeechProbability: null));
    }

    [Theory]
    // No-speech comparison is `< 0.3`, so exactly 0.3 still counts as a hallucination while just
    // below it is trusted as confident speech.
    [InlineData(0.3f, true)]
    [InlineData(0.2999f, false)]
    public void IsLikelyHallucination_HonorsNoSpeechProbabilityThresholdBoundary(
        float noSpeechProbability,
        bool expected)
    {
        Assert.Equal(
            expected,
            WhisperHallucinationFilter.IsLikelyHallucination(
                "Thank you.",
                durationSeconds: 1.0,
                noSpeechProbability));
    }

    [Fact]
    public void TryStripTerminalSegment_StripsHighNoSpeechThankYouAfterRealText()
    {
        const string text = "Please send the updated draft. Thank you.";

        Assert.True(WhisperHallucinationFilter.TryStripTerminalSegment(
            text, " Thank you.", 0.95f, out var strippedText));
        Assert.Equal("Please send the updated draft.", strippedText);
    }

    [Fact]
    public void TryStripTerminalSegment_KeepsConfidentTerminalThankYou()
    {
        const string text = "Please send the updated draft. Thank you.";

        Assert.False(WhisperHallucinationFilter.TryStripTerminalSegment(
            text, " Thank you.", 0.05f, out var strippedText));
        Assert.Equal(text, strippedText);
    }

    [Fact]
    public void TryStripTerminalSegment_KeepsWhenProbabilityUnknown()
    {
        const string text = "Please send the updated draft. Thank you.";

        Assert.False(WhisperHallucinationFilter.TryStripTerminalSegment(
            text, " Thank you.", null, out var strippedText));
        Assert.Equal(text, strippedText);
    }

    [Fact]
    public void TryStripTerminalSegment_KeepsNonStockPhrase()
    {
        const string text = "Please send the updated draft. Tomorrow morning.";

        Assert.False(WhisperHallucinationFilter.TryStripTerminalSegment(
            text, " Tomorrow morning.", 0.95f, out var strippedText));
        Assert.Equal(text, strippedText);
    }

    [Fact]
    public void TryStripTerminalSegment_KeepsStandalonePhrase()
    {
        const string text = "Thank you.";

        Assert.False(WhisperHallucinationFilter.TryStripTerminalSegment(
            text, " Thank you.", 0.95f, out var strippedText));
        Assert.Equal(text, strippedText);
    }

    [Fact]
    public void TryStripTerminalSegment_KeepsWhenSegmentIsNotTheSuffix()
    {
        const string text = "Please send it. Thanks.";

        Assert.False(WhisperHallucinationFilter.TryStripTerminalSegment(
            text, " Thank you.", 0.95f, out var strippedText));
        Assert.Equal(text, strippedText);
    }

    [Fact]
    public void TryStripTerminalSegment_AtThresholdIsKept()
    {
        const string text = "Please send the updated draft. Thank you.";

        Assert.False(WhisperHallucinationFilter.TryStripTerminalSegment(
            text, " Thank you.", 0.8f, out var strippedText));
        Assert.Equal(text, strippedText);
    }

    [Theory]
    [InlineData(" Thanks for watching!")]
    [InlineData(" THANK YOU!!")]
    [InlineData(" Bye.")]
    public void TryStripTerminalSegment_MatchesAnyStockPhrase(string terminalText)
    {
        const string realText = "Please send the updated draft.";

        Assert.True(WhisperHallucinationFilter.TryStripTerminalSegment(
            realText + terminalText, terminalText, 0.95f, out var strippedText));
        Assert.Equal(realText, strippedText);
    }
}
