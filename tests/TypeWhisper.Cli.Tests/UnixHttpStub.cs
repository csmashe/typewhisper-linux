using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TypeWhisper.Cli.Tests;

internal sealed record CapturedRequest(
    string RequestLine,
    IReadOnlyDictionary<string, string> Headers,
    string Body
);

internal sealed class UnixHttpStub : IAsyncDisposable
{
    private readonly Func<CapturedRequest, Task>? _beforeResponse;
    private readonly CancellationTokenSource _cts = new();
    private readonly Socket _listener =
        new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly byte[] _response;
    private readonly Task _serveTask;
    private readonly string _tempDirectory;
    private int _requestCount;

    internal UnixHttpStub(
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string responseBody = """{"text":"ok"}""",
        Func<CapturedRequest, Task>? beforeResponse = null
    )
    {
        _beforeResponse = beforeResponse;
        _response = CreateResponse(statusCode, responseBody);
        _tempDirectory = Path.Join(
            Path.GetTempPath(),
            "typewhisper-cli-transcribe-uds-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempDirectory);
        SocketPath = Path.Join(_tempDirectory, "api.sock");
        _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        _listener.Listen(8);
        _serveTask = ServeAsync();
    }

    internal bool StallResponse { get; init; }

    internal Exception? CallbackException { get; private set; }

    internal TaskCompletionSource<CapturedRequest> FirstRequest { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal int RequestCount => Volatile.Read(ref _requestCount);

    internal string SocketPath { get; }

    public async ValueTask DisposeAsync()
    {
        // ReSharper disable once MethodHasAsyncOverload -- Cancel() is fine in teardown; there are no cancellation callbacks to defer.
        _cts.Cancel();
        _listener.Dispose();
        try
        {
            await _serveTask;
        }
        // Teardown faults must not replace whatever the test was actually asserting.
        catch (EndOfStreamException)
        {
            // Expected when the CLI closes before finishing its request.
        }
        catch (SocketException ex) when (SocketShutdown.IsShutdownError(ex))
        {
            // Expected when the CLI resets the connection during shutdown.
        }
        finally
        {
            _cts.Dispose();
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
    }

    private static byte[] CreateResponse(
        HttpStatusCode statusCode,
        string responseBody
    )
    {
        var body = Encoding.UTF8.GetBytes(responseBody);
        var reason = statusCode switch
        {
            HttpStatusCode.OK => "OK",
            HttpStatusCode.InternalServerError => "Internal Server Error",
            _ => statusCode.ToString(),
        };
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(int)statusCode} {reason}\r\n"
                + "Content-Type: application/json\r\n"
                + $"Content-Length: {body.Length}\r\n"
                + "Connection: close\r\n\r\n"
        );
        return [.. header, .. body];
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i <= bytes.Length - 4; i++)
        {
            if (
                bytes[i] == '\r'
                && bytes[i + 1] == '\n'
                && bytes[i + 2] == '\r'
                && bytes[i + 3] == '\n'
            )
            {
                return i;
            }
        }

        return -1;
    }

    private static async Task<CapturedRequest> ReadRequestAsync(
        Socket connection,
        CancellationToken cancellationToken
    )
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        var headerEnd = -1;
        var contentLength = 0;
        string[]? headerLines = null;

        while (true)
        {
            var read = await connection.ReceiveAsync(
                buffer,
                SocketFlags.None,
                cancellationToken
            );
            if (read == 0)
            {
                throw new EndOfStreamException("Client closed before sending the request.");
            }

            bytes.Write(buffer, 0, read);
            if (headerEnd < 0)
            {
                headerEnd = FindHeaderEnd(
                    bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))
                );
                if (headerEnd >= 0)
                {
                    var headerText = Encoding.ASCII.GetString(
                        bytes.GetBuffer(),
                        0,
                        headerEnd
                    );
                    headerLines = headerText.Split("\r\n");
                    var lengthHeader = headerLines.SingleOrDefault(line =>
                        line.StartsWith(
                            "Content-Length:",
                            StringComparison.OrdinalIgnoreCase
                        )
                    );
                    contentLength = lengthHeader is null ? 0 : int.Parse(
                        lengthHeader[(lengthHeader.IndexOf(':') + 1)..].Trim()
                    );
                }
            }

            if (
                headerEnd >= 0
                && bytes.Length >= headerEnd + 4L + contentLength
            )
            {
                break;
            }
        }

        var headers = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var line in headerLines![1..])
        {
            var separator = line.IndexOf(':');
            headers.Add(line[..separator], line[(separator + 1)..].Trim());
        }

        return new CapturedRequest(
            headerLines[0],
            headers,
            Encoding.UTF8.GetString(
                bytes.GetBuffer(),
                headerEnd + 4,
                contentLength
            )
        );
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                using var connection = await _listener.AcceptAsync(_cts.Token);
                var request = await ReadRequestAsync(connection, _cts.Token);
                Interlocked.Increment(ref _requestCount);
                FirstRequest.TrySetResult(request);
                if (_beforeResponse is not null)
                {
                    try
                    {
                        await _beforeResponse(request);
                    }
                    catch (Exception ex)
                    {
                        CallbackException = ex;
                    }
                }

                if (StallResponse) await Task.Delay(Timeout.InfiniteTimeSpan, _cts.Token);
                var offset = 0;
                while (offset < _response.Length)
                {
                    offset += await connection.SendAsync(
                        _response.AsMemory(offset),
                        SocketFlags.None,
                        _cts.Token
                    );
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Expected while ending the accept loop.
        }
        catch (ObjectDisposedException) when (_cts.IsCancellationRequested)
        {
            // Expected while ending the accept loop.
        }
    }
}

