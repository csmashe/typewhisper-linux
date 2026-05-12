using System.IO;
using System.Net.Sockets;
using System.Text;

namespace TypeWhisper.Linux.Services.Ipc;

/// <summary>
/// Client side of the control-socket IPC. A second invocation of the
/// <c>typewhisper</c> binary uses this to ask a running instance to toggle
/// dictation, then exit. See <see cref="ControlSocketServer"/> for the
/// server-side protocol.
/// </summary>
internal static class ControlSocketClient
{
    private const int TimeoutMillis = 2000;

    /// <summary>
    /// Side-effect-free liveness probe. Returns true if a server is
    /// currently bound to the socket at <paramref name="path"/>. Used by
    /// argument-bearing launches (e.g. <c>typewhisper --minimized</c>) that
    /// must NOT trigger a toggle just to discover that another instance is
    /// running. Stale-socket cleanup mirrors <see cref="TrySendToggle"/>.
    /// </summary>
    public static bool IsLivePeer(string path)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            {
                SendTimeout = TimeoutMillis,
                ReceiveTimeout = TimeoutMillis,
            };
            sock.Connect(new UnixDomainSocketEndPoint(path));
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            try { File.Delete(path); } catch { /* best-effort */ }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to send a <c>toggle</c> command to the running instance over
    /// the Unix domain socket at <paramref name="path"/>. Returns true if the
    /// server acknowledged with <c>ok</c>. If the socket file exists but the
    /// peer is dead (ECONNREFUSED), the stale file is deleted before
    /// returning false so the caller can bind a fresh server.
    /// </summary>
    /// <param name="path">Absolute path to the control socket file.</param>
    /// <param name="error">Set to a diagnostic message on non-stale failures.</param>
    public static bool TrySendToggle(string path, out string? error)
    {
        error = null;

        if (!File.Exists(path))
            return false;

        try
        {
            using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            {
                SendTimeout = TimeoutMillis,
                ReceiveTimeout = TimeoutMillis,
            };
            sock.Connect(new UnixDomainSocketEndPoint(path));

            var msg = Encoding.UTF8.GetBytes("toggle\n");
            sock.Send(msg);

            // The server replies with a short line ("ok\n" or "err ..."). 64
            // bytes is more than enough for any current or near-future reply.
            var buf = new byte[64];
            var n = sock.Receive(buf);
            if (n <= 0)
            {
                error = "control socket closed without reply";
                return false;
            }

            var reply = Encoding.UTF8.GetString(buf, 0, n).TrimEnd();
            if (reply.StartsWith("ok", StringComparison.Ordinal))
                return true;

            error = $"control socket replied: {reply}";
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            // Stale socket: file exists but nobody is listening. Remove so
            // the new GUI instance can bind without hitting EADDRINUSE.
            try { File.Delete(path); } catch { /* best-effort */ }
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
