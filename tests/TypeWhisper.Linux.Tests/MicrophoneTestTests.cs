using PortAudioSharp;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class MicrophoneTestTests
{
    private const float Threshold = 0.01f;

    [Fact]
    public void Snapshot_WaitsThenReportsNoAudioAfterOneSecondWithoutBuffers()
    {
        var clock = new ManualClock();
        var test = new MicrophoneTest("Mic", Threshold, clock);

        Assert.Equal(MicrophoneTestState.WaitingForAudio, test.Snapshot().State);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(MicrophoneTestState.WaitingForAudio, test.Snapshot().State);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(MicrophoneTestState.NoAudio, test.Snapshot().State);
    }

    [Fact]
    public void Snapshot_SeparatesZeroSamplesQuietAndSignal()
    {
        var clock = new ManualClock();
        var test = new MicrophoneTest("Mic", Threshold, clock);

        Observe(test, 0f, -0f, 0f);
        Assert.Equal(MicrophoneTestState.ZeroSamples, test.Snapshot().State);

        // A real noise floor is input, not digital silence.
        Observe(test, 0.0001f, -0.0002f);
        Assert.Equal(MicrophoneTestState.Quiet, test.Snapshot().State);

        Observe(test, 0.2f, -0.2f);
        var heard = test.Snapshot();
        Assert.Equal(MicrophoneTestState.Signal, heard.State);
        Assert.Equal(0.2f, heard.Level, precision: 5);

        // Speech was heard; quiet afterwards keeps the result, but muting is reported.
        Observe(test, 0.0001f);
        Assert.Equal(MicrophoneTestState.Signal, test.Snapshot().State);
        Observe(test, 0f, 0f);
        Assert.Equal(MicrophoneTestState.ZeroSamples, test.Snapshot().State);
    }

    [Fact]
    public void Snapshot_BuffersThatStopReportNoAudioWithZeroLevel()
    {
        var clock = new ManualClock();
        var test = new MicrophoneTest("Mic", Threshold, clock);
        Observe(test, 0.3f);

        clock.Advance(TimeSpan.FromSeconds(1));

        var snapshot = test.Snapshot();
        Assert.Equal(MicrophoneTestState.NoAudio, snapshot.State);
        Assert.Equal(0f, snapshot.Level);
    }

    [Fact]
    public void Stop_FreezesResultKeepsFirstReasonAndIgnoresLaterFrames()
    {
        var clock = new ManualClock();
        var test = new MicrophoneTest("Mic", Threshold, clock);
        Observe(test, 0.0001f);
        clock.Advance(TimeSpan.FromMilliseconds(200));

        Assert.True(test.Stop(MicrophoneTestEnd.DeviceChanged));
        Assert.False(test.Stop(MicrophoneTestEnd.StoppedByUser));
        Observe(test, 0.5f);
        clock.Advance(TimeSpan.FromSeconds(5));

        var snapshot = test.Snapshot();
        Assert.False(snapshot.Running);
        Assert.Equal(MicrophoneTestEnd.DeviceChanged, snapshot.End);
        Assert.Equal(MicrophoneTestState.Quiet, snapshot.State);
        Assert.Equal(TimeSpan.FromMilliseconds(200), snapshot.Duration);
    }

    [Fact]
    public void Service_StartsPreviewForTestAndFeedsItPreviewFrames()
    {
        var opened = new List<int>();
        using var audio = CreateService(opened);

        var test = audio.TryStartMicrophoneTest(out var failure);

        Assert.NotNull(test);
        Assert.Equal(MicrophoneTestStartFailure.None, failure);
        Assert.Equal([0], opened);
        Assert.Equal("Default Mic", test.DeviceName);
        audio.ProcessAudioBufferForTest([0.2f, -0.2f]);
        Assert.Equal(MicrophoneTestState.Signal, test.Snapshot().State);
    }

    [Fact]
    public void Service_TestReopensRunningPreviewAndStoppingTestKeepsPreview()
    {
        var opened = new List<int>();
        var closed = 0;
        using var audio = CreateService(opened, () => closed++);
        Assert.True(audio.StartPreview());

        var test = audio.TryStartMicrophoneTest(out _);
        Assert.NotNull(test);
        audio.StopMicrophoneTest(test, MicrophoneTestEnd.StoppedByUser);

        Assert.Equal([0, 0], opened);
        Assert.Equal(1, closed);
        Assert.Equal(MicrophoneTestEnd.StoppedByUser, test.Snapshot().End);
        // The page's level meter keeps its preview: a second preview start is refused.
        Assert.False(audio.StartPreview());
    }

    [Fact]
    public void Service_RetryAfterStalledStreamReopensAndRecovers()
    {
        var clock = new ManualClock();
        var opened = new List<int>();
        using var audio = new AudioRecordingService(
            () => [new AudioInputDevice(0, "Default Mic", 1, true, "Default Mic|1")],
            opened.Add,
            () => 0,
            () => { },
            timeProvider: clock,
            postToUiThread: static _ => { }
        );
        var first = audio.TryStartMicrophoneTest(out _);
        Assert.NotNull(first);
        audio.ProcessAudioBufferForTest([0.2f]);

        // The device stops delivering buffers without the stream reporting an error.
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(MicrophoneTestState.NoAudio, first.Snapshot().State);
        audio.StopMicrophoneTest(first, MicrophoneTestEnd.StoppedByUser);

        var retry = audio.TryStartMicrophoneTest(out _);
        Assert.NotNull(retry);
        audio.ProcessAudioBufferForTest([0.2f]);

        Assert.Equal([0, 0], opened);
        Assert.Equal(MicrophoneTestState.Signal, retry.Snapshot().State);
    }

    [Fact]
    public void Service_FailedReopenLeavesPreviewClosedAndZeroesMeter()
    {
        var throwOnOpen = false;
        var levels = new List<float>();
        var queued = new List<Action>();
        using var audio = new AudioRecordingService(
            () => [new AudioInputDevice(0, "Default Mic", 1, true, "Default Mic|1")],
            _ =>
            {
                // ReSharper disable once AccessToModifiedClosure -- the test flips the flag to fail and then recover the open.
                if (throwOnOpen)
                {
                    throw new InvalidOperationException("device gone");
                }
            },
            () => 0,
            () => { },
            postToUiThread: queued.Add
        );
        audio.LevelChanged += (_, level) => levels.Add(level);
        Assert.True(audio.StartPreview());
        audio.ProcessAudioBufferForTest([0.2f]);

        throwOnOpen = true;
        Assert.Null(audio.TryStartMicrophoneTest(out var failure));
        foreach (var job in queued.ToArray())
        {
            job();
        }

        Assert.Equal(MicrophoneTestStartFailure.OpenFailed, failure);
        Assert.Equal(0f, levels[^1]);
        throwOnOpen = false;
        Assert.True(audio.StartPreview());
    }

    [Fact]
    public void Service_StartingAnotherTestEndsThePreviousOne()
    {
        using var audio = CreateService([]);
        var first = audio.TryStartMicrophoneTest(out _);
        var second = audio.TryStartMicrophoneTest(out _);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.False(first.Snapshot().Running);
        Assert.True(second.Snapshot().Running);
        audio.ProcessAudioBufferForTest([0f]);
        Assert.Equal(MicrophoneTestState.WaitingForAudio, first.Snapshot().State);
        Assert.Equal(MicrophoneTestState.ZeroSamples, second.Snapshot().State);
    }

    [Fact]
    public void Service_RefusesTestWhileDictationOwnsOrReservedCapture()
    {
        using var audio = CreateService([]);
        var session = audio.TryStartRecording(false);
        Assert.NotNull(session);
        Assert.Null(audio.TryStartMicrophoneTest(out var failure));
        Assert.Equal(MicrophoneTestStartFailure.Busy, failure);
        audio.StopRecording(session);

        using (audio.TryReserveCapture())
        {
            Assert.Null(audio.TryStartMicrophoneTest(out failure));
            Assert.Equal(MicrophoneTestStartFailure.Busy, failure);
        }

        Assert.NotNull(audio.TryStartMicrophoneTest(out _));
    }

    [Fact]
    public void Service_ReportsMissingDeviceAndOpenFailureThenRecovers()
    {
        var defaultIndex = PortAudio.NoDevice;
        var throwOnOpen = false;
        using var audio = new AudioRecordingService(
            () => [new AudioInputDevice(0, "Default Mic", 1, true, "Default Mic|1")],
            _ =>
            {
                // ReSharper disable once AccessToModifiedClosure -- the test flips the flag to fail and then recover the open.
                if (throwOnOpen)
                {
                    throw new InvalidOperationException("device busy");
                }
            },
            // ReSharper disable once AccessToModifiedClosure -- the test changes the default to simulate a device appearing.
            () => defaultIndex,
            () => { },
            postToUiThread: static _ => { }
        );

        Assert.Null(audio.TryStartMicrophoneTest(out var failure));
        Assert.Equal(MicrophoneTestStartFailure.NoDevice, failure);

        defaultIndex = 0;
        throwOnOpen = true;
        Assert.Null(audio.TryStartMicrophoneTest(out failure));
        Assert.Equal(MicrophoneTestStartFailure.OpenFailed, failure);

        throwOnOpen = false;
        Assert.NotNull(audio.TryStartMicrophoneTest(out failure));
        Assert.Equal(MicrophoneTestStartFailure.None, failure);
    }

    [Fact]
    public void Service_StopPreviewEndsTest()
    {
        using var audio = CreateService([]);
        var test = audio.TryStartMicrophoneTest(out _);
        Assert.NotNull(test);

        audio.StopPreview();
        audio.ProcessAudioBufferForTest([0.5f]);

        var snapshot = test.Snapshot();
        Assert.Equal(MicrophoneTestEnd.PreviewStopped, snapshot.End);
        Assert.Equal(MicrophoneTestState.WaitingForAudio, snapshot.State);
    }

    [Fact]
    public void Service_DictationEndsTestAndRecordedFramesNeverReachIt()
    {
        using var audio = CreateService([]);
        var test = audio.TryStartMicrophoneTest(out _);
        Assert.NotNull(test);

        var session = audio.TryStartRecording(false);
        Assert.NotNull(session);
        audio.ProcessAudioBufferForTest([0.5f]);

        var snapshot = test.Snapshot();
        Assert.Equal(MicrophoneTestEnd.DictationStarted, snapshot.End);
        Assert.Equal(MicrophoneTestState.WaitingForAudio, snapshot.State);
        audio.StopRecording(session);
    }

    [Fact]
    public void Service_ReservationEndsTest()
    {
        using var audio = CreateService([]);
        var test = audio.TryStartMicrophoneTest(out _);
        Assert.NotNull(test);

        using (audio.TryReserveCapture())
        {
            Assert.Equal(MicrophoneTestEnd.DictationStarted, test.Snapshot().End);
        }
    }

    [Fact]
    public void Service_DefaultDeviceMigrationEndsTestAndReopensPreview()
    {
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "A", 1, isDefault: true),
            new FakeDevice(1, "B", 1, isDefault: false)
        );
        var opened = new List<int>();
        using var audio = new AudioRecordingService(
            devices.GetDevices,
            opened.Add,
            () => devices.GetDevices().Single(device => device.IsDefault).Index,
            () => { },
            postToUiThread: static _ => { }
        );
        audio.FollowSystemDefault = true;
        var test = audio.TryStartMicrophoneTest(out _);
        Assert.NotNull(test);
        Assert.Equal("A", test.DeviceName);

        devices.SetDefault("B|1");
        audio.CheckForDefaultDeviceChange();

        Assert.Equal(MicrophoneTestEnd.DeviceChanged, test.Snapshot().End);
        Assert.Equal([0, 1], opened);
        var next = audio.TryStartMicrophoneTest(out _);
        Assert.NotNull(next);
        Assert.Equal("B", next.DeviceName);
        Assert.Equal([0, 1, 1], opened);
    }

    [Fact]
    public void Service_DisposeEndsTest()
    {
        using var audio = CreateService([]);
        var test = audio.TryStartMicrophoneTest(out _);
        Assert.NotNull(test);

        // ReSharper disable once DisposeOnUsingVariable -- the explicit Dispose is what this test exercises; `using` only covers an assert throwing before it.
        audio.Dispose();

        Assert.False(test.Snapshot().Running);
        Assert.Null(audio.TryStartMicrophoneTest(out var failure));
        Assert.Equal(MicrophoneTestStartFailure.OpenFailed, failure);
    }

    private static void Observe(MicrophoneTest test, params float[] samples)
    {
        test.Observe(samples, AudioRecordingService.ComputeRmsLevel(samples));
    }

    private static AudioRecordingService CreateService(List<int> opened, Action? close = null)
    {
        return new AudioRecordingService(
            () => [new AudioInputDevice(0, "Default Mic", 1, true, "Default Mic|1")],
            opened.Add,
            () => 0,
            close ?? (static () => { }),
            postToUiThread: static _ => { }
        );
    }
}

// Manual clock whose timers fire synchronously from Advance, so 100 ms refresh ticks
// and the 15-second limit run deterministically.
internal sealed class ManualClock : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _timestamp;

    public int ActiveTimerCount => _timers.Count(timer => !timer.IsDisposed);

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period
    )
    {
        var timer = new ManualTimer(callback, state, _timestamp + dueTime.Ticks, period.Ticks);
        _timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan elapsed)
    {
        var target = _timestamp + elapsed.Ticks;
        while (true)
        {
            var next = _timers
                .Where(timer => !timer.IsDisposed && timer.DueAt <= target)
                .MinBy(timer => timer.DueAt);
            if (next is null)
            {
                break;
            }

            _timestamp = Math.Max(_timestamp, next.DueAt);
            next.Fire();
        }

        _timestamp = target;
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, long dueAt, long period)
        : ITimer
    {
        public long DueAt { get; private set; } = dueAt;
        public bool IsDisposed { get; private set; }

        public void Fire()
        {
            if (period > 0)
            {
                DueAt += period;
            }
            else
            {
                IsDisposed = true;
            }

            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan newPeriod) => throw new NotSupportedException();

        public void Dispose() => IsDisposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
