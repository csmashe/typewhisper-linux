using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Linux.Services.Ipc;

/// <summary>
///     Server side of the control-socket IPC. Listens on a Unix domain socket
///     inside <c>$XDG_RUNTIME_DIR/typewhisper/</c> (or a 0700 fallback) and
///     accepts either the Phase 5 JSON-line protocol or the Phase 4 plain-text
///     <c>toggle</c> line for upgrade-window backwards compatibility. The
///     JSON protocol is documented in <see cref="JsonControlProtocol" />.
/// </summary>
/// <remarks>
///     The socket bind itself doubles as the app's single-instance guard. A
///     second <c>typewhisper</c> launch attempts to send <c>toggle</c> via
///     <see cref="ControlSocketClient" />; if that succeeds it exits, and if the
///     existing socket is stale it gets cleaned up before bind. A second GUI
///     launch with args still falls into the bind path and fails with
///     <see cref="SocketError.AddressAlreadyInUse" />, which the caller handles
///     by exiting with a "TypeWhisper is already running" message.
/// </remarks>
internal sealed class ControlSocketServer : IDisposable
{
    /// <summary>
    ///     Sentinel value returned by <see cref="ReadCappedLineAsync" /> when the
    ///     client overruns the protocol's max line size. Distinct from a plain
    ///     long string so callers can branch without re-measuring.
    /// </summary>
    private const string LineTooLongSentinel = "LINE_TOO_LONG";

    // Hyprland's `bindr` for a quick tap can deliver the release before the
    // press's exec finishes spawning, so a `record.stop` lands while the
    // matching `record.start` is still awaiting its toggle gate. Window for
    // treating that arrival pattern as a tap: stop the start as soon as it
    // settles. 100 ms covers normal tap latencies (~30-70 ms key down→up)
    // with margin without smearing into intentional very-short PTT holds.
    private static readonly TimeSpan s_startStopRaceWindow = TimeSpan.FromMilliseconds(100);
    private readonly HotkeyService? _hotkey;

    private readonly DictationOrchestrator _orchestrator;
    private readonly ISettingsService? _settings;
    private readonly ControlSocketStartCoordinator _startCoordinator;
    private readonly Lock _lifecycleGate = new();
    private Task? _acceptLoop;

    // True only after the complete bind/listen startup has been published.
    private bool _bound;
    private CancellationTokenSource? _cts;
    private int _disposed;
    private Socket? _listener;
    private ControlSocketOwnership? _ownership;

    // ReSharper disable once IntroduceOptionalParameters.Global -- kept as explicit overloads; collapsing into optional parameters would delete a member.
    public ControlSocketServer(DictationOrchestrator orchestrator)
        : this(orchestrator, null, null)
    {
    }

    public ControlSocketServer(
        DictationOrchestrator orchestrator,
        HotkeyService? hotkey,
        ISettingsService? settings
    )
    {
        _orchestrator = orchestrator;
        _hotkey = hotkey;
        _settings = settings;
        _startCoordinator = new ControlSocketStartCoordinator(
            () => _orchestrator.CurrentStateLabel,
            ex =>
                Trace.WriteLine(
                    $"[ControlSocketServer] StartAsync faulted: {ex.GetBaseException().Message}"
                )
        );
        SocketPath = SocketPathResolver.ResolveControlSocketPath();
    }

