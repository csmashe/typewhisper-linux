using System.Net;
using System.Text;
using Moq;
using TypeWhisper.Plugin.Voxtral;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class VoxtralPluginTests
{
    private const string MiniTranscribe = "voxtral-mini-transcribe-3-0";
    private const string SmallTranscribe = "voxtral-small-transcribe-3-0";

    [Fact]
    public Task RequestFailures_AreClassified() =>
        ProviderFailureAssertions.VerifyAsync<VoxtralPlugin>(alsoTranscription: true);


    [Fact]
    public async Task ActivateAsync_UsesMiniTranscribe3AndDisablesTranslation()
    {
        var host = CreateHostMock();

        using var sut = new VoxtralPlugin();
        await sut.ActivateAsync(host.Object);

        Assert.Equal(MiniTranscribe, sut.SelectedModelId);
        Assert.False(sut.SupportsTranslation);
        host.Verify(service => service.SetSetting("selectedModel", It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void TranscriptionModels_OfferMiniAndSmallTranscribe3()
    {
        using var sut = new VoxtralPlugin();

        Assert.Equal(
            [(MiniTranscribe, "Voxtral Mini Transcribe 3"), (SmallTranscribe, "Voxtral Small Transcribe 3")],
            sut.TranscriptionModels.Select(model => (model.Id, model.DisplayName)).ToArray()
        );
    }

    [Fact]
    public void Capabilities_TakeStructuredDictionaryTermsAndTheTranscribe3Languages()
    {
        using var sut = new VoxtralPlugin();

        Assert.True(sut.SupportsStructuredDictionaryTerms);
        Assert.Equal(new DictionaryTermsBudget(MaxTerms: 200, MaxCharsPerTerm: 200, MaxTotalChars: 8000), sut.DictionaryTermsBudget);
        Assert.Equal(58, sut.SupportedLanguages.Count);
        Assert.Contains("de", sut.SupportedLanguages);
        Assert.Contains("mt", sut.SupportedLanguages);
    }

    [Theory]
    [InlineData("mistral-whisper", MiniTranscribe)]
    [InlineData("voxtral-mini-latest", MiniTranscribe)]
    [InlineData("voxtral-mini-2602", MiniTranscribe)]
    [InlineData("voxtral-small-transcribe-3", SmallTranscribe)]
    [InlineData("retired-unknown-model", MiniTranscribe)]
    public async Task ActivateAsync_MigratesEarlierModelSelection(string saved, string expected)
    {
        var host = CreateHostMock(saved);

        using var sut = new VoxtralPlugin();
        await sut.ActivateAsync(host.Object);

        Assert.Equal(expected, sut.SelectedModelId);
        host.Verify(service => service.SetSetting("selectedModel", expected), Times.Once);
    }

    [Fact]
    public async Task ActivateAsync_KeepsSavedSmallTranscribe3()
    {
        var host = CreateHostMock(SmallTranscribe);

        using var sut = new VoxtralPlugin();
        await sut.ActivateAsync(host.Object);

        Assert.Equal(SmallTranscribe, sut.SelectedModelId);
        host.Verify(service => service.SetSetting("selectedModel", It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SelectModel_NormalizesEarlierIdsAndRejectsUnknownOnes()
    {
        var host = CreateHostMock();

        using var sut = new VoxtralPlugin();
        await sut.ActivateAsync(host.Object);

        sut.SelectModel("mistral-whisper");
        Assert.Equal(MiniTranscribe, sut.SelectedModelId);
        sut.SelectModel(SmallTranscribe);
        Assert.Equal(SmallTranscribe, sut.SelectedModelId);
        Assert.Throws<ArgumentException>(() => sut.SelectModel("whisper-1"));
        Assert.Equal(SmallTranscribe, sut.SelectedModelId);
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
                ["file", "model", "timestamp_granularities", "return_language", "context_bias"],
                parts.Select(GetPartName).ToArray()
            );

            var file = Assert.Single(parts, part => GetPartName(part) == "file");
            Assert.Equal("audio.wav", file.Headers.ContentDisposition?.FileName?.Trim('"'));
            Assert.Equal("audio/wav", file.Headers.ContentType?.MediaType);
            Assert.Equal([1, 2, 3], await file.ReadAsByteArrayAsync(ct));

            var model = Assert.Single(parts, part => GetPartName(part) == "model");
            Assert.Equal(MiniTranscribe, await model.ReadAsStringAsync(ct));

            var granularities = Assert.Single(
                parts,
                part => GetPartName(part) == "timestamp_granularities"
            );
            Assert.Equal("segment", await granularities.ReadAsStringAsync(ct));
            var returnLanguage = Assert.Single(parts, part => GetPartName(part) == "return_language");
            Assert.Equal("true", await returnLanguage.ReadAsStringAsync(ct));
            // An API caller's free prompt text is context, sent whole.
            var context = Assert.Single(parts, part => GetPartName(part) == "context_bias");
            Assert.Equal("Meeting about Quillara, Northwind", await context.ReadAsStringAsync(ct));

            return JsonResponse("""{ "text": "Hello", "language": "en", "usage": {} }""");
        });
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.TranscribeAsync(
            [1, 2, 3],
            null,
            translate: false,
            prompt: "  Meeting about Quillara, Northwind ",
            CancellationToken.None
        );

        Assert.Equal("Hello", result.Text);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task TranscribeAsync_SendsExplicitLanguageWithoutTimestampsOrReturnLanguage()
    {
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            var content = Assert.IsType<MultipartFormDataContent>(request.Content);
            var parts = content.ToArray();
            Assert.Equal(
                ["file", "model", "language"],
                parts.Select(GetPartName).ToArray()
            );
            var language = Assert.Single(parts, part => GetPartName(part) == "language");
            Assert.Equal("de", await language.ReadAsStringAsync(ct));

            return JsonResponse("""{ "text": "Hallo", "language": "de", "usage": {} }""");
        });
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.TranscribeAsync(
            [1, 2, 3],
            "de-DE",
            translate: false,
            prompt: null,
            CancellationToken.None
        );

        Assert.Equal("de", result.DetectedLanguage);
    }

    [Fact]
    public async Task TranscribeAsync_DetectsALanguageTranscribe3DoesNotList()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var parts = Assert.IsType<MultipartFormDataContent>(request.Content).ToArray();
            Assert.Equal(
                ["file", "model", "timestamp_granularities", "return_language"],
                parts.Select(GetPartName).ToArray()
            );
            return Task.FromResult(JsonResponse("""{ "text": "Kia ora", "language": "mi" }"""));
        });
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.TranscribeAsync([1, 2, 3], "mi", translate: false, prompt: null, CancellationToken.None);

        Assert.Equal("mi", result.DetectedLanguage);
    }

    [Fact]
    public async Task TranscribeAsync_SendsEachDictionaryTermAsItsOwnContextBiasField()
    {
        var sent = new List<string>();
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            var parts = Assert.IsType<MultipartFormDataContent>(request.Content).ToArray();
            foreach (var part in parts.Where(part => GetPartName(part) == "context_bias"))
                sent.Add(await part.ReadAsStringAsync(ct));
            var model = Assert.Single(parts, part => GetPartName(part) == "model");
            Assert.Equal(SmallTranscribe, await model.ReadAsStringAsync(ct));
            return JsonResponse("""{ "text": "ok" }""");
        });
        using var sut = await CreateConfiguredPluginAsync(handler);
        sut.SelectModel(SmallTranscribe);
        var terms = new[] { "Saoirse Ronan", "Washington, D.C.", "saoirse ronan", new string('x', 201) }
            .Concat(Enumerable.Range(0, 250).Select(i => $"term{i}"));

        await sut.TranscribeAsync(
            [1, 2, 3],
            "en",
            translate: false,
            PluginTranscriptionPrompt.Encode(null, terms),
            CancellationToken.None
        );

        Assert.Equal(200, sent.Count);
        Assert.Equal(["Saoirse Ronan", "Washington, D.C.", "term0"], sent.Take(3));
        Assert.DoesNotContain(sent, term => term.Length > 200);
    }

    [Fact]
    public async Task TranscribeAsync_ExplicitLanguageTakesDurationFromUsageWithoutSegments()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse(
                """{ "text": "Hallo", "language": null, "segments": [], "usage": { "prompt_audio_seconds": 4.5 } }"""
            ))
        );
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.TranscribeAsync(
            [1, 2, 3],
            "de",
            translate: false,
            prompt: null,
            CancellationToken.None
        );

        Assert.Equal("Hallo", result.Text);
        Assert.Null(result.DetectedLanguage);
        Assert.Equal(4.5, result.DurationSeconds);
        Assert.Empty(result.Segments);
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

        var exception = await Assert.ThrowsAsync<PluginRequestException>(
            () => sut.TranscribeAsync(
                [1, 2, 3],
                null,
                translate: false,
                prompt: null,
                CancellationToken.None
            )
        );

        Assert.Equal("Mistral API error 422: Unsupported audio format", exception.Message);
        Assert.Equal((int)HttpStatusCode.UnprocessableEntity, exception.HttpStatusCode);
        Assert.Equal(PluginRequestFailureKind.InvalidRequest, exception.FailureKind);
    }

    [Theory]
    [InlineData(
        """{"object":"error","message":{"detail":[{"type":"assertion_error","loc":["body","language"],"msg":"Assertion failed, Cannot do language prediction if language is already set","input":"private-input"}]},"type":"invalid_request_error"}""",
        "Cannot do language prediction if language is already set")]
    [InlineData(
        """{"detail":[{"loc":["body","model"],"msg":"Field required","input":{"file":"private-input"}},{"msg":"Bad language"}]}""",
        "Field required; Bad language")]
    [InlineData("""{"object":"error","message":"Invalid model: voxtral-x","type":"invalid_model"}""", "Invalid model: voxtral-x")]
    [InlineData("""{"error":{"message":"Already rewritten"}}""", "Already rewritten")]
    public async Task TranscribeAsync_ReducesMistralErrorBodiesToTheirMessage(string body, string expected)
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse(body, HttpStatusCode.UnprocessableEntity)));
        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<PluginRequestException>(
            () => sut.TranscribeAsync([1, 2, 3], null, translate: false, prompt: null, CancellationToken.None));

        Assert.Equal($"Mistral API error 422: {expected}", exception.Message);
        Assert.DoesNotContain("private-input", exception.Message);
    }

    [Fact]
    public void ErrorHandler_KeepsABodyWithoutAMessage()
    {
        Assert.Null(MistralErrorHandler.Message("not json"));
        Assert.Null(MistralErrorHandler.Message("""{"detail":[]}"""));
        // Left whole so the SDK redacts a key before it caps the length.
        Assert.Equal(400, MistralErrorHandler.Message($$"""{"message":"{{new string('m', 400)}}"}""")!.Length);
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
