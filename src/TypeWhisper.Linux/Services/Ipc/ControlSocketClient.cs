using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TypeWhisper.Linux.Services.Ipc;

/// <summary>
///     Client side of the control-socket IPC. A second invocation of the
///     <c>typewhisper</c> binary uses this to ask a running instance to toggle
///     dictation, then exit. See <see cref="ControlSocketServer" /> for the
///     server-side protocol.
/// </summary>
internal static class ControlSocketClient
{
    private const int TimeoutMillis = 2000;

    /// <summary>
    ///     Liveness probe: returns true if a server is bound to <paramref name="path" />.
    ///     Never toggles recording state, so argument-bearing launches (e.g. <c>--minimized</c>)
    ///     can use it to check for a running instance. It may, however, unlink a socket path
    ///     confirmed stale under the ownership lock.
    /// </summary>
    public static bool IsLivePeer(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var sock = new Socket(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified
            );
            sock.SendTimeout = TimeoutMillis;
            sock.ReceiveTimeout = TimeoutMillis;
            sock.Connect(new UnixDomainSocketEndPoint(path));
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            // ECONNREFUSED alone isn't proof of staleness — the peer may have bound but not
            // yet started listening. Re-probe under the ownership lock before unlinking.
            ControlSocketOwnership.TryCleanupStaleSocket(path);

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Sends a <c>toggle</c> command over the Unix socket at <paramref name="path" />.
    ///     Returns true if the server acknowledged with <c>ok</c>. Deletes a stale socket
    ///     file (ECONNREFUSED) so the caller can bind a fresh server.
    /// </summary>
    /// <param name="path">Absolute path to the control socket file.</param>
    /// <param name="error">Set to a diagnostic message on non-stale failures.</param>
    public static bool TrySendToggle(string path, out string? error)
    {
        error = null;

        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var sock = new Socket(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified
            );
            sock.SendTimeout = TimeoutMillis;
            sock.ReceiveTimeout = TimeoutMillis;
            sock.Connect(new UnixDomainSocketEndPoint(path));

            var msg = "toggle\n"u8.ToArray();
            var sent = 0;
            while (sent < msg.Length)
            {
                var w = sock.Send(msg, sent, msg.Length - sent, SocketFlags.None);
                if (w <= 0)
                {
                    error = "control socket closed during send";
                    return false;
                }

                sent += w;
            }

            // Server replies with a short line ("ok\n" or "err ..."); 64 bytes is ample.
            var buf = new byte[64];
            var total = 0;
            while (total < buf.Length)
            {
                var n = sock.Receive(buf, total, buf.Length - total, SocketFlags.None);
                if (n <= 0)
                {
                    break;
                }

                total += n;
                var nl = Array.IndexOf(buf, (byte)'\n', 0, total);
                if (nl < 0)
                {
                    continue;
                }

                total = nl;
                break;
            }

            if (total == 0)
            {
                error = "control socket closed without reply";
                return false;
            }

            var reply = Encoding.UTF8.GetString(buf, 0, total).TrimEnd();
            if (reply.StartsWith("ok", StringComparison.Ordinal))
            {
                return true;
            }

            error = $"control socket replied: {reply}";
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            // ECONNREFUSED alone isn't proof of staleness; re-probe under the ownership lock before unlinking.
            ControlSocketOwnership.TryCleanupStaleSocket(path);

            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    ///     Sends one JSON request line and reads back the JSON response line.
    ///     Returns true if the wire exchange completed; inspect <paramref name="responseJson" />
    ///     for the <c>ok</c> field for logical success. One request per connection;
    ///     response is capped at 4 KB (matches the server's line cap).
    /// </summary>
    /// <param name="path">Absolute path to the control socket file.</param>
    /// <param name="request">Object to serialize as the JSON request line.</param>
    /// <param name="responseJson">JSON response line, trimmed of trailing newline. Empty on error.</param>
    /// <param name="error">Set to a diagnostic message on wire failures.</param>
    public static bool TrySendJson(
        string path,
        object request,
        out string responseJson,
        out string? error
    )
    {
        responseJson = "";
        error = null;

        if (!File.Exists(path))
        {
            // No socket file = no running instance; callers print "not running" and exit non-zero.
            return false;
        }

        try
        {
            using var sock = new Socket(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified
            );
            sock.SendTimeout = TimeoutMillis;
            sock.ReceiveTimeout = TimeoutMillis;
            sock.Connect(new UnixDomainSocketEndPoint(path));

            var json = JsonSerializer.Serialize(request, JsonControlProtocol.JsonOptions);
            // Enforce 4 KB cap client-side for a clearer error than a remote rejection.
            var payload = Encoding.UTF8.GetBytes(json + "\n");
            if (payload.Length > JsonControlProtocol.MaxLineBytes)
            {
                error = "request exceeds 4 KB protocol cap";
                return false;
            }

            var sent = 0;
            while (sent < payload.Length)
            {
                var w = sock.Send(payload, sent, payload.Length - sent, SocketFlags.None);
                if (w <= 0)
                {
                    error = "control socket closed during send";
                    return false;
                }

                sent += w;
            }

            // Read until newline or close, capped at MaxLineBytes. StreamReader can't be used
            // here — it buffers ahead and would swallow the EOF the server emits after its reply.
            var buf = new byte[JsonControlProtocol.MaxLineBytes];
            var total = 0;
            while (total < buf.Length)
            {
                var n = sock.Receive(buf, total, buf.Length - total, SocketFlags.None);
                if (n <= 0)
                {
                    break;
                }

                total += n;
                var nl = Array.IndexOf(buf, (byte)'\n', 0, total);
                if (nl < 0)
                {
                    continue;
                }

                total = nl;
                break;
            }

            if (total == 0)
            {
                error = "control socket closed without reply";
                return false;
            }

            responseJson = Encoding.UTF8.GetString(buf, 0, total).TrimEnd();
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            // ECONNREFUSED alone isn't proof of staleness; re-probe under the ownership lock before unlinking.
            ControlSocketOwnership.TryCleanupStaleSocket(path);

            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
