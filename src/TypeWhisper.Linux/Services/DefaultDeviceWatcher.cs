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
    // after the burst settles" contract. Fire() only proceeds when the generation it
    // observes still matches the latest armed generation.
    private long _armedGeneration;
    private long _firedGeneration;

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
            _armedGeneration++;

            // (Re)arm a one-shot timer. Each new signal pushes the deadline out so a
            // continuous burst fires exactly once after it settles.
            if (_timer is null)
            {
                _timer = _timeProvider.CreateTimer(
                    _ => Fire(),
                    null,
                    _debounce,
                    Timeout.InfiniteTimeSpan
                );
            }
            else
            {
                _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Fire()
    {
        Action? callback;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            // Stale-callback guard: a Signal() that extended the deadline after this
            // callback was already queued bumped _armedGeneration past what we've fired.
            // Only the callback for the latest armed generation should run, and only once.
            if (_armedGeneration == _firedGeneration)
            {
                return;
            }

            _firedGeneration = _armedGeneration;
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
    private readonly TimeSpan _debounce;
    private readonly Lock _gate = new();

    private DefaultDeviceChangeDispatcher? _dispatcher;
    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _readerTask;
    private int _disposed;

    public PactlDefaultDeviceWatcher(SystemCommandAvailabilityService commands)
        : this(() => commands.HasPactl)
    {
    }

    // Availability-probe seam: tests inject a probe that reports pactl absent so the
    // graceful-fallback path (Start does nothing, never throws) is verifiable without
    // a real pactl on PATH.
    internal PactlDefaultDeviceWatcher(Func<bool> isPactlAvailable)
    {
        _isPactlAvailable = isPactlAvailable;
        _debounce = TimeSpan.FromMilliseconds(350);
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

            if (_process is not null)
            {
                // Already running — idempotent.
                return;
            }

            _dispatcher = new DefaultDeviceChangeDispatcher(onDefaultDeviceChanged, _debounce);

            Process? process;
            try
            {
                process = StartSubscribeProcess();
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

            _process = process;
            _cts = new CancellationTokenSource();
            _readerTask = Task.Run(() => ReadLoopAsync(process, _dispatcher, _cts.Token));
        }
    }

    private static Process StartSubscribeProcess()
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

        return Process.Start(psi)
               ?? throw new InvalidOperationException("Process.Start returned null for pactl.");
    }

    // Thin, untested shell: read subscribe stdout line by line and forward relevant
    // lines to the (tested) dispatcher. Any failure just ends the loop — the watcher
    // then degrades to lazy re-resolve until the next Start().
    private async Task ReadLoopAsync(
        Process process,
        DefaultDeviceChangeDispatcher dispatcher,
        CancellationToken ct
    )
    {
        try
        {
            var reader = process.StandardOutput;
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
    }

    public void Stop()
    {
        Process? process;
        CancellationTokenSource? cts;
        Task? readerTask;
        DefaultDeviceChangeDispatcher? dispatcher;

        lock (_gate)
        {
            process = _process;
            cts = _cts;
            readerTask = _readerTask;
            dispatcher = _dispatcher;
            _process = null;
            _cts = null;
            _readerTask = null;
            _dispatcher = null;
        }

        if (process is null)
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
            if (!process.HasExited)
            {
                process.Kill(true);
            }
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
            process.Dispose();
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
