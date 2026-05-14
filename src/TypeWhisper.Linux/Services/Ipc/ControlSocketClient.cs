using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

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

            // The server replies with a short line ("ok\n" or "err ..."). 64
            // bytes is more than enough for any current or near-future reply.
            var buf = new byte[64];
            var total = 0;
            while (total < buf.Length)
            {
                var n = sock.Receive(buf, total, buf.Length - total, SocketFlags.None);
                if (n <= 0) break;
                total += n;
                var nl = Array.IndexOf(buf, (byte)'\n', 0, total);
                if (nl >= 0)
                {
                    total = nl;
                    break;
                }
            }

            if (total == 0)
            {
                error = "control socket closed without reply";
                return false;
            }

            var reply = Encoding.UTF8.GetString(buf, 0, total).TrimEnd();
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

    /// <summary>
    /// Sends one JSON request line to the running instance and reads back
    /// the JSON response line. Returns true if the wire exchange completed
    /// (the server replied with valid bytes); inspect <paramref name="responseJson"/>
    /// for the <c>ok</c> field to determine logical success.
    /// </summary>
    /// <remarks>
    /// One request per connection — the server reads exactly one line and
    /// closes. We cap the response read at 4 KB to match the server's own
    /// line cap; any reasonable response (the longest is <c>status</c>) fits
    /// in well under that.
    /// </remarks>
    /// <param name="path">Absolute path to the control socket file.</param>
    /// <param name="request">Object to serialize as the JSON request line.</param>
    /// <param name="responseJson">JSON response line, trimmed of trailing newline. Empty on error.</param>
    /// <param name="error">Set to a diagnostic message on wire failures.</param>
    public static bool TrySendJson(string path, object request, out string responseJson, out string? error)
    {
        responseJson = "";
        error = null;

        if (!File.Exists(path))
        {
            // No socket file means no running instance. Don't treat this as
            // an exception path; callers (CLI subcommands) use this signal
            // to print "not running" and exit non-zero.
            return false;
        }

        try
        {
            using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            {
                SendTimeout = TimeoutMillis,
                ReceiveTimeout = TimeoutMillis,
            };
            sock.Connect(new UnixDomainSocketEndPoint(path));

            var json = JsonSerializer.Serialize(request, JsonControlProtocol.JsonOptions);
            // Enforce the protocol's 4 KB cap on the request side too — the
            // server will reject anything larger, but failing fast here gives
            // a clearer error than a remote line-too-long bounce.
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

            // Read until newline or socket close, capped at MaxLineBytes.
            // We can't use NetworkStream/StreamReader here because
            // StreamReader buffers ahead and would swallow the EOF that the
            // server emits right after the single response line.
            var buf = new byte[JsonControlProtocol.MaxLineBytes];
            var total = 0;
            while (total < buf.Length)
            {
                var n = sock.Receive(buf, total, buf.Length - total, SocketFlags.None);
                if (n <= 0) break;
                total += n;
                // Stop at the first newline so we don't block waiting for
                // a follow-up that will never come.
                var nl = Array.IndexOf(buf, (byte)'\n', 0, total);
                if (nl >= 0)
                {
                    total = nl;
                    break;
                }
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
            // Stale socket file with no listener. Clean it up so a follow-up
            // GUI launch can bind cleanly; report as "not running" to the
            // caller by returning false with no error.
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
