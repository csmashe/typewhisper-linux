using System.Buffers.Binary;
using TypeWhisper.Plugin.NeedleWhistle;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class NeedleWhistleContractTests
{
    [Fact]
    public void BuildArguments_UsesAudioLanguageAndKeywordFileAsSeparateArguments()
    {
        var args = NeedleWhistleContract.BuildArguments(
            "/assets/whistle.cact",
            "/tmp/audio clip.wav",
            "de",
            "/tmp/keywords file.txt"
        );

        Assert.Equal(
            [
                "--model", "/assets/whistle.cact",
                "--audio", "/tmp/audio clip.wav",
                "--audio-word-timestamps",
                "--audio-language", "de",
                "--audio-keywords", "/tmp/keywords file.txt",
            ],
            args
        );
    }

    [Fact]
    public void ParseResult_MapsDetectedLanguageAndWordTimes()
    {
        const string output = """
                            {"text":"Hello there.","language":"en","words":[
                              {"word":"Hello","start":0.1,"end":0.5,"probability":0.98},
                              {"word":"there.","start":0.55,"end":0.9,"probability":0.91}],"ttft_ms":11.2,"decode_tps":1200.0}
                            """;

        var result = NeedleWhistleContract.ParseResult(output, 1.25);

        Assert.Equal("Hello there.", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal(1.25, result.DurationSeconds);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(new PluginTranscriptionSegment("Hello", 0.1, 0.5), result.Segments[0]);
        Assert.Equal(new PluginTranscriptionSegment("there.", 0.55, 0.9), result.Segments[1]);
    }

    [Fact]
    public void ParseResult_EmptySilenceResponseReturnsEmptyTranscriptWithoutLanguage()
    {
        var result = NeedleWhistleContract.ParseResult(
            """{"text":"","language":"","words":[],"ttft_ms":0,"decode_tps":0}""",
            2
        );

        Assert.Equal("", result.Text);
        Assert.Null(result.DetectedLanguage);
        Assert.Empty(result.Segments);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"text\":42,\"language\":\"en\"}")]
    public void ParseResult_RejectsInvalidRunnerOutput(string output)
    {
        Assert.Throws<InvalidOperationException>(
            () => NeedleWhistleContract.ParseResult(output, 1)
        );
    }

    [Fact]
    public void ReadWavDuration_ReadsPcmDataChunk()
    {
        var wav = BuildPcmWav(sampleRate: 16_000, channels: 1, bitsPerSample: 16, dataBytes: 32_000);

        Assert.Equal(1, NeedleWhistleContract.ReadWavDurationSeconds(wav), precision: 5);
    }

    [Fact]
    public void ReadWavDuration_RejectsMalformedOrUnsupportedWaveHeaders()
    {
        Assert.Throws<InvalidDataException>(() => NeedleWhistleContract.ReadWavDurationSeconds([]));

        var wav = BuildPcmWav(sampleRate: 16_000, channels: 1, bitsPerSample: 16, dataBytes: 16_000);
        wav[0] = (byte)'X';

        Assert.Throws<InvalidDataException>(() => NeedleWhistleContract.ReadWavDurationSeconds(wav));
    }

    private static byte[] BuildPcmWav(int sampleRate, short channels, short bitsPerSample, int dataBytes)
    {
        var wav = new byte[44 + dataBytes];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), channels);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), sampleRate * channels * bitsPerSample / 8);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), (short)(channels * bitsPerSample / 8));
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), bitsPerSample);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), dataBytes);
        return wav;
    }
}
