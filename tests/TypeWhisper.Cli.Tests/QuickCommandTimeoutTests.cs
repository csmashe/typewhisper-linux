using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using TypeWhisper.Cli.Commands;
using TypeWhisper.Cli.Services;
using Xunit;

namespace TypeWhisper.Cli.Tests;

public sealed class QuickCommandTimeoutTests
{
    private static readonly TimeSpan s_hardTestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_shortBudget = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task StatusCommand_StalledListener_ReturnsTimeoutPromptly()
    {
        await using var stub = new UnixHttpStub();
        var api = new ApiClient(stub.SocketPath, null);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var (exitCode, error) = await CaptureErrorAsync(() =>
                StatusCommand
                    .RunAsync(api, json: false, CancellationToken.None, s_shortBudget)
                    .WaitAsync(s_hardTestTimeout)
            );

            Assert.Equal(1, exitCode);
            Assert.Contains("The API did not respond within 0.25 seconds.", error);
            Assert.DoesNotContain("Cancelled.", error);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        }
        finally
        {
            Dispose(api);
        }
    }

    [Fact]
    public async Task ModelsCommand_StalledListener_ReturnsTimeoutPromptly()
    {
        await using var stub = new UnixHttpStub();
        var api = new ApiClient(stub.SocketPath, null);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var (exitCode, error) = await CaptureErrorAsync(() =>
                ModelsCommand
                    .RunAsync(api, json: false, CancellationToken.None, s_shortBudget)
                    .WaitAsync(s_hardTestTimeout)
            );

            Assert.Equal(1, exitCode);
            Assert.Contains("The API did not respond within 0.25 seconds.", error);
            Assert.DoesNotContain("Cancelled.", error);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        }
        finally
        {
            Dispose(api);
        }
    }

    [Fact]
    public async Task StatusCommand_CallerAlreadyCancelled_ReportsCancellation()
    {
        await using var stub = new UnixHttpStub();
        var api = new ApiClient(stub.SocketPath, null);
        using var callerCts = new CancellationTokenSource();
        // ReSharper disable once MethodHasAsyncOverload -- pre-cancelling the caller token is setup, not teardown; CancelAsync() adds nothing.
        callerCts.Cancel();

        try
        {
            var (exitCode, error) = await CaptureErrorAsync(() =>
                StatusCommand
                    .RunAsync(api, json: false, callerCts.Token, s_shortBudget)
                    // ReSharper disable once MethodSupportsCancellation -- this is the unconditional test backstop; passing the already-cancelled callerCts.Token would abort the wait before the command could report "Cancelled.".
                    .WaitAsync(s_hardTestTimeout)
            );

            Assert.Equal(1, exitCode);
            Assert.Contains("Cancelled.", error);
            Assert.DoesNotContain("did not respond", error);
        }
        finally
        {
            Dispose(api);
        }
    }

    [Fact]
    public async Task ModelsCommand_CallerAlreadyCancelled_ReportsCancellation()
    {
        await using var stub = new UnixHttpStub();
        var api = new ApiClient(stub.SocketPath, null);
        using var callerCts = new CancellationTokenSource();
        // ReSharper disable once MethodHasAsyncOverload -- pre-cancelling the caller token is setup, not teardown; CancelAsync() adds nothing.
        callerCts.Cancel();

        try
        {
            var (exitCode, error) = await CaptureErrorAsync(() =>
                ModelsCommand
                    .RunAsync(api, json: false, callerCts.Token, s_shortBudget)
                    // ReSharper disable once MethodSupportsCancellation -- this is the unconditional test backstop; passing the already-cancelled callerCts.Token would abort the wait before the command could report "Cancelled.".
                    .WaitAsync(s_hardTestTimeout)
            );

            Assert.Equal(1, exitCode);
            Assert.Contains("Cancelled.", error);
            Assert.DoesNotContain("did not respond", error);
        }
        finally
        {
            Dispose(api);
        }
    }

    [Fact]
    public async Task StatusCommand_FastReply_Succeeds()
    {
        const string body = """{"status":"ready","engine":"whisper","model":"tiny"}""";
        await using var stub = new UnixHttpStub(body);
        var api = new ApiClient(stub.SocketPath, null);

        try
        {
            var exitCode = await StatusCommand
                .RunAsync(api, json: false, CancellationToken.None, s_shortBudget)
                .WaitAsync(s_hardTestTimeout);

            Assert.Equal(0, exitCode);
        }
        finally
        {
            Dispose(api);
        }
    }

    [Fact]
    public async Task ModelsCommand_FastReply_Succeeds()
    {
        const string body = """{"models":[]}""";
        await using var stub = new UnixHttpStub(body);
        var api = new ApiClient(stub.SocketPath, null);

        try
        {
            var exitCode = await ModelsCommand
                .RunAsync(api, json: false, CancellationToken.None, s_shortBudget)
                .WaitAsync(s_hardTestTimeout);

            Assert.Equal(0, exitCode);
        }
        finally
        {
            Dispose(api);
        }
    }

    private static async Task<(int ExitCode, string Error)> CaptureErrorAsync(
        Func<Task<int>> run
    )
    {
        var originalError = Console.Error;
        await using var writer = new StringWriter();
        try
        {
            Console.SetError(writer);
            var exitCode = await run();
            return (exitCode, writer.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    private static void Dispose(ApiClient api)
    {
        api.Http.Dispose();
        api.TranscribeHttp.Dispose();
    }

    private sealed class UnixHttpStub : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Socket _listener =
            new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly byte[]? _response;
        private readonly Task _serveTask;
        private readonly string _tempDirectory;

        internal UnixHttpStub(string? responseBody = null)
        {
            if (responseBody is not null)
            {
                var body = Encoding.UTF8.GetBytes(responseBody);
                _response = Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{responseBody}"
                );
            }

            _tempDirectory = Path.Join(
                Path.GetTempPath(),
                "typewhisper-cli-quick-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(_tempDirectory);
            SocketPath = Path.Join(_tempDirectory, "api.sock");
            _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            _listener.Listen(1);
            _serveTask = ServeAsync();
        }

        internal string SocketPath { get; }

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
                // Expected while releasing a stalled connection or accept.
            }
            catch (ObjectDisposedException)
            {
                // Expected while releasing the listener.
            }
            catch (SocketException)
            {
                // Expected when the timed-out client resets its connection.
            }

            _cts.Dispose();
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        private async Task ServeAsync()
        {
            using var connection = await _listener.AcceptAsync(_cts.Token);
            await ReadRequestAsync(connection, _cts.Token);

            if (_response is null)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, _cts.Token);
                return;
            }

            await connection.SendAsync(_response, SocketFlags.None, _cts.Token);
        }

        private static async Task ReadRequestAsync(Socket connection, CancellationToken ct)
        {
            using var output = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var read = await connection.ReceiveAsync(buffer, SocketFlags.None, ct);
                if (read == 0)
                {
                    return;
                }

                output.Write(buffer, 0, read);
                if (
                    Encoding.ASCII
                        .GetString(output.GetBuffer(), 0, checked((int)output.Length))
                        .Contains("\r\n\r\n", StringComparison.Ordinal)
                )
                {
                    return;
                }
            }
        }
    }
}
