using System.Buffers.Binary;
using System.Text;
using PortAudioSharp;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
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

    [Theory]
    [InlineData(480, 48000, 16000, 160)]
    [InlineData(441, 44100, 16000, 160)]
    public void ResampleToSampleRate_DownsamplesToRoundedTargetLength(
        int inputLength,
        int sourceSampleRate,
        int targetSampleRate,
        int expectedLength
    )
    {
        var samples = new float[inputLength];

        var processed = AudioRecordingService.ResampleToSampleRate(
            samples,
            sourceSampleRate,
            targetSampleRate
        );

        Assert.Equal(expectedLength, processed.Length);
    }

    [Fact]
    public void ResampleToSampleRate_DownsamplingRejectsStopbandAlias()
    {
        const int sourceSampleRate = 48000;
        const int targetSampleRate = 16000;
        const int edgeGuard = 256;
        var samples = GenerateTone(12000, sourceSampleRate);

        var processed = AudioRecordingService.ResampleToSampleRate(
            samples,
            sourceSampleRate,
            targetSampleRate
        );
        var baseline = DownsampleThreeToOneUnfiltered(samples, processed.Length);
        var baselinePower = MeanSquare(baseline, edgeGuard, baseline.Length - edgeGuard);
        var filteredPower = MeanSquare(processed, edgeGuard, processed.Length - edgeGuard);
        var attenuationDb = 10 * Math.Log10(Math.Max(filteredPower, 1e-300) / baselinePower);

        Assert.True(baselinePower > 0.1, $"Baseline power was only {baselinePower:R}.");
        Assert.True(
            attenuationDb <= -50,
            $"Stopband attenuation was {attenuationDb:R} dB."
        );
    }

    [Fact]
    public void ResampleToSampleRate_DownsamplingPreservesInBandGainAndAlignment()
    {
        const int sourceSampleRate = 48000;
        const int targetSampleRate = 16000;
        const int edgeGuard = 256;
        var samples = GenerateTone(1000, sourceSampleRate);

        var processed = AudioRecordingService.ResampleToSampleRate(
            samples,
            sourceSampleRate,
            targetSampleRate
        );
        var baseline = DownsampleThreeToOneUnfiltered(samples, processed.Length);
        var baselinePower = MeanSquare(baseline, edgeGuard, baseline.Length - edgeGuard);
        var outputPower = MeanSquare(processed, edgeGuard, processed.Length - edgeGuard);
        var gainDb = 10 * Math.Log10(outputPower / baselinePower);
        var rmsError = RootMeanSquareError(
            processed,
            baseline,
            edgeGuard,
            processed.Length - edgeGuard
        );

        Assert.InRange(gainDb, -0.25, 0.25);
        Assert.True(rmsError < 0.01, $"In-band RMS sample error was {rmsError:R}.");
    }

    [Fact]
    public void ResampleToSampleRate_DownsamplingPreservesPassbandEdgeTone()
    {
        // 6 kHz sits inside the passband (Fp = 0.40 * 16 kHz = 6.4 kHz) and below
        // the 8 kHz output Nyquist, so its unfiltered 3:1 baseline is alias-free
        // and the anti-alias filter must pass it at essentially unity gain. A
        // decimate-then-filter design using source-rate coefficients would instead
        // gut the 2.4-8 kHz band, so this probe fails that mistake decisively.
        const int sourceSampleRate = 48000;
        const int targetSampleRate = 16000;
        const int edgeGuard = 256;
        var samples = GenerateTone(6000, sourceSampleRate);

        var processed = AudioRecordingService.ResampleToSampleRate(
            samples,
            sourceSampleRate,
            targetSampleRate
        );
        var baseline = DownsampleThreeToOneUnfiltered(samples, processed.Length);
        var baselinePower = MeanSquare(baseline, edgeGuard, baseline.Length - edgeGuard);
        var outputPower = MeanSquare(processed, edgeGuard, processed.Length - edgeGuard);
        var gainDb = 10 * Math.Log10(outputPower / baselinePower);
        var rmsError = RootMeanSquareError(
            processed,
            baseline,
            edgeGuard,
            processed.Length - edgeGuard
        );

        Assert.InRange(gainDb, -1.0, 1.0);
        Assert.True(rmsError < 0.01, $"Passband-edge RMS sample error was {rmsError:R}.");
    }

    [Fact]
    public void ResampleToSampleRate_DownsamplingPreservesConstantSignalAndFiniteEndpoints()
    {
        const float signal = 0.25f;
        var samples = Enumerable.Repeat(signal, 480).ToArray();

        var processed = AudioRecordingService.ResampleToSampleRate(samples, 48000, 16000);

        Assert.All(
            processed,
            sample =>
            {
                Assert.True(float.IsFinite(sample));
                Assert.InRange(sample, signal - 1e-5f, signal + 1e-5f);
            }
        );
    }

    [Fact]
    public void ResampleToSampleRate_ReturnsSameArrayWhenRateAlreadyMatches()
    {
        var samples = new[] { 0.1f, 0.2f };

        var processed = AudioRecordingService.ResampleToSampleRate(samples, 16000, 16000);

        Assert.Same(samples, processed);
    }

    [Fact]
    public void GetCurrentBuffer_GrowingSnapshotsPreserveEverySampleInOrder()
    {
        using var service = new AudioRecordingService(_ => { }, () => 0, () => { });
        var session = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            service.TryStartRecording(whisperModeEnabled: false)
        );
        var firstFrame = new[] { 0.25f, -0.5f, 0.75f };
        var secondFrame = new[] { -1f, 0.125f };
        var lateFrame = new[] { 0.5f, -0.25f, 1f, -0.125f };

        service.ProcessAudioBufferForTest(firstFrame);
        service.ProcessAudioBufferForTest(secondFrame);
        var snapshotA = Assert.IsType<byte[]>(service.GetCurrentBuffer(session));
        var expectedA = ToPcm16(firstFrame, secondFrame);
        var pcmA = AssertPcm16Wav(snapshotA, expectedA);

        service.ProcessAudioBufferForTest(lateFrame);
        var snapshotB = Assert.IsType<byte[]>(service.GetCurrentBuffer(session));
        var expectedLate = ToPcm16(lateFrame);
        var expectedB = expectedA.Concat(expectedLate).ToArray();
        var pcmB = AssertPcm16Wav(snapshotB, expectedB);

        Assert.Equal(pcmA, pcmB[..pcmA.Length]);
        Assert.Equal(expectedLate, pcmB[pcmA.Length..]);

        var finalWav = service.StopRecording(session);
        Assert.Equal(snapshotB, finalWav);
    }

    [Fact]
    public void GetCurrentBuffer_MaterializationStartsWithoutHoldingSampleLock()
    {
        var observerInvoked = false;
        var sampleLockHeld = true;
        using var service = new AudioRecordingService(
            _ => { },
            () => 0,
            () => { },
            wavMaterializationObserver: isSampleLockHeld =>
            {
                observerInvoked = true;
                sampleLockHeld = isSampleLockHeld;
            }
        );
        var session = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            service.TryStartRecording(whisperModeEnabled: false)
        );
        service.ProcessAudioBufferForTest([0.25f, -0.5f, 0.75f]);

        Assert.NotNull(service.GetCurrentBuffer(session));

        Assert.True(observerInvoked);
        Assert.False(sampleLockHeld);
    }

    [Fact]
    public void GetCurrentBuffer_CallbackAtSnapshotBoundaryAppearsInNextSnapshotExactlyOnce()
    {
        var shouldInjectLateFrame = true;
        var injectionCount = 0;
        AudioRecordingService? recorder = null;
        var prefixFrame = new[] { 0.1f, -0.3f, 0.7f };
        var secondPrefixFrame = new[] { -0.2f, 0.4f };
        var lateFrame = new[] { -0.9f, 0.6f, -0.1f, 0.3f };
        using var service = new AudioRecordingService(
            _ => { },
            () => 0,
            () => { },
            // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local -- asserting the lock is not held is the point of this seam
            wavMaterializationObserver: isSampleLockHeld =>
            {
                Assert.False(isSampleLockHeld);
                if (!shouldInjectLateFrame)
                {
                    return;
                }

                shouldInjectLateFrame = false;
                injectionCount++;
                // ReSharper disable once AccessToModifiedClosure -- deliberate late binding: the service reference exists only after construction
                recorder!.ProcessAudioBufferForTest(lateFrame);
            }
        );
        recorder = service;
        var session = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            service.TryStartRecording(whisperModeEnabled: false)
        );
        service.ProcessAudioBufferForTest(prefixFrame);
        service.ProcessAudioBufferForTest(secondPrefixFrame);
        var expectedPrefix = ToPcm16(prefixFrame, secondPrefixFrame);

        var firstSnapshot = Assert.IsType<byte[]>(service.GetCurrentBuffer(session));
        AssertPcm16Wav(firstSnapshot, expectedPrefix);

        var expectedComplete = expectedPrefix.Concat(ToPcm16(lateFrame)).ToArray();
        var secondSnapshot = Assert.IsType<byte[]>(service.GetCurrentBuffer(session));
        AssertPcm16Wav(secondSnapshot, expectedComplete);

        var finalWav = service.StopRecording(session);
        AssertPcm16Wav(finalWav, expectedComplete);
        Assert.Equal(1, injectionCount);
    }

    [Fact]
    public void LiveFrameSink_InvokedFromCallback_WithProcessedSamples()
    {
        using var service = new AudioRecordingService(_ => { }, () => 0, () => { });
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
        using var service = new AudioRecordingService(_ => { }, () => 0, () => { });
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
        using var service = new AudioRecordingService(_ => { }, () => 0, () => { });
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
    public void ApplyConfiguredMicrophone_WhenSavedIdentityIsMissing_UsesDefaultWithoutChangingPreference()
    {
        const int staleIndex = 4;
        const int defaultIndex = 9;
        const string missingId = "Wanted Mic|1";
        IReadOnlyList<AudioInputDevice> devices =
        [
            new(staleIndex, "Replacement Mic", 1, false, "Replacement Mic|1"),
            new(defaultIndex, "Current Default", 1, true, "Current Default|1"),
        ];
        var operations = new List<string>();
        using var service = CreateConfiguredDeviceService(devices, defaultIndex, operations);
        service.SelectedDeviceIndex = staleIndex;
        var originalSettings = AppSettings.Default with
        {
            SelectedMicrophoneDevice = staleIndex,
            SelectedMicrophoneDeviceId = missingId,
        };
        var settings = new FakeSettingsService(originalSettings);

        App.ApplyConfiguredMicrophone(service, settings);

        Assert.Equal(0, settings.SaveCount);
        Assert.Same(originalSettings, settings.Current);
        Assert.Equal(staleIndex, settings.Current.SelectedMicrophoneDevice);
        Assert.Equal(missingId, settings.Current.SelectedMicrophoneDeviceId);
        Assert.Null(service.SelectedDeviceIndex);

        var session = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            service.TryStartRecording(whisperModeEnabled: false)
        );
        Assert.Equal(["open:9"], operations);

        service.StopRecording(session);
        Assert.Equal(["open:9", "stop:9"], operations);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void ApplyConfiguredMicrophone_WhenStoredIdentityIsAbsent_DoesNotTrustCachedIndex(
        string? storedDeviceId
    )
    {
        const int staleIndex = 6;
        const int defaultIndex = 8;
        IReadOnlyList<AudioInputDevice> devices =
        [
            new(staleIndex, "Cached Index Device", 1, false, "Cached Index Device|1"),
            new(defaultIndex, "Current Default", 1, true, "Current Default|1"),
        ];
        var operations = new List<string>();
        using var service = CreateConfiguredDeviceService(devices, defaultIndex, operations);
        service.SelectedDeviceIndex = staleIndex;
        var originalSettings = AppSettings.Default with
        {
            SelectedMicrophoneDevice = staleIndex,
            SelectedMicrophoneDeviceId = storedDeviceId,
        };
        var settings = new FakeSettingsService(originalSettings);

        App.ApplyConfiguredMicrophone(service, settings);

        Assert.Equal(0, settings.SaveCount);
        Assert.Same(originalSettings, settings.Current);
        Assert.Equal(staleIndex, settings.Current.SelectedMicrophoneDevice);
        Assert.Equal(storedDeviceId, settings.Current.SelectedMicrophoneDeviceId);
        Assert.Null(service.SelectedDeviceIndex);

        var session = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            service.TryStartRecording(whisperModeEnabled: false)
        );
        Assert.Equal(["open:8"], operations);

        service.StopRecording(session);
        Assert.Equal(["open:8", "stop:8"], operations);
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(9, 0)]
    public void ApplyConfiguredMicrophone_WhenStoredIdentityIsUnique_SelectsItAndRefreshesIndexOnlyWhenNeeded(
        int storedIndex,
        int expectedSaveCount
    )
    {
        const int intendedIndex = 9;
        const int defaultIndex = 12;
        const string intendedId = "Wanted Mic|1";
        IReadOnlyList<AudioInputDevice> devices =
        [
            new(4, "Replacement Mic", 1, false, "Replacement Mic|1"),
            new(intendedIndex, "Wanted Mic", 1, false, intendedId),
            new(defaultIndex, "Current Default", 1, true, "Current Default|1"),
        ];
        var operations = new List<string>();
        using var service = CreateConfiguredDeviceService(devices, defaultIndex, operations);
        var settings = new FakeSettingsService(
            AppSettings.Default with
            {
                SelectedMicrophoneDevice = storedIndex,
                SelectedMicrophoneDeviceId = intendedId,
            }
        );

        App.ApplyConfiguredMicrophone(service, settings);

        Assert.Equal(expectedSaveCount, settings.SaveCount);
        Assert.Equal(intendedIndex, settings.Current.SelectedMicrophoneDevice);
        Assert.Equal(intendedId, settings.Current.SelectedMicrophoneDeviceId);
        Assert.Equal(intendedIndex, service.SelectedDeviceIndex);

        var session = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            service.TryStartRecording(whisperModeEnabled: false)
        );
        Assert.Equal(["open:9"], operations);

        service.StopRecording(session);
        Assert.Equal(["open:9", "stop:9"], operations);
    }

    [Fact]
    public void ApplyConfiguredMicrophone_WhenStoredIdentityIsAmbiguous_UsesDefaultWithoutChangingPreference()
    {
        const int staleIndex = 4;
        const int defaultIndex = 12;
        const string duplicateId = "Identical Mic|1";
        IReadOnlyList<AudioInputDevice> devices =
        [
            new(7, "Identical Mic", 1, false, duplicateId),
            new(staleIndex, "Identical Mic", 1, false, duplicateId),
            new(defaultIndex, "Current Default", 1, true, "Current Default|1"),
        ];
        var operations = new List<string>();
        using var service = CreateConfiguredDeviceService(devices, defaultIndex, operations);
        service.SelectedDeviceIndex = staleIndex;
        var originalSettings = AppSettings.Default with
        {
            SelectedMicrophoneDevice = staleIndex,
            SelectedMicrophoneDeviceId = duplicateId,
        };
        var settings = new FakeSettingsService(originalSettings);

        App.ApplyConfiguredMicrophone(service, settings);

        Assert.Equal(0, settings.SaveCount);
        Assert.Same(originalSettings, settings.Current);
        Assert.Equal(staleIndex, settings.Current.SelectedMicrophoneDevice);
        Assert.Equal(duplicateId, settings.Current.SelectedMicrophoneDeviceId);
        Assert.Null(service.SelectedDeviceIndex);

        var session = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            service.TryStartRecording(whisperModeEnabled: false)
        );
        Assert.Equal(["open:12"], operations);

        service.StopRecording(session);
        Assert.Equal(["open:12", "stop:12"], operations);
    }

    [Fact]
    public void TryStartRecording_WhenPreviewDeviceChanged_RebuildsBeforeCreatingOwner()
    {
        const int deviceA = 4;
        const int deviceB = 9;
        int? openDevice = null;
        var operations = new List<string>();
        // holder lets the delegates read `service`, which isn't assigned until
        // construction returns. The IsRecording checks confirm capture ownership
        // is granted only after the device rebuild's stop/open pair completes.
        var holder = new AudioRecordingService[1];
        using var service = new AudioRecordingService(
            deviceIndex =>
            {
                if (holder[0] is { } recorderAtOpen)
                {
                    Assert.False(recorderAtOpen.IsRecording);
                }

                Assert.Null(openDevice);
                openDevice = deviceIndex;
                operations.Add($"open:{deviceIndex}");
            },
            () => 1,
            () =>
            {
                if (holder[0] is { } recorderAtStop && operations.Count < 3)
                {
                    Assert.False(recorderAtStop.IsRecording);
                }

                Assert.NotNull(openDevice);
                operations.Add($"stop:{openDevice}");
                openDevice = null;
            }
        );
        holder[0] = service;

        service.SelectedDeviceIndex = deviceA;
        Assert.True(service.StartPreview());

        service.SelectedDeviceIndex = deviceB;
        var session = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            service.TryStartRecording(whisperModeEnabled: false)
        );

        Assert.Equal(["open:4", "stop:4", "open:9"], operations);
        Assert.Equal(deviceB, openDevice);

        service.StopPreview();
        Assert.Equal(["open:4", "stop:4", "open:9"], operations);

        // ReSharper disable once MethodHasAsyncOverload -- synchronous stop verifies the owning capture performs final stream teardown.
        service.StopRecording(session);
        Assert.Equal(["open:4", "stop:4", "open:9", "stop:9"], operations);
        Assert.Null(openDevice);
    }

    [Fact]
    public void TryStartRecording_WhenPreviewDeviceUnchanged_ReusesOpenStream()
    {
        const int deviceA = 4;
        int? openDevice = null;
        var operations = new List<string>();
        using var service = new AudioRecordingService(
            deviceIndex =>
            {
                Assert.Null(openDevice);
                openDevice = deviceIndex;
                operations.Add($"open:{deviceIndex}");
            },
            () => 1,
            () =>
            {
                Assert.NotNull(openDevice);
                operations.Add($"stop:{openDevice}");
                openDevice = null;
            }
        );

        service.SelectedDeviceIndex = deviceA;
        Assert.True(service.StartPreview());
        var session = Assert.IsType<AudioRecordingService.AudioCaptureSession>(
            service.TryStartRecording(whisperModeEnabled: false)
        );

        Assert.Equal(["open:4"], operations);
        Assert.Equal(deviceA, openDevice);

        service.StopPreview();
        // ReSharper disable once MethodHasAsyncOverload -- synchronous stop completes cleanup after asserting preview reuse.
        service.StopRecording(session);
        Assert.Equal(["open:4", "stop:4"], operations);
    }

    [Fact]
    public void TryStartRecording_WhenBusy_ReturnsNullWithoutAdoptingOrReconfiguringOwner()
    {
        var streamStartCount = 0;
        var streamStopCount = 0;
        using var service = new AudioRecordingService(
            _ =>
            {
                streamStartCount++;
            },
            () => 0,
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
            _ =>
            {
                streamStartCount++;
            },
            () => 0,
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
            _ =>
            {
                Interlocked.Increment(ref streamStartCount);
            },
            () => 0,
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
            _ => { },
            () => 0,
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

    private static float[] GenerateTone(
        double frequency,
        int sampleRate,
        double durationSeconds = 0.5,
        double amplitude = 0.8,
        double phase = 0.37
    )
    {
        var samples = new float[(int)(durationSeconds * sampleRate)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / sampleRate + phase));
        }

        return samples;
    }

    private static float[] DownsampleThreeToOneUnfiltered(float[] samples, int outputLength)
    {
        var output = new float[outputLength];
        for (var i = 0; i < output.Length; i++)
        {
            output[i] = samples[3 * i];
        }

        return output;
    }

    private static double MeanSquare(float[] samples, int startIndex, int endIndex)
    {
        double sumSquares = 0;
        for (var i = startIndex; i < endIndex; i++)
        {
            sumSquares += (double)samples[i] * samples[i];
        }

        return sumSquares / (endIndex - startIndex);
    }

    private static double RootMeanSquareError(
        float[] actual,
        float[] expected,
        int startIndex,
        int endIndex
    )
    {
        double sumSquares = 0;
        for (var i = startIndex; i < endIndex; i++)
        {
            var error = (double)actual[i] - expected[i];
            sumSquares += error * error;
        }

        return Math.Sqrt(sumSquares / (endIndex - startIndex));
    }

    private static AudioRecordingService CreateConfiguredDeviceService(
        IReadOnlyList<AudioInputDevice> devices,
        int defaultDeviceIndex,
        List<string> operations
    )
    {
        int? openDeviceIndex = null;
        return new AudioRecordingService(
            () => devices,
            deviceIndex =>
            {
                Assert.Null(openDeviceIndex);
                openDeviceIndex = deviceIndex;
                operations.Add($"open:{deviceIndex}");
            },
            () => defaultDeviceIndex,
            () =>
            {
                Assert.True(openDeviceIndex.HasValue);
                operations.Add($"stop:{openDeviceIndex.Value}");
                openDeviceIndex = null;
            }
        );
    }

    private static short[] ToPcm16(params float[][] frames)
    {
        return frames
            .SelectMany(frame => frame)
            .Select(AudioRecordingService.ToPcm16)
            .ToArray();
    }

    private static short[] AssertPcm16Wav(byte[] wav, short[] expectedSamples)
    {
        var expectedDataSize = expectedSamples.Length * sizeof(short);
        Assert.Equal(44 + expectedDataSize, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(36 + expectedDataSize, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4, 4)));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(16, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22, 2)));
        Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24, 4)));
        Assert.Equal(32000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28, 4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(32, 2)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34, 2)));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(expectedDataSize, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40, 4)));

        var actualSamples = Enumerable
            .Range(0, expectedSamples.Length)
            .Select(i => BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2, 2)))
            .ToArray();
        Assert.Equal(expectedSamples, actualSamples);
        return actualSamples;
    }

    private sealed class FakeSettingsService(AppSettings current) : ISettingsService
    {
        // ISettingsService.Update must read and persist under the same gate as Save.
        private readonly Lock _gate = new();

        public int SaveCount { get; private set; }
        public AppSettings Current { get; private set; } = current;

        public AppSettings Load()
        {
            return Current;
        }

        public void Save(AppSettings settings)
        {
            lock (_gate)
            {
                SaveCount++;
                Current = settings;
                SettingsChanged?.Invoke(settings);
            }
        }

        public AppSettings Update(Func<AppSettings, AppSettings> mutate)
        {
            lock (_gate)
            {
                var updated = mutate(Current);
                Save(updated);
                return updated;
            }
        }

        public event Action<AppSettings>? SettingsChanged;
    }
}

