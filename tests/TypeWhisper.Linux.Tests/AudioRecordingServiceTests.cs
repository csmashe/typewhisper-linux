using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class AudioRecordingServiceTests
{
    [Fact]
    public void ApplyWhisperModeGain_BoostsQuietAudio()
    {
        var samples = new[] { 0.01f, -0.01f, 0.01f, -0.01f };

        var processed = AudioRecordingService.ApplyWhisperModeGain(samples, whisperModeEnabled: true);

        Assert.NotSame(samples, processed);
        Assert.True(AudioRecordingService.ComputeRmsLevel(processed) > AudioRecordingService.ComputeRmsLevel(samples));
    }

    [Fact]
    public void ApplyWhisperModeGain_LeavesAudioUnchangedWhenDisabled()
    {
        var samples = new[] { 0.01f, -0.01f, 0.01f, -0.01f };

        var processed = AudioRecordingService.ApplyWhisperModeGain(samples, whisperModeEnabled: false);

        Assert.Same(samples, processed);
    }

    [Fact]
    public void ResampleToSampleRate_DownsamplesToTargetLength()
    {
        var samples = Enumerable.Range(0, 480).Select(i => i / 480f).ToArray();

        var processed = AudioRecordingService.ResampleToSampleRate(samples, 48000, 16000);

        Assert.Equal(160, processed.Length);
        Assert.Equal(samples[0], processed[0]);
    }

    [Fact]
    public void ResampleToSampleRate_ReturnsSameArrayWhenRateAlreadyMatches()
    {
        var samples = new[] { 0.1f, 0.2f };

        var processed = AudioRecordingService.ResampleToSampleRate(samples, 16000, 16000);

        Assert.Same(samples, processed);
    }
}
