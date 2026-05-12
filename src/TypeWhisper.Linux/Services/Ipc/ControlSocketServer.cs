using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace TypeWhisper.Linux.Services.Ipc;

/// <summary>
/// Server side of the control-socket IPC. Listens on a Unix domain socket
/// inside <c>$XDG_RUNTIME_DIR/typewhisper/</c> (or a 0700 fallback) and
/// accepts a single-line text protocol from clients. The current protocol is
/// minimal: a line <c>toggle</c> drives <see cref="DictationOrchestrator.ToggleAsync"/>
/// and the server replies <c>ok</c>. Anything else gets <c>err unknown-command</c>.
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
    private readonly DictationOrchestrator _orchestrator;
    private readonly string _path;
    private Socket? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _disposed;
    // True between successful bind and listener close — used by Dispose to
    // distinguish "we created and own this socket path" from "we never
    // succeeded in binding". The path-ownership check on dispose still
    // probes the live socket to guard against a successor stealing the
    // path during shutdown.
    private bool _bound;

    public ControlSocketServer(DictationOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
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
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            // ReadLineAsync(CancellationToken) is overloaded for ValueTask in
            // net8+; cooperative cancel keeps a hung client from leaking the
            // task on shutdown.
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                return;

            line = line.Trim();
            if (line.Equals("toggle", StringComparison.Ordinal))
            {
                try
                {
                    await _orchestrator.ToggleAsync().ConfigureAwait(false);
                    await writer.WriteLineAsync("ok").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[ControlSocketServer] ToggleAsync threw: {ex.Message}");
                    await writer.WriteLineAsync("err internal").ConfigureAwait(false);
                }
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
