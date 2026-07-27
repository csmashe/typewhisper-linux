using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.Cli.Commands;
using TypeWhisper.Cli.Models;
using TypeWhisper.Cli.Services;
using Xunit;

namespace TypeWhisper.Cli.Tests;

public sealed class TranscribeCommandTests : IDisposable
{
    private static readonly string[] s_expectedLanguageHints = ["en", "fr"];

    private readonly string _tempDirectory =
        Path.Join(
            Path.GetTempPath(),
            "typewhisper-cli-transcribe-" + Guid.NewGuid().ToString("N")
        );

    public TranscribeCommandTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RegularFileUsesLocalFileEndpointAndMapsOptions()
    {
        var filePath = Path.Join(_tempDirectory, "relative.wav");
        await File.WriteAllBytesAsync(filePath, "RIFF....WAVEaudio"u8.ToArray());
        var relativePath = Path.GetRelativePath(Environment.CurrentDirectory, filePath);
        await using var stub = new UnixHttpStub();
        var options = new CliOptions
        {
            Positionals = [relativePath],
            LanguageHints = ["en", "fr"],
            Task = "translate",
            TranslateTo = "de",
            ResponseFormat = "verbose_json",
            Prompt = "A name",
            Engine = "whisper",
            Model = "large-v3",
            AwaitDownload = true,
        };

        var result = await RunCommandAsync(
            stub,
            options,
            Stream.Null
        );
        var request = await stub.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok", result.Output.TrimEnd());
        Assert.Equal("POST /v1/transcribe/local-file HTTP/1.1", request.RequestLine);
        Assert.True(request.Headers.TryGetValue("Content-Type", out var contentType));
        Assert.StartsWith("application/json", contentType, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "multipart/form-data",
            contentType,
            StringComparison.OrdinalIgnoreCase
        );

        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        Assert.Equal(Path.GetFullPath(relativePath), root.GetProperty("path").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("language").ValueKind);
        Assert.Equal(
            s_expectedLanguageHints,
            root.GetProperty("language_hints")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray()
        );
        Assert.Equal("translate", root.GetProperty("task").GetString());
        Assert.Equal("de", root.GetProperty("target_language").GetString());
        Assert.Equal("verbose_json", root.GetProperty("response_format").GetString());
        Assert.Equal("A name", root.GetProperty("prompt").GetString());
        Assert.Equal("whisper", root.GetProperty("engine").GetString());
        Assert.Equal("large-v3", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("await_download").GetBoolean());
        Assert.Equal(1, stub.RequestCount);
    }

    [Fact]
    public async Task RegularFileMapsLanguageWhenHintsAreAbsent()
    {
        var filePath = Path.Join(_tempDirectory, "language.flac");
        await File.WriteAllBytesAsync(filePath, "fLaCaudio"u8.ToArray());
        await using var stub = new UnixHttpStub();
        var options = new CliOptions
        {
            Positionals = [filePath],
            Language = "es",
        };

        var result = await RunCommandAsync(stub, options, Stream.Null);
        var request = await stub.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(request.Body);
        Assert.Equal("es", document.RootElement.GetProperty("language").GetString());
        Assert.Empty(
            document.RootElement.GetProperty("language_hints").EnumerateArray()
        );
        Assert.False(document.RootElement.GetProperty("await_download").GetBoolean());
    }

    [Fact]
    public async Task PaddedOptionValuesAreTrimmedAndBlanksDropped()
    {
        var filePath = Path.Join(_tempDirectory, "padded.wav");
        await File.WriteAllBytesAsync(filePath, "RIFF....WAVEaudio"u8.ToArray());
        await using var stub = new UnixHttpStub();
        var options = new CliOptions
        {
            Positionals = [filePath],
            Language = "  es  ",
            LanguageHints = [],
            Task = "  translate  ",
            TranslateTo = "  de  ",
            ResponseFormat = "  verbose_json  ",
            Prompt = "   ",
            Engine = "  whisper  ",
            Model = "  large-v3  ",
        };

        var result = await RunCommandAsync(stub, options, Stream.Null);
        var request = await stub.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        Assert.Equal("es", root.GetProperty("language").GetString());
        Assert.Equal("translate", root.GetProperty("task").GetString());
        Assert.Equal("de", root.GetProperty("target_language").GetString());
        Assert.Equal("verbose_json", root.GetProperty("response_format").GetString());
        Assert.Equal("whisper", root.GetProperty("engine").GetString());
        Assert.Equal("large-v3", root.GetProperty("model").GetString());

        // Blank stays null, not empty string — local-file treats a forwarded blank literally.
        Assert.Equal(JsonValueKind.Null, root.GetProperty("prompt").ValueKind);
    }

