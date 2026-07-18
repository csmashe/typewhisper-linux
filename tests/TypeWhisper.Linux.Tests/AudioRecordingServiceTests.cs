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
        using var service = new AudioRecordingService(() => true, () => { });
        var session = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            service.TryStartRecording(whisperModeEnabled: true)
        );
        var captured = new List<float[]>();
        Assert.True(service.TrySetLiveFrameSink(session, captured.Add));

        var input = new[] { 0.01f, -0.01f, 0.01f, -0.01f };
        var expected = AudioRecordingService.ApplyWhisperModeGain(
            (float[])input.Clone(),
            true
        );

        var result = service.ProcessAudioBufferForTest(input);

        Assert.Equal(StreamCallbackResult.Continue, result);
        Assert.Single(captured);
        Assert.Equal(expected.Length, captured[0].Length);
        Assert.Equal(expected, captured[0]);
    }

    [Fact]
    public void LiveFrameSink_ThrowingSubscriber_DoesNotKillCapture()
    {
        using var service = new AudioRecordingService(() => true, () => { });
        var session = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            service.TryStartRecording(whisperModeEnabled: false)
        );
        var invocationCount = 0;
        Assert.True(
            service.TrySetLiveFrameSink(
                session,
                _ =>
                {
                    invocationCount++;
                    throw new InvalidOperationException("boom");
                }
            )
        );

        var frame1 = new[] { 0.1f, -0.2f, 0.1f, -0.2f };
        var result1 = service.ProcessAudioBufferForTest(frame1);

        Assert.Equal(StreamCallbackResult.Continue, result1);
        Assert.Equal(1, invocationCount);

        var frame2 = new[] { 0.4f, -0.3f, 0.4f, -0.3f };
        var result2 = service.ProcessAudioBufferForTest(frame2);

        Assert.Equal(StreamCallbackResult.Continue, result2);
        Assert.Equal(1, invocationCount);
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
        using var service = new AudioRecordingService(() => true, () => { });
        var session = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            service.TryStartRecording(whisperModeEnabled: false)
        );
        var invoked = false;
        Assert.True(service.TrySetLiveFrameSink(session, _ => invoked = true));
        service.StopRecording(session);

        var frame = new[] { 0.1f, -0.2f, 0.1f, -0.2f };
        var result = service.ProcessAudioBufferForTest(frame);

        Assert.Equal(StreamCallbackResult.Continue, result);
        Assert.False(invoked);
    }

    [Fact]
    public void TryStartRecording_WhenBusy_ReturnsNullWithoutAdoptingOrReconfiguringOwner()
    {
        var streamStartCount = 0;
        var streamStopCount = 0;
        using var service = new AudioRecordingService(
            () =>
            {
                streamStartCount++;
                return true;
            },
            () => streamStopCount++
        );

        var first = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            service.TryStartRecording(whisperModeEnabled: false)
        );
        var competing = service.TryStartRecording(whisperModeEnabled: true);

        Assert.Null(competing);
        Assert.True(service.IsRecordingOwnedBy(first));
        Assert.Equal(1, streamStartCount);
        Assert.Equal(0, streamStopCount);

        service.ProcessAudioBufferForTest([0.01f]);
        var wav = service.StopRecording(first);

        Assert.Equal(1, streamStopCount);
        Assert.InRange(BitConverter.ToInt16(wav, 44), (short)300, (short)350);
    }

    [Fact]
    public async Task StaleSession_CannotReadOrStopNewOwner()
    {
        var streamStartCount = 0;
        var streamStopCount = 0;
        using var service = new AudioRecordingService(
            () =>
            {
                streamStartCount++;
                return true;
            },
            () => streamStopCount++
        );

        var sessionA = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            service.TryStartRecording(whisperModeEnabled: false)
        );
        service.ProcessAudioBufferForTest([0.1f, -0.1f, 0.1f]);
        var delayedStopA = service.StopRecordingAsync(sessionA);
        // ReSharper disable once MethodHasAsyncOverload -- deliberately races the synchronous stop against the pending async stop.
        var wavA = service.StopRecording(sessionA);
        Assert.True(wavA.Length > 44);

        var sessionB = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            service.TryStartRecording(whisperModeEnabled: false)
        );
        service.ProcessAudioBufferForTest([0.2f, -0.2f]);

        Assert.False(service.TrySetWhisperMode(sessionA, enabled: true));
        Assert.Null(service.GetCurrentBuffer(sessionA));
        // ReSharper disable once MethodHasAsyncOverload -- verifies the synchronous stop overload is a no-op for a superseded session.
        Assert.Empty(service.StopRecording(sessionA));
        Assert.Empty(await delayedStopA);
        Assert.Equal(1, streamStopCount);
        Assert.True(service.IsRecordingOwnedBy(sessionB));

        var currentB = Assert.IsType<byte[]>(service.GetCurrentBuffer(sessionB));
        // ReSharper disable once MethodHasAsyncOverload -- exercises the synchronous stop overload for the owning session.
        var wavB = service.StopRecording(sessionB);

        Assert.Equal(2, streamStartCount);
        Assert.Equal(2, streamStopCount);
        Assert.Equal(48, currentB.Length);
        Assert.Equal(currentB, wavB);
    }

    [Fact]
    public async Task TryStartRecording_ConcurrentCallers_YieldExactlyOneOwnerAndOneStreamStart()
    {
        var streamStartCount = 0;
        var streamStopCount = 0;
        using var service = new AudioRecordingService(
            () =>
            {
                Interlocked.Increment(ref streamStartCount);
                return true;
            },
            () => Interlocked.Increment(ref streamStopCount)
        );
        using var barrier = new Barrier(3);

        // ReSharper disable once MoveLocalFunctionAfterJumpStatement -- kept adjacent to its two call sites below for readability.
        Task<AudioRecordingService.AudioCaptureSession?> StartConcurrently() => Task.Run(() =>
        {
            barrier.SignalAndWait();
            return service.TryStartRecording(whisperModeEnabled: false);
        });

        var firstStart = StartConcurrently();
        var secondStart = StartConcurrently();
        barrier.SignalAndWait();
        var sessions = await Task.WhenAll(firstStart, secondStart);

        var owner = Assert.Single(sessions, session => session is not null)!;
        Assert.Equal(1, streamStartCount);
        Assert.True(service.IsRecordingOwnedBy(owner));

        // ReSharper disable once MethodHasAsyncOverload -- synchronous stop is sufficient to assert a single stream-stop for the sole owner.
        service.StopRecording(owner);
        Assert.Equal(1, streamStopCount);
    }

    [Fact]
    public void OwningSession_StopsOnce_AndRepeatedStopCannotAffectLaterSession()
    {
        var streamStopCount = 0;
        using var service = new AudioRecordingService(
            () => true,
            () => streamStopCount++
        );

        var sessionA = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            service.TryStartRecording(whisperModeEnabled: false)
        );
        service.ProcessAudioBufferForTest([0.1f]);

        Assert.True(service.StopRecording(sessionA).Length > 44);
        Assert.Empty(service.StopRecording(sessionA));
        Assert.Equal(1, streamStopCount);

        var sessionB = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            service.TryStartRecording(whisperModeEnabled: false)
        );
        service.ProcessAudioBufferForTest([0.2f, -0.2f]);

        Assert.Empty(service.StopRecording(sessionA));
        Assert.Equal(1, streamStopCount);
        Assert.True(service.IsRecordingOwnedBy(sessionB));
        Assert.True(service.StopRecording(sessionB).Length > 44);
        Assert.Equal(2, streamStopCount);
    }
}
