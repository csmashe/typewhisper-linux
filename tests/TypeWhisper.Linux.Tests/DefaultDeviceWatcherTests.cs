using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

// Testable core of the runtime default-device watcher: debounce/coalescing and
// dispatch. Drives the dispatcher with a manual TimeProvider so the debounce is
// exercised deterministically, with no real pactl process and no wall-clock sleeps.
public sealed class DefaultDeviceChangeDispatcherTests
{
    private static readonly TimeSpan s_debounce = TimeSpan.FromMilliseconds(300);

    [Fact]
    public void Signal_FiresCallbackOnce_AfterDebounceElapses()
    {
        var time = new ManualTimeProvider();
        var fired = 0;
        using var sut = new DefaultDeviceChangeDispatcher(() => fired++, s_debounce, time);

        sut.Signal();
        Assert.Equal(0, fired); // Not yet — the debounce window hasn't elapsed.

        time.Advance(s_debounce);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Signal_CoalescesBurst_IntoASingleCallback()
    {
        var time = new ManualTimeProvider();
        var fired = 0;
        using var sut = new DefaultDeviceChangeDispatcher(() => fired++, s_debounce, time);

        // A burst of events (PipeWire emits several server events per default switch)
        // each within the window: the deadline keeps getting pushed out.
        sut.Signal();
        time.Advance(TimeSpan.FromMilliseconds(100));
        sut.Signal();
        time.Advance(TimeSpan.FromMilliseconds(100));
        sut.Signal();
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(0, fired); // Still coalescing — never idle for a full window.

        time.Advance(s_debounce);
        Assert.Equal(1, fired); // Exactly one callback for the whole burst.
    }

    [Fact]
    public void Signal_AfterFiring_ArmsANewDebounce()
    {
        var time = new ManualTimeProvider();
        var fired = 0;
        using var sut = new DefaultDeviceChangeDispatcher(() => fired++, s_debounce, time);

        sut.Signal();
        time.Advance(s_debounce);
        Assert.Equal(1, fired);

        // A later, separate change is its own coalesced callback.
        sut.Signal();
        time.Advance(s_debounce);
        Assert.Equal(2, fired);
    }

    [Fact]
    public void Dispose_PreventsPendingCallback()
    {
        var time = new ManualTimeProvider();
        var fired = 0;
        // `using` guarantees cleanup even if Signal() throws before the explicit Dispose().
        using var sut = new DefaultDeviceChangeDispatcher(() => fired++, s_debounce, time);

        sut.Signal();
        // Explicit Dispose() is the behavior under test (it must run before the timer would
        // fire); Dispose() is idempotent, so the scope-end call is a harmless no-op.
        // ReSharper disable once DisposeOnUsingVariable
        sut.Dispose();

        // The armed timer must not fire after disposal.
        time.Advance(s_debounce);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void Signal_AfterDispose_IsANoOp()
    {
        var time = new ManualTimeProvider();
        var fired = 0;
        // `using` adds cleanup-on-throw safety; the explicit Dispose() below is the setup
        // under test (a Signal after Dispose must be a no-op).
        using var sut = new DefaultDeviceChangeDispatcher(() => fired++, s_debounce, time);
        // ReSharper disable once DisposeOnUsingVariable
        sut.Dispose();

        sut.Signal();
        time.Advance(s_debounce);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void CallbackThrow_DoesNotEscape()
    {
        var time = new ManualTimeProvider();
        using var sut = new DefaultDeviceChangeDispatcher(
            () => throw new InvalidOperationException("boom"),
            s_debounce,
            time
        );

        sut.Signal();
        // A throwing callback must be swallowed (it runs on the timer thread); the
        // manual timer surfaces it synchronously here, so assert it does not escape.
        var ex = Record.Exception(() => time.Advance(s_debounce));
        Assert.Null(ex);
    }

    [Fact]
    public void StaleTimerCallback_QueuedBeforeALaterSignal_DoesNotFireEarly()
    {
        // Regression: a timer callback that was already dispatched for an EARLIER deadline
        // can run after a later Signal() extended the deadline. It must be recognized as
        // stale (its captured generation is no longer current) and skipped, so the burst
        // still fires exactly once — for the NEWEST deadline — honoring the debounce.
        var time = new DeferredFireTimeProvider();
        var fired = 0;
        using var sut = new DefaultDeviceChangeDispatcher(() => fired++, s_debounce, time);

        // gen 1: arm the timer, then let its due time elapse so its callback is DISPATCHED
        // (captured as pending) but not yet executed.
        sut.Signal();
        time.ElapseAndCapturePending();

        // gen 2: a fresh Signal arrives before the gen-1 callback ran, pushing the deadline
        // out. This arms a new timer for gen 2.
        sut.Signal();

        // The stale gen-1 callback now runs. It must NOT fire (gen 2 is the latest arming).
        time.RunCapturedPending();
        Assert.Equal(0, fired);

        // gen 2's deadline elapses and its callback runs → exactly one fire for the burst.
        time.ElapseAndCapturePending();
        time.RunCapturedPending();
        Assert.Equal(1, fired);
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
        using var sut = new AudioRecordingService(deviceEnumerator: devices, deviceWatcher: watcher);
        sut.FollowSystemDefault = true;
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

        // Record.Exception invokes the closure synchronously, before `sut` is disposed at
        // scope end, so the captured `sut` is never accessed after disposal.
        // ReSharper disable once AccessToDisposedClosure
        var ex = Record.Exception(() => sut.FollowSystemDefault = true);
        Assert.Null(ex);
    }

    [Fact]
    public void PactlWatcher_WithoutPactl_StartDoesNothing_AndDoesNotThrow()
    {
        // Real pactl watcher with an availability probe that reports pactl absent:
        // Start must be a graceful no-op (no child process spawned) and never throw.
        using var sut = new PactlDefaultDeviceWatcher(isPactlAvailable: () => false);

        // Record.Exception invokes the closure synchronously, before `sut` is disposed at
        // scope end, so the captured `sut` is never accessed after disposal.
        // ReSharper disable once AccessToDisposedClosure
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

    [Fact]
    public void PactlWatcher_Restarts_AfterReadLoopExitsOnEof()
    {
        // Regression: when the 'pactl subscribe' read loop hits EOF/error the watcher must
        // clear its run state so a LATER Start() spawns a fresh subscription. Previously the
        // subscription handle stayed set after the loop ended, so Start() saw "already
        // running" and never restarted. Here each fake subscription returns one relevant
        // line then EOF, ending the loop; Start() must be able to spin up a second one.
        var starts = 0;
        // Factory is kept next to where it is wired into the watcher below rather than
        // moved after the asserts; define-before-use reads more clearly for this seam.
        // ReSharper disable once MoveLocalFunctionAfterJumpStatement
        PactlSubscription Factory()
        {
            // ReSharper disable once AccessToModifiedClosure -- the reader threads write the
            // counter and the asserts read it; interlocked on both sides.
            Interlocked.Increment(ref starts);
            // One relevant line then EOF (StringReader returns null after its content).
            return PactlSubscription.ForTest(new StringReader("Event 'change' on server #0\n"));
        }

        // This test is about the EXPLICIT restart, so both reconnect knobs are pinned out of
        // reach: every instant-EOF run counts as dying young and its backoff never elapses here.
        // Otherwise an automatic reconnect could bump `starts` under the assertions below.
        using var sut = new PactlDefaultDeviceWatcher(
            isPactlAvailable: () => true,
            subscriptionFactory: Factory,
            minRunBeforeRestart: TimeSpan.FromHours(1),
            retryBackoff: TimeSpan.FromHours(1));

        sut.Start(() => { });
        // The first run's read loop hits EOF and self-tears-down: wait until it clears.
        // WaitUntil runs the closure synchronously (spin-wait) before `sut` is disposed.
        // ReSharper disable once AccessToDisposedClosure
        WaitUntil(() => !sut.IsRunningForTest);
        Assert.Equal(1, Volatile.Read(ref starts));

        // A second Start() must NOT be rejected as "already running" — it spawns a fresh
        // subscription because the first run cleared its state on exit.
        sut.Start(() => { });
        // Synchronous spin-wait again; captured `sut` is not accessed after disposal.
        // ReSharper disable once AccessToDisposedClosure
        WaitUntil(() => !sut.IsRunningForTest);
        Assert.Equal(2, Volatile.Read(ref starts));
    }

    [Fact]
    public void PactlWatcher_Reconnects_WhenASurvivingSubscriptionEnds()
    {
        // Regression: pactl exits when the sound server restarts, and AudioRecordingService
        // latches "watcher started" and never calls Start() again — so the watcher had to
        // reconnect itself or live default-device following was over for the session.
        // minRunBeforeRestart: Zero makes every instant-EOF fake count as a surviving run, so
        // each one reconnects immediately and at full budget. The 3rd subscription stays open,
        // which ends the chain at an exact count instead of leaving it to the retry budget.
        var starts = 0;
        var live = new BlockingSubscriptionReader();
        // ReSharper disable once MoveLocalFunctionAfterJumpStatement
        PactlSubscription Factory()
        {
            // ReSharper disable once AccessToModifiedClosure -- counting the reconnects IS the
            // assertion; the reader threads write it and the waits below read it, both interlocked.
            return Interlocked.Increment(ref starts) >= 3
                ? PactlSubscription.ForTest(live, onKill: live.Release, onDispose: live.Release)
                : PactlSubscription.ForTest(new StringReader("Event 'change' on server #0\n"));
        }

        using var sut = new PactlDefaultDeviceWatcher(
            isPactlAvailable: () => true,
            subscriptionFactory: Factory,
            minRunBeforeRestart: TimeSpan.Zero,
            retryBackoff: TimeSpan.FromMilliseconds(1));

        sut.Start(() => { });

        // Two self-reconnects (after runs 1 and 2), then the third stays up.
        WaitUntil(() => Volatile.Read(ref starts) == 3);
        // ReSharper disable once AccessToDisposedClosure -- WaitUntil spin-waits synchronously.
        WaitUntil(() => sut.IsRunningForTest);
        Assert.Equal(3, Volatile.Read(ref starts));
    }

    [Fact]
    public void PactlWatcher_RecoversFromATemporarilyUnavailableServer()
    {
        // The first reconnect after a server restart routinely lands before the server is
        // listening again, so pactl dies young. That must back off and keep trying, not
        // abandon recovery — otherwise the most common real outage kills the watcher.
        var starts = 0;
        var live = new BlockingSubscriptionReader();
        // ReSharper disable once MoveLocalFunctionAfterJumpStatement
        PactlSubscription Factory()
        {
            // ReSharper disable once AccessToModifiedClosure -- see above; interlocked throughout.
            var attempt = Interlocked.Increment(ref starts);
            // Attempts 1-3 die instantly (server still down); the 4th connects and stays up.
            // Kill/Dispose release the blocked reader so teardown does not strand a thread.
            return attempt >= 4
                ? PactlSubscription.ForTest(live, onKill: live.Release, onDispose: live.Release)
                : PactlSubscription.ForTest(new StringReader(""));
        }

        using var sut = new PactlDefaultDeviceWatcher(
            isPactlAvailable: () => true,
            // Every instant-EOF run counts as dying young, so all of them take the backoff path.
            minRunBeforeRestart: TimeSpan.FromHours(1),
            subscriptionFactory: Factory,
            retryBackoff: TimeSpan.FromMilliseconds(1));

        sut.Start(() => { });

        // Wait on the attempt count first: IsRunningForTest also flickers true for each of
        // the three short-lived runs, so it alone would not identify the recovered one.
        WaitUntil(() => Volatile.Read(ref starts) >= 4);
        // ReSharper disable once AccessToDisposedClosure -- WaitUntil spin-waits synchronously.
        WaitUntil(() => sut.IsRunningForTest);
        Assert.Equal(4, Volatile.Read(ref starts));
    }

    [Fact]
    public void PactlWatcher_GivesUp_WhenTheServerNeverComesBack()
    {
        // The bounded budget is what stops a permanently broken server from respawning pactl
        // forever: the initial start plus MaxAutoRestarts attempts, then it stays down.
        var starts = 0;
        // ReSharper disable once MoveLocalFunctionAfterJumpStatement
        PactlSubscription Factory()
        {
            // ReSharper disable once AccessToModifiedClosure -- see above; interlocked throughout.
            Interlocked.Increment(ref starts);
            return PactlSubscription.ForTest(new StringReader(""));
        }

        using var sut = new PactlDefaultDeviceWatcher(
            isPactlAvailable: () => true,
            minRunBeforeRestart: TimeSpan.FromHours(1),
            subscriptionFactory: Factory,
            retryBackoff: TimeSpan.FromMilliseconds(1));

        sut.Start(() => { });

        WaitUntil(() => Volatile.Read(ref starts) == 11);
        Thread.Sleep(150);
        Assert.Equal(11, Volatile.Read(ref starts));
    }

    [Fact]
    public void PactlWatcher_Stop_IsNotUndoneByAConcurrentReconnect()
    {
        // Stop() and the read loop's reconnect both take the watcher's lock; the reconnect
        // publishes its replacement without releasing it in between, so a Stop landing in
        // that window can never be overtaken by a watcher that restarts itself afterwards.
        var starts = 0;
        // ReSharper disable once MoveLocalFunctionAfterJumpStatement
        PactlSubscription Factory()
        {
            // ReSharper disable once AccessToModifiedClosure -- see above; interlocked throughout.
            Interlocked.Increment(ref starts);
            return PactlSubscription.ForTest(new StringReader("Event 'change' on server #0\n"));
        }

        for (var i = 0; i < 50; i++)
        {
            using var sut = new PactlDefaultDeviceWatcher(
                isPactlAvailable: () => true,
                subscriptionFactory: Factory,
                minRunBeforeRestart: TimeSpan.Zero,
                retryBackoff: TimeSpan.FromMilliseconds(1));

            sut.Start(() => { });
            sut.Stop();

            // Whatever the interleaving, a stopped watcher must settle with nothing running.
            // ReSharper disable once AccessToDisposedClosure -- WaitUntil spin-waits synchronously.
            WaitUntil(() => !sut.IsRunningForTest);
            Thread.Sleep(5);
            Assert.False(sut.IsRunningForTest, "Stop() was undone by a concurrent reconnect.");
        }
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        Assert.True(condition(), "Condition was not met within the timeout.");
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

    // GetTimestamp hands out TimeSpan ticks, so the frequency has to match or
    // GetElapsedTime would scale them by Stopwatch.Frequency.
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

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
            // Single guarded action; inverting to a guard clause would only negate a
            // positive pattern-match (`is { } due`) for no readability gain in this tiny body.
            // ReSharper disable once InvertIf
            if (_dueTicks is { } due && nowTicks >= due)
            {
                // One-shot in this suite: disarm before firing (the callback may re-arm).
                _dueTicks = null;
                callback(state);
            }
        }

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

// TimeProvider that models the real thread-pool race the debounce guard defends against:
// a timer's due time can elapse and DISPATCH its callback (queue it) before the callback
// actually runs, and a later Signal() (which disposes that timer and arms a new one) can
// slip in between. ElapseAndCapturePending() captures the due timer's callback WITHOUT
// running it (mirroring "dispatched but not yet executed"); RunCapturedPending() runs the
// captured callbacks. A callback captured this way still runs even if its timer is later
// disposed — exactly as a queued thread-pool work item would.
internal sealed class DeferredFireTimeProvider : TimeProvider
{
    private readonly List<DeferredTimer> _timers = [];
    private readonly List<Action> _pending = [];

    // Mark every currently-armed timer as due and capture its callback for deferred
    // execution, then disarm it (one-shot).
    public void ElapseAndCapturePending()
    {
        foreach (var timer in _timers.ToArray())
        {
            timer.CaptureIfArmed(_pending);
        }
    }

    // Run (and clear) all callbacks captured by ElapseAndCapturePending().
    public void RunCapturedPending()
    {
        var toRun = _pending.ToArray();
        _pending.Clear();
        foreach (var run in toRun)
        {
            run();
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period
    )
    {
        var timer = new DeferredTimer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    private void Remove(DeferredTimer timer) => _timers.Remove(timer);

    private sealed class DeferredTimer(
        DeferredFireTimeProvider owner,
        TimerCallback callback,
        object? state
    ) : ITimer
    {
        private bool _armed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _armed = dueTime != Timeout.InfiniteTimeSpan;
            return true;
        }

        // If armed, capture the callback into the pending list (dispatched, not run) and
        // disarm. The captured Action still runs later even if this timer is disposed —
        // modeling a work item that is already queued on the thread pool.
        public void CaptureIfArmed(List<Action> pending)
        {
            if (!_armed)
            {
                return;
            }

            _armed = false;
            pending.Add(() => callback(state));
        }

        public void Dispose()
        {
            _armed = false;
            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

// A subscription reader that stays open until released, modeling a pactl that connected
// successfully and is waiting for events. Release() (wired to the fake subscription's
// Kill/Dispose) unblocks it and reports EOF so teardown never strands a pool thread.
internal sealed class BlockingSubscriptionReader : TextReader
{
    private readonly ManualResetEventSlim _released = new(false);

    public override string? ReadLine()
    {
        _released.Wait();
        return null;
    }

    public void Release() => _released.Set();

    protected override void Dispose(bool disposing)
    {
        _released.Set();
        base.Dispose(disposing);
    }
}