    [Fact]
    public async Task BlankLanguageHintsAreDroppedAndSurvivorsTrimmed()
    {
        var filePath = Path.Join(_tempDirectory, "hints.wav");
        await File.WriteAllBytesAsync(filePath, "RIFF....WAVEaudio"u8.ToArray());
        await using var stub = new UnixHttpStub();
        var options = new CliOptions
        {
            Positionals = [filePath],
            LanguageHints = ["  en  ", "   ", "fr"],
        };

        var result = await RunCommandAsync(stub, options, Stream.Null);
        var request = await stub.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(request.Body);
        Assert.Equal(
            s_expectedLanguageHints,
            document
                .RootElement.GetProperty("language_hints")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray()
        );
    }

    [Fact]
    public async Task StdinSpoolIsPrivateSniffedAndDeletedAfterSuccess()
    {
        var audio = new byte[4096];
        "fLaC"u8.CopyTo(audio);
        string? observedPath = null;
        byte[]? observedAudio = null;
        UnixFileMode? observedMode = null;
        await using var stub = new UnixHttpStub(
            beforeResponse: async request =>
            {
                using var document = JsonDocument.Parse(request.Body);
                observedPath = document.RootElement.GetProperty("path").GetString();
                if (!OperatingSystem.IsWindows())
                {
                    observedMode = File.GetUnixFileMode(observedPath!);
                }

                observedAudio = await File.ReadAllBytesAsync(observedPath!);
            }
        );
        var options = new CliOptions { Positionals = ["-"] };

        var result = await RunCommandAsync(
            stub,
            options,
            new MemoryStream(audio, writable: false)
        );

        Assert.Null(stub.CallbackException);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(observedPath);
        Assert.True(Path.IsPathFullyQualified(observedPath));
        Assert.StartsWith(
            "typewhisper-stdin-",
            Path.GetFileName(observedPath),
            StringComparison.Ordinal
        );
        Assert.EndsWith(".flac", observedPath, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                observedMode
            );
        }