// Selection policy: ResolveConfiguredDevice + the follow-default sentinel.
public sealed class AudioRecordingServiceSelectionTests
{
    [Fact]
    public void ResolveConfiguredDevice_PrefersSystemDefault_WhenNothingConfigured()
    {
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "Built-in Mic", 2, isDefault: false),
            new FakeDevice(1, "USB Mic", 1, isDefault: true));
        using var sut = new AudioRecordingService(deviceEnumerator: devices);

        var resolved = sut.ResolveConfiguredDevice(null, null);

        Assert.Equal(1, resolved!.Index);
        Assert.Equal("USB Mic|1", resolved.PersistentId);
    }

    [Fact]
    public void ResolveConfiguredDevice_FallsBackToFirst_WhenNoDefaultFlagged()
    {
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "Built-in Mic", 2, isDefault: false),
            new FakeDevice(1, "USB Mic", 1, isDefault: false));
        using var sut = new AudioRecordingService(deviceEnumerator: devices);

        var resolved = sut.ResolveConfiguredDevice(null, null);

        Assert.Equal(0, resolved!.Index);
    }

    [Fact]
    public void ResolveConfiguredDevice_MatchesPinnedDeviceById()
    {
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "Built-in Mic", 2, isDefault: true),
            new FakeDevice(1, "USB Mic", 1, isDefault: false));
        using var sut = new AudioRecordingService(deviceEnumerator: devices);

        var resolved = sut.ResolveConfiguredDevice(preferredIndex: 99, preferredDeviceId: "USB Mic|1");

        Assert.Equal(1, resolved!.Index);
    }

    [Fact]
    public void ResolveConfiguredDevice_ReturnsNull_WhenIdNoLongerMatches()
    {
        // No index fallback: PipeWire/PulseAudio re-index freely, so a stale index would
        // silently bind a DIFFERENT microphone. Returning null lets the caller keep the
        // saved preference intact until the named device comes back (audit §5 M4).
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "Built-in Mic", 2, isDefault: true),
            new FakeDevice(1, "USB Mic", 1, isDefault: false));
        using var sut = new AudioRecordingService(deviceEnumerator: devices);

        var resolved = sut.ResolveConfiguredDevice(preferredIndex: 1, preferredDeviceId: "Gone Mic|4");

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveConfiguredDevice_FollowSentinel_IgnoresPinAndReturnsCurrentDefault()
    {
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "Built-in Mic", 2, isDefault: false),
            new FakeDevice(1, "USB Mic", 1, isDefault: true));
        using var sut = new AudioRecordingService(deviceEnumerator: devices);

        // A stale pinned index (0) must be ignored: the sentinel always follows the default.
        var resolved = sut.ResolveConfiguredDevice(
            preferredIndex: 0,
            preferredDeviceId: AppSettings.FollowSystemDefaultMicrophoneId);

        Assert.Equal(1, resolved!.Index);
        Assert.Equal("USB Mic|1", resolved.PersistentId);
    }

    [Fact]
    public void ResolveConfiguredDevice_FollowSentinel_TracksDefaultChange()
    {
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "Built-in Mic", 2, isDefault: true),
            new FakeDevice(1, "USB Mic", 1, isDefault: false));
        using var sut = new AudioRecordingService(deviceEnumerator: devices);

        var first = sut.ResolveConfiguredDevice(null, AppSettings.FollowSystemDefaultMicrophoneId);
        Assert.Equal(0, first!.Index);

        devices.SetDefault("USB Mic|1");
        var second = sut.ResolveConfiguredDevice(null, AppSettings.FollowSystemDefaultMicrophoneId);
        Assert.Equal(1, second!.Index);
    }

    [Fact]
    public void ResolveConfiguredDevice_ReturnsNull_WhenNoDevices()
    {
        var devices = new FakeAudioDeviceEnumerator();
        using var sut = new AudioRecordingService(deviceEnumerator: devices);

        Assert.Null(sut.ResolveConfiguredDevice(null, null));
    }

    [Fact]
    public void CreateFollowSystemDefaultOption_UsesSentinelIdAndNegativeIndex()
    {
        var option = AudioRecordingService.CreateFollowSystemDefaultOption("Automatic");

        Assert.Equal(-1, option.Index);
        Assert.Equal(AppSettings.FollowSystemDefaultMicrophoneId, option.PersistentId);
        Assert.True(AudioRecordingService.IsFollowSystemDefault(option.PersistentId));
    }
}

