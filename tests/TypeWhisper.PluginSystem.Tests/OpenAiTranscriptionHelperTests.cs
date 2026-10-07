using System.Globalization;
using System.Net;
using System.Text;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class OpenAiTranscriptionHelperTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task TranscribeAsync_EndpointAndHeadersOverrideDefaults(
        bool translate, bool useOverrides, bool overrideAuthorization)
    {
        var endpoint = new Uri("https://foo.openai.azure.com/deployments/whisper/audio/transcriptions?api-version=2025-03-01-preview");
        var headers = new Dictionary<string, string> { ["api-key"] = "azure-key" };
        if (overrideAuthorization)
            headers["authorization"] = "Basic supplied";
        using var client = new HttpClient(new OverrideHandler(request =>
        {
            Assert.Equal(useOverrides ? endpoint : new Uri("https://example.test/v1/audio/" +
                (translate ? "translations" : "transcriptions")), request.RequestUri);
            Assert.Equal(overrideAuthorization ? "Basic supplied" : "Bearer key",
                Assert.Single(request.Headers.GetValues("Authorization")));
            if (useOverrides)
                Assert.Equal("azure-key", Assert.Single(request.Headers.GetValues("api-key")));
            else
                Assert.False(request.Headers.Contains("api-key"));
        }));
        var result = await OpenAiTranscriptionHelper.TranscribeAsync(client, "https://example.test", "key",
            "whisper", [], null, translate, "json", prompt: null,
            endpointOverride: useOverrides ? endpoint : null, requestHeaders: useOverrides ? headers : null,
            ct: CancellationToken.None);
        Assert.Equal("ok", result.Text);
    }

    private sealed class OverrideHandler(Action<HttpRequestMessage> inspect) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            inspect(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"ok"}"""),
            });
        }
    }

    [Theory]
    [InlineData(true, "de")]
    [InlineData(true, "en")]
    [InlineData(false, "de")]
    public async Task TranscribeAsync_OnlySendsSourceLanguageForTranscription(bool translate, string language)
    {
        var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);

        await OpenAiTranscriptionHelper.TranscribeAsync(
            httpClient, "https://example.test", "test-key", "test-model",
            [1, 2, 3], language, translate, "json", CancellationToken.None
        );

        Assert.EndsWith(
            translate ? "/v1/audio/translations" : "/v1/audio/transcriptions",
            handler.RequestUri?.ToString()
        );
        Assert.NotNull(handler.RequestBody);
        if (translate)
        {
            Assert.DoesNotContain("name=language", handler.RequestBody);
            Assert.DoesNotContain("name=\"language\"", handler.RequestBody);
        }
        else
        {
            Assert.Contains("name=language\r\n\r\nde\r\n", handler.RequestBody);
        }
    }

    [Fact]
    public async Task TranscribeAsync_AutoLanguage_OmitsSourceLanguage()
    {
        var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);

        await OpenAiTranscriptionHelper.TranscribeAsync(
            httpClient, "https://example.test", "test-key", "test-model",
            [1, 2, 3], language: "auto", translate: false, "json", CancellationToken.None
        );

        Assert.EndsWith("/v1/audio/transcriptions", handler.RequestUri?.ToString());
        Assert.NotNull(handler.RequestBody);
        Assert.DoesNotContain("name=language", handler.RequestBody);
        Assert.DoesNotContain("name=\"language\"", handler.RequestBody);
    }

    [Fact]
    public void ParseTranscriptionResponse_MissingText_ThrowsProtocolFailure()
    {
        const string json = """{"language":"en","duration":1.0}""";

        var exception = Assert.Throws<InvalidOperationException>(
            () => OpenAiTranscriptionHelper.ParseTranscriptionResponse(json));

        Assert.Contains("'text'", exception.Message);
        Assert.Contains("Body:", exception.Message);
        Assert.Contains(json, exception.Message);
    }

    [Fact]
    public void ParseTranscriptionResponse_NonStringText_ThrowsProtocolFailure()
    {
        const string json = """{"text":42,"language":"en","duration":1.0}""";

        var exception = Assert.Throws<InvalidOperationException>(
            () => OpenAiTranscriptionHelper.ParseTranscriptionResponse(json));

        Assert.Contains("'text'", exception.Message);
        Assert.Contains("Body:", exception.Message);
        Assert.Contains(json, exception.Message);
    }

    [Fact]
    public async Task TranscribeAsync_SuccessfulErrorObject_SurfacesProviderMessage()
    {
        const string json = """
                            {
                                "error": {
                                    "message": "The audio format is not supported.",
                                    "type": "invalid_request_error"
                                }
                            }
                            """;
        using var httpClient = new HttpClient(new JsonResponseHandler(json));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => OpenAiTranscriptionHelper.TranscribeAsync(
                httpClient,
                "https://example.test",
                "test-key",
                "test-model",
                [],
                null,
                false,
                "json",
                CancellationToken.None
            ));

        Assert.Contains("'text'", exception.Message);
        Assert.Contains("The audio format is not supported.", exception.Message);
        Assert.Contains("Body:", exception.Message);
        Assert.Contains(json.Length > 200 ? json[..200] : json, exception.Message);
    }

    [Fact]
    public async Task TranscribeAsync_TextFormat_RemovesOnlyOneTrailingNewline()
    {
        using var httpClient = new HttpClient(new PlainTextResponseHandler("Plain transcription\n\n"));

        var result = await TranscribeAsync(httpClient, "text");

        Assert.Equal("Plain transcription\n", result.Text);
        Assert.Null(result.DetectedLanguage);
        Assert.Equal(0, result.DurationSeconds);
        Assert.Null(result.NoSpeechProbability);
        Assert.Empty(result.Segments);
    }

    [Fact]
    public async Task TranscribeAsync_TextFormat_EmptyBody_ReturnsSuccessfulEmptyResult()
    {
        using var httpClient = new HttpClient(new PlainTextResponseHandler(""));

        var result = await TranscribeAsync(httpClient, "text");

        Assert.Equal("", result.Text);
    }

    [Fact]
    public async Task TranscribeAsync_TextFormat_JsonLookingBody_RemainsPlainText()
    {
        const string body = """{"text":"JSON value"}""";
        using var httpClient = new HttpClient(new PlainTextResponseHandler(body));

        var result = await TranscribeAsync(httpClient, "text");

        Assert.Equal(body, result.Text);
    }

    [Theory]
    [InlineData("srt")]
    [InlineData("vtt")]
    public async Task TranscribeAsync_SubtitleFormat_ThrowsUnsupportedFormat(string responseFormat)
    {
        using var httpClient = new HttpClient(new UnexpectedRequestHandler());

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => TranscribeAsync(httpClient, responseFormat));

        Assert.Equal("responseFormat", exception.ParamName);
        Assert.Contains(responseFormat, exception.Message);
        Assert.Contains("Supported formats", exception.Message);
    }

    [Fact]
    public async Task TranscribeAsync_UnknownFormat_ThrowsUnsupportedFormat()
    {
        using var httpClient = new HttpClient(new UnexpectedRequestHandler());

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => TranscribeAsync(httpClient, "yaml"));

        Assert.Equal("responseFormat", exception.ParamName);
        Assert.Contains("yaml", exception.Message);
        Assert.Contains("Supported formats", exception.Message);
    }

    [Fact]
    public void ParseTranscriptionResponse_VerboseJson_ExtractsNoSpeechProb()
    {
        const string json = """
                            {
                                "text": "So.",
                                "language": "en",
                                "duration": 2.5,
                                "segments": [
                                    { "text": "So.", "start": 0.0, "end": 0.7, "no_speech_prob": 0.95 }
                                ]
                            }
                            """;

        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse(json);

        Assert.Equal("So.", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.NotNull(result.NoSpeechProbability);
        Assert.True(result.NoSpeechProbability > 0.9f);
        Assert.Single(result.Segments);
        Assert.Equal("So.", result.Segments[0].Text);
        Assert.Equal(0.7, result.Segments[0].End, 0.01);
    }

    [Fact]
    public void ParseTranscriptionResponse_VerboseJson_ReturnsMinNoSpeechProb()
    {
        // Uses min so that mixed speech/silence audio is NOT filtered out
        const string json = """
                            {
                                "text": "Hello world. So.",
                                "language": "en",
                                "duration": 5.0,
                                "segments": [
                                    { "text": "Hello world.", "no_speech_prob": 0.1 },
                                    { "text": "So.", "no_speech_prob": 0.92 }
                                ]
                            }
                            """;

        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse(json);

        Assert.NotNull(result.NoSpeechProbability);
        Assert.Equal(0.1f, result.NoSpeechProbability.Value, 0.01f);
    }

    [Fact]
    public void ParseTranscriptionResponse_AllSegmentsSilence_ReturnsHighProb()
    {
        const string json = """
                            {
                                "text": "So. Vorsicht!",
                                "language": "en",
                                "duration": 3.0,
                                "segments": [
                                    { "text": "So.", "no_speech_prob": 0.95 },
                                    { "text": "Vorsicht!", "no_speech_prob": 0.88 }
                                ]
                            }
                            """;

        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse(json);

        Assert.NotNull(result.NoSpeechProbability);
        Assert.True(result.NoSpeechProbability > 0.8f);
    }

    [Fact]
    public void ParseTranscriptionResponse_JsonFormat_NoSegments_ReturnsNull()
    {
        const string json = """
                            {
                                "text": "Hello world",
                                "language": "en",
                                "duration": 2.0
                            }
                            """;

        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse(json);

        Assert.Equal("Hello world", result.Text);
        Assert.Null(result.NoSpeechProbability);
    }

    [Fact]
    public void ParseTranscriptionResponse_EmptySegments_ReturnsNull()
    {
        const string json = """
                            {
                                "text": "",
                                "language": "en",
                                "duration": 1.0,
                                "segments": []
                            }
                            """;

        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse(json);

        Assert.Null(result.NoSpeechProbability);
    }

    [Fact]
    public void ParseTranscriptionResponse_LowNoSpeechProb_IndicatesSpeech()
    {
        const string json = """
                            {
                                "text": "This is a normal sentence.",
                                "language": "en",
                                "duration": 3.0,
                                "segments": [
                                    { "text": "This is a normal sentence.", "no_speech_prob": 0.02 }
                                ]
                            }
                            """;

        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse(json);

        Assert.NotNull(result.NoSpeechProbability);
        Assert.True(result.NoSpeechProbability < 0.1f);
    }

    [Fact]
    public void ParseTranscriptionResponse_PopulatesPerSegmentNoSpeechProbability()
    {
        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse("""
            {
                "text": "Please send the updated draft. Thank you.",
                "segments": [
                    { "text": " Please send the updated draft.", "start": 0, "end": 5, "no_speech_prob": 0.02 },
                    { "text": " Thank you.", "start": 5, "end": 8, "no_speech_prob": 0.95 }
                ]
            }
            """);

        Assert.Equal("Please send the updated draft. Thank you.", result.Text);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(0.02f, result.Segments[0].NoSpeechProbability);
        Assert.Equal(0.95f, result.Segments[1].NoSpeechProbability);
        Assert.Equal(0.02f, result.NoSpeechProbability);
    }

    [Fact]
    public void ParseTranscriptionResponse_NonNumericNoSpeechProb_IsNull()
    {
        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse("""
            { "text": "Thank you.", "segments": [{ "text": " Thank you.", "no_speech_prob": "high" }] }
            """);

        Assert.Null(Assert.Single(result.Segments).NoSpeechProbability);
    }

    [Fact]
    public void ParseTranscriptionResponse_SegmentWithoutNoSpeechProb_IsNull()
    {
        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse("""
            { "text": "Thank you.", "segments": [{ "text": " Thank you." }] }
            """);

        Assert.Null(Assert.Single(result.Segments).NoSpeechProbability);
    }

    [Fact]
    public void ParseTranscriptionResponse_NumericStrings_MatchNumbers()
    {
        var fromNumbers = OpenAiTranscriptionHelper.ParseTranscriptionResponse("""
            {"text":"Hello there","language":"en","duration":2.49,"segments":[
              {"text":"Hello","start":0,"end":1.25,"no_speech_prob":0.8},
              {"text":" there","start":1.25,"end":2.49,"no_speech_prob":0.05}]}
            """);
        var fromStrings = OpenAiTranscriptionHelper.ParseTranscriptionResponse("""
            {"text":"Hello there","language":"en","duration":"2.49","segments":[
              {"text":"Hello","start":"0.0","end":" 1.25 ","no_speech_prob":"8e-1"},
              {"text":" there","start":"1.25","end":"2.49","no_speech_prob":"0.05"}]}
            """);

        foreach (var result in new[] { fromNumbers, fromStrings })
        {
            Assert.Equal("Hello there", result.Text);
            Assert.Equal(2.49, result.DurationSeconds);
            Assert.Equal(0.05f, result.NoSpeechProbability);
            Assert.Equal(
                [("Hello", 0.0, 1.25, (float?)0.8f), (" there", 1.25, 2.49, 0.05f)],
                result.Segments.Select(s => (s.Text, s.Start, s.End, s.NoSpeechProbability)));
        }
    }

    [Fact]
    public void ParseTranscriptionResponse_NumericStrings_IgnoreCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse("""
                {"text":"Hallo","duration":"2.49","segments":[{"text":"Hallo","start":"0","end":"2,49"}]}
                """);

            Assert.Equal(2.49, result.DurationSeconds);
            Assert.Equal(0, Assert.Single(result.Segments).End);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ParseTranscriptionResponse_MalformedOptionalMetadata_KeepsText()
    {
        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse("""
            {"text":" Hello ","language":[],"duration":"unknown","segments":[null,5,"x",
              {"text":false,"start":{},"end":null,"no_speech_prob":"no"},
              {"text":"Hello","start":1,"end":2,"no_speech_prob":0.2}]}
            """);

        Assert.Equal("Hello", result.Text);
        Assert.Null(result.DetectedLanguage);
        Assert.Equal(0, result.DurationSeconds);
        Assert.Equal(
            [("", 0.0, 0.0, (float?)null), ("Hello", 1.0, 2.0, 0.2f)],
            result.Segments.Select(s => (s.Text, s.Start, s.End, s.NoSpeechProbability)));
        Assert.Null(result.NoSpeechProbability);
    }

    [Theory]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"-Infinity\"")]
    [InlineData("\"1e400\"")]
    [InlineData("1e400")]
    [InlineData("\"\"")]
    [InlineData("true")]
    [InlineData("null")]
    public void ParseTranscriptionResponse_NonFiniteOrNonNumericMetadata_IsIgnored(string value)
    {
        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse($$"""
            {"text":"Hello","duration":{{value}},"segments":[
              {"text":"Hello","start":{{value}},"end":{{value}},"no_speech_prob":{{value}}}]}
            """);

        Assert.Equal("Hello", result.Text);
        Assert.Equal(0, result.DurationSeconds);
        var segment = Assert.Single(result.Segments);
        Assert.Equal((0.0, 0.0), (segment.Start, segment.End));
        Assert.Null(segment.NoSpeechProbability);
        Assert.Null(result.NoSpeechProbability);
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("1.5")]
    [InlineData("\"2\"")]
    public void ParseTranscriptionResponse_OutOfRangeNoSpeechProb_DoesNotImplySilence(string value)
    {
        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse($$"""
            {"text":"Hello","segments":[
              {"text":"Hello","no_speech_prob":{{value}}},
              {"text":"","no_speech_prob":0.9}]}
            """);

        Assert.Null(result.Segments[0].NoSpeechProbability);
        Assert.Equal(0.9f, result.Segments[1].NoSpeechProbability);
        Assert.Null(result.NoSpeechProbability);
    }

    [Theory]
    [InlineData("""{"text":" Hello"}""")]
    [InlineData("""{"text":42,"no_speech_prob":null}""")]
    [InlineData("""{"no_speech_prob":"NaN"}""")]
    public void ParseTranscriptionResponse_UnratedSpokenSegment_DoesNotImplySilence(string unrated)
    {
        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse($$"""
            {"text":"Hello. So.","segments":[{{unrated}},{"text":" So.","no_speech_prob":0.95}]}
            """);

        Assert.Null(result.NoSpeechProbability);
    }

    [Fact]
    public void ParseTranscriptionResponse_UnratedBlankSegment_KeepsSilenceProbability()
    {
        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse("""
            {"text":"So.","segments":[{"text":" "},{"text":" So.","no_speech_prob":0.95}]}
            """);

        Assert.Equal(0.95f, result.NoSpeechProbability);
    }

    [Fact]
    public async Task TranscribeAsync_VerboseJsonWithNumericStrings_ReturnsMetadata()
    {
        using var httpClient = new HttpClient(new JsonResponseHandler("""
            {"text":"Hello","language":"en","duration":"2.49",
             "segments":[{"text":"Hello","start":"0","end":"2.49","no_speech_prob":"0.1"}]}
            """));

        var result = await TranscribeAsync(httpClient, "verbose_json");

        Assert.Equal("Hello", result.Text);
        Assert.Equal(2.49, result.DurationSeconds);
        Assert.Equal(2.49, Assert.Single(result.Segments).End);
        Assert.Equal(0.1f, result.NoSpeechProbability);
    }

    private static Task<PluginTranscriptionResult> TranscribeAsync(
        HttpClient httpClient,
        string responseFormat
    )
    {
        return OpenAiTranscriptionHelper.TranscribeAsync(
            httpClient,
            "https://example.test",
            "test-key",
            "test-model",
            [],
            null,
            false,
            responseFormat,
            CancellationToken.None
        );
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"ok"}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class JsonResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class PlainTextResponseHandler(string text) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(text, Encoding.UTF8, "text/plain"),
            });
        }
    }

    private sealed class UnexpectedRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            throw new InvalidOperationException("The request should fail validation before it is sent.");
        }
    }
}