    /// <summary>Absolute path of the socket file once <see cref="Start" /> succeeds.</summary>
    private string SocketPath { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        lock (_lifecycleGate)
        {
            var listener = _listener;
            var cts = _cts;
            var acceptLoop = _acceptLoop;
            var ownership = _ownership;

            _listener = null;
            _cts = null;
            _acceptLoop = null;
            _ownership = null;

            // Order: cancel → close → await loop → unlink → release, all under _lifecycleGate.
            // Reversing close/wait risks an indefinite accept block; unlinking before the loop
            // drains risks deleting the file while a handler still holds it.
            try
            {
                cts?.Cancel();
            }
            catch
            {
                /* ignored */
            }

            try
            {
                listener?.Close();
            }
            catch
            {
                /* ignored */
            }

            try
            {
                listener?.Dispose();
            }
            catch
            {
                /* ignored */
            }

            try
            {
                acceptLoop?.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ControlSocketServer] Accept loop wait threw: {ex.Message}");
            }

            try
            {
                cts?.Dispose();
            }
            catch
            {
                /* ignored */
            }

            try
            {
                // ReSharper disable once InvertIf -- inverting would early-return out of a multi-stage cleanup and skip the stages below.
                if (_bound && ownership is not null)
                {
                    var cleanup = ownership.CleanupStaleSocket();
                    if (cleanup is ControlSocketCleanupResult.Live)
                    {
                        Trace.WriteLine(
                            $"[ControlSocketServer] Socket path {SocketPath} is held by another listener; leaving it in place."
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"[ControlSocketServer] Could not remove socket file on dispose: {ex.Message}"
                );
            }
            finally
            {
                _bound = false;
                try
                {
                    ownership?.Dispose();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(
                        $"[ControlSocketServer] Could not release socket ownership: {ex.Message}"
                    );
                }
            }
        }
    }

    /// <summary>
    ///     Binds the socket and starts the accept loop. Throws
    ///     <see cref="SocketException" /> with
    ///     <see cref="SocketError.AddressAlreadyInUse" /> when another live
    ///     instance owns the path, and — failing closed — whenever ownership
    ///     cannot be established (lock contention or an indeterminate probe);
    ///     callers should treat that as the single-instance signal and exit.
    /// </summary>
    public void Start()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);

            if (_listener is not null)
            {
                return;
            }

