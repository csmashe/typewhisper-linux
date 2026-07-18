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
            new(defaultIndex, "Current Default", 1, true, "Current Default|1")
        ];
        var operations = new List<string>();
        using var service = CreateConfiguredDeviceService(devices, defaultIndex, operations);
        service.SelectedDeviceIndex = staleIndex;
        var originalSettings = AppSettings.Default with
        {
            SelectedMicrophoneDevice = staleIndex,
            SelectedMicrophoneDeviceId = missingId
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
            new(defaultIndex, "Current Default", 1, true, "Current Default|1")
        ];
        var operations = new List<string>();
        using var service = CreateConfiguredDeviceService(devices, defaultIndex, operations);
        service.SelectedDeviceIndex = staleIndex;
        var originalSettings = AppSettings.Default with
        {
            SelectedMicrophoneDevice = staleIndex,
            SelectedMicrophoneDeviceId = storedDeviceId
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
            new(defaultIndex, "Current Default", 1, true, "Current Default|1")
        ];
        var operations = new List<string>();
        using var service = CreateConfiguredDeviceService(devices, defaultIndex, operations);
        var settings = new FakeSettingsService(
            AppSettings.Default with
            {
                SelectedMicrophoneDevice = storedIndex,
                SelectedMicrophoneDeviceId = intendedId
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
            new(defaultIndex, "Current Default", 1, true, "Current Default|1")
        ];
        var operations = new List<string>();
        using var service = CreateConfiguredDeviceService(devices, defaultIndex, operations);
        service.SelectedDeviceIndex = staleIndex;
        var originalSettings = AppSettings.Default with
        {
            SelectedMicrophoneDevice = staleIndex,
            SelectedMicrophoneDeviceId = duplicateId
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
        public int SaveCount { get; private set; }
        public AppSettings Current { get; private set; } = current;

        public AppSettings Load()
        {
            return Current;
        }

        public void Save(AppSettings settings)
        {
            SaveCount++;
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }

        public event Action<AppSettings>? SettingsChanged;
    }
}
