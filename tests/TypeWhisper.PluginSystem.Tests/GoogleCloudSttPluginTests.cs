using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Moq;
using TypeWhisper.Plugin.GoogleCloudStt;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public class GoogleCloudSttPluginTests
{
    private const int SampleRateHertz = 16000;
    private const int BytesPerSample = sizeof(short);
    private const int BytesPerSecond = SampleRateHertz * BytesPerSample;
    private const int ChunkLimitSeconds = 55;
    private const int QuietWindowMilliseconds = 20;
    private const int QuietWindowBytes =
        SampleRateHertz * BytesPerSample * QuietWindowMilliseconds / 1000;
    private const int ProviderDurationLimitBytes = 60 * BytesPerSecond;
    private const int ProviderRequestLimitBytes = 10 * 1024 * 1024;

    // Single-chunk boundary regression guard: audio at the plugin's conservative
    // chunk limit must not be split merely because Google itself allows up to 60s.
    [Fact]
    public async Task TranscribeAsync_AtChunkLimit_SendsOneRequestBelowProviderLimits()
    {
        var handler = CreateSuccessfulHandler();
        using var sut = await CreateConfiguredPluginAsync(handler);
        var wavAudio = BuildFfmpegStyleWav(ChunkLimitSeconds * BytesPerSecond);

        var result = await sut.TranscribeAsync(
            wavAudio,
            "en",
            translate: false,
            prompt: null,
            CancellationToken.None
        );

        Assert.NotNull(result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(ChunkLimitSeconds * BytesPerSecond, request.Audio.Length);
        AssertRequestWithinProviderLimits(request);
    }

    [Fact]
    public async Task TranscribeAsync_SixtyOneSeconds_SendsTwoSampleAlignedRequestsAtQuietBoundary()
    {
        const int audioBytes = 61 * BytesPerSecond;
        const int quietWindowStart = 53 * BytesPerSecond;
        var wavAudio = BuildFfmpegStyleWav(audioBytes, amplitude: 1200);
        var pcmOffset = wavAudio.Length - audioBytes;
        wavAudio.AsSpan(pcmOffset + quietWindowStart, QuietWindowBytes).Clear();

        var handler = CreateSuccessfulHandler();
        using var sut = await CreateConfiguredPluginAsync(handler);

        await sut.TranscribeAsync(
            wavAudio,
            "en",
            translate: false,
            prompt: null,
            CancellationToken.None
        );

        Assert.Equal(2, handler.Requests.Count);
        const int expectedFirstChunkBytes = quietWindowStart + QuietWindowBytes / 2;
        Assert.Equal(expectedFirstChunkBytes, handler.Requests[0].Audio.Length);
        Assert.Equal(audioBytes - expectedFirstChunkBytes, handler.Requests[1].Audio.Length);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(0, request.Audio.Length % BytesPerSample);
            AssertRequestWithinProviderLimits(request);
        });

        var reconstructedAudio = handler.Requests.SelectMany(request => request.Audio).ToArray();
        Assert.Equal(wavAudio.AsSpan(pcmOffset, audioBytes).ToArray(), reconstructedAudio);
    }

    [Fact]
    public async Task TranscribeAsync_MultipleChunks_ConcatenatesTranscriptsAndSumsDurationsInOrder()
    {
        var transcripts = new[] { "first", "second", "third" };
        var billedTimes = new[] { "1.250s", "2.500s", "3.750s" };
        var handler = new CapturingHandler((callNumber, _, _) =>
            Task.FromResult(
                JsonResponse(
                    RecognitionResponse(
                        transcripts[callNumber - 1],
                        billedTimes[callNumber - 1],
                        "en-US"
                    )
                )
            )
        );
        using var sut = await CreateConfiguredPluginAsync(handler);
        var wavAudio = BuildFfmpegStyleWav(121 * BytesPerSecond);

        var result = await sut.TranscribeAsync(
            wavAudio,
            "en",
            translate: false,
            prompt: null,
            CancellationToken.None
        );

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("first second third", result.Text);
        Assert.Equal("en-US", result.DetectedLanguage);
        Assert.Equal(7.5, result.DurationSeconds);
    }

    [Fact]
    public async Task TranscribeAsync_LaterChunkHttpFailure_FailsWholeTranscription()
    {
        var handler = new CapturingHandler((callNumber, _, _) =>
            Task.FromResult(
                callNumber == 1
                    ? JsonResponse(RecognitionResponse("prefix", "55s", "en-US"))
                    : JsonResponse("""{"error":{"message":"quota failure"}}""", HttpStatusCode.BadGateway)
            )
        );
        using var sut = await CreateConfiguredPluginAsync(handler);
        var wavAudio = BuildFfmpegStyleWav(61 * BytesPerSecond);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.TranscribeAsync(
                wavAudio,
                "en",
                translate: false,
                prompt: null,
                CancellationToken.None
            )
        );

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task TranscribeAsync_LaterChunkMalformedResponse_FailsWholeTranscription()
    {
        var handler = new CapturingHandler((callNumber, _, _) =>
            Task.FromResult(
                callNumber == 1
                    ? JsonResponse(RecognitionResponse("prefix", "55s", "en-US"))
                    : JsonResponse("""{"results":"not-an-array"}""")
            )
        );
        using var sut = await CreateConfiguredPluginAsync(handler);
        var wavAudio = BuildFfmpegStyleWav(61 * BytesPerSecond);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.TranscribeAsync(
                wavAudio,
                "en",
                translate: false,
                prompt: null,
                CancellationToken.None
            )
        );

        Assert.Equal(
            "Invalid Google Cloud STT response: 'results' must be an array.",
            exception.Message
        );
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task TranscribeAsync_CancellationBetweenChunks_StopsBeforeNextRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new CapturingHandler((_, _, _) =>
            Task.FromResult(
                JsonResponse(
                    RecognitionResponse("prefix", "55s", "en-US"),
                    // ReSharper disable once AccessToDisposedClosure -- Cancel runs synchronously while TranscribeAsync below is awaited, before the using disposes cancellation at scope end.
                    afterContentRead: cancellation.Cancel
                )
            )
        );
        using var sut = await CreateConfiguredPluginAsync(handler);
        var wavAudio = BuildFfmpegStyleWav(61 * BytesPerSecond);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.TranscribeAsync(
                wavAudio,
                "en",
                translate: false,
                prompt: null,
                cancellation.Token
            )
        );

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TranscribeAsync_EveryChunkRequestStaysBelowProviderLimits()
    {
        var handler = CreateSuccessfulHandler();
        using var sut = await CreateConfiguredPluginAsync(handler);
        var wavAudio = BuildFfmpegStyleWav(181 * BytesPerSecond);

        await sut.TranscribeAsync(
            wavAudio,
            "en",
            translate: false,
            prompt: null,
            CancellationToken.None
        );

        Assert.True(handler.Requests.Count >= 4);
        Assert.All(handler.Requests, AssertRequestWithinProviderLimits);
    }

    // Single-chunk transport regression guard: segmentation must not alter the
    // established v1 route, API-key query authentication, or recognition config.
    [Fact]
    public async Task TranscribeAsync_SingleChunk_PreservesV1RouteApiKeyAndRecognitionConfig()
    {
        var handler = new CapturingHandler((_, _, _) =>
            Task.FromResult(
                JsonResponse(RecognitionResponse("Guten Tag", "1.500s", "de-DE"))
            )
        );
        using var sut = await CreateConfiguredPluginAsync(handler);
        var wavAudio = BuildFfmpegStyleWav(BytesPerSecond, amplitude: 400);
        var pcmOffset = wavAudio.Length - BytesPerSecond;

        var result = await sut.TranscribeAsync(
            wavAudio,
            "de",
            translate: false,
            prompt: "unchanged ignored prompt",
            CancellationToken.None
        );

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post.Method, request.Method);
        Assert.Equal(
            "https://speech.googleapis.com/v1/speech:recognize?key=dummy-key",
            request.Uri
        );
        Assert.Null(request.Authorization);
        Assert.Equal("application/json", request.MediaType);
        Assert.Equal("LINEAR16", request.AudioEncoding);
        Assert.Equal(SampleRateHertz, request.SampleRateHertz);
        Assert.Equal("de-DE", request.LanguageCode);
        Assert.Equal("latest_long", request.Model);
        Assert.Equal(
            wavAudio.AsSpan(pcmOffset, BytesPerSecond).ToArray(),
            request.Audio
        );
        Assert.Equal("Guten Tag", result.Text);
        Assert.Equal("de-DE", result.DetectedLanguage);
        Assert.Equal(1.5, result.DurationSeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TranscribeAsync_AutomaticLanguage_ThrowsBeforeParsingAudioOrSendingRequest(
        string? language
    )
    {
        var handler = CreateSuccessfulHandler();
        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () =>
                sut.TranscribeAsync(
                    [],
                    language,
                    translate: false,
                    prompt: null,
                    CancellationToken.None
                )
        );

        Assert.Equal(
            "Google Cloud STT requires an explicit language; automatic language detection is not supported.",
            exception.Message
        );
        Assert.Empty(handler.Requests);
    }

    private static void AssertRequestWithinProviderLimits(CapturedRequest request)
    {
        Assert.True(request.Audio.Length < ProviderDurationLimitBytes);
        Assert.True(request.BodyByteCount < ProviderRequestLimitBytes);
    }

    private static CapturingHandler CreateSuccessfulHandler() =>
        new((_, _, _) => Task.FromResult(JsonResponse("""{"results":[]}""")));

    private static async Task<GoogleCloudSttPlugin> CreateConfiguredPluginAsync(
        CapturingHandler handler
    )
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(service => service.LoadSecretAsync("api-key")).ReturnsAsync("dummy-key");

        var sut = new GoogleCloudSttPlugin(handler);
        await sut.ActivateAsync(host.Object);
        return sut;
    }

    private static string RecognitionResponse(
        string transcript,
        string billedTime,
        string detectedLanguage
    ) =>
        JsonSerializer.Serialize(
            new
            {
                results = new[]
                {
                    new
                    {
                        alternatives = new[] { new { transcript } },
                        languageCode = detectedLanguage,
                    },
                },
                totalBilledTime = billedTime,
            }
        );

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        Action? afterContentRead = null
    )
    {
        HttpContent content =
            afterContentRead is null
                ? new StringContent(json, Encoding.UTF8, "application/json")
                : new CallbackJsonContent(json, afterContentRead);
        return new HttpResponseMessage(statusCode) { Content = content };
    }

    // Mirrors ffmpeg's `-f wav pipe:1` output: RIFF/WAVE + fmt + LIST(INFO) + data,
    // with 0xffffffff placeholder sizes (a pipe can't be seeked to backfill them).
    private static byte[] BuildFfmpegStyleWav(int dataBytes, short amplitude = 0)
    {
        var listBody = "INFOISFT\u000e\0\0\0Lavf62.12.102\0"u8.ToArray();
        var buffer = new byte[12 + 24 + 8 + listBody.Length + 8 + dataBytes];
        var span = buffer.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 0xFFFFFFFF);
        "WAVE"u8.CopyTo(span[8..]);

        var offset = 12;
        "fmt "u8.CopyTo(span[offset..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 4)..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(offset + 8)..], 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(span[(offset + 10)..], 1); // mono
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 12)..], SampleRateHertz);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 16)..], BytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(offset + 20)..], BytesPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(offset + 22)..], 16);
        offset += 24;

        "LIST"u8.CopyTo(span[offset..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 4)..], (uint)listBody.Length);
        listBody.CopyTo(span[(offset + 8)..]);
        offset += 8 + listBody.Length;

        "data"u8.CopyTo(span[offset..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 4)..], 0xFFFFFFFF);
        offset += 8;

        // ReSharper disable once InvertIf -- the positive form states the fill case of this WAV builder.
        if (amplitude != 0)
        {
            for (var sampleOffset = offset; sampleOffset < buffer.Length; sampleOffset += 2)
            {
                BinaryPrimitives.WriteInt16LittleEndian(
                    span.Slice(sampleOffset, BytesPerSample),
                    amplitude
                );
            }
        }

        return buffer;
    }

    private sealed record CapturedRequest(
        string Method,
        string Uri,
        string? Authorization,
        string? MediaType,
        int BodyByteCount,
        byte[] Audio,
        string AudioEncoding,
        // ReSharper disable once MemberHidesStaticFromOuterClass -- captured request field, only read as request.SampleRateHertz; the name mirrors the outer const deliberately.
        int SampleRateHertz,
        string LanguageCode,
        string Model
    );

    private sealed class CapturingHandler(
        Func<int, CapturedRequest, CancellationToken, Task<HttpResponseMessage>> responder
    ) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var body = await Assert
                .IsType<HttpContent>(request.Content, exactMatch: false)
                .ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var config = root.GetProperty("config");
            var captured = new CapturedRequest(
                request.Method.Method,
                request.RequestUri?.AbsoluteUri ?? string.Empty,
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.MediaType,
                Encoding.UTF8.GetByteCount(body),
                Convert.FromBase64String(root.GetProperty("audio").GetProperty("content").GetString()!),
                config.GetProperty("encoding").GetString()!,
                config.GetProperty("sampleRateHertz").GetInt32(),
                config.GetProperty("languageCode").GetString()!,
                config.GetProperty("model").GetString()!
            );
            Requests.Add(captured);
            return await responder(Requests.Count, captured, cancellationToken);
        }
    }

    private sealed class CallbackJsonContent : HttpContent
    {
        private readonly Action _afterContentRead;
        private readonly byte[] _content;

        public CallbackJsonContent(string json, Action afterContentRead)
        {
            _content = Encoding.UTF8.GetBytes(json);
            _afterContentRead = afterContentRead;
            Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context
        )
        {
            await stream.WriteAsync(_content);
            _afterContentRead();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _content.Length;
            return true;
        }
    }
}
