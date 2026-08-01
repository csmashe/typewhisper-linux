using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK.Processes;
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
    public void PactlWatcher_starts_and_stops_supervised_line_session()
    {
        var session = new ControlledSession();
        var runner = new FakeProcessRunner
        {
            SessionFactory = (_, _) => new ProcessSessionStartOutcome(
                session,
                null
            ),
        };
        var commands = new SystemCommandAvailabilityService(runner);
        commands.RaiseSnapshotChangedForTests(
            commands.GetSnapshot() with { HasPactl = true }
        );
        runner.SessionInvocations.Clear();
        using var watcher = new PactlDefaultDeviceWatcher(commands, runner);

        watcher.Start(() => { });

        var invocation = Assert.Single(runner.SessionInvocations);
        Assert.Equal("pactl", invocation.Command.FileName);
        Assert.Equal(["subscribe"], invocation.Command.Arguments);
        Assert.Equal("C", invocation.Command.Environment!["LC_ALL"]);
        Assert.Equal(
            ProcessSessionOutputMode.Lines,
            invocation.Options.StandardOutput
        );
        Assert.Equal(
            ProcessSessionOutputMode.Discard,
            invocation.Options.StandardError
        );

        watcher.Stop();
        Assert.True(session.Terminated);
    }

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
            starts++;
            // One relevant line then EOF (StringReader returns null after its content).
            return PactlSubscription.ForTest(new StringReader("Event 'change' on server #0\n"));
        }

        using var sut = new PactlDefaultDeviceWatcher(
            isPactlAvailable: () => true,
            subscriptionFactory: Factory);

        sut.Start(() => { });
        // The first run's read loop hits EOF and self-tears-down: wait until it clears.
        // WaitUntil runs the closure synchronously (spin-wait) before `sut` is disposed.
        // ReSharper disable once AccessToDisposedClosure
        WaitUntil(() => !sut.IsRunningForTest);
        Assert.Equal(1, starts);

        // A second Start() must NOT be rejected as "already running" — it spawns a fresh
        // subscription because the first run cleared its state on exit.
        sut.Start(() => { });
        // Synchronous spin-wait again; captured `sut` is not accessed after disposal.
        // ReSharper disable once AccessToDisposedClosure
        WaitUntil(() => !sut.IsRunningForTest);
        Assert.Equal(2, starts);
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

    private sealed class ControlledSession : IPluginProcessSession
    {
        private readonly TaskCompletionSource<ProcessExitOutcome> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Terminated { get; private set; }
        public int ProcessId => 123;
        public bool IsRunning => !_completion.Task.IsCompleted;
        public Task<ProcessExitOutcome> Completion => _completion.Task;

        public async IAsyncEnumerable<ProcessOutputLine> ReadOutputAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default
        )
        {
            await Completion.WaitAsync(cancellationToken);
            yield break;
        }

        public ValueTask WriteStandardInputAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public ValueTask CompleteStandardInputAsync(
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public void Terminate()
        {
            Terminated = true;
            _completion.TrySetResult(
                new ProcessExitOutcome(ProcessExitReason.Terminated, null)
            );
        }

        public void Dispose()
        {
            Terminate();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
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
