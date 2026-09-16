using System.Net;
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
    public async Task ExtensionlessFileUsesOriginalAbsolutePathWithoutCopyOrMultipart()
    {
        var filePath = Path.Join(_tempDirectory, "extensionless audio");
        await File.WriteAllBytesAsync(filePath, "fLaCaudio"u8.ToArray());
        await using var stub = new UnixHttpStub();
        var options = new CliOptions { Positionals = [filePath] };

        var result = await RunCommandAsync(stub, options, Stream.Null);
        var request = await stub.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("POST /v1/transcribe/local-file HTTP/1.1", request.RequestLine);
        Assert.True(request.Headers.TryGetValue("Content-Type", out var contentType));
        Assert.StartsWith("application/json", contentType, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "multipart/form-data",
            contentType,
            StringComparison.OrdinalIgnoreCase
        );
        using var document = JsonDocument.Parse(request.Body);
        Assert.Equal(filePath, document.RootElement.GetProperty("path").GetString());
        Assert.Equal(new[] { filePath }, Directory.GetFiles(_tempDirectory));
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
        Assert.Equal(3, result.ExitCode);
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
    public async Task EmptyStdinFailsWithoutSendingRequest()
    {
        await using var stub = new UnixHttpStub();
        var options = new CliOptions { Positionals = ["-"] };

        var result = await RunCommandAsync(stub, options, Stream.Null);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Empty audio data", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, stub.RequestCount);
        Assert.False(stub.FirstRequest.Task.IsCompleted);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorrectionsFlagIsOnlySentWhenDisabled(bool disabled)
    {
        await using var stub = new UnixHttpStub();
        var options = CliOptions.Parse(disabled ? ["transcribe", "-", "--no-corrections"] : ["transcribe", "-"]);
        using var stdin = new MemoryStream("RIFF....WAVEaudio"u8.ToArray());
        var result = await RunCommandAsync(stub, options, stdin);
        Assert.Equal(0, result.ExitCode);
        var request = await stub.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var document = JsonDocument.Parse(request.Body);
        Assert.Equal(disabled, document.RootElement.TryGetProperty("apply_corrections", out var value));
        if (disabled) Assert.False(value.GetBoolean());
    }

    [Theory]
    [InlineData("text", "exact text without trailing newline")]
    [InlineData("srt", "1\n00:00:00,200 --> 00:00:01,000\nHello\n\n")]
    [InlineData("vtt", "WEBVTT\n\n00:00.200 --> 00:01.000\nHello\n")]
    public async Task RawFormatsAreWrittenUnchanged(string format, string body)
    {
        await using var stub = new UnixHttpStub(responseBody: body);
        using var stdin = new MemoryStream("RIFF....WAVEaudio"u8.ToArray());
        var result = await RunCommandAsync(stub, CliOptions.Parse(["transcribe", "-", "--response-format", format]), stdin);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(body, result.Output);
        Assert.Empty(result.Error);
        var request = await stub.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var document = JsonDocument.Parse(request.Body);
        Assert.Equal(format, document.RootElement.GetProperty("response_format").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImplicitStdinDependsOnRedirection(bool redirected)
    {
        string? spool = null;
        await using var stub = new UnixHttpStub(beforeResponse: async request =>
        {
            using var document = JsonDocument.Parse(request.Body);
            spool = document.RootElement.GetProperty("path").GetString()!;
            Assert.Equal("RIFF....WAVEaudio"u8.ToArray(), await File.ReadAllBytesAsync(spool));
        });
        using var stdin = new MemoryStream("RIFF....WAVEaudio"u8.ToArray());
        var result = await RunCommandAsync(stub, CliOptions.Parse(["transcribe"]), stdin, () => redirected);
        Assert.Equal(redirected ? 0 : 1, result.ExitCode);
        Assert.Null(stub.CallbackException);
        Assert.Equal(redirected ? 1 : 0, stub.RequestCount);
        if (redirected)
        {
            Assert.NotNull(spool);
            Assert.False(File.Exists(spool));
            Assert.Equal("POST /v1/transcribe/local-file HTTP/1.1", (await stub.FirstRequest.Task).RequestLine);
        }
        else
        {
            Assert.Contains("Provide an audio file or pipe audio to stdin.", result.Error);
        }
    }

    [Fact]
    public async Task UnreadableStdinIsLocalInputError()
    {
        await using var stub = new UnixHttpStub();
        var stdin = new MemoryStream();
        await stdin.DisposeAsync();
        var result = await RunCommandAsync(stub, new CliOptions { Positionals = ["-"] }, stdin);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Could not spool stdin", result.Error);
        Assert.Equal(0, stub.RequestCount);
    }

    [Fact]
    public async Task CancellationWhileSpoolingDeletesPrivateFile()
    {
        await using var stub = new UnixHttpStub();
        using var cts = new CancellationTokenSource();
        await using var stdin = new CancellingStream(cts);
        var before = Directory.GetFiles(Path.GetTempPath(), "typewhisper-stdin-*").ToHashSet();
        var result = await RunCommandAsync(stub, new CliOptions { Positionals = ["-"] }, stdin, ct: cts.Token);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Cancelled.", result.Error);
        Assert.Equal(0, stub.RequestCount);
        Assert.DoesNotContain(Directory.GetFiles(Path.GetTempPath(), "typewhisper-stdin-*"), path => !before.Contains(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightCancellationKeepsCancellationMessage(bool awaitDownload)
    {
        using var cts = new CancellationTokenSource();
        await using var stub = new UnixHttpStub(beforeResponse: _ =>
        {
            // ReSharper disable once AccessToDisposedClosure -- the stub is declared after cts, so its DisposeAsync awaits the serve loop (and this callback) before the using disposes cts.
            cts.Cancel();
            return Task.CompletedTask;
        }) { StallResponse = true };
        using var stdin = new MemoryStream("RIFF....WAVEaudio"u8.ToArray());
        var result = await RunCommandAsync(stub, new CliOptions
        {
            Positionals = ["-"],
            AwaitDownload = awaitDownload,
        }, stdin, ct: cts.Token);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Cancelled.", result.Error);
        Assert.DoesNotContain("timed out", result.Error);
        using var document = JsonDocument.Parse((await stub.FirstRequest.Task).Body);
        Assert.False(File.Exists(document.RootElement.GetProperty("path").GetString()));
    }

    [Fact]
    public async Task CancellationInterruptsBlockedStdinRead()
    {
        await using var stub = new UnixHttpStub();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await using var stdin = new BlockingStream();
        // ReSharper disable once MethodSupportsCancellation -- this is the unconditional test backstop; passing cts.Token would abort the wait when it fires instead of letting the command report "Cancelled.".
        var result = await RunCommandAsync(stub, new CliOptions { Positionals = ["-"] }, stdin, ct: cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Cancelled.", result.Error);
        Assert.Equal(0, stub.RequestCount);
        stdin.Release();
    }

    // Mimics the console stream: the token is ignored once the read has started.
    private sealed class BlockingStream : MemoryStream
    {
        private readonly SemaphoreSlim _gate = new(0);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _gate.Wait(CancellationToken.None);
            return ValueTask.FromResult(0);
        }

        public void Release()
        {
            _gate.Release();
        }
    }

    private sealed class CancellingStream(CancellationTokenSource cts) : MemoryStream("RIFF....WAVEaudio"u8.ToArray())
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= 12) cts.Cancel();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private static async Task<CommandResult> RunCommandAsync(
        UnixHttpStub stub,
        CliOptions options,
        Stream stdin,
        Func<bool>? isInputRedirected = null,
        CancellationToken ct = default
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
            var exitCode = await TranscribeCommand.RunAsync(api, options, stdin, isInputRedirected: isInputRedirected, ct: ct);
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
