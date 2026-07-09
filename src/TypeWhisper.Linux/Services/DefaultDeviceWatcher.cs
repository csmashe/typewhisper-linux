using System.Diagnostics;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Watches for OS default capture-device changes at runtime and, when one is
///     detected, invokes a callback (in practice
///     <see cref="AudioRecordingService.CheckForDefaultDeviceChange" />) so the
///     service can migrate to the new default while it is idle. The callback is
///     always dispatched off the caller's thread and never on the PortAudio
///     realtime thread.
///     <para>
///         Split into a testable dispatch/debounce core (
///         <see cref="DefaultDeviceChangeDispatcher" />) and a thin, untested event
///         source (<see cref="PactlDefaultDeviceWatcher" />) that shells out to
///         <c>pactl subscribe</c>. When <c>pactl</c> is unavailable the watcher is
///         simply never started and behavior degrades to the existing lazy
///         re-resolve at the next recording start.
///     </para>
/// </summary>
public interface IDefaultDeviceChangeWatcher : IDisposable
{
    /// <summary>
    ///     Begin watching for default-device changes. <paramref name="onDefaultDeviceChanged" />
    ///     is invoked (debounced/coalesced) once per burst of relevant events. Idempotent:
    ///     a second call while already running is a no-op. Never throws — if the underlying
    ///     event source cannot be started it logs and stays stopped.
    /// </summary>
    void Start(Action onDefaultDeviceChanged);

    /// <summary>Stop watching and release the event source. Safe to call repeatedly.</summary>
    void Stop();
}

/// <summary>
///     Testable core of the default-device watcher: coalesces a burst of raw change
///     signals into a single debounced callback. Deliberately has no dependency on
///     <c>pactl</c> or any process, so the debounce/dispatch behavior is unit-tested
///     directly. The event SOURCE (reading pactl stdout) lives in
///     <see cref="PactlDefaultDeviceWatcher" /> and calls <see cref="Signal" /> per
///     relevant line.
/// </summary>
public sealed class DefaultDeviceChangeDispatcher : IDisposable
{
    private readonly TimeSpan _debounce;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private Action? _callback;
    private ITimer? _timer;
    private int _disposed;

    // Debounce generation guard. Each Signal() bumps _armedGeneration and records it as
    // the generation the timer is now armed for. A timer callback can already be queued
    // on the thread pool when a fresh Signal() extends the deadline via _timer.Change();
    // without this guard that stale callback would fire early, breaking the "one callback
    // after the burst settles" contract.
    //
    // The generation the timer was (re)armed for is CAPTURED and handed to the scheduled
    // Fire() callback. Fire() only proceeds when the generation it was scheduled for still
    // matches the latest armed generation — so a callback that was queued for an earlier
    // deadline (before a later Signal() pushed the deadline out) is recognized as stale
    // and skipped, letting the newest deadline's callback be the one that fires.
    private long _armedGeneration;

    // The generation that has already fired its callback. Guards the (theoretical)
    // case of a single generation's one-shot callback being delivered more than once,
    // preserving the "exactly one callback per settled burst" contract.
    private long _firedGeneration;

    /// <summary>
    ///     Creates a dispatcher that coalesces bursts of raw change signals into a single
    ///     debounced callback fired once the burst settles.
    /// </summary>
    /// <param name="onChanged">Invoked once per coalesced burst of signals.</param>
    /// <param name="debounce">
    ///     Window over which a burst of raw signals is coalesced into a single
    ///     callback. A burst of default-source churn (PipeWire emits several server
    ///     events per switch) collapses to one re-resolve.
    /// </param>
    /// <param name="timeProvider">
    ///     Clock/timer source. Injected so tests can drive the debounce deterministically
    ///     without wall-clock sleeps; defaults to <see cref="TimeProvider.System" />.
    /// </param>
    public DefaultDeviceChangeDispatcher(
        Action onChanged,
        TimeSpan? debounce = null,
        TimeProvider? timeProvider = null
    )
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        _callback = onChanged;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(350);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    ///     Record one raw change signal. Multiple signals inside the debounce window
    ///     collapse into a single callback fired once the window elapses. Cheap and
    ///     non-blocking; safe to call from the stdout-reader thread.
    /// </summary>
    public void Signal()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            // Bump the armed generation so an already-queued Fire() for the previous
            // deadline is recognized as stale and skipped.
            var scheduledGeneration = ++_armedGeneration;

