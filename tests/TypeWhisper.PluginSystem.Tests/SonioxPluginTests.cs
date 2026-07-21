using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Soniox;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using SonioxSession = TypeWhisper.Plugin.Soniox.SonioxStreamingSession;

// The CapturingHandler lambdas assert on the outgoing 'request' (method, URI,
// headers) and return a canned response. ReSharper reads xUnit asserts as
// precondition checks and concludes 'request' is only validated, never used —
// but asserting on the request is exactly what these tests verify, so the
// inspection is a false positive here.
// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local

namespace TypeWhisper.PluginSystem.Tests;

public class SonioxPluginTests
{
    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifest = LoadManifest();
        var sut = new SonioxPlugin();

        Assert.Equal(manifest.GetProperty("version").GetString(), sut.PluginVersion);
    }

    [Fact]
    public void Manifest_AdvertisesTranscriptionCapabilitiesAndApiKeyRequirement()
    {
        var manifest = LoadManifest();

        Assert.Equal("com.typewhisper.soniox", manifest.GetProperty("id").GetString());
        Assert.Equal("Soniox", manifest.GetProperty("name").GetString());
        Assert.Equal("transcription", manifest.GetProperty("category").GetString());
        Assert.Equal(["transcription"], manifest.GetProperty("categories").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.False(manifest.GetProperty("isLocal").GetBoolean());
        Assert.True(manifest.GetProperty("requiresApiKey").GetBoolean());
    }

    [Fact]
    public async Task ActivateAsync_RestoresApiKeyAndExposesAsyncModel()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "soniox-key" } };

        var sut = new SonioxPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("com.typewhisper.soniox", sut.PluginId);
        Assert.Equal("Soniox", sut.PluginName);
        Assert.Equal("soniox", sut.ProviderId);
        Assert.Equal("Soniox", sut.ProviderDisplayName);
        Assert.True(sut.IsConfigured);
        Assert.False(sut.SupportsTranslation);
        Assert.Equal("default", sut.SelectedModelId);
        Assert.Equal(["default"], sut.TranscriptionModels.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task ActivateAsync_SetsIdentityAndSupportsStreaming()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "soniox-key" } };

        var sut = new SonioxPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("com.typewhisper.soniox", sut.PluginId);
        Assert.Equal("soniox", sut.ProviderId);
        Assert.True(sut.IsConfigured);
        Assert.True(sut.SupportsStreaming);
        Assert.False(sut.SupportsTranslation);
    }

    [Fact]
    public async Task StartStreamingAsync_Throws_WhenNotConfigured()
    {
        var sut = new SonioxPlugin();
        await sut.ActivateAsync(new TestPluginHostServices());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartStreamingAsync(null, CancellationToken.None)
        );
    }

    [Fact]
    public void ParseTranscript_GroupsTokensIntoSubtitleSegments()
    {
        const string transcript = """
                                  {
                                    "text": "The quick brown fox jumps. Over the lazy dog.",
                                    "tokens": [
                                      { "text": "The",    "start_ms": 0,    "end_ms": 400 },
                                      { "text": "quick",  "start_ms": 400,  "end_ms": 800 },
                                      { "text": "brown",  "start_ms": 800,  "end_ms": 1200 },
                                      { "text": "fox",    "start_ms": 1200, "end_ms": 1600 },
                                      { "text": "jumps.", "start_ms": 1600, "end_ms": 2000 },
                                      { "text": "Over",   "start_ms": 3000, "end_ms": 3400 },
                                      { "text": "the",    "start_ms": 3400, "end_ms": 3800 },
                                      { "text": "lazy",   "start_ms": 3800, "end_ms": 4200 },
                                      { "text": "dog.",   "start_ms": 4200, "end_ms": 4600 }
                                    ]
                                  }
                                  """;
        using var details = JsonDocument.Parse("{}");

        var result = SonioxPlugin.ParseTranscript(transcript, details.RootElement, null);

        // A sentence terminator ends the first segment; the >0.75s pause forces the
        // second — so nine word tokens collapse into two subtitle cues, not nine.
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal("The quick brown fox jumps.", result.Segments[0].Text);
        Assert.Equal("Over the lazy dog.", result.Segments[1].Text);
    }

    [Fact]
    public void ParseTranscript_DropsTokensWithNonPositiveDuration()
    {
        const string transcript = """
                                  {
                                    "text": "Hello there",
                                    "tokens": [
                                      { "text": "Hello", "start_ms": 0,    "end_ms": 500 },
                                      { "text": "bad",   "start_ms": 1000, "end_ms": 1000 },
                                      { "text": "there", "start_ms": 1100, "end_ms": 1600 }
                                    ]
                                  }
                                  """;
        using var details = JsonDocument.Parse("{}");

        var result = SonioxPlugin.ParseTranscript(transcript, details.RootElement, null);

        var segment = Assert.Single(result.Segments);
        Assert.DoesNotContain("bad", segment.Text);
        Assert.Equal("Hello there", segment.Text);
    }

    [Fact]
    public void BuildConfigMessage_IncludesRawPcmFormatAndModel()
    {
        var json = SonioxSession.BuildConfigMessage("k-123", SonioxSession.RealtimeModel, null);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("k-123", root.GetProperty("api_key").GetString());
        Assert.Equal("stt-rt-v4", root.GetProperty("model").GetString());
        Assert.Equal("pcm_s16le", root.GetProperty("audio_format").GetString());
        Assert.Equal(16000, root.GetProperty("sample_rate").GetInt32());
        Assert.Equal(1, root.GetProperty("num_channels").GetInt32());
        Assert.True(root.GetProperty("enable_endpoint_detection").GetBoolean());
        // No language → no hints.
        Assert.False(root.TryGetProperty("language_hints", out _));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData(null)]
    public void BuildConfigMessage_OmitsLanguageHints_ForAutoOrEmpty(string? language)
    {
        var json = SonioxSession.BuildConfigMessage("k", SonioxSession.RealtimeModel, language);

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("language_hints", out _));
    }

    [Fact]
    public void BuildConfigMessage_AddsLanguageHints_WhenLanguageGiven()
    {
        var json = SonioxSession.BuildConfigMessage("k", SonioxSession.RealtimeModel, "de");

        using var doc = JsonDocument.Parse(json);
        var hints = doc.RootElement.GetProperty("language_hints");
        Assert.Equal(JsonValueKind.Array, hints.ValueKind);
        Assert.Equal("de", hints[0].GetString());
    }

    [Fact]
    public void ParseMessage_DiscriminatesFinalAndNonFinalTokens()
    {
        var message = SonioxSession.ParseMessage(
            """
            { "tokens": [
                { "text": "Hello", "is_final": true },
                { "text": " world", "is_final": false }
            ] }
            """
        );

        Assert.Null(message.ErrorMessage);
        Assert.False(message.Finished);
        Assert.Equal(2, message.Tokens.Count);
        Assert.Equal(new SonioxSession.SonioxToken("Hello", true), message.Tokens[0]);
        Assert.Equal(new SonioxSession.SonioxToken(" world", false), message.Tokens[1]);
    }

    [Fact]
    public void ParseMessage_DetectsFinished()
    {
        var message = SonioxSession.ParseMessage("""{ "tokens": [], "finished": true }""");

        Assert.True(message.Finished);
        Assert.Empty(message.Tokens);
        Assert.Null(message.ErrorMessage);
    }

    [Fact]
    public void ParseMessage_SurfacesErrorMessage()
    {
        var message = SonioxSession.ParseMessage(
            """{ "tokens": [], "error_code": 503, "error_message": "service unavailable" }"""
        );

        Assert.Equal("service unavailable", message.ErrorMessage);
    }

    [Fact]
    public void ParseMessage_SurfacesError_WhenOnlyCodePresent()
    {
        var message = SonioxSession.ParseMessage("""{ "error_code": 401 }""");

        Assert.NotNull(message.ErrorMessage);
    }

    [Fact]
    public void ParseMessage_ReturnsEmpty_OnMalformedJson()
    {
        var message = SonioxSession.ParseMessage("not json {");

        Assert.Empty(message.Tokens);
        Assert.False(message.Finished);
        Assert.Null(message.ErrorMessage);
    }

    [Fact]
    public void Aggregator_AccumulatesFinals_AndReplacesProvisionalTail()
    {
        var aggregator = new SonioxTranscriptAggregator();

        var first = aggregator.Apply(
            new SonioxSession.SonioxMessage(
                [new SonioxSession.SonioxToken("Hello", true),
                 new SonioxSession.SonioxToken(" wor", false)],
                Finished: false,
                ErrorMessage: null
            )
        );
        Assert.Equal("Hello wor", first.PreviewText);
        Assert.False(first.Finished);

        // Next message: the provisional tail is replaced (not appended), and a new
        // final token is committed.
        var second = aggregator.Apply(
            new SonioxSession.SonioxMessage(
                [new SonioxSession.SonioxToken(" world", true),
                 new SonioxSession.SonioxToken(" how", false)],
                Finished: false,
                ErrorMessage: null
            )
        );
        Assert.Equal("Hello world how", second.PreviewText);
        Assert.Equal("Hello world", second.FinalText);
    }

    [Fact]
    public void Aggregator_ProducesFullTranscript_OnFinished()
    {
        var aggregator = new SonioxTranscriptAggregator();
        aggregator.Apply(Final("Hello"));
        aggregator.Apply(Final(" there"));
        var finished = aggregator.Apply(
            new SonioxSession.SonioxMessage([], Finished: true, ErrorMessage: null)
        );

        Assert.True(finished.Finished);
        Assert.Equal("Hello there", finished.FinalText);
        Assert.Equal("Hello there", aggregator.FinalText);
    }

    [Fact]
    public void Aggregator_SkipsControlTokens()
    {
        var aggregator = new SonioxTranscriptAggregator();
        var update = aggregator.Apply(
            new SonioxSession.SonioxMessage(
                [new SonioxSession.SonioxToken("Hi", true),
                 new SonioxSession.SonioxToken("<end>", true)],
                Finished: false,
                ErrorMessage: null
            )
        );

        Assert.Equal("Hi", update.FinalText);
        Assert.DoesNotContain("<end>", update.PreviewText);
    }

    [Fact]
    public async Task SetApiKeyAsync_NotifiesOnlyWhenConfigurationStateChanges()
    {
        var host = new TestPluginHostServices();
        var sut = new SonioxPlugin();
        await sut.ActivateAsync(host);

        await sut.SetApiKeyAsync(" soniox-key ");
        await sut.SetApiKeyAsync("soniox-key");
        await sut.SetApiKeyAsync("");

        Assert.Equal(2, host.NotifyCapabilitiesChangedCount);
        Assert.False(host.Secrets.ContainsKey("api-key"));
        Assert.False(sut.IsConfigured);
    }

    [Fact]
    public async Task SetApiKeyAsync_DoesNotConfigurePluginWhenStoreSecretFails()
    {
        var host = new TestPluginHostServices
        {
            StoreSecretException = new InvalidOperationException("store failed"),
        };
        var sut = new SonioxPlugin();
        await sut.ActivateAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.SetApiKeyAsync("soniox-key"));

        Assert.False(sut.IsConfigured);
        Assert.Null(sut.ApiKey);
        Assert.False(host.Secrets.ContainsKey("api-key"));
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SetApiKeyAsync_KeepsExistingConfigurationWhenDeleteSecretFails()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "soniox-key" } };
        var sut = new SonioxPlugin();
        await sut.ActivateAsync(host);
        host.DeleteSecretException = new InvalidOperationException("delete failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.SetApiKeyAsync(""));

        Assert.True(sut.IsConfigured);
        Assert.Equal("soniox-key", sut.ApiKey);
        Assert.Equal("soniox-key", host.Secrets["api-key"]);
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task ValidateApiKeyAsync_UsesModelsEndpointAndBearerHeader()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.soniox.com/v1/models", request.RequestUri?.ToString());
            Assert.Equal("Bearer probe-key", request.Headers.Authorization?.ToString());
            return JsonResponse("""{ "models": [] }""");
        });

        using var httpClient = new HttpClient(handler);
        var sut = new SonioxPlugin(httpClient);

        Assert.True(await sut.ValidateApiKeyAsync(" probe-key "));
    }

    [Fact]
    public async Task TranscribeAsync_UsesAsyncTranscriptionFlowAndCleansUp()
    {
        var seen = new List<string>();
        var handler = new CapturingHandler((request, body) =>
        {
            seen.Add($"{request.Method} {request.RequestUri}");
            Assert.Equal("Bearer soniox-key", request.Headers.Authorization?.ToString());

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/files")
            {
                Assert.StartsWith("multipart/form-data", request.Content?.Headers.ContentType?.MediaType);
                Assert.NotNull(body);
                var multipartBody = Encoding.UTF8.GetString(body);
                Assert.Contains("name=file", multipartBody);
                Assert.Contains("filename=audio.wav", multipartBody);
                return JsonResponse("""{ "id": "84c32fc6-4fb5-4e7a-b656-b5ec70493753", "filename": "audio.wav", "size": 3 }""", HttpStatusCode.Created);
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/transcriptions")
            {
                using var doc = JsonDocument.Parse(body ?? throw new InvalidOperationException("Missing body"));
                var root = doc.RootElement;
                Assert.Equal("stt-async-v4", root.GetProperty("model").GetString());
                Assert.Equal("84c32fc6-4fb5-4e7a-b656-b5ec70493753", root.GetProperty("file_id").GetString());
                Assert.Equal(["de"], root.GetProperty("language_hints").EnumerateArray().Select(e => e.GetString()!).ToArray());
                return JsonResponse("""{ "id": "73d4357d-cad2-4338-a60d-ec6f2044f721", "status": "queued" }""", HttpStatusCode.Created);
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/transcriptions/73d4357d-cad2-4338-a60d-ec6f2044f721")
            {
                var pollCount = seen.Count(item => item == "GET https://api.soniox.com/v1/transcriptions/73d4357d-cad2-4338-a60d-ec6f2044f721");
                return pollCount == 1
                    ? JsonResponse("""{ "id": "73d4357d-cad2-4338-a60d-ec6f2044f721", "status": "processing", "audio_duration_ms": 660000 }""")
                    : JsonResponse("""{ "id": "73d4357d-cad2-4338-a60d-ec6f2044f721", "status": "completed", "audio_duration_ms": 660000 }""");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/transcriptions/73d4357d-cad2-4338-a60d-ec6f2044f721/transcript")
            {
                return JsonResponse("""
                    {
                      "id": "73d4357d-cad2-4338-a60d-ec6f2044f721",
                      "text": "Hallo Welt",
                      "tokens": [
                        { "text": "Hallo", "start_ms": 0, "end_ms": 500, "language": "de" },
                        { "text": " ", "start_ms": 500, "end_ms": 520, "language": "de" },
                        { "text": "Welt", "start_ms": 520, "end_ms": 1100, "language": "de" }
                      ]
                    }
                    """);
            }

            return request.Method == HttpMethod.Delete ? JsonResponse("{}") 
                : throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "soniox-key" } };
        using var httpClient = new HttpClient(handler);
        var sut = new SonioxPlugin(httpClient, pollDelay: TimeSpan.Zero, maxPollAttempts: 3);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync([1, 2, 3], "de", translate: false, prompt: null, CancellationToken.None);

        Assert.Equal("Hallo Welt", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(660.0, result.DurationSeconds);
        // Tokens are now grouped into subtitle-sized segments rather than one cue per token.
        Assert.Equal(["Hallo Welt"], result.Segments.Select(s => s.Text).ToArray());
        Assert.Contains("DELETE https://api.soniox.com/v1/transcriptions/73d4357d-cad2-4338-a60d-ec6f2044f721", seen);
        Assert.Contains("DELETE https://api.soniox.com/v1/files/84c32fc6-4fb5-4e7a-b656-b5ec70493753", seen);
    }

    [Fact]
    public async Task TranscribeAsync_UsesInitialApiKeyForWholeAsyncFlow()
    {
        var seenAuthorizations = new List<string?>();
        SonioxPlugin? sut = null;
        var handler = new AsyncCapturingHandler(async (request, _, _) =>
        {
            seenAuthorizations.Add(request.Headers.Authorization?.ToString());

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/files")
            {
                // The handler is wired before `sut` is constructed below, so capturing the
                // mutable local is intentional: this clears the key mid-flow to prove the
                // async transcription pipeline keeps using the key captured at start.
                // ReSharper disable once AccessToModifiedClosure
                await sut!.SetApiKeyAsync("");
                return JsonResponse("""{ "id": "84c32fc6-4fb5-4e7a-b656-b5ec70493753" }""", HttpStatusCode.Created);
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/transcriptions")
                return JsonResponse("""{ "id": "73d4357d-cad2-4338-a60d-ec6f2044f721", "status": "queued" }""", HttpStatusCode.Created);

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/transcriptions/73d4357d-cad2-4338-a60d-ec6f2044f721")
                return JsonResponse("""{ "status": "completed", "audio_duration_ms": 1000 }""");

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/transcriptions/73d4357d-cad2-4338-a60d-ec6f2044f721/transcript")
                return JsonResponse("""{ "text": "Hello", "tokens": [] }""");

            return request.Method == HttpMethod.Delete ? JsonResponse("{}")
                : throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "initial-key" } };
        using var httpClient = new HttpClient(handler);
        sut = new SonioxPlugin(httpClient, pollDelay: TimeSpan.Zero, maxPollAttempts: 2);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync([1, 2, 3], "en", translate: false, prompt: null, CancellationToken.None);

        Assert.Equal("Hello", result.Text);
        Assert.False(sut.IsConfigured);
        Assert.DoesNotContain("api-key", host.Secrets.Keys);
        Assert.All(seenAuthorizations, authorization => Assert.Equal("Bearer initial-key", authorization));
    }

    [Fact]
    public async Task TranscribeAsync_OmitsLanguageHintsForAuto()
    {
        var handler = new SonioxFlowHandler(createBody =>
        {
            using var doc = JsonDocument.Parse(createBody);
            Assert.False(doc.RootElement.TryGetProperty("language_hints", out _));
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "soniox-key" } };
        using var httpClient = new HttpClient(handler);
        var sut = new SonioxPlugin(httpClient, pollDelay: TimeSpan.Zero, maxPollAttempts: 2);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync([1, 2, 3], "auto", translate: false, prompt: null, CancellationToken.None);

        Assert.Equal("Hello", result.Text);
    }

    [Fact]
    public async Task TranscribeAsync_OmitsLanguageHintsForWhitespacePaddedAuto()
    {
        var handler = new SonioxFlowHandler(createBody =>
        {
            using var doc = JsonDocument.Parse(createBody);
            Assert.False(doc.RootElement.TryGetProperty("language_hints", out _));
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "soniox-key" } };
        using var httpClient = new HttpClient(handler);
        var sut = new SonioxPlugin(httpClient, pollDelay: TimeSpan.Zero, maxPollAttempts: 2);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync([1, 2, 3], " auto ", translate: false, prompt: null, CancellationToken.None);

        Assert.Equal("Hello", result.Text);
        Assert.Null(result.DetectedLanguage);
    }

    [Fact]
    public async Task TranscribeAsync_RejectsTranslation()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "soniox-key" } };
        var sut = new SonioxPlugin();
        await sut.ActivateAsync(host);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.TranscribeAsync([1, 2, 3], "en", translate: true, prompt: null, CancellationToken.None));

        Assert.Contains("does not support translation", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_StatusErrorIncludesSonioxDetailsAndCleansUp()
    {
        var seen = new List<string>();
        var handler = new CapturingHandler((request, body) =>
        {
            seen.Add($"{request.Method} {request.RequestUri}");

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/files")
                return JsonResponse("""{ "id": "84c32fc6-4fb5-4e7a-b656-b5ec70493753" }""", HttpStatusCode.Created);

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/transcriptions")
                return JsonResponse("""{ "id": "73d4357d-cad2-4338-a60d-ec6f2044f721", "status": "queued" }""", HttpStatusCode.Created);

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/transcriptions/73d4357d-cad2-4338-a60d-ec6f2044f721")
            {
                return JsonResponse("""
                    {
                      "status": "error",
                      "error_type": "invalid_audio",
                      "error_message": "Cannot decode audio",
                      "request_id": "req-1"
                    }
                    """);
            }

            return request.Method == HttpMethod.Delete ? JsonResponse("{}") 
                : throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}; body={Encoding.UTF8.GetString(body ?? [])}");
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "soniox-key" } };
        using var httpClient = new HttpClient(handler);
        var sut = new SonioxPlugin(httpClient, pollDelay: TimeSpan.Zero, maxPollAttempts: 2);
        await sut.ActivateAsync(host);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.TranscribeAsync([1, 2, 3], "en", translate: false, prompt: null, CancellationToken.None));

        Assert.Contains("invalid_audio", ex.Message);
        Assert.Contains("Cannot decode audio", ex.Message);
        Assert.Contains("req-1", ex.Message);
        Assert.Contains("DELETE https://api.soniox.com/v1/transcriptions/73d4357d-cad2-4338-a60d-ec6f2044f721", seen);
        Assert.Contains("DELETE https://api.soniox.com/v1/files/84c32fc6-4fb5-4e7a-b656-b5ec70493753", seen);
    }

    [Fact]
    public async Task TranscribeAsync_HttpErrorIncludesSonioxDetails()
    {
        var handler = new CapturingHandler((_, _) =>
            JsonResponse("""
                {
                  "status_code": 401,
                  "error_type": "unauthenticated",
                  "message": "Incorrect API key",
                  "request_id": "req-unauth"
                }
                """, HttpStatusCode.Unauthorized));

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "soniox-key" } };
        using var httpClient = new HttpClient(handler);
        var sut = new SonioxPlugin(httpClient);
        await sut.ActivateAsync(host);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            sut.TranscribeAsync([1, 2, 3], "en", translate: false, prompt: null, CancellationToken.None));

        Assert.Contains("unauthenticated", ex.Message);
        Assert.Contains("Incorrect API key", ex.Message);
        Assert.Contains("req-unauth", ex.Message);
    }

    [Fact]
    public async Task TranscribeAsync_PollTimeoutThrowsTimeoutException()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/files")
                return JsonResponse("""{ "id": "84c32fc6-4fb5-4e7a-b656-b5ec70493753" }""", HttpStatusCode.Created);

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/transcriptions")
                return JsonResponse("""{ "id": "73d4357d-cad2-4338-a60d-ec6f2044f721", "status": "queued" }""", HttpStatusCode.Created);

            return JsonResponse(request.Method == HttpMethod.Get ? """{ "status": "processing" }""" : "{}");
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "soniox-key" } };
        using var httpClient = new HttpClient(handler);
        var sut = new SonioxPlugin(httpClient, pollDelay: TimeSpan.Zero, maxPollAttempts: 2);
        await sut.ActivateAsync(host);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            sut.TranscribeAsync([1, 2, 3], "en", translate: false, prompt: null, CancellationToken.None));

        Assert.Contains("did not complete", ex.Message);
    }

    private static SonioxSession.SonioxMessage Final(string text) =>
        new([new SonioxSession.SonioxToken(text, true)], Finished: false, ErrorMessage: null);

    private static JsonElement LoadManifest()
    {
        var basePath = Path.GetFullPath(AppContext.BaseDirectory);
        var relativeManifestPath = Path.Join(
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.Soniox", "manifest.json");
        var manifestPath = Path.GetFullPath(relativeManifestPath, basePath);
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return doc.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class SonioxFlowHandler(Action<string> inspectCreateBody) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/files")
                return JsonResponse("""{ "id": "84c32fc6-4fb5-4e7a-b656-b5ec70493753" }""", HttpStatusCode.Created);

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/transcriptions")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                inspectCreateBody(body);
                return JsonResponse("""{ "id": "73d4357d-cad2-4338-a60d-ec6f2044f721", "status": "queued" }""", HttpStatusCode.Created);
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/transcriptions/73d4357d-cad2-4338-a60d-ec6f2044f721")
                return JsonResponse("""{ "status": "completed", "audio_duration_ms": 1000 }""");

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/transcriptions/73d4357d-cad2-4338-a60d-ec6f2044f721/transcript")
                return JsonResponse("""{ "text": "Hello", "tokens": [] }""");

            return request.Method == HttpMethod.Delete ? JsonResponse("{}") 
                : throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        }
    }

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, byte[]?, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return responder(request, body);
        }
    }

    private sealed class AsyncCapturingHandler(
        Func<HttpRequestMessage, byte[]?, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return await responder(request, body, cancellationToken);
        }
    }

    private sealed class TestPluginHostServices : IPluginHostServices
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly Dictionary<string, JsonElement> _settings = [];
        public Dictionary<string, string?> Secrets { get; } = [];
        public int NotifyCapabilitiesChangedCount { get; private set; }
        public Exception? StoreSecretException { get; init; }
        public Exception? DeleteSecretException { get; set; }

        public Task StoreSecretAsync(string key, string value)
        {
            if (StoreSecretException is not null)
                throw StoreSecretException;

            Secrets[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadSecretAsync(string key) =>
            Task.FromResult(Secrets.GetValueOrDefault(key));

        public Task DeleteSecretAsync(string key)
        {
            if (DeleteSecretException is not null)
                throw DeleteSecretException;

            Secrets.Remove(key);
            return Task.CompletedTask;
        }

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value)
                ? value.Deserialize<T>(s_jsonOptions)
                : default;

        public void SetSetting<T>(string key, T value) =>
            _settings[key] = JsonSerializer.SerializeToElement(value, s_jsonOptions);

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() => NotifyCapabilitiesChangedCount++;
        public IPluginLocalization Localization { get; } = new TestPluginLocalization();
    }

    private sealed class TestPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(key, args);
    }

    private sealed class TestPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }

        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent =>
            new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
