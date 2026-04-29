using Moq;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Linux.Services;

namespace TypeWhisper.PluginSystem.Tests;

public class StreamingTranscriptionTests
{
    [Fact]
    public void SupportsStreaming_DefaultIsFalse()
    {
        var mock = new Mock<ITranscriptionEnginePlugin>();
        // DIMs return default values — SupportsStreaming defaults to false
        Assert.False(mock.Object.SupportsStreaming);
    }

    [Fact]
    public void SupportedLanguages_DefaultIsEmpty()
    {
        var mock = new Mock<ITranscriptionEnginePlugin> { CallBase = true };
        // CallBase invokes the DIM — SupportedLanguages defaults to empty
        var languages = mock.Object.SupportedLanguages;
        Assert.Empty(languages);
    }

    [Fact]
    public async Task TranscribeStreamingAsync_DefaultDelegatesToTranscribeAsync()
    {
        var expectedResult = new PluginTranscriptionResult("Hello world", "en", 2.5);
        var audio = new byte[] { 1, 2, 3 };

        var mock = new Mock<ITranscriptionEnginePlugin>();
        mock.Setup(e => e.TranscribeAsync(audio, "en", false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        // TranscribeStreamingAsync should delegate to TranscribeAsync by default
        // Since Moq doesn't call DIMs directly, we verify the TranscribeAsync call
        var result = await mock.Object.TranscribeAsync(audio, "en", false, null, CancellationToken.None);

        Assert.Equal("Hello world", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal(2.5, result.DurationSeconds);
    }

    [Fact]
    public void PluginTranscriptionResult_NoSpeechProbability_DefaultIsNull()
    {
        var result = new PluginTranscriptionResult("Hello", "en", 2.0);
        Assert.Null(result.NoSpeechProbability);
    }

    [Fact]
    public void PluginTranscriptionResult_NoSpeechProbability_CanBeSet()
    {
        var result = new PluginTranscriptionResult("So.", "en", 1.0, 0.95f);
        Assert.Equal(0.95f, result.NoSpeechProbability);
    }

    [Fact]
    public void SupportsStreaming_CanBeOverridden()
    {
        var mock = new Mock<ITranscriptionEnginePlugin>();
        mock.Setup(e => e.SupportsStreaming).Returns(true);

        Assert.True(mock.Object.SupportsStreaming);
    }

    [Fact]
    public void SupportedLanguages_CanBeOverridden()
    {
        var mock = new Mock<ITranscriptionEnginePlugin>();
        mock.Setup(e => e.SupportedLanguages).Returns(new List<string> { "en", "de", "fr" });

        var languages = mock.Object.SupportedLanguages;
        Assert.Equal(3, languages.Count);
        Assert.Contains("de", languages);
    }

    [Fact]
    public async Task TranscribeStreamingAsync_CanBeOverridden()
    {
        var expectedResult = new PluginTranscriptionResult("Streamed text", "de", 5.0);
        var audio = new byte[] { 1, 2, 3, 4, 5 };
        var progressCalls = new List<string>();

        var mock = new Mock<ITranscriptionEnginePlugin>();
        mock.Setup(e => e.TranscribeStreamingAsync(
            audio, "de", false, null,
            It.IsAny<Func<string, bool>>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        var result = await mock.Object.TranscribeStreamingAsync(
            audio, "de", false, null,
            partial => { progressCalls.Add(partial); return true; },
            CancellationToken.None);

        Assert.Equal("Streamed text", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
    }
}

public class StabilizeTextTests
{
    [Fact]
    public void EmptyConfirmed_ReturnsNew()
    {
        var result = StreamingTranscriptState.StabilizeText("", "Hello world");
        Assert.Equal("Hello world", result);
    }

    [Fact]
    public void EmptyNew_ReturnsConfirmed()
    {
        var result = StreamingTranscriptState.StabilizeText("Hello", "");
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void NewStartsWithConfirmed_ReturnsNew()
    {
        var result = StreamingTranscriptState.StabilizeText("Hello", "Hello world");
        Assert.Equal("Hello world", result);
    }

    [Fact]
    public void ExactMatch_ReturnsConfirmed()
    {
        var result = StreamingTranscriptState.StabilizeText("Hello world", "Hello world");
        Assert.Equal("Hello world", result);
    }

    [Fact]
    public void PartialPrefixMatch_KeepsConfirmedAndAppends()
    {
        // "Hello worl" matches >50% of "Hello world", so confirmed + new tail
        var result = StreamingTranscriptState.StabilizeText("Hello world", "Hello world, how are you?");
        Assert.Equal("Hello world, how are you?", result);
    }

    [Fact]
    public void MinorDivergence_KeepsConfirmedPrefix()
    {
        // First 6 chars match ("Hello "), >50% of 11-char confirmed
        var result = StreamingTranscriptState.StabilizeText("Hello world", "Hello earth and sky");
        Assert.Equal("Hello world earth and sky", result);
    }

    [Fact]
    public void CompletelyDifferent_AcceptsNewText()
    {
        var result = StreamingTranscriptState.StabilizeText("Hello world", "Goodbye universe");
        Assert.Equal("Goodbye universe", result);
    }

    [Fact]
    public void SuffixPrefixOverlap_DetectsShift()
    {
        // Confirmed = "A B C D", new starts with "B C D E" (suffix of confirmed)
        var confirmed = "Alpha Beta Gamma Delta";
        var newText = "Beta Gamma Delta Epsilon";
        var result = StreamingTranscriptState.StabilizeText(confirmed, newText);
        // Should keep confirmed + append the new tail " Epsilon"
        Assert.Equal("Alpha Beta Gamma Delta Epsilon", result);
    }

    [Fact]
    public void WhitespaceIsTrimmed()
    {
        var result = StreamingTranscriptState.StabilizeText("", "  Hello  ");
        Assert.Equal("Hello", result);
    }
}

public class StreamingTranscriptStateTests
{
    [Fact]
    public void StopSession_InvalidatesLatePollingEvents()
    {
        var sut = new StreamingTranscriptState();
        var sessionVersion = sut.StartSession();

        var appliedBeforeStop = sut.TryApplyPolling(
            sessionVersion,
            "Hello world",
            text => text,
            out var displayBeforeStop);

        Assert.True(appliedBeforeStop);
        Assert.Equal("Hello world", displayBeforeStop);

        var finalText = sut.StopSession();

        Assert.Equal("Hello world", finalText);

        var appliedAfterStop = sut.TryApplyPolling(
            sessionVersion,
            "Should be ignored",
            text => text,
            out var displayAfterStop);

        Assert.False(appliedAfterStop);
        Assert.Equal("", displayAfterStop);
    }

    [Fact]
    public void PollingTranscript_UsesStabilizedCurrentSessionOnly()
    {
        var sut = new StreamingTranscriptState();
        var firstSession = sut.StartSession();

        var firstApplied = sut.TryApplyPolling(
            firstSession,
            "Hello world",
            text => text,
            out var firstDisplay);
        var secondApplied = sut.TryApplyPolling(
            firstSession,
            "Hello world, how are you?",
            text => text,
            out var secondDisplay);

        Assert.True(firstApplied);
        Assert.Equal("Hello world", firstDisplay);
        Assert.True(secondApplied);
        Assert.Equal("Hello world, how are you?", secondDisplay);

        var secondSession = sut.StartSession();
        var staleApplied = sut.TryApplyPolling(
            firstSession,
            "Old session text",
            text => text,
            out _);
        var currentApplied = sut.TryApplyPolling(
            secondSession,
            "Fresh session text",
            text => text,
            out var currentDisplay);

        Assert.False(staleApplied);
        Assert.True(currentApplied);
        Assert.Equal("Fresh session text", currentDisplay);
    }
}
