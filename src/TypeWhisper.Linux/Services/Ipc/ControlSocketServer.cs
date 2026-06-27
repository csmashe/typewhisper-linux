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
    private Task? _acceptLoop;

    // True after a successful bind — lets Dispose distinguish "we own this path" from
    // "we never bound", while the live-probe guard still covers a successor stealing the path.
    private bool _bound;
    private CancellationTokenSource? _cts;
    private int _disposed;
    private Task? _lastStartTask;

    // UTC ticks of the last accepted record.start; used by the tap race guard to decide
    // whether an arriving record.stop should await the in-flight start before calling StopAsync.
    // Stored as ticks so reads are atomic without a lock.
    private long _lastStartTicks;
    private Socket? _listener;

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

        // Order: cancel → close listener (unblocks in-flight AcceptAsync) → await loop → unlink.
        // Reversing close/wait risks an indefinite accept block; reversing wait/unlink risks
        // deleting the file while the loop still holds it.
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            /* ignored */
        }

        var listener = _listener;
        _listener = null;
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
            _acceptLoop?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ControlSocketServer] Accept loop wait threw: {ex.Message}");
        }

        try
        {
            _cts?.Dispose();
        }
        catch
        {
            /* ignored */
        }

        // Unlink only if we own the path AND no live peer is listening — a successor instance
        // may have already taken it over before our Dispose reaches this point.
        try
        {
            if (!_bound || !File.Exists(SocketPath))
            {
                return;
            }

            if (NoLivePeer(SocketPath))
            {
                File.Delete(SocketPath);
            }
            else
            {
                Trace.WriteLine(
                    $"[ControlSocketServer] Socket path {SocketPath} is held by another listener; leaving it in place."
                );
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[ControlSocketServer] Could not remove socket file on dispose: {ex.Message}"
            );
        }
    }

    /// <summary>
    ///     Binds the socket and starts the accept loop. Throws
    ///     <see cref="SocketException" /> with
    ///     <see cref="SocketError.AddressAlreadyInUse" /> when another live
    ///     instance owns the path; callers should treat that as the
    ///     single-instance signal and exit.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (_listener is not null)
        {
            return;
        }

        TryRemoveStaleSocket(SocketPath);

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        }
        catch
        {
            listener.Dispose();
            throw;
        }

        // 0600: owner-only read/write. Defense in depth on shared /tmp; on
        // XDG_RUNTIME_DIR the parent dir is already 0700.
        SocketPathResolver.TryChmod(SocketPath, 0b110_000_000); // 0600
        _bound = true;
        listener.Listen(8);

        _listener = listener;
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));

        Trace.WriteLine($"[ControlSocketServer] Listening on {SocketPath}");
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
                _ => JsonControlProtocol.SerializeError(JsonControlProtocol.ErrUnknownCommand)
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

    private async Task<string> HandleStartAsync()
    {
        var prev = SnapshotState();

        // Publish the TCS BEFORE invoking the orchestrator: StartAsync runs synchronously
        // until its first real await and can hold _toggleGate before yielding. A concurrent
        // record.stop would otherwise see IsRecording==false and no-op. HandleStopAsync awaits
        // this TCS to ensure the start has completed before calling StopAsync.
        var startCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
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

    private async Task<string> HandleStopAsync()
    {
        var prev = SnapshotState();

        // Hyprland `bindr` tap guard: a record.stop within StartStopRaceWindow of a start is
        // treated as a tap. Await the in-flight start's TCS so StopAsync sees IsRecording==true;
        // without this, _toggleGate.WaitAsync(0) fails and the user ends up with a stuck recording.
        var startTicks = Interlocked.Read(ref _lastStartTicks);
        var elapsed = DateTime.UtcNow - new DateTime(startTicks, DateTimeKind.Utc);
        if (elapsed < s_startStopRaceWindow)
        {
            var pendingStart = _lastStartTask;
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
            Mode = _settings?.Current.Mode.ToString()
        };
        return JsonControlProtocol.SerializeStatus(response);
    }

    /// <summary>Maps observable orchestrator state to the wire string via <see cref="DictationOrchestrator.CurrentStateLabel" />.</summary>
    private string SnapshotState()
    {
        return _orchestrator.CurrentStateLabel;
    }

    /// <summary>
    ///     If the socket path exists but no live peer is listening, deletes it.
    ///     Never deletes a path that has a live peer — that would silently
    ///     detach another running instance.
    /// </summary>
    private static void TryRemoveStaleSocket(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var probe = new Socket(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified
            );
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
                Trace.WriteLine(
                    $"[ControlSocketServer] Failed to remove stale socket {path}: {delEx.Message}"
                );
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ControlSocketServer] Probe of {path} threw: {ex.Message}");
        }
    }

    /// <summary>True when ECONNREFUSED — no live listener, safe to unlink. False on any other outcome.</summary>
    private static bool NoLivePeer(string path)
    {
        try
        {
            using var probe = new Socket(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified
            );
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