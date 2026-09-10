using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.Cli.Commands;
using TypeWhisper.Cli.Models;
using TypeWhisper.Cli.Output;
using TypeWhisper.Cli.Services;
using Xunit;

namespace TypeWhisper.Cli.Tests;

public sealed class ProtocolResponseValidationTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Join(
            Path.GetTempPath(),
            "typewhisper-cli-protocol-" + Guid.NewGuid().ToString("N")
        );
    private readonly string? _originalConfigHome =
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    private readonly string _audioPath;

    public ProtocolResponseValidationTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _tempDirectory);
        _audioPath = Path.Join(_tempDirectory, "audio.wav");
        File.WriteAllBytes(_audioPath, "RIFF....WAVEaudio"u8.ToArray());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalConfigHome);
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, "not-json", "not valid JSON")]
    [InlineData(true, "not-json", "not valid JSON")]
    [InlineData(false, "{}", "missing required field 'status'")]
    [InlineData(true, "{}", "missing required field 'status'")]
    [InlineData(false, """{"status":1}""", "field 'status' must be a string")]
    [InlineData(true, """{"status":1}""", "field 'status' must be a string")]
    [InlineData(false, """{"status":"loading"}""", "unknown status value 'loading'")]
    [InlineData(true, """{"status":"loading"}""", "unknown status value 'loading'")]
    [InlineData(
        false,
        """{"status":"ready","api_version":"9.9"}""",
        "API version '9.9' is not supported"
    )]
    [InlineData(
        true,
        """{"status":"ready","api_version":"9.9"}""",
        "API version '9.9' is not supported"
    )]
    public async Task Status_InvalidSuccessfulBody_ReturnsProtocolError(
        bool json,
        string body,
        string expectedDetail
    )
    {
        var result = await RunCommandAsync(CommandKind.Status, body, json);

        AssertProtocolError(result, expectedDetail);
    }

    [Theory]
    [InlineData(false, "not-json", "not valid JSON")]
    [InlineData(true, "not-json", "not valid JSON")]
    [InlineData(false, "{}", "missing required field 'models'")]
    [InlineData(true, "{}", "missing required field 'models'")]
    [InlineData(false, """{"models":{}}""", "field 'models' must be an array")]
    [InlineData(true, """{"models":{}}""", "field 'models' must be an array")]
    [InlineData(
        false,
        """{"models":[{"selected":"yes"}]}""",
        "field 'models[0].selected' must be a boolean"
    )]
    [InlineData(
        true,
        """{"models":[{"selected":"yes"}]}""",
        "field 'models[0].selected' must be a boolean"
    )]
    [InlineData(
        false,
        """{"models":[{"id":"tiny","engine":7,"name":"Tiny","status":"ready"}]}""",
        "field 'models[0].engine' must be a string"
    )]
    [InlineData(
        true,
        """{"models":[{"id":"tiny","engine":"whisper","name":"Tiny","status":true}]}""",
        "field 'models[0].status' must be a string"
    )]
    public async Task Models_InvalidSuccessfulBody_ReturnsProtocolError(
        bool json,
        string body,
        string expectedDetail
    )
    {
        var result = await RunCommandAsync(CommandKind.Models, body, json);

        AssertProtocolError(result, expectedDetail);
    }

    [Theory]
    [InlineData(false, "not-json", "not valid JSON")]
    [InlineData(true, "not-json", "not valid JSON")]
    [InlineData(false, "{}", "missing required field 'text'")]
    [InlineData(true, "{}", "missing required field 'text'")]
    [InlineData(false, """{"text":42}""", "field 'text' must be a string")]
    [InlineData(true, """{"text":42}""", "field 'text' must be a string")]
    public async Task Transcribe_InvalidSuccessfulBody_ReturnsProtocolError(
        bool json,
        string body,
        string expectedDetail
    )
    {
        var result = await RunCommandAsync(CommandKind.Transcribe, body, json);

        AssertProtocolError(result, expectedDetail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Status_ValidSuccessfulBody_KeepsExistingOutput(bool json)
    {
        const string body =
            """{"status":"ready","engine":"whisper","model":"tiny","api_version":"1.0"}""";

        var result = await RunCommandAsync(CommandKind.Status, body, json);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        Assert.Equal(
            json
                ? JsonFormatting.PrettyJson(body) + Environment.NewLine
                : "Ready - whisper (tiny)" + Environment.NewLine,
            result.Output
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Status_NoModelBodyWithNullFields_KeepsExistingOutput(bool json)
    {
        const string body =
            """{"status":"no_model","engine":null,"model":null,"api_version":"1.0"}""";

        var result = await RunCommandAsync(CommandKind.Status, body, json);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        Assert.Equal(
            json
                ? JsonFormatting.PrettyJson(body) + Environment.NewLine
                : "No model loaded - " + Environment.NewLine,
            result.Output
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Models_ValidSuccessfulBody_KeepsExistingOutput(bool json)
    {
        const string body =
            """{"models":[{"id":"tiny","engine":"whisper","name":"Tiny","status":"ready","selected":true}]}""";

        var result = await RunCommandAsync(CommandKind.Models, body, json);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        if (json)
        {
            Assert.Equal(
                JsonFormatting.PrettyJson(body) + Environment.NewLine,
                result.Output
            );
        }
        else
        {
            Assert.Equal(
                "ID    ENGINE   NAME  STATUS"
                    + Environment.NewLine
                    + "---------------------------"
                    + Environment.NewLine
                    + "tiny  whisper  Tiny  ready *"
                    + Environment.NewLine,
                result.Output
            );
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transcribe_ValidSuccessfulBody_KeepsExistingOutput(bool json)
    {
        const string body = """{"text":"ok","language":"en"}""";

        var result = await RunCommandAsync(CommandKind.Transcribe, body, json);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        Assert.Equal(
            json
                ? JsonFormatting.PrettyJson(body) + Environment.NewLine
                : "ok" + Environment.NewLine,
            result.Output
        );
    }

    [Fact]
    public async Task DiscoveryVersionMismatch_FailsBeforeConnecting()
    {
        await using var stub = new UnixHttpStub(
            """{"status":"ready","engine":"whisper"}"""
        );
        WriteDiscovery(
            JsonSerializer.Serialize(
                new
                {
                    version = 3,
                    port = 9876,
                    token = "secret",
                    socket_path = stub.SocketPath,
                }
            )
        );

        var result = await CaptureConsoleAsync(() => Program.RunAsync(["status"]));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Contains(
            "discovery protocol version 3",
            result.Error,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "this CLI speaks version 2",
            result.Error,
            StringComparison.Ordinal
        );
        Assert.Contains("out of sync", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, stub.RequestCount);
    }

    private async Task<CommandResult> RunCommandAsync(
        CommandKind command,
        string body,
        bool json
    )
    {
        await using var stub = new UnixHttpStub(body);
        var api = new ApiClient(stub.SocketPath, null, validateServer: _ => true);
        try
        {
            return await CaptureConsoleAsync(() =>
                command switch
                {
                    CommandKind.Status => StatusCommand.RunAsync(
                        api,
                        json,
                        CancellationToken.None
                    ),
                    CommandKind.Models => ModelsCommand.RunAsync(
                        api,
                        json,
                        CancellationToken.None
                    ),
                    CommandKind.Transcribe => TranscribeCommand.RunAsync(
                        api,
                        new CliOptions
                        {
                            Positionals = [_audioPath],
                            Json = json,
                        },
                        Stream.Null
                    ),
                    _ => throw new ArgumentOutOfRangeException(nameof(command)),
                }
            );
        }
        finally
        {
            api.Http.Dispose();
            api.TranscribeHttp.Dispose();
        }
    }

    private static async Task<CommandResult> CaptureConsoleAsync(Func<Task<int>> run)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await run();
            return new CommandResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static void AssertProtocolError(
        CommandResult result,
        string expectedDetail
    )
    {
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Contains("Protocol error:", result.Error, StringComparison.Ordinal);
        Assert.Contains(expectedDetail, result.Error, StringComparison.Ordinal);
        Assert.Contains("out of sync", result.Error, StringComparison.Ordinal);
    }

    private void WriteDiscovery(string json)
    {
        var directory = Path.Join(_tempDirectory, "typewhisper");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Join(directory, "api-discovery.json"), json);
    }

    private enum CommandKind
    {
        Status,
        Models,
        Transcribe,
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);

    private sealed class UnixHttpStub : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Socket _listener =
            new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly byte[] _response;
        private readonly Task _serveTask;
        private readonly string _socketDirectory;
        private int _requestCount;

        internal UnixHttpStub(string responseBody)
        {
            var body = Encoding.UTF8.GetBytes(responseBody);
            var header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n"
                    + "Content-Type: application/json\r\n"
                    + $"Content-Length: {body.Length}\r\n"
                    + "Connection: close\r\n\r\n"
            );
            _response = [.. header, .. body];
            _socketDirectory = Path.Join(
                Path.GetTempPath(),
                "typewhisper-cli-protocol-uds-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(_socketDirectory);
            SocketPath = Path.Join(_socketDirectory, "api.sock");
            _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            _listener.Listen(1);
            _serveTask = ServeAsync();
        }

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
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                // Expected when a version mismatch leaves the listener untouched.
            }
            catch (ObjectDisposedException) when (_cts.IsCancellationRequested)
            {
                // Expected when a version mismatch leaves the listener untouched.
            }

            _cts.Dispose();
            if (Directory.Exists(_socketDirectory))
            {
                Directory.Delete(_socketDirectory, recursive: true);
            }
        }

        private async Task ServeAsync()
        {
            using var connection = await _listener.AcceptAsync(_cts.Token);
            await ReadRequestAsync(connection, _cts.Token);
            Interlocked.Increment(ref _requestCount);

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

        private static async Task ReadRequestAsync(
            Socket connection,
            CancellationToken ct
        )
        {
            using var bytes = new MemoryStream();
            var buffer = new byte[4096];
            var headerEnd = -1;
            var contentLength = 0;
            while (true)
            {
                var read = await connection.ReceiveAsync(buffer, SocketFlags.None, ct);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        "Client closed before sending the complete request."
                    );
                }

                bytes.Write(buffer, 0, read);
                if (headerEnd < 0)
                {
                    headerEnd = FindHeaderEnd(
                        bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))
                    );
                    if (headerEnd >= 0)
                    {
                        var headers = Encoding.ASCII.GetString(
                            bytes.GetBuffer(),
                            0,
                            headerEnd
                        );
                        foreach (var line in headers.Split("\r\n"))
                        {
                            if (
                                line.StartsWith(
                                    "Content-Length:",
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                            {
                                contentLength = int.Parse(
                                    line[(line.IndexOf(':') + 1)..].Trim()
                                );
                            }
                        }
                    }
                }

                if (
                    headerEnd >= 0
                    && bytes.Length >= headerEnd + 4L + contentLength
                )
                {
                    return;
                }
            }
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
    }
}
