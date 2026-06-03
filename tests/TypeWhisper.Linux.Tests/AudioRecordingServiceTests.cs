using PortAudioSharp;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class AudioRecordingServiceTests
{
    [Fact]
    public void ApplyWhisperModeGain_BoostsQuietAudio()
    {
        var samples = new[] { 0.01f, -0.01f, 0.01f, -0.01f };

        var processed = AudioRecordingService.ApplyWhisperModeGain(
            samples,
            true
        );

        Assert.NotSame(samples, processed);
        Assert.True(
            AudioRecordingService.ComputeRmsLevel(processed)
            > AudioRecordingService.ComputeRmsLevel(samples)
        );
    }

    [Fact]
    public void ApplyWhisperModeGain_LeavesAudioUnchangedWhenDisabled()
    {
        var samples = new[] { 0.01f, -0.01f, 0.01f, -0.01f };

        var processed = AudioRecordingService.ApplyWhisperModeGain(
            samples,
            false
        );

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

    [Fact]
    public void LiveFrameSink_InvokedFromCallback_WithProcessedSamples()
    {
        using var service = new AudioRecordingService { WhisperModeEnabled = true };
        var captured = new List<float[]>();
        service.LiveFrameSink = frame => captured.Add(frame);

        var input = new[] { 0.01f, -0.01f, 0.01f, -0.01f };
        var expected = AudioRecordingService.ApplyWhisperModeGain(
            (float[])input.Clone(),
            true
        );

        var result = service.ProcessAudioBufferForTest(input, copySamples: true);

        Assert.Equal(StreamCallbackResult.Continue, result);
        Assert.Single(captured);
        Assert.Equal(expected.Length, captured[0].Length);
        Assert.Equal(expected, captured[0]);
    }

    [Fact]
    public void LiveFrameSink_ThrowingSubscriber_DoesNotKillCapture()
    {
        using var service = new AudioRecordingService();
        service.LiveFrameSink = _ => throw new InvalidOperationException("boom");

        var frame1 = new[] { 0.1f, -0.2f, 0.1f, -0.2f };
        var result1 = service.ProcessAudioBufferForTest(frame1, copySamples: true);

        Assert.Equal(StreamCallbackResult.Continue, result1);
        Assert.Null(service.LiveFrameSink);

        var frame2 = new[] { 0.4f, -0.3f, 0.4f, -0.3f };
        var result2 = service.ProcessAudioBufferForTest(frame2, copySamples: true);

        Assert.Equal(StreamCallbackResult.Continue, result2);
        // CurrentRmsLevel is written synchronously inside ProcessAudioBuffer
        // before the UI-thread post; reading it confirms the second frame's
        // processing path ran end-to-end after the throwing sink detached.
        Assert.Equal(
            AudioRecordingService.ComputeRmsLevel(frame2),
            service.CurrentRmsLevel,
            precision: 5
        );
    }

    [Fact]
    public void LiveFrameSink_OnlyFiresWhenIsRecording()
    {
        using var service = new AudioRecordingService();
        var invoked = false;
        service.LiveFrameSink = _ => invoked = true;

        var frame = new[] { 0.1f, -0.2f, 0.1f, -0.2f };
        var result = service.ProcessAudioBufferForTest(frame, copySamples: false);

        Assert.Equal(StreamCallbackResult.Continue, result);
        Assert.False(invoked);
    }
}