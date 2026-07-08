using PortAudioSharp;
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
    public void LiveFrameSink_InvokedFromCallback_WithProcessedSamples()
    {
        using var service = new AudioRecordingService { WhisperModeEnabled = true };
        var captured = new List<float[]>();
        service.LiveFrameSink = captured.Add;

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
    public void ResolveConfiguredDevice_FallsBackToIndex_WhenIdNoLongerMatches()
    {
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "Built-in Mic", 2, isDefault: true),
            new FakeDevice(1, "USB Mic", 1, isDefault: false));
        using var sut = new AudioRecordingService(deviceEnumerator: devices);

        var resolved = sut.ResolveConfiguredDevice(preferredIndex: 1, preferredDeviceId: "Gone Mic|4");

        Assert.Equal(1, resolved!.Index);
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
        using var sut = new AudioRecordingService(deviceEnumerator: devices)
        {
            FollowSystemDefault = false
        };
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
        using var sut = new AudioRecordingService(deviceEnumerator: devices)
        {
            FollowSystemDefault = true
        };
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
        using var sut = new AudioRecordingService(deviceEnumerator: devices)
        {
            FollowSystemDefault = true
        };
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
        using var sut = new AudioRecordingService(deviceEnumerator: devices)
        {
            FollowSystemDefault = true
        };
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
        using var sut = new AudioRecordingService(deviceEnumerator: devices)
        {
            FollowSystemDefault = true
        };
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
        using var sut = new AudioRecordingService(deviceEnumerator: devices)
        {
            FollowSystemDefault = true
        };
        sut.SetActiveDeviceIdForTest("A|1", 0);
        sut.SetRecordingForTest(true);

        devices.SetDefault("B|1");
        sut.CheckForDefaultDeviceChange();
        Assert.True(sut.MigrationPendingForTest);

        // StopRecording finalizes the (empty) buffer, then re-runs the check.
        sut.StopRecording();

        Assert.Equal("B|1", sut.ActiveDeviceIdForTest);
        Assert.False(sut.MigrationPendingForTest);
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
        using var sut = new AudioRecordingService(deviceEnumerator: devices)
        {
            FollowSystemDefault = true
        };
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
        using var sut = new AudioRecordingService(deviceEnumerator: devices)
        {
            FollowSystemDefault = true
        };
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
        using var sut = new AudioRecordingService(deviceEnumerator: devices)
        {
            FollowSystemDefault = true
        };
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