            // (Re)arm a one-shot timer. Each new signal pushes the deadline out so a
            // continuous burst fires exactly once after it settles.
            //
            // The generation this arming targets is CAPTURED into the callback closure so
            // Fire() can tell whether it is still the latest deadline when it eventually
            // runs. A previous timer's due time may already have elapsed and dispatched its
            // callback onto the thread pool by the time we get here; that queued callback
            // carries the OLD (now-stale) generation and Fire() will drop it. We therefore
            // dispose the old timer and create a fresh one per (re)arm rather than reusing
            // it via ITimer.Change(): reuse keeps the ORIGINAL closure (and its original
            // captured generation), which would defeat this guard. The dispatcher only
            // arms on a burst of device-change events, so the per-signal timer allocation
            // is negligible.
            _timer?.Dispose();
            _timer = _timeProvider.CreateTimer(
                _ => Fire(scheduledGeneration),
                null,
                _debounce,
                Timeout.InfiniteTimeSpan
            );
        }
    }

    // scheduledGeneration is the _armedGeneration value captured when THIS timer was
    // armed. A later Signal() that extended the deadline bumped _armedGeneration and
    // armed a newer timer; this (older) callback must then be recognized as stale and
    // skipped so only the newest deadline's callback fires — honoring the debounce.
    private void Fire(long scheduledGeneration)
    {
        Action? callback;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            // Stale-callback guard: if a later Signal() re-armed after this callback was
            // scheduled, _armedGeneration has moved past the generation we were scheduled
            // for. Only the callback for the latest armed generation should run.
            if (scheduledGeneration != _armedGeneration)
            {
                return;
            }

            // Fire-once guard: never invoke the same generation's callback twice.
            if (scheduledGeneration == _firedGeneration)
            {
                return;
            }

            _firedGeneration = scheduledGeneration;
            callback = _callback;
        }

        try
        {
            callback?.Invoke();
        }
        catch (Exception ex)
        {
            // The callback is CheckForDefaultDeviceChange, which is itself defensive;
            // still never let a stray throw kill the timer thread.
            Trace.WriteLine(
                $"[DefaultDeviceChangeDispatcher] Change callback threw: {ex.Message}"
            );
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _callback = null;
        }
    }
}

/// <summary>
///     Production watcher: runs <c>pactl subscribe</c> as a long-lived child process
///     and forwards default-device-relevant events to a
///     <see cref="DefaultDeviceChangeDispatcher" />. Under both native PulseAudio and
///     PipeWire's <c>pipewire-pulse</c> compatibility layer (modern Fedora/GNOME), a
///     default-source change surfaces as an <c>Event 'change' on server</c> line;
///     source add/remove surface as <c>… on source</c>. The <c>server</c>/<c>source</c>
///     lines are the ones we react to.
///     <para>
///         GRACEFUL FALLBACK: if <c>pactl</c> is unavailable the watcher never starts
///         and the app degrades to the existing lazy re-resolve at the next recording
///         start. The subscribe process dying is logged and stops the watcher; it is
///         never fatal.
///     </para>
///     <para>
///         The stdout-reader is a thin, untested shell; the coalescing/dispatch logic
///         it feeds lives in the unit-tested <see cref="DefaultDeviceChangeDispatcher" />.
///     </para>
/// </summary>
public sealed class PactlDefaultDeviceWatcher : IDefaultDeviceChangeWatcher
{
    private readonly Func<bool> _isPactlAvailable;
    private readonly Func<PactlSubscription> _subscriptionFactory;
    private readonly TimeSpan _debounce;
    private readonly Lock _gate = new();

    private DefaultDeviceChangeDispatcher? _dispatcher;
    private PactlSubscription? _subscription;
    private CancellationTokenSource? _cts;
    private Task? _readerTask;
    // Identity token for the CURRENT run. The read loop captures its own token and, when
    // it exits (EOF/error), only clears watcher state if this token is still current — so
    // a self-teardown can never clobber a concurrent Stop()/Start() that already swapped
    // in (or nulled) the state. Guards fix: without a self-teardown, _subscription stayed
    // non-null after the loop ended and Start() wrongly reported "already running".
    private object? _runToken;
    private int _disposed;

    public PactlDefaultDeviceWatcher(SystemCommandAvailabilityService commands)
        : this(() => commands.HasPactl)
    {
    }

    // Availability-probe seam: tests inject a probe that reports pactl absent so the
    // graceful-fallback path (Start does nothing, never throws) is verifiable without
    // a real pactl on PATH.
    internal PactlDefaultDeviceWatcher(Func<bool> isPactlAvailable)
        : this(isPactlAvailable, LaunchPactlSubscribe)
    {
    }

    // Subscription-factory seam: tests inject a fake event source (a TextReader over a
    // MemoryStream, no real process) so the read-loop lifecycle — including the restart
    // after the loop exits on EOF/error — is verifiable without spawning 'pactl subscribe'.
    internal PactlDefaultDeviceWatcher(
        Func<bool> isPactlAvailable,
        Func<PactlSubscription> subscriptionFactory
    )
    {
        _isPactlAvailable = isPactlAvailable;
        _subscriptionFactory = subscriptionFactory;
        _debounce = TimeSpan.FromMilliseconds(350);
    }

