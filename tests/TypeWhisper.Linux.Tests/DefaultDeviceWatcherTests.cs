using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

// Testable core of the runtime default-device watcher: debounce/coalescing and
// dispatch. Drives the dispatcher with a manual TimeProvider so the debounce is
// exercised deterministically, with no real pactl process and no wall-clock sleeps.
public sealed class DefaultDeviceChangeDispatcherTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    [Fact]
    public void Signal_FiresCallbackOnce_AfterDebounceElapses()
    {
        var time = new ManualTimeProvider();
        var fired = 0;
        using var sut = new DefaultDeviceChangeDispatcher(() => fired++, Debounce, time);

        sut.Signal();
        Assert.Equal(0, fired); // Not yet — the debounce window hasn't elapsed.

        time.Advance(Debounce);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Signal_CoalescesBurst_IntoASingleCallback()
    {
        var time = new ManualTimeProvider();
        var fired = 0;
        using var sut = new DefaultDeviceChangeDispatcher(() => fired++, Debounce, time);

        // A burst of events (PipeWire emits several server events per default switch)
        // each within the window: the deadline keeps getting pushed out.
        sut.Signal();
        time.Advance(TimeSpan.FromMilliseconds(100));
        sut.Signal();
        time.Advance(TimeSpan.FromMilliseconds(100));
        sut.Signal();
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(0, fired); // Still coalescing — never idle for a full window.

        time.Advance(Debounce);
        Assert.Equal(1, fired); // Exactly one callback for the whole burst.
    }

    [Fact]
    public void Signal_AfterFiring_ArmsANewDebounce()
    {
        var time = new ManualTimeProvider();
        var fired = 0;
        using var sut = new DefaultDeviceChangeDispatcher(() => fired++, Debounce, time);

        sut.Signal();
        time.Advance(Debounce);
        Assert.Equal(1, fired);

        // A later, separate change is its own coalesced callback.
        sut.Signal();
        time.Advance(Debounce);
        Assert.Equal(2, fired);
    }

    [Fact]
    public void Dispose_PreventsPendingCallback()
    {
        var time = new ManualTimeProvider();
        var fired = 0;
        var sut = new DefaultDeviceChangeDispatcher(() => fired++, Debounce, time);

        sut.Signal();
        sut.Dispose();

        // The armed timer must not fire after disposal.
        time.Advance(Debounce);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void Signal_AfterDispose_IsANoOp()
    {
        var time = new ManualTimeProvider();
        var fired = 0;
        var sut = new DefaultDeviceChangeDispatcher(() => fired++, Debounce, time);
        sut.Dispose();

        sut.Signal();
        time.Advance(Debounce);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void CallbackThrow_DoesNotEscape()
    {
        var time = new ManualTimeProvider();
        using var sut = new DefaultDeviceChangeDispatcher(
            () => throw new InvalidOperationException("boom"),
            Debounce,
            time
        );

        sut.Signal();
        // A throwing callback must be swallowed (it runs on the timer thread); the
        // manual timer surfaces it synchronously here, so assert it does not escape.
        var ex = Record.Exception(() => time.Advance(Debounce));
        Assert.Null(ex);
    }
}

// Line classifier: which pactl-subscribe lines are relevant to a default-capture
// change. Pure/static, so it needs no pactl process.
public sealed class PactlDefaultDeviceRelevanceTests
{
    [Theory]
    [InlineData("Event 'change' on server #0")]
    [InlineData("Event 'new' on source #42")]
    [InlineData("Event 'remove' on source #42")]
    [InlineData("Event 'change' on source #3")]
    public void RelevantLines_AreDetected(string line)
    {
        Assert.True(PactlDefaultDeviceWatcher.IsDefaultDeviceRelevant(line));
    }

    [Theory]
    [InlineData("Event 'new' on sink #1")]
    [InlineData("Event 'change' on sink-input #7")]
    [InlineData("Event 'change' on source-output #9")]
    [InlineData("Event 'new' on client #12")]
    [InlineData("Event 'change' on card #0")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IrrelevantLines_AreIgnored(string? line)
    {
        Assert.False(PactlDefaultDeviceWatcher.IsDefaultDeviceRelevant(line));
    }
}

// Fallback + fake-watcher wiring: with no real pactl, nothing starts and nothing
// throws; a change event through the fake watcher triggers exactly one re-resolve.
public sealed class DefaultDeviceWatcherFallbackTests
{
    [Fact]
    public void FollowDefault_WithFakeWatcher_StartsExactlyOnce_AndIsIdempotent()
    {
        var watcher = new FakeDefaultDeviceChangeWatcher();
        using var sut = new AudioRecordingService(deviceWatcher: watcher);

        sut.FollowSystemDefault = true;
        Assert.Equal(1, watcher.StartCount);

        // Re-asserting the same mode must not spawn a second watcher.
        sut.FollowSystemDefault = true;
        Assert.Equal(1, watcher.StartCount);
    }

    [Fact]
    public void TurningOffFollowDefault_StopsTheWatcher()
    {
        var watcher = new FakeDefaultDeviceChangeWatcher();
        using var sut = new AudioRecordingService(deviceWatcher: watcher);

        sut.FollowSystemDefault = true;
        sut.FollowSystemDefault = false;

        Assert.Equal(1, watcher.StartCount);
        Assert.Equal(1, watcher.StopCount);
    }

    [Fact]
    public void WatcherChangeEvent_TriggersExactlyOneCheck()
    {
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "A", 1, isDefault: true),
            new FakeDevice(1, "B", 1, isDefault: false));
        var watcher = new FakeDefaultDeviceChangeWatcher();
        using var sut = new AudioRecordingService(deviceEnumerator: devices, deviceWatcher: watcher)
        {
            FollowSystemDefault = true
        };
        sut.SetActiveDeviceIdForTest("A|1", 0);

        // The OS default moved to B; firing the watcher runs CheckForDefaultDeviceChange
        // exactly once, migrating the (idle) service to B.
        devices.SetDefault("B|1");
        watcher.FireChange();

        Assert.Equal("B|1", sut.ActiveDeviceIdForTest);
        Assert.Equal(1, sut.SelectedDeviceIndex);
    }

    [Fact]
    public void NoWatcher_FollowDefault_DoesNotThrow_AndDegradesToLazy()
    {
        // deviceWatcher: null models "pactl unavailable, watcher never constructed".
        // Follow mode must still work; nothing to start, nothing throws.
        var devices = new FakeAudioDeviceEnumerator(
            new FakeDevice(0, "A", 1, isDefault: true));
        using var sut = new AudioRecordingService(deviceEnumerator: devices);

        var ex = Record.Exception(() => sut.FollowSystemDefault = true);
        Assert.Null(ex);
    }

    [Fact]
    public void PactlWatcher_WithoutPactl_StartDoesNothing_AndDoesNotThrow()
    {
        // Real pactl watcher with an availability probe that reports pactl absent:
        // Start must be a graceful no-op (no child process spawned) and never throw.
        using var sut = new PactlDefaultDeviceWatcher(isPactlAvailable: () => false);

        var ex = Record.Exception(() => sut.Start(() => { }));
        Assert.Null(ex);
    }

    [Fact]
    public void PactlWatcher_StartAfterDispose_IsANoOp_AndDoesNotThrow()
    {
        // Guards the Start()/Dispose() race: once disposed, Start() must not launch a
        // 'pactl subscribe' child (which Dispose()'s Stop() would no longer reap). The
        // probe would report pactl available, so only the _disposed re-check keeps Start
        // from spawning. Must be a silent no-op and never throw.
        var sut = new PactlDefaultDeviceWatcher(isPactlAvailable: () => true);
        sut.Dispose();

        var ex = Record.Exception(() => sut.Start(() => { }));
        Assert.Null(ex);
    }
}

// Fake event source: no real pactl process. Records Start/Stop and lets a test
// fire the debounced-callback synchronously via FireChange.
internal sealed class FakeDefaultDeviceChangeWatcher : IDefaultDeviceChangeWatcher
{
    private Action? _onChanged;

    public int StartCount { get; private set; }
    public int StopCount { get; private set; }

    public void Start(Action onDefaultDeviceChanged)
    {
        StartCount++;
        _onChanged = onDefaultDeviceChanged;
    }

    public void Stop()
    {
        StopCount++;
        _onChanged = null;
    }

    // Simulate the watcher observing a default-device change and (post-debounce)
    // invoking its callback exactly once.
    public void FireChange() => _onChanged?.Invoke();

    public void Dispose() => Stop();
}

// Deterministic TimeProvider: timers fire synchronously when Advance() crosses
// their due time. Only the members the dispatcher uses are implemented.
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private long _nowTicks;

    public void Advance(TimeSpan by)
    {
        _nowTicks += by.Ticks;
        // Snapshot: a timer callback may (re)schedule via Change; iterate a copy.
        foreach (var timer in _timers.ToArray())
        {
            timer.MaybeFire(_nowTicks);
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period
    )
    {
        var timer = new ManualTimer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    private void Remove(ManualTimer timer) => _timers.Remove(timer);

    public override long GetTimestamp() => _nowTicks;

    private sealed class ManualTimer(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state
    ) : ITimer
    {
        private long? _dueTicks;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _dueTicks = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : owner._nowTicks + dueTime.Ticks;
            return true;
        }

        public void MaybeFire(long nowTicks)
        {
            if (_dueTicks is { } due && nowTicks >= due)
            {
                // One-shot in this suite: disarm before firing (the callback may re-arm).
                _dueTicks = null;
                callback(state);
            }
        }

        public bool Dispose(WaitHandle notifyObject) => throw new NotSupportedException();

        public void Dispose()
        {
            _dueTicks = null;
            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
