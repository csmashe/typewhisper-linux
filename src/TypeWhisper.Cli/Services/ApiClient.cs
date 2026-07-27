using System.Net.Sockets;
using System.Net.Http.Headers;

namespace TypeWhisper.Cli.Services;

/// <summary>
///     Holds the HTTP clients used to talk to the running TypeWhisper app's
///     REST API and applies bearer auth once at construction. Two clients are
///     kept: a 5-minute one for quick status/models calls, and an unbounded one
///     for transcribe (which can run far longer when <c>--await-download</c>
///     triggers a server-side model fetch; each transcribe request bounds
///     itself with a <see cref="CancellationTokenSource" /> instead).
/// </summary>
internal sealed class ApiClient
{
    private readonly Func<Socket, bool> _validateServer;

    public ApiClient(
        string socketPath,
        string? token,
        Func<Socket, bool>? validateServer = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        _validateServer = validateServer ?? UnixPeerCredentials.IsOwnedByEffectiveUser;
        BaseUrl = "http://localhost";
        Http = new HttpClient(CreateHandler(socketPath)) { Timeout = TimeSpan.FromMinutes(5) };
        TranscribeHttp = new HttpClient(CreateHandler(socketPath))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var auth = new AuthenticationHeaderValue("Bearer", token);
        Http.DefaultRequestHeaders.Authorization = auth;
        TranscribeHttp.DefaultRequestHeaders.Authorization = auth;
    }

    public string BaseUrl { get; }
    public HttpClient Http { get; }

    public HttpClient TranscribeHttp { get; }

    private SocketsHttpHandler CreateHandler(string socketPath)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // ConnectCallback always dials the Unix socket directly, so an ambient
            // HTTP_PROXY/ALL_PROXY would just make the client speak proxy/SOCKS
            // negotiation at Kestrel — never useful for a local socket.
            UseProxy = false,
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(
                    AddressFamily.Unix,
                    SocketType.Stream,
                    ProtocolType.Unspecified
                );
                try
                {
                    await socket.ConnectAsync(
                        new UnixDomainSocketEndPoint(socketPath),
                        ct
                    );
                    if (!_validateServer(socket))
                    {
                        throw new UnauthorizedAccessException(
                            "TypeWhisper API socket is owned by a different user."
                        );
                    }

                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }
}
