using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public class HttpApiRequestParserTests
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void ParseTranscribe_MultipartAudioSharesRequestBodyBackingArray()
    {
        const string boundary = "Boundary123";
        var body = Multipart(
            boundary,
            ("language_hint", null, null, "de"u8.ToArray()),
            ("language_hint", null, null, "en"u8.ToArray()),
            ("task", null, null, "translate"u8.ToArray()),
            ("target_language", null, null, "fr"u8.ToArray()),
            ("response_format", null, null, "verbose_json"u8.ToArray()),
            ("prompt", null, null, "Project names"u8.ToArray()),
            ("engine", null, null, "groq"u8.ToArray()),
            ("model", null, null, "whisper-large-v3"u8.ToArray()),
            ("file", "audio.wav", "audio/wav", [1, 2, 3, 4])
        );

        var request = new HttpApiRequest(
            "POST",
            "/v1/transcribe",
            new NameValueCollection { ["await_download"] = "1" },
            new Dictionary<string, string>
            {
                ["content-type"] = $"multipart/form-data; boundary={boundary}",
            },
            body
        );

        var parsed = HttpApiRequestParser.ParseTranscribe(request);

        Assert.True(MemoryMarshal.TryGetArray(request.Body, out var bodySegment));
        Assert.True(MemoryMarshal.TryGetArray(parsed.AudioData, out var audioSegment));
        Assert.Same(bodySegment.Array, audioSegment.Array);
        Assert.Equal(
            bodySegment.Offset + body.AsSpan().IndexOf(new byte[] { 1, 2, 3, 4 }),
            audioSegment.Offset
        );
        Assert.Equal(4, audioSegment.Count);
        Assert.Equal([1, 2, 3, 4], parsed.AudioData.ToArray());
        Assert.Equal("wav", parsed.FileExtension);
        Assert.Null(parsed.Language);
        Assert.Equal(["de", "en"], parsed.LanguageHints);
        Assert.Equal(TranscriptionTask.Translate, parsed.Task);
        Assert.Equal("fr", parsed.TargetLanguage);
        Assert.Equal("verbose_json", parsed.ResponseFormat);
        Assert.Equal("Project names", parsed.Prompt);
        Assert.Equal("groq", parsed.Engine);
        Assert.Equal("whisper-large-v3", parsed.Model);
        Assert.True(parsed.AwaitDownload);
    }

    [Fact]
    public void ParseTranscribe_ReadsRawBodyHeaders()
    {
        var request = new HttpApiRequest(
            "POST",
            "/v1/transcribe",
            new NameValueCollection(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["content-type"] = "audio/mpeg",
                ["x-language-hints"] = "de, en",
                ["x-task"] = "translate",
                ["x-target-language"] = "es",
                ["x-response-format"] = "verbose_json",
                ["x-prompt"] = "Names",
                ["x-engine"] = "openai",
                ["x-model"] = "gpt-4o-transcribe",
            },
            new byte[] { 9, 8, 7 }
        );

        var parsed = HttpApiRequestParser.ParseTranscribe(request);

        Assert.Equal([9, 8, 7], parsed.AudioData.ToArray());
        Assert.Equal("mp3", parsed.FileExtension);
        Assert.Equal(["de", "en"], parsed.LanguageHints);
        Assert.Equal(TranscriptionTask.Translate, parsed.Task);
        Assert.Equal("es", parsed.TargetLanguage);
        Assert.Equal("verbose_json", parsed.ResponseFormat);
        Assert.Equal("Names", parsed.Prompt);
        Assert.Equal("openai", parsed.Engine);
        Assert.Equal("gpt-4o-transcribe", parsed.Model);
    }

    [Fact]
    public void ParseTranscribe_MultipartPayloadMayContainBoundaryLikeBytes()
    {
        const string boundary = "Boundary123";
        // Boundary text lacking the CRLF prefix and CRLF/"--" suffix a real delimiter carries.
        var audio = new List<byte> { 1, 2 };
        audio.AddRange(Encoding.UTF8.GetBytes($"--{boundary}x"));
        audio.AddRange("\r\n"u8.ToArray());
        audio.AddRange(Encoding.UTF8.GetBytes($"--{boundary}"));
        audio.AddRange(" trailing"u8.ToArray());
        // A closing marker whose epilogue does not start on its own line is not a delimiter.
        audio.AddRange("\r\n"u8.ToArray());
        audio.AddRange(Encoding.UTF8.GetBytes($"--{boundary}--junk"));
        audio.AddRange([3, 4]);
        var payload = audio.ToArray();

        var body = Multipart(
            boundary,
            ("file", "audio.wav", "audio/wav", payload),
            ("language", null, null, "de"u8.ToArray())
        );

        var request = new HttpApiRequest(
            "POST",
            "/v1/transcribe",
            new NameValueCollection(),
            new Dictionary<string, string>
            {
                ["content-type"] = $"multipart/form-data; boundary={boundary}",
            },
            body
        );

        var parsed = HttpApiRequestParser.ParseTranscribe(request);

        Assert.Equal(payload, parsed.AudioData.ToArray());
        Assert.Equal("de", parsed.Language);
    }

    [Fact]
    public void ParseTranscribe_AcceptsMultipartTransportPaddingAfterDelimiters()
    {
        const string boundary = "Boundary123";
        // RFC 2046 permits SP/HTAB padding between a delimiter and its CRLF.
        using var stream = new MemoryStream();
        Write(stream, $"--{boundary} \t\r\n");
        Write(
            stream,
            "Content-Disposition: form-data; name=\"file\"; filename=\"audio.wav\"\r\n"
        );
        Write(stream, "Content-Type: audio/wav\r\n\r\n");
        stream.Write([9, 8, 7]);
        Write(stream, $"\r\n--{boundary}  \r\n");
        Write(stream, "Content-Disposition: form-data; name=\"language\"\r\n\r\n");
        Write(stream, "de");
        Write(stream, $"\r\n--{boundary}--  \r\n");

        var request = new HttpApiRequest(
            "POST",
            "/v1/transcribe",
            new NameValueCollection(),
            new Dictionary<string, string>
            {
                ["content-type"] = $"multipart/form-data; boundary={boundary}",
            },
            stream.ToArray()
        );

        var parsed = HttpApiRequestParser.ParseTranscribe(request);

        Assert.Equal([9, 8, 7], parsed.AudioData.ToArray());
        Assert.Equal("wav", parsed.FileExtension);
        Assert.Equal("de", parsed.Language);
    }

    [Fact]
    public void ParseTranscribe_RejectsLanguageAndHintsTogether()
    {
        const string boundary = "Boundary123";
        var body = Multipart(
            boundary,
            ("language", null, null, "de"u8.ToArray()),
            ("language_hint", null, null, "en"u8.ToArray()),
            ("file", "audio.wav", "audio/wav", [1])
        );

        var request = new HttpApiRequest(
            "POST",
            "/v1/transcribe",
            new NameValueCollection(),
            new Dictionary<string, string>
            {
                ["content-type"] = $"multipart/form-data; boundary={boundary}",
            },
            body
        );

        var ex = Assert.Throws<HttpApiRequestException>(() =>
            HttpApiRequestParser.ParseTranscribe(request)
        );
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("language", ex.Message);
    }

    [Fact]
    public void ParseTranscribe_RejectsMultipartWithoutFile()
    {
        const string boundary = "Boundary123";
        var body = Multipart(boundary, ("language", null, null, "de"u8.ToArray()));
        var request = new HttpApiRequest(
            "POST",
            "/v1/transcribe",
            new NameValueCollection(),
            new Dictionary<string, string>
            {
                ["content-type"] = $"multipart/form-data; boundary={boundary}",
            },
            body
        );

        var ex = Assert.Throws<HttpApiRequestException>(() =>
            HttpApiRequestParser.ParseTranscribe(request)
        );
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("file", ex.Message);
    }

    [Fact]
    public void ParseTranscribeLocalFile_DeserializesFullBody()
    {
        const string json = """
            {
              "path": "/tmp/clip.wav",
              "language": "en",
              "language_hints": ["de", "fr"],
              "task": "translate",
              "target_language": "es",
              "response_format": "verbose_json",
              "prompt": "Names",
              "engine": "groq",
              "model": "whisper-large-v3",
              "await_download": true
            }
            """;
        var parsed = JsonSerializer.Deserialize<LocalFileTranscribeRequest>(json, s_jsonOptions)!;

        Assert.Equal("/tmp/clip.wav", parsed.Path);
        Assert.Equal("en", parsed.Language);
        Assert.Equal(["de", "fr"], parsed.LanguageHints);
        Assert.Equal("translate", parsed.Task);
        Assert.Equal("es", parsed.TargetLanguage);
        Assert.Equal("verbose_json", parsed.ResponseFormat);
        Assert.Equal("Names", parsed.Prompt);
        Assert.Equal("groq", parsed.Engine);
        Assert.Equal("whisper-large-v3", parsed.Model);
        Assert.True(parsed.AwaitDownload);
    }

    [Fact]
    public void ParseDictionaryTermDelete_RequiresTerm()
    {
        var withTerm = JsonSerializer.Deserialize<DictionaryTermDeleteRequest>(
            """{"term":"FooCorp"}""",
            s_jsonOptions
        );
        Assert.Equal("FooCorp", withTerm!.Term);

        var empty = JsonSerializer.Deserialize<DictionaryTermDeleteRequest>("{}", s_jsonOptions);
        Assert.Null(empty!.Term);
    }

    [Fact]
    public void ParseCorrectionUpsert_AcceptsOptionalCaseSensitive()
    {
        var parsed = JsonSerializer.Deserialize<CorrectionUpsertRequest>(
            """{"original":"teh","replacement":"the"}""",
            s_jsonOptions
        );
        Assert.Equal("teh", parsed!.Original);
        Assert.Equal("the", parsed.Replacement);
        Assert.Null(parsed.CaseSensitive);

        var withFlag = JsonSerializer.Deserialize<CorrectionUpsertRequest>(
            """{"original":"teh","replacement":"the","case_sensitive":true}""",
            s_jsonOptions
        );
        Assert.True(withFlag!.CaseSensitive);
    }

    [Fact]
    public async Task ReadBodyAsync_KnownOversizedLengthRejectsBeforeReading()
    {
        var stream = new CountingThrowingReadStream();

        var ex = await Assert.ThrowsAsync<HttpApiRequestException>(() =>
            HttpApiRequestParser.ReadBodyAsync(
                stream,
                declaredLength: 9,
                maxBytes: 8,
                CancellationToken.None
            )
        );

        Assert.Equal(413, ex.StatusCode);
        Assert.Equal("Request body too large", ex.Message);
        Assert.Equal(0, stream.ReadCalls);
    }

    [Fact]
    public async Task ReadBodyAsync_UnknownLengthRejectsOverCapAndAcceptsExactCap()
    {
        var ex = await Assert.ThrowsAsync<HttpApiRequestException>(() =>
            HttpApiRequestParser.ReadBodyAsync(
                new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8, 9]),
                declaredLength: -1,
                maxBytes: 8,
                CancellationToken.None
            )
        );

        Assert.Equal(413, ex.StatusCode);
        Assert.Equal("Request body too large", ex.Message);

        var exact = await HttpApiRequestParser.ReadBodyAsync(
            new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]),
            declaredLength: -1,
            maxBytes: 8,
            CancellationToken.None
        );

        Assert.Equal(8, exact.Length);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], exact.ToArray());
    }

    [Fact]
    public void RequestBodyCaps_ArePinned()
    {
        Assert.Equal(100L * 1024 * 1024, HttpApiService.MaxTranscribeRequestBytes);
        Assert.Equal(1L * 1024 * 1024, HttpApiService.MaxJsonRequestBytes);
    }

    private static byte[] Multipart(
        string boundary,
        params (string Name, string? FileName, string? ContentType, byte[] Data)[] parts
    )
    {
        using var body = new MemoryStream();
        foreach (var part in parts)
        {
            Write(body, $"--{boundary}\r\n");
            var disposition = $"Content-Disposition: form-data; name=\"{part.Name}\"";
            if (part.FileName is not null)
            {
                disposition += $"; filename=\"{part.FileName}\"";
            }

            Write(body, disposition + "\r\n");
            if (part.ContentType is not null)
            {
                Write(body, $"Content-Type: {part.ContentType}\r\n");
            }

            Write(body, "\r\n");
            body.Write(part.Data);
            Write(body, "\r\n");
        }

        Write(body, $"--{boundary}--\r\n");
        return body.ToArray();
    }

    private static void Write(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
    }

    private sealed class CountingThrowingReadStream : Stream
    {
        public int ReadCalls { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            throw new InvalidOperationException("The body must not be read.");
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            ReadCalls++;
            throw new InvalidOperationException("The body must not be read.");
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
    }
}
