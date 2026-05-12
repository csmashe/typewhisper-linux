using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services;

namespace TypeWhisper.Linux.Services.Ipc;

/// <summary>
/// Server side of the control-socket IPC. Listens on a Unix domain socket
/// inside <c>$XDG_RUNTIME_DIR/typewhisper/</c> (or a 0700 fallback) and
/// accepts either the Phase 5 JSON-line protocol or the Phase 4 plain-text
/// <c>toggle</c> line for upgrade-window backwards compatibility. The
/// JSON protocol is documented in <see cref="JsonControlProtocol"/>.
/// </summary>
/// <remarks>
/// The socket bind itself doubles as the app's single-instance guard. A
/// second <c>typewhisper</c> launch attempts to send <c>toggle</c> via
/// <see cref="ControlSocketClient"/>; if that succeeds it exits, and if the
/// existing socket is stale it gets cleaned up before bind. A second GUI
/// launch with args still falls into the bind path and fails with
/// <see cref="SocketError.AddressAlreadyInUse"/>, which the caller handles
/// by exiting with a "TypeWhisper is already running" message.
/// </remarks>
internal sealed class ControlSocketServer : IDisposable
{
    // Hyprland's `bindr` for a quick tap can deliver the release before the
    // press's exec finishes spawning, so a `record.stop` lands while the
    // matching `record.start` is still awaiting its toggle gate. Window for
    // treating that arrival pattern as a tap: stop the start as soon as it
    // settles. 100 ms covers normal tap latencies (~30-70 ms key down→up)
    // with margin without smearing into intentional very-short PTT holds.
    private static readonly TimeSpan StartStopRaceWindow = TimeSpan.FromMilliseconds(100);

    private readonly DictationOrchestrator _orchestrator;
    private readonly HotkeyService? _hotkey;
    private readonly ISettingsService? _settings;
    private readonly string _path;
    private Socket? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _disposed;
    // Last time a record.start request was accepted by the server. Used by
    // the start-then-immediate-stop race guard: a record.stop arriving
    // within StartStopRaceWindow of an in-flight start treats the start as
    // a tap and forces a stop once the start awaits complete. Stored as
    // UTC ticks so reads are atomic without a lock.
    private long _lastStartTicks;
    private Task? _lastStartTask;
    // True between successful bind and listener close — used by Dispose to
    // distinguish "we created and own this socket path" from "we never
    // succeeded in binding". The path-ownership check on dispose still
    // probes the live socket to guard against a successor stealing the
    // path during shutdown.
    private bool _bound;

    public ControlSocketServer(DictationOrchestrator orchestrator)
        : this(orchestrator, hotkey: null, settings: null)
    {
    }

    public ControlSocketServer(DictationOrchestrator orchestrator, HotkeyService? hotkey, ISettingsService? settings)
    {
        _orchestrator = orchestrator;
        _hotkey = hotkey;
        _settings = settings;
        _path = SocketPathResolver.ResolveControlSocketPath();
    }

    /// <summary>Absolute path of the socket file once <see cref="Start"/> succeeds.</summary>
    public string SocketPath => _path;

