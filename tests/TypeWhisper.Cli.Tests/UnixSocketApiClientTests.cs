using System.Net;
using System.Net.Sockets;
using System.Text;
using TypeWhisper.Cli.Models;
using TypeWhisper.Cli.Services;
using Xunit;

namespace TypeWhisper.Cli.Tests;

public sealed class UnixSocketApiClientTests
{
    [Fact]
    public async Task RequestsFlowOverUnixSocketWithBearer()
    {
        await using var stub = new UnixHttpStub();
        var api = new ApiClient(stub.SocketPath, "socket-secret");

        using var response = await api.Http.GetAsync($"{api.BaseUrl}/v1/status");
        var request = await stub.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("GET /v1/status HTTP/1.1", request, StringComparison.Ordinal);
        Assert.Contains(
            "Authorization: Bearer socket-secret",
            request,
            StringComparison.Ordinal
        );
        Dispose(api);
    }

    [Fact]
    public async Task RedirectsAreNotFollowed()
    {
        await using var stub = new UnixHttpStub(
            "HTTP/1.1 302 Found\r\nLocation: /redirected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
        );
        var api = new ApiClient(stub.SocketPath, null);

        using var response = await api.Http.GetAsync($"{api.BaseUrl}/v1/status");
        await Task.Delay(100);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(1, stub.RequestCount);
        Dispose(api);
    }

    [Fact]
    public async Task AmbientProxyConfigurationIsIgnored()
    {
        await using var stub = new UnixHttpStub();
        var originalProxy = HttpClient.DefaultProxy;
        // A SOCKS proxy would otherwise be negotiated over the Unix socket itself.
        HttpClient.DefaultProxy = new WebProxy("socks5://127.0.0.1:1")
        {
            BypassProxyOnLocal = false,
        };

        try
        {
            // ReSharper disable once UseObjectOrCollectionInitializer -- a nested Http initializer would strip the comment explaining the timeout.
            var api = new ApiClient(stub.SocketPath, "socket-secret");
            // Bound the test failure in case proxy bypass regresses.
            api.Http.Timeout = TimeSpan.FromSeconds(3);

            using var response = await api.Http.GetAsync($"{api.BaseUrl}/v1/status");
            var request = await stub.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.StartsWith("GET /v1/status HTTP/1.1", request, StringComparison.Ordinal);
            Dispose(api);
        }
        finally
        {
            HttpClient.DefaultProxy = originalProxy;
        }
    }

    [Fact]
    public async Task UidMismatchAbortsBeforeAnyHttpBytesAreSent()
    {
        await using var stub = new UnixHttpStub();
        var api = new ApiClient(stub.SocketPath, "must-not-leak", _ => false);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            api.TranscribeHttp.PostAsync(
                $"{api.BaseUrl}/v1/transcribe",
                new ByteArrayContent("private-audio"u8.ToArray())
            )
        );

        var bytes = await stub.FirstConnectionBytes.Task.WaitAsync(
            TimeSpan.FromSeconds(2)
        );
        Assert.Empty(bytes);
        Assert.Equal(0, stub.RequestCount);
        Dispose(api);
    }

    [Fact]
    public async Task HostileTcpListenerOnDiscoveryPortIsNeverContacted()
    {
        await using var stub = new UnixHttpStub();
        using var hostileTcp = new TcpListener(IPAddress.Loopback, 0);
        hostileTcp.Start();
        var discovery = new DiscoveryFile(
            ((IPEndPoint)hostileTcp.LocalEndpoint).Port,
            null,
            stub.SocketPath
        );
        var api = new ApiClient(discovery.SocketPath!, discovery.Token);

        using var response = await api.Http.GetAsync($"{api.BaseUrl}/v1/status");
        await stub.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(hostileTcp.Pending());
        Dispose(api);
    }

    private static void Dispose(ApiClient api)
    {
        api.Http.Dispose();
        api.TranscribeHttp.Dispose();
    }

    private sealed class UnixHttpStub : IAsyncDisposable
    {
        private const string DefaultResponse =
            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}";

        private readonly CancellationTokenSource _cts = new();
        private readonly Socket _listener =
            new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly string _response;
        private readonly string _tempDirectory;
        private readonly Task _serveTask;
        private int _requestCount;

        internal UnixHttpStub(string? response = null)
        {
            _response = response ?? DefaultResponse;
            _tempDirectory = Path.Join(
                Path.GetTempPath(),
                "typewhisper-cli-uds-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(_tempDirectory);
            SocketPath = Path.Join(_tempDirectory, "api.sock");
            _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            _listener.Listen(8);
            _serveTask = ServeAsync();
        }

        internal string SocketPath { get; }

        internal int RequestCount => Volatile.Read(ref _requestCount);

        internal TaskCompletionSource<string> FirstRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<byte[]> FirstConnectionBytes { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DisposeAsync()
        {
            // ReSharper disable once MethodHasAsyncOverload -- Cancel() is fine in these teardown paths; CancelAsync() only defers callbacks, with no benefit here.
            _cts.Cancel();
            _listener.Dispose();
            try
            {
                await _serveTask;
            }
            catch (OperationCanceledException)
            {
                // Expected while ending the accept loop.
            }
            catch (ObjectDisposedException)
            {
                // Expected while ending the accept loop.
            }

            _cts.Dispose();
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        private async Task ServeAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                using var connection = await _listener.AcceptAsync(_cts.Token);
                var bytes = await ReadRequestAsync(connection, _cts.Token);
                FirstConnectionBytes.TrySetResult(bytes);
                if (bytes.Length == 0)
                {
                    continue;
                }

                var request = Encoding.ASCII.GetString(bytes);
                Interlocked.Increment(ref _requestCount);
                FirstRequest.TrySetResult(request);
                await connection.SendAsync(
                    Encoding.ASCII.GetBytes(_response),
                    SocketFlags.None,
                    _cts.Token
                );
            }
        }

        private static async Task<byte[]> ReadRequestAsync(
            Socket connection,
            CancellationToken ct
        )
        {
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var read = await connection.ReceiveAsync(buffer, SocketFlags.None, ct);
                if (read == 0)
                {
                    return output.ToArray();
                }

                output.Write(buffer, 0, read);
                if (
                    Encoding.ASCII
                        .GetString(output.GetBuffer(), 0, checked((int)output.Length))
                        .Contains("\r\n\r\n", StringComparison.Ordinal)
                )
                {
                    return output.ToArray();
                }
            }
        }
    }
}
