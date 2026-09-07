using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class TerminalHallucinationTrimmerTests
{
    [Fact]
    public void Trim_RemovesTrailingHallucinationSegment()
    {
        var result = CreateResult(0.95f);

        var trimmed = TerminalHallucinationTrimmer.Trim(result);

        Assert.Equal("Please send the updated draft.", trimmed.Text);
        Assert.Same(result.Segments[0], Assert.Single(trimmed.Segments));
        Assert.Equal(0.02f, trimmed.NoSpeechProbability);
    }

    [Fact]
    public void Trim_ReturnsSameInstanceWhenNothingToStrip()
    {
        var result = CreateResult(0.05f);

        Assert.Same(result, TerminalHallucinationTrimmer.Trim(result));
    }

    [Fact]
    public void Trim_ReturnsSameInstanceWithoutSegments()
    {
        var result = new PluginTranscriptionResult("Thank you.", "en", 8, 0.95f);

        Assert.Same(result, TerminalHallucinationTrimmer.Trim(result));
    }

    [Fact]
    public void Trim_ReturnsNullForNullResult()
    {
        Assert.Null(TerminalHallucinationTrimmer.Trim(null));
    }

    [Fact]
    public void Trim_KeepsSegmentWithoutProbability()
    {
        var result = CreateResult(null);

        Assert.Same(result, TerminalHallucinationTrimmer.Trim(result));
    }

    private static PluginTranscriptionResult CreateResult(float? terminalProbability)
    {
        return new PluginTranscriptionResult(
            "Please send the updated draft. Thank you.", "en", 8, 0.02f)
        {
            Segments =
            [
                new PluginTranscriptionSegment(" Please send the updated draft.", 0, 5)
                {
                    NoSpeechProbability = 0.02f,
                },
                new PluginTranscriptionSegment(" Thank you.", 5, 8)
                {
                    NoSpeechProbability = terminalProbability,
                },
            ],
        };
    }
}