    // True while a subscription run is active. Test-only: lets a test wait for the read
    // loop's self-teardown (which nulls _subscription) so the restart path is observable.
    internal bool IsRunningForTest
    {
        get
        {
            lock (_gate)
            {
                return _subscription is not null;
            }
        }
    }

    /// <summary>
    ///     Returns true if a raw <c>pactl subscribe</c> line describes a change that
    ///     could move the default capture device: an event on the <c>server</c>
    ///     facility (the default source/sink changed) or on the <c>source</c> facility
    ///     (a capture device was added/removed, which can shift the default). Static +
    ///     internal so the line classifier is unit-testable without spawning pactl.
    ///     <para>
    ///         The facility is matched EXACTLY so distinct entity types like
    ///         <c>source-output</c> and <c>sink-input</c> — which are not default-capture
    ///         changes — are correctly ignored.
    ///     </para>
    /// </summary>
    internal static bool IsDefaultDeviceRelevant(string? line)
    {
        var facility = ExtractFacility(line);
        return facility is "server" or "source";
    }

    // pactl subscribe lines look like:
    //   Event 'change' on server #0
    //   Event 'new' on source #42
    //   Event 'change' on source-output #9   (NOT a capture-default change)
    // Extract the facility token that follows " on " up to the next space or '#'.
    private static string? ExtractFacility(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        const string marker = " on ";
        var markerIdx = line.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIdx < 0)
        {
            return null;
        }

        var start = markerIdx + marker.Length;
        var end = start;
        while (end < line.Length && line[end] != ' ' && line[end] != '#')
        {
            end++;
        }