// Migration-deferral state machine: CheckForDefaultDeviceChange with no real
// PortAudio stream (the seams drive the "active device" + recording flag).
public sealed class AudioRecordingServiceMigrationTests
{
    [Fact]
    public void CheckForDefaultDeviceChange_NoOp_WhenNotFollowingDefault()
    {
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "A", 1, isDefault: true),
            new FakeDevice(1, "B", 1, isDefault: false));
        using var sut = new AudioRecordingService(deviceEnumerator: devices);
        sut.FollowSystemDefault = false;
        sut.SetActiveDeviceIdForTest("A|1", 0);

        devices.SetDefault("B|1");
        sut.CheckForDefaultDeviceChange();

        // Pin mode: no migration, active device unchanged.
        Assert.Equal("A|1", sut.ActiveDeviceIdForTest);
    }

    [Fact]
    public void CheckForDefaultDeviceChange_MigratesToNewDefault_WhenIdle()
    {
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "A", 1, isDefault: true),
            new FakeDevice(1, "B", 1, isDefault: false));
        using var sut = new AudioRecordingService(deviceEnumerator: devices);
        sut.FollowSystemDefault = true;
        sut.SetActiveDeviceIdForTest("A|1", 0);

        devices.SetDefault("B|1");
        sut.CheckForDefaultDeviceChange();

        Assert.Equal("B|1", sut.ActiveDeviceIdForTest);
        Assert.Equal(1, sut.SelectedDeviceIndex);
        Assert.False(sut.MigrationPendingForTest);
    }

    [Fact]
    public void CheckForDefaultDeviceChange_NoOp_WhenAlreadyOnDefault()
    {
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "A", 1, isDefault: true),
            new FakeDevice(1, "B", 1, isDefault: false));
        using var sut = new AudioRecordingService(deviceEnumerator: devices);
        sut.FollowSystemDefault = true;
        sut.SetActiveDeviceIdForTest("A|1", 0);

        sut.CheckForDefaultDeviceChange();

        Assert.Equal("A|1", sut.ActiveDeviceIdForTest);
        Assert.False(sut.MigrationPendingForTest);
    }

    [Fact]
    public void CheckForDefaultDeviceChange_DefersMigration_WhileRecording()
    {
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "A", 1, isDefault: true),
            new FakeDevice(1, "B", 1, isDefault: false));
        using var sut = new AudioRecordingService(deviceEnumerator: devices);
        sut.FollowSystemDefault = true;
        sut.SetActiveDeviceIdForTest("A|1", 0);
        sut.SetRecordingForTest(true);

        devices.SetDefault("B|1");
        sut.CheckForDefaultDeviceChange();

        // In-flight recording: the live device MUST NOT be swapped; defer instead.
        Assert.Equal("A|1", sut.ActiveDeviceIdForTest);
        Assert.Equal(0, sut.SelectedDeviceIndex);
        Assert.True(sut.MigrationPendingForTest);
    }

    [Fact]
    public void PendingMigration_Applied_AfterRecordingStops()
    {
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "A", 1, isDefault: true),
            new FakeDevice(1, "B", 1, isDefault: false));
        using var sut = new AudioRecordingService(deviceEnumerator: devices);
        sut.FollowSystemDefault = true;
        sut.SetActiveDeviceIdForTest("A|1", 0);
        sut.SetRecordingForTest(true);

        devices.SetDefault("B|1");
        sut.CheckForDefaultDeviceChange();
        Assert.True(sut.MigrationPendingForTest);

        // Recording stops → the deferred migration is applied on the next check.
        sut.SetRecordingForTest(false);
        sut.CheckForDefaultDeviceChange();

        Assert.Equal("B|1", sut.ActiveDeviceIdForTest);
        Assert.Equal(1, sut.SelectedDeviceIndex);
        Assert.False(sut.MigrationPendingForTest);
    }

    [Fact]
    public void StopRecording_AppliesPendingMigration()
    {
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "A", 1, isDefault: true),
            new FakeDevice(1, "B", 1, isDefault: false));
        // Real capture session (over the stream seam, no PortAudio) so the deferral is
        // replayed by StopRecording itself rather than a simulated recording flag.
        using var sut = CreateMigrationCaptureService(devices);
        sut.FollowSystemDefault = true;

        var session = sut.TryStartRecording(false);
        Assert.NotNull(session);
        Assert.Equal("A|1", sut.ActiveDeviceIdForTest);

        devices.SetDefault("B|1");
        sut.CheckForDefaultDeviceChange();
        Assert.True(sut.MigrationPendingForTest);

        // StopRecording finalizes the (empty) buffer, then re-runs the check.
        sut.StopRecording(session);

        Assert.Equal("B|1", sut.ActiveDeviceIdForTest);
        Assert.False(sut.MigrationPendingForTest);
    }

    // Capture service over the stream seam: opens/closes are recorded, never native.
    private static AudioRecordingService CreateMigrationCaptureService(
        FakeAudioDeviceEnumerator devices
    )
    {
        return new AudioRecordingService(
            devices.GetDevices,
            static _ => { },
            static () => 0,
            static () => { }
        );
    }

    [Fact]
    public void CheckForDefaultDeviceChange_KeepsPendingMigration_WhenTableStaleWhileRecording()
    {
        // Regression: a deferred migration must NOT be lost to a stale device table.
        // While a recording is in flight, RefreshPortAudioDeviceTable is skipped (it may
        // not re-init PortAudio under a live stream), so the enumerator still reports the
        // OLD default. If that old default equals the active device, the "already on
        // default" branch must NOT clear a genuinely-pending migration — otherwise
        // StopRecording would find nothing to replay and the swap would be dropped.
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "A", 1, isDefault: true),
            new FakeDevice(1, "B", 1, isDefault: false));
        using var sut = new AudioRecordingService(deviceEnumerator: devices);
        sut.FollowSystemDefault = true;
        sut.SetActiveDeviceIdForTest("A|1", 0);
        sut.SetRecordingForTest(true);

        // A migration to B was detected earlier and deferred; it is pending. The device
        // table is now STALE (recording in flight) and still reports A as the default,
        // matching the active device.
        sut.SetMigrationPendingForTest(true);

        sut.CheckForDefaultDeviceChange();

        // The stale "already on A" reading must not clear the pending migration.
        Assert.True(sut.MigrationPendingForTest);
        Assert.Equal("A|1", sut.ActiveDeviceIdForTest);
        Assert.Equal(0, sut.SelectedDeviceIndex);
    }

    [Fact]
    public void CheckForDefaultDeviceChange_ClearsStalePending_WhenTrulyOnDefault_AndTableFresh()
    {
        // Contrast to the stale-table case: when NOT recording the table is refreshable
        // (fresh), so an "already on the current default" reading IS authoritative and a
        // leftover pending flag should be cleared — the migration really is moot.
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "A", 1, isDefault: true),
            new FakeDevice(1, "B", 1, isDefault: false));
        using var sut = new AudioRecordingService(deviceEnumerator: devices);
        sut.FollowSystemDefault = true;
        sut.SetActiveDeviceIdForTest("A|1", 0);
        // Idle (not recording) → RefreshPortAudioDeviceTable reports a fresh table.
        sut.SetMigrationPendingForTest(true);

        sut.CheckForDefaultDeviceChange();

        Assert.False(sut.MigrationPendingForTest);
        Assert.Equal("A|1", sut.ActiveDeviceIdForTest);
    }

    [Fact]
    public void CheckForDefaultDeviceChange_MigratesOnStableId_DespiteIndexReorder()
    {
        // PipeWire re-indexes on reconnect: the same device ("B") reappears at a
        // different index. Migration keys off the stable name-derived id, not index.
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "A", 1, isDefault: true),
            new FakeDevice(1, "B", 1, isDefault: false));
        using var sut = new AudioRecordingService(deviceEnumerator: devices);
        sut.FollowSystemDefault = true;
        sut.SetActiveDeviceIdForTest("B|1", 1);

        // Same devices, reordered indices, "B" is now the default at index 0.
        devices.SetDevices(
            new FakeDevice(0, "B", 1, isDefault: true),
            new FakeDevice(1, "A", 1, isDefault: false));
        sut.CheckForDefaultDeviceChange();

        // Already on "B" by stable id → no churn even though its index moved.
        Assert.Equal("B|1", sut.ActiveDeviceIdForTest);
        Assert.False(sut.MigrationPendingForTest);
    }
}

// Fake device table: reports a controllable device list + default, so selection
// and migration logic run without PortAudio or hardware.
internal sealed class FakeAudioDeviceEnumerator : IAudioDeviceEnumerator
{
    private List<AudioInputDevice> _devices;

    public FakeAudioDeviceEnumerator(params FakeDevice[] devices)
    {
        _devices = devices.Select(d => d.ToDevice()).ToList();
    }

    public IReadOnlyList<AudioInputDevice> GetDevices() => _devices;

    public void SetDevices(params FakeDevice[] devices)
    {
        _devices = devices.Select(d => d.ToDevice()).ToList();
    }

    public void SetDefault(string persistentId)
    {
        _devices = _devices
            .Select(d => d with { IsDefault = d.PersistentId == persistentId })
            .ToList();
    }
}

internal sealed class FakeDevice(int index, string name, int channels, bool isDefault)
{
    public AudioInputDevice ToDevice() =>
        new(index, name, channels, isDefault, $"{name}|{channels}");
}