        Assert.Equal(audio, observedAudio);
        Assert.False(File.Exists(observedPath));
    }

    [Fact]
    public async Task StdinSpoolIsDeletedWhenServerReturnsFailure()
    {
        string? observedPath = null;
        await using var stub = new UnixHttpStub(
            HttpStatusCode.InternalServerError,
            """{"error":"stub failure"}""",
            request =>
            {
                using var document = JsonDocument.Parse(request.Body);
                observedPath = document.RootElement.GetProperty("path").GetString();
                return Task.CompletedTask;
            }
        );
        var options = new CliOptions { Positionals = ["-"] };

        var result = await RunCommandAsync(
            stub,
            options,
            new MemoryStream("ID3audio"u8.ToArray(), writable: false)
        );

        Assert.Null(stub.CallbackException);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            "Transcription failed (500): stub failure",
            result.Error,
            StringComparison.Ordinal
        );
        Assert.NotNull(observedPath);
        Assert.EndsWith(".mp3", observedPath, StringComparison.Ordinal);
        Assert.False(File.Exists(observedPath));
    }

    [Fact]
    public async Task MultiChunkStdinSpoolsByteForByteWithoutSizeRejection()
    {
        var audio = new byte[1024 * 1024];
        "OggS"u8.CopyTo(audio);
        for (var i = 4; i < audio.Length; i++)
        {
            audio[i] = (byte)(i % 251);
        }

        string? observedPath = null;
        byte[]? observedAudio = null;
        await using var stub = new UnixHttpStub(
            beforeResponse: async request =>
            {
                using var document = JsonDocument.Parse(request.Body);
                observedPath = document.RootElement.GetProperty("path").GetString();
                observedAudio = await File.ReadAllBytesAsync(observedPath!);
            }
        );
        var options = new CliOptions { Positionals = ["-"] };

        var result = await RunCommandAsync(
            stub,
            options,
            new ChunkedReadStream(audio, 1024)
        );

        Assert.Null(stub.CallbackException);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(observedPath);
        Assert.EndsWith(".ogg", observedPath, StringComparison.Ordinal);
        Assert.Equal(audio, observedAudio);
        Assert.False(File.Exists(observedPath));
    }

    [Fact]
    public async Task DribbledStdinHeaderIsSniffedAfterAccumulatingTheSniffWindow()
    {
        var audio = new byte[64];
        "fLaC"u8.CopyTo(audio);
        for (var i = 4; i < audio.Length; i++)
        {
            audio[i] = (byte)(i % 251);
        }

        string? observedPath = null;
        byte[]? observedAudio = null;
        await using var stub = new UnixHttpStub(
            beforeResponse: async request =>
            {
                using var document = JsonDocument.Parse(request.Body);
                observedPath = document.RootElement.GetProperty("path").GetString();
                observedAudio = await File.ReadAllBytesAsync(observedPath!);
            }
        );
        var options = new CliOptions { Positionals = ["-"] };

        // A pipe can hand back fewer bytes than the sniffer's 12-byte window needs.
        var result = await RunCommandAsync(
            stub,
            options,
            new ChunkedReadStream(audio, 2)
        );

        Assert.Null(stub.CallbackException);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(observedPath);
        Assert.EndsWith(".flac", observedPath, StringComparison.Ordinal);
        Assert.Equal(audio, observedAudio);
    }

    [Fact]
    public async Task EmptyStdinFailsWithoutSendingRequestOrSpooling()
    {
        var spooledBefore = Directory.GetFiles(Path.GetTempPath(), "typewhisper-stdin-*").Length;
        await using var stub = new UnixHttpStub();
        var options = new CliOptions { Positionals = ["-"] };

        var result = await RunCommandAsync(stub, options, Stream.Null);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Empty audio data", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, stub.RequestCount);
        Assert.False(stub.FirstRequest.Task.IsCompleted);
        Assert.Equal(
            spooledBefore,
            Directory.GetFiles(Path.GetTempPath(), "typewhisper-stdin-*").Length
        );
    }

    [Fact]
    public async Task MissingFileFailsWithoutSendingRequest()
    {
        var missingPath = Path.Join(_tempDirectory, "missing.wav");
        await using var stub = new UnixHttpStub();
        var options = new CliOptions { Positionals = [missingPath] };

        var result = await RunCommandAsync(stub, options, Stream.Null);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            $"File not found: {missingPath}",
            result.Error,
            StringComparison.Ordinal
        );
        Assert.Equal(0, stub.RequestCount);
        Assert.False(stub.FirstRequest.Task.IsCompleted);
    }

    private static async Task<CommandResult> RunCommandAsync(
        UnixHttpStub stub,
        CliOptions options,
        Stream stdin
    )
    {
        var api = new ApiClient(
            stub.SocketPath,
            "test-token",
            validateServer: _ => true
        );
        var originalOut = Console.Out;
        var originalError = Console.Error;
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await TranscribeCommand.RunAsync(api, options, stdin);
            return new CommandResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            api.Http.Dispose();
            api.TranscribeHttp.Dispose();
        }
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);

    private sealed record CapturedRequest(
        string RequestLine,
        IReadOnlyDictionary<string, string> Headers,
        string Body
    );

    private sealed class UnixHttpStub : IAsyncDisposable
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
            await _serveTask;
            _cts.Dispose();
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
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
                        var lengthHeader = headerLines.Single(line =>
                            line.StartsWith(
                                "Content-Length:",
                                StringComparison.OrdinalIgnoreCase
                            )
                        );
                        contentLength = int.Parse(
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

    private sealed class ChunkedReadStream(byte[] contents, int maximumRead) : Stream
    {
        private readonly MemoryStream _inner = new(contents, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _inner.Read(buffer, offset, Math.Min(count, maximumRead));
        }

        public override int Read(Span<byte> buffer)
        {
            return _inner.Read(buffer[..Math.Min(buffer.Length, maximumRead)]);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            return _inner.ReadAsync(
                buffer[..Math.Min(buffer.Length, maximumRead)],
                cancellationToken
            );
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
