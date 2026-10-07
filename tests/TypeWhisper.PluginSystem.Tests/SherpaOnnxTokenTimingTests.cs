extern alias SherpaOnnx;

using SherpaOnnx::TypeWhisper.Plugin.SherpaOnnx;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SherpaOnnxTokenTimingTests
{
    [Fact]
    public void SingleChunkParakeet_PopulatesTokenTimings()
    {
        var audio = new float[SherpaDecodeCoordinator.SampleRate];
        var coordinator = new SherpaDecodeCoordinator(_ => new SherpaDecodeChunk(
            "hello world",
            ["▁hello", "▁world"],
            [0.1f, 0.5f],
            [0.3f, 0.2f]
        ));

        var result = coordinator.Decode(audio, parseCanaryPayload: false, CancellationToken.None);

        Assert.Equal("hello world", result.Text);
        Assert.Collection(
            result.TokenTimings,
            timing => AssertTiming(timing, "▁hello", 0.1, 0.4),
            timing => AssertTiming(timing, "▁world", 0.5, 0.7)
        );
    }

    [Fact]
    public void SingleChunk_SharedFrameTokensShareInterval()
    {
        var audio = new float[SherpaDecodeCoordinator.SampleRate];
        var coordinator = new SherpaDecodeCoordinator(_ => new SherpaDecodeChunk(
            "TypeWhisper",
            ["▁Type", "Whis", "per"],
            [0.2f, 0.2f, 0.9f]
        ));

        var result = coordinator.Decode(audio, parseCanaryPayload: false, CancellationToken.None);

        Assert.Collection(
            result.TokenTimings,
            timing => AssertTiming(timing, "▁Type", 0.2, 0.9),
            timing => AssertTiming(timing, "Whis", 0.2, 0.9),
            timing => AssertTiming(timing, "per", 0.9, 1.0)
        );
    }

    [Fact]
    public void SingleChunk_WithoutTimestamps_ReturnsEmpty()
    {
        var audio = new float[SherpaDecodeCoordinator.SampleRate];
        var coordinator = new SherpaDecodeCoordinator(_ => new SherpaDecodeChunk(
            "hello",
            ["▁hello"]
        ));

        var result = coordinator.Decode(audio, parseCanaryPayload: false, CancellationToken.None);

        Assert.Equal("hello", result.Text);
        Assert.Empty(result.TokenTimings);
    }

    [Fact]
    public void CanaryPayload_ReturnsNoTimings()
    {
        var audio = new float[SherpaDecodeCoordinator.SampleRate];
        var coordinator = new SherpaDecodeCoordinator(_ => new SherpaDecodeChunk(
            """{"text":"the quick brown fox","lang":"en"}""",
            ["▁the", "▁quick", "▁brown", "▁fox"],
            [0.1f, 0.3f, 0.5f, 0.7f]
        ));

        var result = coordinator.Decode(audio, parseCanaryPayload: true, CancellationToken.None);

        Assert.Equal("the quick brown fox", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Empty(result.TokenTimings);
    }

    [Fact]
    public void RunDecodeTransactionForTests_CarriesTimings()
    {
        using var plugin = new SherpaOnnxPlugin();
        var audio = new float[SherpaDecodeCoordinator.SampleRate];
        SherpaDecodeDelegate decode = _ => new SherpaDecodeChunk(
            "hello world",
            ["▁hello", "▁world"],
            [0.1f, 0.5f],
            [0.3f, 0.2f]
        );
        var expected = new SherpaDecodeCoordinator(decode).Decode(
            audio,
            parseCanaryPayload: false,
            CancellationToken.None
        );

        var result = plugin.RunDecodeTransactionForTests(
            audio,
            parseCanaryPayload: false,
            decode,
            CancellationToken.None
        );

        Assert.NotEmpty(expected.TokenTimings);
        Assert.Equal(expected.TokenTimings, result.TokenTimings);
    }

    private static void AssertTiming(
        VocabularyTokenTiming timing,
        string text,
        double startSeconds,
        double endSeconds
    )
    {
        Assert.Equal(text, timing.Text);
        Assert.InRange(Math.Abs(timing.StartSeconds - startSeconds), 0, 1e-6);
        Assert.InRange(Math.Abs(timing.EndSeconds - endSeconds), 0, 1e-6);
    }
}