        return end > start ? line[start..end] : null;
    }

    public void Start(Action onDefaultDeviceChanged)
    {
        ArgumentNullException.ThrowIfNull(onDefaultDeviceChanged);

        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        // GRACEFUL FALLBACK: no pactl → never start; lazy re-resolve still applies.
        if (!_isPactlAvailable())
        {
            Trace.WriteLine(
                "[PactlDefaultDeviceWatcher] pactl not available; default-device watcher disabled "
                + "(falling back to lazy re-resolve at next recording start)."
            );
            return;
        }

        lock (_gate)
        {
            // Re-check under the lock: a concurrent Dispose() (which sets _disposed then
            // calls Stop()) may have run between the early-out above and acquiring _gate.
            // Without this, Start() could launch a 'pactl subscribe' child AFTER Stop()
            // already found no process to kill, leaking that process for the app's life.
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            if (_subscription is not null)
            {
                // Already running — idempotent.
                return;
            }

            _dispatcher = new DefaultDeviceChangeDispatcher(onDefaultDeviceChanged, _debounce);

            PactlSubscription subscription;
            try
            {
                subscription = _subscriptionFactory();
            }
            catch (Exception ex)
            {
                // Launch failure is non-fatal: log, drop the dispatcher, stay stopped.
                Trace.WriteLine(
                    $"[PactlDefaultDeviceWatcher] Failed to start 'pactl subscribe': {ex.Message}"
                );
                _dispatcher.Dispose();
                _dispatcher = null;
                return;
            }

            var runToken = new object();
            var dispatcher = _dispatcher;
            var cts = new CancellationTokenSource();
            var token = cts.Token;
            _subscription = subscription;
            _runToken = runToken;
            _cts = cts;
            // Capture locals (not fields) into the loop so a concurrent Stop() nulling the
            // fields can't turn a field read on the task thread into a NullReferenceException.
            // ReSharper disable once MethodSupportsCancellation
            // Deliberately do NOT pass `token` to Task.Run: if it were already cancelled the
            // delegate would never run, so ReadLoopAsync's finally (ClearRunState) would not
            // execute and _subscription would stay set. The token is honored INSIDE the loop
            // instead, which still lets teardown run.
            _readerTask = Task.Run(
                () => ReadLoopAsync(subscription, dispatcher, runToken, token)
            );
        }
    }

    private static PactlSubscription LaunchPactlSubscribe()
    {
        var psi = new ProcessStartInfo("pactl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("subscribe");
        // Force a stable, parseable locale for the event lines.
        psi.Environment["LC_ALL"] = "C";

        var process = Process.Start(psi)
                      ?? throw new InvalidOperationException(
                          "Process.Start returned null for pactl."
                      );

        return PactlSubscription.FromProcess(process);
    }

    // Thin, untested shell: read subscribe stdout line by line and forward relevant
    // lines to the (tested) dispatcher. Any failure just ends the loop — the watcher
    // then clears its own state (see ClearRunState) so a later Start() can restart it.
    private async Task ReadLoopAsync(
        PactlSubscription subscription,
        DefaultDeviceChangeDispatcher dispatcher,
        object runToken,
        CancellationToken ct
    )
    {
        try
        {
            var reader = subscription.Output;
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    // EOF: pactl exited (server restart, session teardown). Stop quietly.
                    break;
                }

                if (IsDefaultDeviceRelevant(line))
                {
                    dispatcher.Signal();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal Stop()/Dispose() path.
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[PactlDefaultDeviceWatcher] subscribe read loop ended: {ex.Message}"
            );
        }
        finally
        {
            // Self-teardown: the subscription ended (EOF, read error, or cancellation).
            // Clear the watcher's run state so a subsequent Start() is not rejected as
            // "already running" and can spawn a fresh subscription. Identity-guarded so a
            // concurrent Stop()/Start() that already replaced this run is never clobbered.
            ClearRunState(runToken, subscription, dispatcher);
        }
    }

    // Clear the state for a specific run (identified by runToken) and release its
    // subscription + dispatcher. Called from the read loop's finally block when the
    // subscription ends. A no-op if runToken is no longer the current run — i.e.
    // Stop()/Dispose() or a newer Start() already took over — so it never disposes a
    // subscription (or dispatcher) that a newer run owns, and never double-disposes one
    // Stop() is already tearing down.
    private void ClearRunState(
        object runToken,
        PactlSubscription subscription,
        DefaultDeviceChangeDispatcher? dispatcher
    )
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_runToken, runToken))
            {
                // A newer run (or an explicit Stop) already owns/cleared the state.
                return;
            }

            _subscription = null;
            _runToken = null;
            _cts = null;
            _readerTask = null;
            _dispatcher = null;
        }

        // Dispose outside the lock (best effort): killing the process / disposing the
        // reader must not run under _gate.
        try
        {
            subscription.Dispose();
        }
        catch
        {
            /* best effort: process may already be gone */
        }

        dispatcher?.Dispose();
    }

    public void Stop()
    {
        PactlSubscription? subscription;
        CancellationTokenSource? cts;
        Task? readerTask;
        DefaultDeviceChangeDispatcher? dispatcher;

        lock (_gate)
        {
            subscription = _subscription;
            cts = _cts;
            readerTask = _readerTask;
            dispatcher = _dispatcher;
            // Retire the current run token so the read loop's self-teardown
            // (ClearRunState) recognizes this run as no longer current and does not
            // double-dispose the subscription we are tearing down here.
            _subscription = null;
            _runToken = null;
            _cts = null;
            _readerTask = null;
            _dispatcher = null;
        }

        if (subscription is null)
        {
            return;
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
            /* best effort */
        }

        try
        {
            subscription.Kill();
        }
        catch
        {
            /* best effort: process may already be gone */
        }

        // Give the reader loop a brief moment to unwind; never block shutdown on it.
        try
        {
            readerTask?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            /* best effort */
        }

        try
        {
            subscription.Dispose();
        }
        catch
        {
            /* best effort */
        }

        cts?.Dispose();
        dispatcher?.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        Stop();
    }
}

/// <summary>
///     A single live <c>pactl subscribe</c> session for
///     <see cref="PactlDefaultDeviceWatcher" />: the event stream to read from
///     (<see cref="Output" />), a best-effort terminate (<see cref="Kill" />), and
///     cleanup (<see cref="Dispose" />). Abstracted from <see cref="Process" /> so the
///     watcher's read-loop lifecycle (including restart after the loop exits) can be
///     unit-tested with a fake in-memory reader and no spawned process.
/// </summary>
internal sealed class PactlSubscription : IDisposable
{
    private readonly Action _kill;
    private readonly Action _dispose;

    private PactlSubscription(TextReader output, Action kill, Action dispose)
    {
        Output = output;
        _kill = kill;
        _dispose = dispose;
    }

    /// <summary>Line-oriented event stream (pactl stdout, or a fake in tests).</summary>
    public TextReader Output { get; }

    public static PactlSubscription FromProcess(Process process) =>
        new(
            process.StandardOutput,
            kill: () =>
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            },
            dispose: process.Dispose
        );

    // Test seam: a fake subscription backed by an arbitrary reader. onKill/onDispose
    // let a test observe teardown; both default to no-ops.
    internal static PactlSubscription ForTest(
        TextReader output,
        Action? onKill = null,
        Action? onDispose = null
    ) => new(output, onKill ?? (() => { }), onDispose ?? (() => { }));

    /// <summary>Best-effort terminate of the underlying event source.</summary>
    public void Kill() => _kill();

    public void Dispose() => _dispose();
}