            ControlSocketOwnership ownership;
            try
            {
                if (!ControlSocketOwnership.TryAcquire(SocketPath, out var acquiredOwnership))
                {
                    throw AddressAlreadyInUse();
                }

                ownership = acquiredOwnership;
            }
            catch (SocketException ex)
                when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Lock uncertainty must fail closed; App already treats this socket error as
                // the authoritative single-instance signal.
                Trace.WriteLine(
                    $"[ControlSocketServer] Could not acquire socket ownership: {ex.Message}"
                );
                throw AddressAlreadyInUse();
            }

            Socket? listener = null;
            CancellationTokenSource? cts = null;
            Task? acceptLoop = null;
            var boundThisAttempt = false;
            try
            {
                var cleanup = ownership.CleanupStaleSocket();
                if (
                    cleanup
                    is not (
                        ControlSocketCleanupResult.Missing
                        or ControlSocketCleanupResult.Removed
                    )
                )
                {
                    throw AddressAlreadyInUse();
                }

                listener = new Socket(
                    AddressFamily.Unix,
                    SocketType.Stream,
                    ProtocolType.Unspecified
                );
                listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
                boundThisAttempt = true;

                // 0600: owner-only read/write. Defense in depth on shared /tmp; on
                // XDG_RUNTIME_DIR the parent dir is already 0700.
                SocketPathResolver.TryChmod(SocketPath, 0b110_000_000); // 0600
                listener.Listen(8);

                cts = new CancellationTokenSource();
                var token = cts.Token;
                acceptLoop = Task.Run(() => AcceptLoopAsync(listener, token), token);

                // Publish only after bind, chmod, listen, and accept-loop creation succeed.
                _ownership = ownership;
                _listener = listener;
                _cts = cts;
                _acceptLoop = acceptLoop;
                _bound = true;

                Trace.WriteLine($"[ControlSocketServer] Listening on {SocketPath}");
            }
            catch
            {
                CleanupFailedStart(
                    ownership,
                    listener,
                    cts,
                    acceptLoop,
                    boundThisAttempt
                );
                throw;
            }
        }
    }

    private static SocketException AddressAlreadyInUse()
    {
        return new SocketException((int)SocketError.AddressAlreadyInUse);
    }

    private void CleanupFailedStart(
        ControlSocketOwnership ownership,
        Socket? listener,
        CancellationTokenSource? cts,
        Task? acceptLoop,
        bool boundThisAttempt
    )
    {
        _listener = null;
        _cts = null;
        _acceptLoop = null;
        _ownership = null;
        _bound = false;

        try
        {
            cts?.Cancel();
        }
        catch
        {
            /* ignored */
        }

        try
        {
            listener?.Close();
        }
        catch
        {
            /* ignored */
        }

        try
        {
            listener?.Dispose();
        }
        catch
        {
            /* ignored */
        }

        try
        {
            acceptLoop?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[ControlSocketServer] Failed-start accept loop wait threw: {ex.Message}"
            );
        }

        try
        {
            cts?.Dispose();
        }
        catch
        {
            /* ignored */
        }

        if (boundThisAttempt)
        {
            try
            {
                ownership.CleanupStaleSocket();
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"[ControlSocketServer] Failed-start socket cleanup threw: {ex.Message}"
                );
            }
        }

        try
        {
            ownership.Dispose();
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[ControlSocketServer] Failed-start ownership release threw: {ex.Message}"
            );
        }
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[ControlSocketServer] Accept failed: {ex.Message}");
                // Brief back-off so a persistent error doesn't pin a core.
                try
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken ct)
    {
        try
        {
            await using var stream = new NetworkStream(client, true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.AutoFlush = true;
            writer.NewLine = "\n";

            // Byte-by-byte read enforces the 4 KB cap before allocating; StreamReader
            // would buffer unboundedly for a runaway client.
            var line = await ReadCappedLineAsync(stream, JsonControlProtocol.MaxLineBytes, ct)
                .ConfigureAwait(false);
            if (line is null)
            {
                return; // peer closed
            }

            if (line.Length == 0)
            {
                // Empty line: unknown command. Reply in legacy format because
                // we don't know what the client speaks yet.
                await writer.WriteLineAsync("err unknown-command").ConfigureAwait(false);
                return;
            }

            if (line == LineTooLongSentinel)
            {
                // Client overran the cap. Reply in JSON because a JSON-capable
                // client is far more likely to send oversized input than a
                // legacy text client (the legacy verb is six bytes).
                await writer
                    .WriteLineAsync(
                        JsonControlProtocol.SerializeError(JsonControlProtocol.ErrLineTooLong)
                    )
                    .ConfigureAwait(false);
                return;
            }

            var trimmed = line.Trim();

            // '{' → JSON (Phase 5); anything else → Phase 4 plain-text for upgrade-window compat.
            if (trimmed.StartsWith('{'))
            {
                await HandleJsonRequestAsync(trimmed, writer, ct).ConfigureAwait(false);
            }
            else if (trimmed.Equals("toggle", StringComparison.Ordinal))
            {
                // Fire-and-forget: Phase 4 clients use a 2 s timeout that StopAsync can blow past.
                DispatchOrchestratorAsync(() => _orchestrator.ToggleAsync(), "Legacy ToggleAsync");
                await writer.WriteLineAsync("ok").ConfigureAwait(false);
            }
            else
            {
                await writer.WriteLineAsync("err unknown-command").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            /* shutdown */
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ControlSocketServer] Client handler failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Reads up to <paramref name="maxBytes" /> from <paramref name="stream" />
    ///     or until a newline arrives. Returns the line without the trailing
    ///     newline. Returns <see cref="LineTooLongSentinel" /> when the cap is
    ///     exceeded; returns <c>null</c> on a clean peer close before any data.
    /// </summary>
    private static async Task<string?> ReadCappedLineAsync(
        Stream stream,
        int maxBytes,
        CancellationToken ct
    )
    {
        var buf = new byte[maxBytes];
        var total = 0;
        while (total < maxBytes)
        {
            var n = await stream
                .ReadAsync(buf.AsMemory(total, maxBytes - total), ct)
                .ConfigureAwait(false);
            if (n <= 0)
            {
                return total == 0 ? null : Encoding.UTF8.GetString(buf, 0, total);
            }

            var nl = Array.IndexOf(buf, (byte)'\n', total, n);
            total += n;
            if (nl >= 0)
            {
                return Encoding.UTF8.GetString(buf, 0, nl);
            }
        }

        return LineTooLongSentinel;
    }

    // ReSharper disable once UnusedParameter.Local -- ct is threaded from the cancellation-aware client loop for signature consistency; the inner JSON dispatch has no cancellable I/O to forward it to
    private async Task HandleJsonRequestAsync(
        string line,
        StreamWriter writer,
        CancellationToken ct
    )
    {
        JsonControlProtocol.Request? req;
        try
        {
            req = JsonSerializer.Deserialize<JsonControlProtocol.Request>(
                line,
                JsonControlProtocol.JsonOptions
            );
        }
        catch (JsonException)
        {
            await writer
                .WriteLineAsync(
                    JsonControlProtocol.SerializeError(JsonControlProtocol.ErrMalformed)
                )
                .ConfigureAwait(false);
            return;
        }

        if (req is null || string.IsNullOrEmpty(req.Command))
        {
            await writer
                .WriteLineAsync(
                    JsonControlProtocol.SerializeError(JsonControlProtocol.ErrMalformed)
                )
                .ConfigureAwait(false);
            return;
        }

        if (req.Version != JsonControlProtocol.CurrentVersion)
        {
            await writer
                .WriteLineAsync(
                    JsonControlProtocol.SerializeError(JsonControlProtocol.ErrUnsupportedVersion)
                )
                .ConfigureAwait(false);
            return;
        }

        try
        {
            var response = req.Command switch
            {
                JsonControlProtocol.CmdRecordStart => await HandleStartAsync()
                    .ConfigureAwait(false),
                JsonControlProtocol.CmdRecordStop => await HandleStopAsync()
                    .ConfigureAwait(false),
                JsonControlProtocol.CmdRecordToggle => await HandleToggleAsync()
                    .ConfigureAwait(false),
                JsonControlProtocol.CmdRecordCancel => await HandleCancelAsync()
                    .ConfigureAwait(false),
                JsonControlProtocol.CmdStatus => HandleStatus(),
                _ => JsonControlProtocol.SerializeError(JsonControlProtocol.ErrUnknownCommand),
            };

            await writer.WriteLineAsync(response).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ControlSocketServer] Command '{req.Command}' threw: {ex.Message}");
            await writer
                .WriteLineAsync(JsonControlProtocol.SerializeError(JsonControlProtocol.ErrInternal))
                .ConfigureAwait(false);
        }
    }

    private Task<string> HandleStartAsync()
    {
        return _startCoordinator.DispatchStart(() => _orchestrator.StartAsync());
    }

    private async Task<string> HandleStopAsync()
    {
        var prev = SnapshotState();

        // Hyprland `bindr` tap guard: a record.stop within StartStopRaceWindow of a start is
        // treated as a tap. Await the in-flight start's TCS so StopAsync sees IsRecording==true;
        // without this, _toggleGate.WaitAsync(0) fails and the user ends up with a stuck recording.
        var (startTicks, pendingStart) = _startCoordinator.GetLastStart();
        var elapsed = DateTime.UtcNow - new DateTime(startTicks, DateTimeKind.Utc);
        if (elapsed < s_startStopRaceWindow)
        {
            if (pendingStart is not null && !pendingStart.IsCompleted)
            {
                try
                {
                    await pendingStart.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(
                        $"[ControlSocketServer] Awaiting in-flight start during tap-stop failed: {ex.Message}"
                    );
                }
            }
        }

        // Fire-and-forget: StopAsync runs the full transcription + insertion pipeline (many
        // seconds) while the client has a 2 s receive timeout. Acknowledge immediately; the
        // pipeline continues in the background.
        DispatchOrchestratorAsync(_orchestrator.StopAsync, "StopAsync");
        return JsonControlProtocol.SerializeAction(prev, JsonControlProtocol.StateIdle);
    }

    private Task<string> HandleToggleAsync()
    {
        var prev = SnapshotState();
        // Fire-and-forget — toggle can route through StopAsync (pipeline-blocking like stop).
        DispatchOrchestratorAsync(() => _orchestrator.ToggleAsync(), "ToggleAsync");
        // Wire response reflects intent rather than confirmed final state.
        var next =
            prev == JsonControlProtocol.StateRecording
                ? JsonControlProtocol.StateIdle
                : JsonControlProtocol.StateRecording;
        return Task.FromResult(JsonControlProtocol.SerializeAction(prev, next));
    }

    private Task<string> HandleCancelAsync()
    {
        var prev = SnapshotState();
        DispatchOrchestratorAsync(_orchestrator.CancelAsync, "CancelAsync");
        return Task.FromResult(
            JsonControlProtocol.SerializeAction(prev, JsonControlProtocol.StateIdle)
        );
    }

    /// <summary>
    ///     Runs an orchestrator verb on the thread pool, logging faults. Used by stop/toggle/cancel
    ///     to keep the socket response under the client's 2 s timeout while the pipeline runs in background.
    /// </summary>
    private static void DispatchOrchestratorAsync(Func<Task> start, string label)
    {
        var task = Task
            .Factory.StartNew(
                start,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default
            )
            .Unwrap();
        task.ContinueWith(
            t =>
                Trace.WriteLine(
                    $"[ControlSocketServer] {label} faulted: {t.Exception?.GetBaseException().Message}"
                ),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private string HandleStatus()
    {
        var response = new JsonControlProtocol.StatusResponse
        {
            Ok = true,
            State = SnapshotState(),
            Backend = _hotkey?.ActiveBackendId,
            SupportsPressRelease = _hotkey?.ActiveBackendSupportsPressRelease ?? false,
            ActiveBinding = _hotkey?.CurrentHotkeyString,
            Mode = _settings?.Current.Mode.ToString(),
        };
        return JsonControlProtocol.SerializeStatus(response);
    }

    /// <summary>
    ///     Projects an accepted start as <c>starting</c> until capture is observably open or
    ///     the complete start operation settles.
    /// </summary>
    private string SnapshotState()
    {
        return _startCoordinator.SnapshotState();
    }

}

/// <summary>
///     Coordinates the one accepted control-socket start phase. The published completion is
///     a tap-stop ordering signal; the separately observed orchestrator task carries failures.
/// </summary>
internal sealed class ControlSocketStartCoordinator
{
    private readonly Lock _gate = new();
    private readonly Action<Exception> _onFault;
    private readonly Func<string> _readState;
    private Task? _lastStartTask;
    private long _lastStartTicks;

    public ControlSocketStartCoordinator(Func<string> readState, Action<Exception> onFault)
    {
        _readState = readState;
        _onFault = onFault;
    }

    /// <summary>
    ///     Accepts one start at a time and returns its action response without awaiting the
    ///     complete orchestrator operation. The delegate is invoked directly so its synchronous
    ///     startup-gate prefix runs on the request handler rather than being deferred to the pool.
    /// </summary>
    public Task<string> DispatchStart(Func<Task> start)
    {
        var prev = SnapshotState();
        TaskCompletionSource? startCompletion = null;

        lock (_gate)
        {
            if (_lastStartTask is null || _lastStartTask.IsCompleted)
            {
                startCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                _lastStartTask = startCompletion.Task;
                _lastStartTicks = DateTime.UtcNow.Ticks;
            }
        }

        if (startCompletion is null)
        {
            return Task.FromResult(JsonControlProtocol.SerializeAction(prev, SnapshotState()));
        }

        Task startTask;
        try
        {
            startTask = start();
        }
        catch (Exception ex)
        {
            ReportFault(ex);
            startCompletion.TrySetResult();
            return Task.FromResult(
                JsonControlProtocol.SerializeError(JsonControlProtocol.ErrInternal)
            );
        }

        _ = ObserveStartAsync(startTask, startCompletion);

        // A failure that settled synchronously is known before the response is committed.
        return startTask is { IsCompleted: true, IsCompletedSuccessfully: false }
            ? Task.FromResult(
                JsonControlProtocol.SerializeError(JsonControlProtocol.ErrInternal)
            )
            : Task.FromResult(JsonControlProtocol.SerializeAction(prev, SnapshotState()));
    }

    /// <summary>
    ///     Returns the timestamp/task pair used by the server's 100 ms tap-stop guard under the
    ///     same synchronization boundary that publishes a new accepted start.
    /// </summary>
    public (long Ticks, Task? Completion) GetLastStart()
    {
        lock (_gate)
        {
            return (_lastStartTicks, _lastStartTask);
        }
    }

    /// <summary>Returns the real state, augmented only by the pending startup phase.</summary>
    public string SnapshotState()
    {
        var state = _readState();
        if (state == JsonControlProtocol.StateRecording)
        {
            return state;
        }

        lock (_gate)
        {
            return _lastStartTask is { IsCompleted: false }
                ? JsonControlProtocol.StateStarting
                : state;
        }
    }

    private async Task ObserveStartAsync(Task startTask, TaskCompletionSource startCompletion)
    {
        try
        {
            await startTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportFault(ex);
        }
        finally
        {
            // Tap-stop waiters need ordering completion, not the orchestrator's fault.
            startCompletion.TrySetResult();
        }
    }

    private void ReportFault(Exception ex)
    {
        try
        {
            _onFault(ex);
        }
        catch
        {
            // Diagnostics must never turn the observer into an unobserved faulted task.
        }
    }
}
