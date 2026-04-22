using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class LinuxStreamingTranscriptStateTests
{
    [Fact]
    public void StabilizeText_PreservesConfirmedPrefixWhenTranscriptGrows()
    {
        var result = StreamingTranscriptState.StabilizeText("Hello world", "Hello world, how are you?");

        Assert.Equal("Hello world, how are you?", result);
    }

    [Fact]
    public void TryApplyPolling_IgnoresStaleSessions()
    {
        var sut = new StreamingTranscriptState();
        var firstSession = sut.StartSession();
        var secondSession = sut.StartSession();

        var staleApplied = sut.TryApplyPolling(firstSession, "stale", text => text, out var staleDisplay);
        var currentApplied = sut.TryApplyPolling(secondSession, "fresh", text => text, out var currentDisplay);

        Assert.False(staleApplied);
        Assert.Equal("", staleDisplay);
        Assert.True(currentApplied);
        Assert.Equal("fresh", currentDisplay);
    }

    [Fact]
    public void TryApplyPolling_AppliesDictionaryCorrectionBeforePublishing()
    {
        var sut = new StreamingTranscriptState();
        var session = sut.StartSession();

        var applied = sut.TryApplyPolling(session, "teh world", text => text.Replace("teh", "the"), out var display);

        Assert.True(applied);
        Assert.Equal("the world", display);
    }
}
