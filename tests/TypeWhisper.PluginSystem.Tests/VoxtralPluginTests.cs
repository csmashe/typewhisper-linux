using System.Net;
using System.Text;
using Moq;
using TypeWhisper.Plugin.Voxtral;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public class VoxtralPluginTests
{
    [Fact]
    public async Task ActivateAsync_UsesVoxtralMiniAndDisablesTranslation()
    {
        var host = CreateHostMock();

        using var sut = new VoxtralPlugin();
        await sut.ActivateAsync(host.Object);

        Assert.Equal("voxtral-mini-latest", sut.SelectedModelId);
        Assert.Equal(
            ["voxtral-mini-latest"],
            sut.TranscriptionModels.Select(model => model.Id).ToArray()
        );
        Assert.False(sut.SupportsTranslation);
    }

    [Fact]
    public void TranscriptionModels_AdvertisesDocumentedLatestAlias()
    {
        using var sut = new VoxtralPlugin();

        var model = Assert.Single(sut.TranscriptionModels);

        Assert.Equal("voxtral-mini-latest", model.Id);
    }

    [Fact]
    public async Task ActivateAsync_MigratesLegacyModelSelection()
    {
        var host = CreateHostMock("mistral-whisper");

        using var sut = new VoxtralPlugin();
        await sut.ActivateAsync(host.Object);

        Assert.Equal("voxtral-mini-latest", sut.SelectedModelId);
        host.Verify(
            service => service.SetSetting("selectedModel", "voxtral-mini-latest"),
            Times.Once
        );
    }

    [Fact]
    public async Task SelectModel_NormalizesLegacyModelId()
    {
        var host = CreateHostMock();

        using var sut = new VoxtralPlugin();
        await sut.ActivateAsync(host.Object);

        sut.SelectModel("mistral-whisper");

        Assert.Equal("voxtral-mini-latest", sut.SelectedModelId);
    }

    [Fact]
    public async Task TranscribeAsync_PostsDocumentedMultipartRequestAndOmitsAutoLanguage()
    {
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "https://api.mistral.ai/v1/audio/transcriptions",
                request.RequestUri?.AbsoluteUri
            );
            Assert.Equal("Bearer voxtral-key", request.Headers.Authorization?.ToString());

            var content = Assert.IsType<MultipartFormDataContent>(request.Content);
            var parts = content.ToArray();
            Assert.Equal(
                ["file", "model", "timestamp_granularities"],
                parts.Select(GetPartName).ToArray()
            );

            var file = Assert.Single(parts, part => GetPartName(part) == "file");
            Assert.Equal("audio.wav", file.Headers.ContentDisposition?.FileName?.Trim('"'));
            Assert.Equal("audio/wav", file.Headers.ContentType?.MediaType);
            Assert.Equal([1, 2, 3], await file.ReadAsByteArrayAsync(ct));

            var model = Assert.Single(parts, part => GetPartName(part) == "model");
            Assert.Equal("voxtral-mini-latest", await model.ReadAsStringAsync(ct));

            var granularities = Assert.Single(
                parts,
                part => GetPartName(part) == "timestamp_granularities"
            );
            Assert.Equal("segment", await granularities.ReadAsStringAsync(ct));

            return JsonResponse("""{ "text": "Hello", "language": "en", "usage": {} }""");
        });
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.TranscribeAsync(
            [1, 2, 3],
            "auto",
            translate: false,
            prompt: "Do not send this as prompt or context_bias",
            CancellationToken.None
        );

        Assert.Equal("Hello", result.Text);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task TranscribeAsync_SendsExplicitLanguage()
    {
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            var content = Assert.IsType<MultipartFormDataContent>(request.Content);
            var parts = content.ToArray();
            Assert.Equal(
                ["file", "model", "timestamp_granularities", "language"],
                parts.Select(GetPartName).ToArray()
            );
            var language = Assert.Single(parts, part => GetPartName(part) == "language");
            Assert.Equal("de", await language.ReadAsStringAsync(ct));

            return JsonResponse("""{ "text": "Hallo", "language": "de", "usage": {} }""");
        });
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.TranscribeAsync(
            [1, 2, 3],
            "de",
            translate: false,
            prompt: null,
            CancellationToken.None
        );

        Assert.Equal("de", result.DetectedLanguage);
    }

    [Fact]
    public async Task TranscribeAsync_ParsesDocumentedTextLanguageSegmentsAndUsage()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(
                JsonResponse(
                    """
                    {
                      "model": "voxtral-mini-2507",
                      "text": "Hello world",
                      "language": "en",
                      "segments": [
                        {
                          "type": "transcription_segment",
                          "text": "Hello",
                          "start": 0.1,
                          "end": 0.7,
                          "score": 0.98,
                          "speaker_id": "speaker_0"
                        },
                        {
                          "type": "transcription_segment",
                          "text": " world",
                          "start": 0.7,
                          "end": 1.2,
                          "score": null,
                          "speaker_id": null
                        }
                      ],
                      "usage": {
                        "prompt_audio_seconds": 2,
                        "prompt_tokens": 4,
                        "completion_tokens": 6,
                        "total_tokens": 10
                      }
                    }
                    """
                )
            )
        );
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.TranscribeAsync(
            [1, 2, 3],
            null,
            translate: false,
            prompt: null,
            CancellationToken.None
        );

        Assert.Equal("Hello world", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal(2, result.DurationSeconds);
        Assert.Collection(
            result.Segments,
            segment => Assert.Equal(("Hello", 0.1, 0.7), (segment.Text, segment.Start, segment.End)),
            segment => Assert.Equal((" world", 0.7, 1.2), (segment.Text, segment.Start, segment.End))
        );
        Assert.Null(result.NoSpeechProbability);
    }

    [Fact]
    public async Task TranscribeAsync_AcceptsExplicitEmptyText()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse("""{ "text": "", "language": null, "usage": {} }"""))
        );
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.TranscribeAsync(
            [1, 2, 3],
            null,
            translate: false,
            prompt: null,
            CancellationToken.None
        );

        Assert.Equal(string.Empty, result.Text);
    }

    [Theory]
    // ReSharper disable once RawStringCanBeSimplified -- kept raw to match the sibling InlineData rows, which need raw strings for their quotes.
    [InlineData("""{}""")]
    [InlineData("""{ "text": null }""")]
    [InlineData("""{ "text": 42 }""")]
    public async Task TranscribeAsync_RejectsResponseWithoutStringText(string responseBody)
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse(responseBody))
        );
        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.TranscribeAsync(
                [1, 2, 3],
                null,
                translate: false,
                prompt: null,
                CancellationToken.None
            )
        );

        Assert.Equal(
            "Invalid Mistral transcription response: required field 'text' must be a string.",
            exception.Message
        );
    }

    [Fact]
    public async Task TranscribeAsync_SurfacesProviderHttpError()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(
                JsonResponse(
                    """{ "detail": "Unsupported audio format" }""",
                    HttpStatusCode.UnprocessableEntity
                )
            )
        );
        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.TranscribeAsync(
                [1, 2, 3],
                null,
                translate: false,
                prompt: null,
                CancellationToken.None
            )
        );

        Assert.Contains("Mistral API error 422", exception.Message);
        Assert.Contains("Unsupported audio format", exception.Message);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, exception.StatusCode);
    }

    [Fact]
    public async Task TranscribeAsync_PropagatesCancellation()
    {
        var requestStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            requestStarted.SetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return JsonResponse("""{ "text": "unreachable" }""");
        });
        using var sut = await CreateConfiguredPluginAsync(handler);
        using var cancellation = new CancellationTokenSource();

        var transcription = sut.TranscribeAsync(
            [1, 2, 3],
            null,
            translate: false,
            prompt: null,
            cancellation.Token
        );
        // ReSharper disable once MethodSupportsCancellation -- the only token in scope is
        // cancellation.Token, which the test cancels below to exercise the cancellation path;
        // passing it here would abort the wait early. The timeout is the intended guard.
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transcription);
    }

    [Fact]
    public async Task TranscribeAsync_RejectsTranslationBeforeSendingHttpRequest()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse("""{ "text": "unexpected" }"""))
        );
        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.TranscribeAsync(
                [1, 2, 3],
                "en",
                translate: true,
                prompt: null,
                CancellationToken.None
            )
        );

        Assert.Equal(
            "Voxtral does not support translation; Mistral only documents the audio transcriptions endpoint.",
            exception.Message
        );
        Assert.Equal(0, handler.CallCount);
    }

    private static async Task<VoxtralPlugin> CreateConfiguredPluginAsync(
        StubHttpMessageHandler handler
    )
    {
        var sut = new VoxtralPlugin(new HttpClient(handler));
        await sut.ActivateAsync(CreateHostMock(apiKey: "voxtral-key").Object);
        return sut;
    }

    private static string GetPartName(HttpContent content)
    {
        var name = content.Headers.ContentDisposition?.Name;
        Assert.NotNull(name);
        return name.Trim('"');
    }

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK
    ) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static Mock<IPluginHostServices> CreateHostMock(
        string? selectedModelId = null,
        string? apiKey = null
    )
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(service => service.LoadSecretAsync("api-key")).ReturnsAsync(apiKey);
        host.Setup(service => service.GetSetting<string>("selectedModel"))
            .Returns(selectedModelId);
        return host;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder
    ) : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref _callCount);
            return responder(request, cancellationToken);
        }
    }
}