    /// <summary>
    /// Binds the socket and starts the accept loop. Throws
    /// <see cref="SocketException"/> with
    /// <see cref="SocketError.AddressAlreadyInUse"/> when another live
    /// instance owns the path; callers should treat that as the
    /// single-instance signal and exit.
    /// </summary>
    public void Start()
    {
        if (_disposed != 0)
            throw new ObjectDisposedException(nameof(ControlSocketServer));
        if (_listener is not null)
            return;

        TryRemoveStaleSocket(_path);

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(_path));
        }
        catch
        {
            listener.Dispose();
            throw;
        }

        // 0600: owner-only read/write. Defense in depth on shared /tmp; on
        // XDG_RUNTIME_DIR the parent dir is already 0700.
        SocketPathResolver.TryChmod(_path, 0b110_000_000); // 0600
        _bound = true;
        listener.Listen(8);

        _listener = listener;
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));

        Trace.WriteLine($"[ControlSocketServer] Listening on {_path}");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener!;
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
                try { await Task.Delay(100, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken ct)
    {
        try
        {
            using var stream = new NetworkStream(client, ownsSocket: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            // Read the first request line byte-by-byte rather than via
            // StreamReader so we can enforce the 4 KB cap before allocating
            // an unbounded buffer for a hostile or runaway client. The cap
            // is shared with the client side via JsonControlProtocol.
            var line = await ReadCappedLineAsync(stream, JsonControlProtocol.MaxLineBytes, ct).ConfigureAwait(false);
            if (line is null)
                return; // peer closed

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
                await writer.WriteLineAsync(
                    JsonControlProtocol.SerializeError(JsonControlProtocol.ErrLineTooLong)).ConfigureAwait(false);
                return;
            }

            var trimmed = line.Trim();

            // Protocol detection by leading byte: '{' means JSON, anything
            // else is treated as the Phase 4 plain-text protocol. The legacy
            // path stays for upgrade-window compatibility — a Phase 4 binary
            // in $PATH must still be able to toggle a running Phase 5 app.
            if (trimmed.StartsWith('{'))
            {
                await HandleJsonRequestAsync(trimmed, writer, ct).ConfigureAwait(false);
            }
            else if (trimmed.Equals("toggle", StringComparison.Ordinal))
            {
                // Fire-and-forget the toggle so the legacy bare-toggle client
                // doesn't sit through the full transcription pipeline if the
                // toggle happens to be the recording->idle direction. Phase 4
                // clients use a 2 s receive timeout that StopAsync can blow
                // through on any normal-length recording.
                DispatchOrchestratorAsync(_orchestrator.ToggleAsync, "Legacy ToggleAsync");
                await writer.WriteLineAsync("ok").ConfigureAwait(false);
            }
            else
            {
                await writer.WriteLineAsync("err unknown-command").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ControlSocketServer] Client handler failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sentinel value returned by <see cref="ReadCappedLineAsync"/> when the
    /// client overruns the protocol's max line size. Distinct from a plain
    /// long string so callers can branch without re-measuring.
    /// </summary>
    private const string LineTooLongSentinel = "LINE_TOO_LONG";

    /// <summary>
    /// Reads up to <paramref name="maxBytes"/> from <paramref name="stream"/>
    /// or until a newline arrives. Returns the line without the trailing
    /// newline. Returns <see cref="LineTooLongSentinel"/> when the cap is
    /// exceeded; returns <c>null</c> on a clean peer close before any data.
    /// </summary>
    private static async Task<string?> ReadCappedLineAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var buf = new byte[maxBytes];
        var total = 0;
        while (total < maxBytes)
        {
            var n = await stream.ReadAsync(buf.AsMemory(total, maxBytes - total), ct).ConfigureAwait(false);
            if (n <= 0)
            {
                if (total == 0) return null;
                return Encoding.UTF8.GetString(buf, 0, total);
            }
            var nl = Array.IndexOf(buf, (byte)'\n', total, n);
            total += n;
            if (nl >= 0)
                return Encoding.UTF8.GetString(buf, 0, nl);
        }
        // Cap reached without seeing a newline.
        return LineTooLongSentinel;
    }

    private async Task HandleJsonRequestAsync(string line, StreamWriter writer, CancellationToken ct)
    {
        JsonControlProtocol.Request? req;
        try
        {
            req = JsonSerializer.Deserialize<JsonControlProtocol.Request>(line, JsonControlProtocol.JsonOptions);
        }
        catch (JsonException)
        {
            await writer.WriteLineAsync(
                JsonControlProtocol.SerializeError(JsonControlProtocol.ErrMalformed)).ConfigureAwait(false);
            return;
        }

        if (req is null || string.IsNullOrEmpty(req.Command))
        {
            await writer.WriteLineAsync(
                JsonControlProtocol.SerializeError(JsonControlProtocol.ErrMalformed)).ConfigureAwait(false);
            return;
        }

        if (req.Version != JsonControlProtocol.CurrentVersion)
        {
            await writer.WriteLineAsync(
                JsonControlProtocol.SerializeError(JsonControlProtocol.ErrUnsupportedVersion)).ConfigureAwait(false);
            return;
        }

        try
        {
            string response = req.Command switch
            {
                JsonControlProtocol.CmdRecordStart  => await HandleStartAsync(ct).ConfigureAwait(false),
                JsonControlProtocol.CmdRecordStop   => await HandleStopAsync(ct).ConfigureAwait(false),
                JsonControlProtocol.CmdRecordToggle => await HandleToggleAsync().ConfigureAwait(false),
                JsonControlProtocol.CmdRecordCancel => await HandleCancelAsync().ConfigureAwait(false),
                JsonControlProtocol.CmdStatus       => HandleStatus(),
                _                                   => JsonControlProtocol.SerializeError(JsonControlProtocol.ErrUnknownCommand),
            };

            await writer.WriteLineAsync(response).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ControlSocketServer] Command '{req.Command}' threw: {ex.Message}");
            await writer.WriteLineAsync(
                JsonControlProtocol.SerializeError(JsonControlProtocol.ErrInternal)).ConfigureAwait(false);
        }
    }

    private async Task<string> HandleStartAsync(CancellationToken ct)
    {
        var prev = SnapshotState();

        // Publish the start marker BEFORE invoking the orchestrator. Async
        // methods run synchronously until their first incomplete await, and
        // StartAsync can hold _toggleGate + flip _audio.IsRecording before
        // ever yielding. A near-simultaneous record.stop arriving in that
        // window would otherwise observe no in-flight start and either
        // race ahead of the start (failing _toggleGate.WaitAsync(0) and
        // no-op'ing) or find IsRecording still false. The TCS is completed
        // when the orchestrator's StartAsync returns, so HandleStopAsync
        // can await it deterministically.
        var startCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _lastStartTicks, DateTime.UtcNow.Ticks);
        _lastStartTask = startCompletion.Task;

        try
        {
            await _orchestrator.StartAsync().ConfigureAwait(false);
            startCompletion.TrySetResult();
        }
        catch (Exception ex)
        {
            startCompletion.TrySetException(ex);
            throw;
        }

        return JsonControlProtocol.SerializeAction(prev, SnapshotState());
    }

    private async Task<string> HandleStopAsync(CancellationToken ct)
    {
        var prev = SnapshotState();

        // Hyprland `bindr` race guard: a record.stop arriving within
        // StartStopRaceWindow of a record.start is almost certainly a tap.
        // Await the in-flight start (signalled via the TCS that
        // HandleStartAsync publishes before invoking the orchestrator) so
        // StopAsync below sees IsRecording==true. Without this await, a
        // tap-stop can land while the start handler is still synchronously
        // entering StartAsync, _toggleGate.WaitAsync(0) fails for us, and
        // the user is left with a stuck recording when the start completes
        // with no matching stop.
        var startTicks = Interlocked.Read(ref _lastStartTicks);
        var elapsed = DateTime.UtcNow - new DateTime(startTicks, DateTimeKind.Utc);
        if (elapsed < StartStopRaceWindow)
        {
            var pendingStart = _lastStartTask;
            if (pendingStart is not null && !pendingStart.IsCompleted)
            {
                try { await pendingStart.ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[ControlSocketServer] Awaiting in-flight start during tap-stop failed: {ex.Message}");
                }
            }
        }

        // Fire-and-forget the orchestrator stop. StopAsync runs the full
        // transcription + post-processing + insertion pipeline before
        // returning, which can take many seconds. The control socket
        // client has a 2-second receive timeout and would otherwise
        // misreport a successful stop as a transport failure (exit 2).
        // The transition the caller cares about — audio capture has been
        // told to stop — has been initiated by the time _orchestrator
        // begins its work; we acknowledge that synchronously and let the
        // transcription pipeline run in the background.
        DispatchOrchestratorAsync(_orchestrator.StopAsync, "StopAsync");
        return JsonControlProtocol.SerializeAction(prev, JsonControlProtocol.StateIdle);
    }

    private Task<string> HandleToggleAsync()
    {
        var prev = SnapshotState();
        // Toggle has the same pipeline-blocking problem as stop when going
        // from recording → idle (it routes through StopAsync). Fire-and-
        // forget so the client gets a snappy ack regardless of which way
        // the toggle goes.
        DispatchOrchestratorAsync(_orchestrator.ToggleAsync, "ToggleAsync");
        // The wire response reflects the intent: if we were recording,
        // we're about to stop (-> idle); if idle, about to start
        // (-> recording). The orchestrator's own idempotency keeps state
        // consistent even if a concurrent client read happens in flight.
        var next = prev == JsonControlProtocol.StateRecording
            ? JsonControlProtocol.StateIdle
            : JsonControlProtocol.StateRecording;
        return Task.FromResult(JsonControlProtocol.SerializeAction(prev, next));
    }

    private Task<string> HandleCancelAsync()
    {
        var prev = SnapshotState();
        DispatchOrchestratorAsync(_orchestrator.CancelAsync, "CancelAsync");
        return Task.FromResult(JsonControlProtocol.SerializeAction(prev, JsonControlProtocol.StateIdle));
    }

    /// <summary>
    /// Runs an orchestrator verb on the thread pool with structured logging
    /// of synchronous and asynchronous faults. Used by stop/toggle/cancel
    /// to keep the control-socket response under the client's 2 s receive
    /// timeout while the full transcription pipeline runs in the background.
    /// </summary>
    private static void DispatchOrchestratorAsync(Func<Task> start, string label)
    {
        Task task;
        try
        {
            task = start();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ControlSocketServer] {label} threw synchronously: {ex.Message}");
            return;
        }
        task.ContinueWith(
            t => Trace.WriteLine($"[ControlSocketServer] {label} faulted: {t.Exception?.GetBaseException().Message}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
    /// Maps the orchestrator's observable state to the wire string. Sources
    /// the value from <see cref="DictationOrchestrator.CurrentStateLabel"/>,
    /// which projects the audio capture flag plus the live overlay status
    /// text into the spec's idle / recording / transcribing / injecting
    /// vocabulary.
    /// </summary>
    private string SnapshotState() => _orchestrator.CurrentStateLabel;

    /// <summary>
    /// If the socket path exists but no live peer is listening, deletes it.
    /// Never deletes a path that has a live peer — that would silently
    /// detach another running instance.
    /// </summary>
    private static void TryRemoveStaleSocket(string path)
    {
        if (!File.Exists(path))
            return;

        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(path));
            // A live peer accepted us; do NOT delete.
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            try
            {
                File.Delete(path);
                Trace.WriteLine($"[ControlSocketServer] Removed stale socket at {path}.");
            }
            catch (Exception delEx)
            {
                Trace.WriteLine($"[ControlSocketServer] Failed to remove stale socket {path}: {delEx.Message}");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ControlSocketServer] Probe of {path} threw: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        try { _cts?.Cancel(); } catch { /* ignored */ }

        var listener = _listener;
        _listener = null;
        try { listener?.Close(); } catch { /* ignored */ }
        try { listener?.Dispose(); } catch { /* ignored */ }

        try
        {
            _acceptLoop?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ControlSocketServer] Accept loop wait threw: {ex.Message}");
        }

        try { _cts?.Dispose(); } catch { /* ignored */ }

        // Only unlink the path if we successfully bound it AND nobody else
        // is currently listening on it. A successor instance can clean up
        // what it thinks is our stale socket and bind a fresh one before
        // our Dispose reaches this point; deleting that fresh socket would
        // silently break their IPC until the next restart.
        try
        {
            if (_bound && File.Exists(_path))
            {
                if (NoLivePeer(_path))
                    File.Delete(_path);
                else
                    Trace.WriteLine($"[ControlSocketServer] Socket path {_path} is held by another listener; leaving it in place.");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ControlSocketServer] Could not remove socket file on dispose: {ex.Message}");
        }
    }

    /// <summary>
    /// Probes the path with a connect. ECONNREFUSED means no live listener
    /// (safe to unlink). A successful connect means another instance owns
    /// the path now and we must not touch it.
    /// </summary>
    private static bool NoLivePeer(string path)
    {
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(path));
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return true;
        }
        catch
        {
            // Any other error (e.g. permission denied) — refuse to delete
            // out of paranoia; leaking a socket file is fine, deleting
            // someone else's is not.
            return false;
        }
    }
}
