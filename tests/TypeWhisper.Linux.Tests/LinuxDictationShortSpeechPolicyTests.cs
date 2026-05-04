using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class LinuxDictationShortSpeechPolicyTests
{
    [Fact]
    public void Classify_EmptyBuffer_IsDiscardedAsTooShort()
    {
        Assert.Equal(
            LinuxShortSpeechDecision.DiscardTooShort,
            LinuxDictationShortSpeechPolicy.Classify(0, peakLevel: 0));
    }

    [Fact]
    public void Classify_ShortVeryQuietClip_IsNoSpeechByDefault()
    {
        Assert.Equal(
            LinuxShortSpeechDecision.DiscardNoSpeech,
            LinuxDictationShortSpeechPolicy.Classify(0.12, peakLevel: 0.0029f));
    }

    [Fact]
    public void Classify_ShortQuietClip_TranscribesWhenAggressivePolicyEnabled()
    {
        Assert.Equal(
            LinuxShortSpeechDecision.Transcribe,
            LinuxDictationShortSpeechPolicy.Classify(
                0.12,
                peakLevel: 0.0029f,
                transcribeShortQuietClipsAggressively: true));
    }

    [Fact]
    public void PadWavForFinalTranscription_ShortClipPadsToMinimumDuration()
    {
        var wav = MakeWav(0.08, 0.1f);

        var padded = LinuxDictationShortSpeechPolicy.PadWavForFinalTranscription(wav, rawDuration: 0.08);

        Assert.Equal(0.75, LinuxDictationShortSpeechPolicy.ComputeDurationSeconds(padded), precision: 4);
        Assert.True(padded.Length > wav.Length);
    }

    [Fact]
    public void PadWavForFinalTranscription_LongClipAddsTailPadding()
    {
        var wav = MakeWav(1.2, 0.1f);

        var padded = LinuxDictationShortSpeechPolicy.PadWavForFinalTranscription(wav, rawDuration: 1.2);

        Assert.Equal(1.5, LinuxDictationShortSpeechPolicy.ComputeDurationSeconds(padded), precision: 4);
    }

    [Fact]
    public void ComputePeakLevel_ReturnsLargestPcmAmplitude()
    {
        var wav = MakeWav(0.1, 0.25f);

        var peak = LinuxDictationShortSpeechPolicy.ComputePeakLevel(wav);

        Assert.InRange(peak, 0.24f, 0.26f);
    }

    private static byte[] MakeWav(double durationSeconds, float sampleValue)
    {
        const int sampleRate = 16000;
        var sampleCount = (int)(durationSeconds * sampleRate);
        var dataSize = sampleCount * 2;
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);

        var sample = (short)(Math.Clamp(sampleValue, -1f, 1f) * short.MaxValue);
        for (var i = 0; i < sampleCount; i++)
            writer.Write(sample);

        return ms.ToArray();
    }
}
