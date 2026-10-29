using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Gemini;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class GeminiTranscriptionTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task TranscriptionRole_AdvertisesTranscribeModelWithLiveStreaming()
    {
        var host = new GeminiTestHost { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);

        Assert.IsType<ITranscriptionEnginePlugin>(sut, exactMatch: false);
        Assert.Equal("gemini", sut.ProviderId);
        Assert.Equal(
            [GeminiPlugin.DefaultTranscriptionModel],
            sut.TranscriptionModels.Select(model => model.Id).ToArray());
        Assert.Equal("Gemini 3.5 Transcribe", sut.TranscriptionModels[0].DisplayName);
        Assert.True(sut.TranscriptionModels[0].IsRecommended);
        Assert.Equal(GeminiPlugin.DefaultTranscriptionModel, sut.SelectedModelId);
        Assert.True(sut.IsConfigured);
        Assert.True(sut.SupportsStreaming);
        Assert.True(sut.SupportsLanguageHints);
        Assert.False(sut.SupportsTranslation);
        Assert.Equal(LanguageSelectionSupport.Supported, sut.AutomaticDetectionSupport);
        Assert.Equal(LanguageSelectionSupport.Supported, sut.ExplicitSelectionSupport);
    }

    [Fact]
    public void SupportsStreaming_IsFalseWithoutApiKey()
    {
        using var sut = new GeminiPlugin();

        Assert.False(sut.IsConfigured);
        Assert.False(sut.SupportsStreaming);
    }

    [Fact]
    public async Task FetchedTranscriptionCatalog_DoesNotInventUnavailableLiveSibling()
    {
        var host = new GeminiTestHost { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);

        Assert.True(await sut.SetFetchedTranscriptionModelsAsync(
            [new GeminiFetchedTranscriptionModel("gemini-3.5-transcribe", "Gemini 3.5 Transcribe", null)]));

        Assert.False(sut.SupportsStreaming);
        await Assert.ThrowsAsync<NotSupportedException>(
            () => sut.StartStreamingAsync("en", CancellationToken.None));
    }

    [Theory]
    [InlineData("models/gemini-3.5-transcribe", true)]
    [InlineData("gemini-3.5-transcribe-preview", true)]
    [InlineData("gemini-3.5-transcribe-live", false)]
    [InlineData("gemini-3.7-flash", false)]
    [InlineData("gemma-3-transcribe", false)]
    [InlineData("", false)]
    public void IsCompatibleTranscriptionModelId_FiltersCatalog(string modelId, bool expected)
    {
        Assert.Equal(expected, GeminiPlugin.IsCompatibleTranscriptionModelId(modelId));
    }

    [Fact]
    public async Task FetchTranscriptionModelsAsync_UsesNativeEndpointAndResolvesLiveSibling()
    {
        HttpRequestMessage? capturedRequest = null;
        using var client = new HttpClient(new GeminiCapturingHandler((request, _) =>
        {
            capturedRequest = request;
            return JsonResponse("""
                {
                  "models": [
                    null,
                    { "name": null, "displayName": "Missing ID" },
                    { "name": "models/gemini-3.7-flash", "baseModelId": "gemini-3.7-flash" },
                    { "name": "models/gemini-3.5-transcribe", "baseModelId": "gemini-3.5-transcribe", "displayName": "Gemini 3.5 Transcribe" },
                    { "name": "models/gemini-3.5-transcribe", "displayName": "Duplicate" },
                    { "name": "models/gemini-3.5-transcribe-live", "displayName": "Gemini 3.5 Transcribe Live" }
                  ]
                }
                """);
        }));
        var host = new GeminiTestHost { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin(client);
        await sut.ActivateAsync(host);

        var models = await sut.FetchTranscriptionModelsAsync();

        var model = Assert.Single(models!);
        Assert.Equal("gemini-3.5-transcribe", model.Id);
        Assert.Equal("Gemini 3.5 Transcribe", model.DisplayName);
        Assert.Equal("gemini-3.5-transcribe-live", model.LiveModelId);
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000",
            capturedRequest?.RequestUri?.ToString());
        Assert.Null(capturedRequest?.Headers.Authorization);
        Assert.Equal("gemini-key", Assert.Single(capturedRequest!.Headers.GetValues("x-goog-api-key")));
    }

    [Fact]
    public async Task FetchTranscriptionModelsAsync_FollowsNativePagination()
    {
        List<string> requestedUris = [];
        using var client = new HttpClient(new GeminiCapturingHandler((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            requestedUris.Add(uri);
            return uri.Contains("pageToken=second%2Bpage", StringComparison.Ordinal)
                ? JsonResponse("""{"models":[{"name":"models/gemini-3.5-transcribe-live"}]}""")
                : JsonResponse("""
                    {
                      "models":[{"name":"models/gemini-3.5-transcribe"}],
                      "nextPageToken":"second+page"
                    }
                    """);
        }));
        var host = new GeminiTestHost { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin(client);
        await sut.ActivateAsync(host);

        var models = await sut.FetchTranscriptionModelsAsync();

        Assert.Equal(2, requestedUris.Count);
        Assert.Contains("pageToken=second%2Bpage", requestedUris[1], StringComparison.Ordinal);
        Assert.Equal("gemini-3.5-transcribe-live", Assert.Single(models!).LiveModelId);
    }

    [Fact]
    public async Task FetchTranscriptionModelsAsync_StopsWhenPageTokenRepeats()
    {
        var requestCount = 0;
        using var client = new HttpClient(new GeminiCapturingHandler((_, _) =>
        {
            requestCount++;
            return JsonResponse("""
                {
                  "models":[{"name":"models/gemini-3.5-transcribe"}],
                  "nextPageToken":"repeated-page"
                }
                """);
        }));
        var host = new GeminiTestHost { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin(client);
        await sut.ActivateAsync(host);

        var models = await sut.FetchTranscriptionModelsAsync();

        Assert.NotNull(models);
        Assert.Equal(2, requestCount);
        Assert.Contains(host.Logs, entry =>
            entry.Level == PluginLogLevel.Warning
            && entry.Message.Contains("repeated page token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchTranscriptionModelsAsync_DiscardsCatalogWhenApiKeyChangesInFlight()
    {
        var handler = new GeminiBlockingHandler(JsonResponse(
            """{"models":[{"name":"models/gemini-3.5-transcribe"}]}"""));
        using var client = new HttpClient(handler);
        var host = new GeminiTestHost { Secrets = { ["api-key"] = "first-key" } };
        using var sut = new GeminiPlugin(client);
        await sut.ActivateAsync(host);

        var fetch = sut.FetchTranscriptionModelsAsync();
        await handler.Started.Task.WaitAsync(s_timeout);
        await sut.SetApiKeyAsync("second-key");
        handler.Release();

        Assert.Null(await fetch);
        Assert.Empty(sut.FetchedTranscriptionModels);
    }

    [Fact]
    public async Task TranscriptionModels_PreferNonPreviewNewestVersionAsDefault()
    {
        var host = new GeminiTestHost { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);

        await sut.SetFetchedTranscriptionModelsAsync(
        [
            new GeminiFetchedTranscriptionModel("gemini-3.9-transcribe-preview", null, null),
            new GeminiFetchedTranscriptionModel("gemini-3.6-transcribe", null, "gemini-3.6-transcribe-live"),
            new GeminiFetchedTranscriptionModel("gemini-3.7-transcribe", null, null),
        ]);

        Assert.Equal(
            ["gemini-3.7-transcribe", "gemini-3.6-transcribe", "gemini-3.9-transcribe-preview"],
            sut.TranscriptionModels.Select(model => model.Id).ToArray());
        Assert.True(sut.TranscriptionModels[0].IsRecommended);
        Assert.Equal("gemini-3.7-transcribe", sut.SelectedModelId);
        Assert.Equal("gemini-3.7-transcribe", host.GetSetting<string>("selectedTranscriptionModel"));
        Assert.Equal("Gemini 3.7 Transcribe", sut.TranscriptionModels[0].DisplayName);
    }

    [Fact]
    public async Task SelectModel_RejectsUnknownModelButSettingsRepairSelection()
    {
        var host = new GeminiTestHost { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);

        Assert.Throws<ArgumentException>(() => sut.SelectModel("gemini-9-transcribe"));

        await sut.SetSettingValueAsync("selectedTranscriptionModel", "gemini-9-transcribe");

        Assert.Equal(GeminiPlugin.DefaultTranscriptionModel, sut.SelectedModelId);
        Assert.Equal(
            GeminiPlugin.DefaultTranscriptionModel,
            await sut.GetSettingValueAsync("selectedTranscriptionModel"));
    }

    [Fact]
    public async Task TranscriptionSettings_RoundTripModelAndMode()
    {
        var host = new GeminiTestHost { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);

        var modelSetting = sut.GetSettingDefinitions()
            .Single(setting => setting.Key == "selectedTranscriptionModel");
        var modeSetting = sut.GetSettingDefinitions()
            .Single(setting => setting.Key == "transcriptionMode");
        Assert.Equal(PluginSettingKind.Dropdown, modelSetting.Kind);
        Assert.Equal("Settings.TranscriptionModelFallback", modelSetting.Description);
        Assert.Equal(
            [GeminiPlugin.DefaultTranscriptionModel],
            modelSetting.Options!.Select(option => option.Value).ToArray());
        Assert.Equal(["smart", "verbatim"], modeSetting.Options!.Select(option => option.Value).ToArray());
        Assert.Equal("smart", await sut.GetSettingValueAsync("transcriptionMode"));

        await sut.SetSettingValueAsync("transcriptionMode", "verbatim");

        Assert.Equal(GeminiTranscriptionMode.Verbatim, sut.TranscriptionMode);
        Assert.Equal("verbatim", host.GetSetting<string>("transcriptionMode"));
        using var restored = new GeminiPlugin();
        await restored.ActivateAsync(host);
        Assert.Equal(GeminiTranscriptionMode.Verbatim, restored.TranscriptionMode);

        await sut.SetFetchedTranscriptionModelsAsync(
            [new GeminiFetchedTranscriptionModel("gemini-3.5-transcribe", "Gemini 3.5 Transcribe", null)]);
        Assert.Equal(
            "Settings.TranscriptionModelsFetched: 1",
            sut.GetSettingDefinitions()
                .Single(setting => setting.Key == "selectedTranscriptionModel")
                .Description);
    }

    [Fact]
    public async Task Transcription_WithoutApiKey_ReportsConfigurationFailure()
    {
        using var sut = new GeminiPlugin();

        var transcribe = await Assert.ThrowsAsync<PluginRequestException>(
            () => sut.TranscribeAsync([1, 2], "en", translate: false, prompt: null, CancellationToken.None));
        var streaming = await Assert.ThrowsAsync<PluginRequestException>(
            () => sut.StartStreamingAsync("en", CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.Configuration, transcribe.FailureKind);
        Assert.Equal(PluginRequestFailureKind.Configuration, streaming.FailureKind);
    }

    [Fact]
    public async Task TranscribeWithLanguageHintsAsync_UploadsInteractionAndDeletesTemporaryAudio()
    {
        string? interactionBody = null;
        List<(HttpMethod Method, string Uri)> requests = [];
        using var client = new HttpClient(new GeminiCapturingHandler((request, body) =>
        {
            var uri = request.RequestUri!.ToString();
            requests.Add((request.Method, uri));
            if (uri.EndsWith("/upload/v1beta/files", StringComparison.Ordinal))
            {
                Assert.Equal("gemini-key", Assert.Single(request.Headers.GetValues("x-goog-api-key")));
                Assert.Equal("resumable", Assert.Single(request.Headers.GetValues("X-Goog-Upload-Protocol")));
                Assert.Equal("start", Assert.Single(request.Headers.GetValues("X-Goog-Upload-Command")));
                Assert.Equal(
                    "32044",
                    Assert.Single(request.Headers.GetValues("X-Goog-Upload-Header-Content-Length")));
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.TryAddWithoutValidation(
                    "X-Goog-Upload-URL",
                    "https://upload.example/session-1");
                return response;
            }

            if (uri == "https://upload.example/session-1")
            {
                Assert.Equal(
                    "upload, finalize",
                    Assert.Single(request.Headers.GetValues("X-Goog-Upload-Command")));
                Assert.Equal("0", Assert.Single(request.Headers.GetValues("X-Goog-Upload-Offset")));
                return JsonResponse("""
                    {
                      "file": {
                        "name": "files/audio-123",
                        "uri": "https://generativelanguage.googleapis.com/v1beta/files/audio-123"
                      }
                    }
                    """);
            }

            if (uri.EndsWith("/v1beta/interactions", StringComparison.Ordinal))
            {
                interactionBody = body;
                return JsonResponse("""
                    {
                      "status": "completed",
                      "steps": [
                        {"type": "reasoning", "content": [{"type":"text","text":"ignored"}]},
                        {"type": "model_output", "content": [
                          {"type":"text","text":"Hallo "},
                          {"type":"text","text":"Welt"}
                        ]}
                      ]
                    }
                    """);
            }

            if (request.Method == HttpMethod.Delete
                && uri.EndsWith("/v1beta/files/audio-123", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {uri}");
        }));
        var host = new GeminiTestHost { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin(client);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeWithLanguageHintsAsync(
            CreatePcm16Wav(),
            [" de ", "auto", "en", "DE"],
            translate: false,
            prompt: "TypeWhisper, Gemini",
            CancellationToken.None);

        Assert.Equal("Hallo Welt", result.Text);
        // A hint is not a detection.
        Assert.Null(result.DetectedLanguage);
        Assert.Equal(1, result.DurationSeconds, precision: 3);
        Assert.Equal(4, requests.Count);
        Assert.Equal(HttpMethod.Delete, requests[^1].Method);

        using var payload = JsonDocument.Parse(Assert.IsType<string>(interactionBody));
        var root = payload.RootElement;
        Assert.Equal("gemini-3.5-transcribe", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal("audio", root.GetProperty("input")[0].GetProperty("type").GetString());
        Assert.Equal("audio/wav", root.GetProperty("input")[0].GetProperty("mime_type").GetString());
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/files/audio-123",
            root.GetProperty("input")[0].GetProperty("uri").GetString());
        var transcriptionConfig = root
            .GetProperty("generation_config")
            .GetProperty("transcription_config");
        Assert.Equal("smart", transcriptionConfig.GetProperty("mode").GetString());
        Assert.Equal(
            ["de", "en"],
            transcriptionConfig.GetProperty("language_codes")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray());
        Assert.Equal(
            ["TypeWhisper", "Gemini"],
            transcriptionConfig.GetProperty("custom_vocabulary")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task TranscribeAsync_AutomaticLanguageSendsNoHintsAndStillDeletesUploadOnFailure()
    {
        string? interactionBody = null;
        List<HttpMethod> methods = [];
        using var client = new HttpClient(new GeminiCapturingHandler((request, body) =>
        {
            var uri = request.RequestUri!.ToString();
            methods.Add(request.Method);
            if (uri.EndsWith("/upload/v1beta/files", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.TryAddWithoutValidation(
                    "X-Goog-Upload-URL",
                    "https://upload.example/session-1");
                return response;
            }

            if (uri == "https://upload.example/session-1")
            {
                return JsonResponse(
                    """{"file":{"name":"files/audio-123","uri":"https://files.example/audio-123"}}""");
            }

            // ReSharper disable once InvertIf -- one branch per route; inverting the last one
            // would bury the route it answers under the fallback.
            if (uri.EndsWith("/v1beta/interactions", StringComparison.Ordinal))
            {
                interactionBody = body;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("""{"error":{"message":"upstream down"}}"""),
                };
            }

            // The cleanup tolerates a file the API has already reclaimed.
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var host = new GeminiTestHost { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin(client);
        await sut.ActivateAsync(host);

        var failure = await Assert.ThrowsAsync<PluginRequestException>(
            () => sut.TranscribeAsync(CreatePcm16Wav(), "auto", translate: false, prompt: null, CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.ServerError, failure.FailureKind);
        Assert.Equal(HttpMethod.Delete, methods[^1]);
        Assert.DoesNotContain(host.Logs, entry => entry.Level == PluginLogLevel.Warning);
        using var payload = JsonDocument.Parse(Assert.IsType<string>(interactionBody));
        var transcriptionConfig = payload.RootElement
            .GetProperty("generation_config")
            .GetProperty("transcription_config");
        Assert.Empty(transcriptionConfig.GetProperty("language_codes").EnumerateArray());
        Assert.False(transcriptionConfig.TryGetProperty("custom_vocabulary", out _));
    }

    [Fact]
    public async Task TranscribeAsync_RejectsTranslation()
    {
        var host = new GeminiTestHost { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.TranscribeAsync(CreatePcm16Wav(), "en", translate: true, prompt: null, CancellationToken.None));
    }

    [Fact]
    public void InteractionPayload_UsesDocumentedSmartAndVerbatimModeShapes()
    {
        using var smart = JsonDocument.Parse(GeminiTranscriptionClient.CreateInteractionPayload(
            "gemini-3.5-transcribe", "https://example.test/audio", [], [], GeminiTranscriptionMode.Smart));
        using var verbatim = JsonDocument.Parse(GeminiTranscriptionClient.CreateInteractionPayload(
            "gemini-3.5-transcribe", "https://example.test/audio", [], [], GeminiTranscriptionMode.Verbatim));

        Assert.Equal("smart", TranscriptionConfig(smart).GetProperty("mode").GetString());
        Assert.Equal(
            "verbatim",
            TranscriptionConfig(verbatim).GetProperty("mode").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("""{"status":"incomplete","steps":[]}""", PluginRequestFailureKind.OutputIncomplete)]
    [InlineData("""{"status":"failed","steps":[]}""", PluginRequestFailureKind.ServerError)]
    [InlineData("""{"status":"completed"}""", PluginRequestFailureKind.EmptyResponse)]
    [InlineData("""{"status":"completed","steps":[]}""", PluginRequestFailureKind.EmptyResponse)]
    [InlineData("""{"status":"completed","steps":[{"type":"model_output","content":[]}]}""",
        PluginRequestFailureKind.EmptyResponse)]
    [InlineData("not json", PluginRequestFailureKind.EmptyResponse)]
    // A response whose shape is wrong throughout stays a PluginRequestException rather than
    // escaping as the InvalidOperationException an unguarded property probe would throw.
    [InlineData("[]", PluginRequestFailureKind.EmptyResponse)]
    [InlineData("""{"steps":[null]}""", PluginRequestFailureKind.EmptyResponse)]
    public void ParseInteractionText_ClassifiesUnusableResponses(string json, PluginRequestFailureKind expected)
    {
        var failure = Assert.Throws<PluginRequestException>(
            () => GeminiTranscriptionClient.ParseInteractionText(json));

        Assert.Equal(expected, failure.FailureKind);
    }

    [Fact]
    public void ParseInteractionText_SkipsNonObjectEntriesAndUsesLastModelOutputStep()
    {
        var text = GeminiTranscriptionClient.ParseInteractionText("""
            {
              "status": "completed",
              "steps": [
                {"type":"model_output","content":[{"type":"text","text":"stale"}]},
                {"type":"model_output","content":[
                  {"type":"reasoning","text":"skip"},
                  null,
                  {"type":"text","text":" Hello "},
                  {"type":"text","text":"world "}
                ]},
                null
              ]
            }
            """);

        Assert.Equal("Hello world", text);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task Transcribe_ClampsRetryAfterDateToNonNegativeDelay(int minutesFromNow)
    {
        using var client = new HttpClient(new GeminiCapturingHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                DateTimeOffset.UtcNow.AddMinutes(minutesFromNow));
            return response;
        }));
        var host = new GeminiTestHost { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin(client);
        await sut.ActivateAsync(host);

        var failure = await Assert.ThrowsAsync<PluginRequestException>(
            () => sut.TranscribeAsync(CreatePcm16Wav(), null, translate: false, prompt: null, CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.RateLimit, failure.FailureKind);
        Assert.Equal((int)HttpStatusCode.TooManyRequests, failure.HttpStatusCode);
        if (minutesFromNow < 0)
        {
            Assert.Equal(TimeSpan.Zero, failure.RetryAfter);
        }
        else
        {
            Assert.True(failure.RetryAfter > TimeSpan.Zero);
            Assert.True(failure.RetryAfter <= TimeSpan.FromMinutes(1));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, PluginRequestFailureKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, PluginRequestFailureKind.Permission)]
    [InlineData(HttpStatusCode.RequestTimeout, PluginRequestFailureKind.Timeout)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, PluginRequestFailureKind.RequestTooLarge)]
    [InlineData(HttpStatusCode.BadRequest, PluginRequestFailureKind.InvalidRequest)]
    [InlineData(HttpStatusCode.InternalServerError, PluginRequestFailureKind.ServerError)]
    public async Task Transcribe_MapsHttpFailuresToFailureKinds(
        HttpStatusCode status, PluginRequestFailureKind expected)
    {
        using var client = new HttpClient(new GeminiCapturingHandler(
            (_, _) => new HttpResponseMessage(status)));
        var host = new GeminiTestHost { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin(client);
        await sut.ActivateAsync(host);

        var failure = await Assert.ThrowsAsync<PluginRequestException>(
            () => sut.TranscribeAsync(CreatePcm16Wav(), null, translate: false, prompt: null, CancellationToken.None));

        Assert.Equal(expected, failure.FailureKind);
        Assert.Equal((int)status, failure.HttpStatusCode);
    }

    [Fact]
    public async Task Transcribe_SurfacesRateLimitRetryAfter()
    {
        using var client = new HttpClient(new GeminiCapturingHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", "42");
            return response;
        }));
        var host = new GeminiTestHost { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin(client);
        await sut.ActivateAsync(host);

        var failure = await Assert.ThrowsAsync<PluginRequestException>(
            () => sut.TranscribeAsync(CreatePcm16Wav(), null, translate: false, prompt: null, CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.RateLimit, failure.FailureKind);
        Assert.Equal(TimeSpan.FromSeconds(42), failure.RetryAfter);
    }

    [Fact]
    public void CalculateWavDurationSeconds_RejectsOverflowingChunkSize()
    {
        var wav = new byte[20];
        "RIFF"u8.CopyTo(wav);
        "WAVE"u8.CopyTo(wav.AsSpan(8));
        "JUNK"u8.CopyTo(wav.AsSpan(12));
        BitConverter.GetBytes(int.MaxValue).CopyTo(wav, 16);

        Assert.Equal(0, GeminiTranscriptionClient.CalculateWavDurationSeconds(wav));
        Assert.Equal(0, GeminiTranscriptionClient.CalculateWavDurationSeconds([1, 2, 3]));
    }

    [Fact]
    public void CalculateWavDurationSeconds_MeasuresFfmpegStdoutWavWithUnknownDataSize()
    {
        // Header layout FFmpeg writes to pipe:1: unknown RIFF/data sizes plus a LIST chunk.
        var wav = new byte[80 + 32_000];
        "RIFF"u8.CopyTo(wav);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(wav, 4);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BitConverter.GetBytes(16).CopyTo(wav, 16);
        BitConverter.GetBytes((short)1).CopyTo(wav, 20);
        BitConverter.GetBytes((short)1).CopyTo(wav, 22);
        BitConverter.GetBytes(16_000).CopyTo(wav, 24);
        BitConverter.GetBytes(32_000).CopyTo(wav, 28);
        BitConverter.GetBytes((short)2).CopyTo(wav, 32);
        BitConverter.GetBytes((short)16).CopyTo(wav, 34);
        "LIST"u8.CopyTo(wav.AsSpan(36));
        BitConverter.GetBytes(28).CopyTo(wav, 40);
        "data"u8.CopyTo(wav.AsSpan(72));
        BitConverter.GetBytes(uint.MaxValue).CopyTo(wav, 76);

        Assert.Equal(1.0, GeminiTranscriptionClient.CalculateWavDurationSeconds(wav), precision: 6);
    }

    [Fact]
    public void ExtractVocabulary_NormalizesDeduplicatesAndClipsToBudget()
    {
        Assert.Empty(GeminiPlugin.ExtractVocabulary(null));
        Assert.Empty(GeminiPlugin.ExtractVocabulary("  "));
        Assert.Equal(
            ["TypeWhisper", "Gemini"],
            GeminiPlugin.ExtractVocabulary(" TypeWhisper ,\nGemini,\r typewhisper "));
        Assert.Equal(
            100,
            GeminiPlugin.ExtractVocabulary(
                string.Join(',', Enumerable.Range(0, 200).Select(index => $"term{index}"))).Count);

        // A sentence out of the caller's free-form prompt is not a vocabulary term.
        Assert.Equal(
            ["TypeWhisper"],
            GeminiPlugin.ExtractVocabulary(
                "TypeWhisper, The speaker discusses Kubernetes at length in this recording"));

        var longTerms = GeminiPlugin.ExtractVocabulary(
            string.Join(',', Enumerable.Range(0, 20).Select(index => new string((char)('a' + index), 500))));

        Assert.Equal(7, longTerms.Count);
    }

    [Fact]
    public async Task TranscriptionRole_OptsIntoStructuredDictionaryTerms()
    {
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(new GeminiTestHost { Secrets = { ["api-key"] = "gemini-key" } });

        Assert.True(sut.SupportsStructuredDictionaryTerms);
        Assert.Equal(new DictionaryTermsBudget(MaxTerms: 100, MaxTotalChars: 4_000), sut.DictionaryTermsBudget);
    }

    [Fact]
    public void ExtractVocabulary_KeepsEnvelopeTermsWhole()
    {
        string[] terms =
        [
            "Washington, D.C.",
            "Äpfel; Öl",
            "quoted \"name\"",
            "line\nbreak",
            "a term with more than eight words in it stays whole",
        ];

        Assert.Equal(terms, GeminiPlugin.ExtractVocabulary(PluginTranscriptionPrompt.Encode(null, terms)));
    }

    [Fact]
    public void ExtractVocabulary_PutsApiPromptItemsBeforeDictionaryTermsWithinBudget()
    {
        var prompt = PluginTranscriptionPrompt.Encode(
            "Kubernetes, BigQuery\nThe speaker discusses Kubernetes at length in this recording",
            ["bigquery", "Washington, D.C."]);

        Assert.Equal(["Kubernetes", "BigQuery", "Washington, D.C."], GeminiPlugin.ExtractVocabulary(prompt));

        var manyTerms = PluginTranscriptionPrompt.Encode(
            "first",
            Enumerable.Range(0, 200).Select(index => $"term{index}"));
        var clipped = GeminiPlugin.ExtractVocabulary(manyTerms);
        Assert.Equal(100, clipped.Count);
        Assert.Equal("first", clipped[0]);
        Assert.Equal("term98", clipped[^1]);
    }

    [Fact]
    public void ExtractVocabulary_RejectsMalformedEnvelope()
    {
        Assert.Throws<FormatException>(
            () => GeminiPlugin.ExtractVocabulary(PluginTranscriptionPrompt.EnvelopePrefix + "{\"dictionaryTerms\":[1]}"));
    }

    [Fact]
    public async Task TranscribeAsync_SendsEnvelopeTermsWholeAsCustomVocabulary()
    {
        string? interactionBody = null;
        using var client = new HttpClient(new GeminiCapturingHandler((request, body) =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri.EndsWith("/upload/v1beta/files", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.TryAddWithoutValidation("X-Goog-Upload-URL", "https://upload.example/session-1");
                return response;
            }

            if (uri == "https://upload.example/session-1")
                return JsonResponse("""{"file":{"name":"files/audio-123","uri":"https://files.example/audio-123"}}""");

            // ReSharper disable once InvertIf -- one branch per route, as in the other fixtures.
            if (uri.EndsWith("/v1beta/interactions", StringComparison.Ordinal))
            {
                interactionBody = body;
                return JsonResponse(
                    """{"status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"ok"}]}]}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        using var sut = new GeminiPlugin(client);
        await sut.ActivateAsync(new GeminiTestHost { Secrets = { ["api-key"] = "gemini-key" } });

        await sut.TranscribeAsync(
            CreatePcm16Wav(),
            "en",
            translate: false,
            PluginTranscriptionPrompt.Encode(null, ["Washington, D.C.", "TypeWhisper"]),
            CancellationToken.None);

        using var payload = JsonDocument.Parse(Assert.IsType<string>(interactionBody));
        Assert.Equal(
            ["Washington, D.C.", "TypeWhisper"],
            payload.RootElement
                .GetProperty("generation_config")
                .GetProperty("transcription_config")
                .GetProperty("custom_vocabulary")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task StartStreaming_SendsPromptTermsToTheLiveSession()
    {
        List<GeminiLiveConnectionOptions> connections = [];
        using var sut = new GeminiPlugin(
            new HttpClient(),
            (options, _) =>
            {
                connections.Add(options);
                return Task.FromResult<IStreamingSession>(new StubSession());
            });
        await sut.ActivateAsync(new GeminiTestHost { Secrets = { ["api-key"] = "gemini-key" } });

        await using (await sut.StartStreamingWithLanguageHintsAndPromptAsync(
            ["de", "en"],
            PluginTranscriptionPrompt.Encode(null, ["Washington, D.C.", "TypeWhisper"]),
            CancellationToken.None))
        {
        }

        await using (await sut.StartStreamingAsync("en", CancellationToken.None))
        {
        }

        Assert.Equal(2, connections.Count);
        Assert.Equal(GeminiPlugin.DefaultLiveTranscriptionModel, connections[0].LiveModelId);
        Assert.Equal(["de", "en"], connections[0].LanguageHints);
        Assert.Equal(["Washington, D.C.", "TypeWhisper"], connections[0].CustomVocabulary);
        Assert.Equal(["en"], connections[1].LanguageHints);
        Assert.Empty(connections[1].CustomVocabulary);
    }

    [Fact]
    public async Task StreamingAdapter_RunsTheLiveTranscriptionProtocol()
    {
        var transport = new ScriptedWebSocketTransport();
        var adapter = new GeminiWebSocketAdapter(
            "gemini key",
            "models/gemini-3.5-transcribe-live",
            ["de", "en"],
            ["TypeWhisper"],
            GeminiTranscriptionMode.Verbatim);
        var connection = await adapter.GetConnectionOptionsAsync(CancellationToken.None);
        var starting = WebSocketSessionPump.StartConnectedAsync(
            adapter, transport, CancellationToken.None);
        var setupFrame = await transport.NextSentAsync();
        transport.EnqueueText("""{"setupComplete":{}}""");
        await using var pump = await starting.WaitAsync(s_timeout);

        Assert.Equal("generativelanguage.googleapis.com", connection.Uri.Host);
        Assert.Equal("key=gemini%20key", connection.Uri.Query.TrimStart('?'));
        using var setup = JsonDocument.Parse(setupFrame.Payload);
        var setupRoot = setup.RootElement.GetProperty("setup");
        Assert.Equal("models/gemini-3.5-transcribe-live", setupRoot.GetProperty("model").GetString());
        Assert.Equal(
            ["TEXT"],
            setupRoot.GetProperty("generationConfig").GetProperty("responseModalities")
                .EnumerateArray().Select(value => value.GetString()!).ToArray());
        var transcription = setupRoot.GetProperty("inputAudioTranscription");
        Assert.Equal("VERBATIM", transcription.GetProperty("mode").GetString());
        Assert.Equal(
            ["de", "en"],
            transcription.GetProperty("languageCodes").EnumerateArray()
                .Select(value => value.GetString()!).ToArray());
        Assert.Equal("TypeWhisper", transcription.GetProperty("customVocabulary")[0].GetString());
        Assert.True(
            setupRoot.GetProperty("realtimeInputConfig").GetProperty("automaticActivityDetection")
                .GetProperty("disabled").GetBoolean());

        List<StreamingTranscriptEvent> events = [];
        pump.TranscriptReceived += events.Add;

        // The first audio opens the recording's only activity; later audio follows it directly.
        await pump.SendAudioAsync(new byte[] { 1, 2, 3, 4 }, CancellationToken.None).WaitAsync(s_timeout);
        Assert.Equal(
            """{"realtimeInput":{"activityStart":{}}}""",
            Encoding.UTF8.GetString((await transport.NextSentAsync()).Payload.Span));
        var audioFrame = await transport.NextSentAsync();
        using var audio = JsonDocument.Parse(audioFrame.Payload);
        var audioPart = audio.RootElement.GetProperty("realtimeInput").GetProperty("audio");
        Assert.Equal("AQIDBA==", audioPart.GetProperty("data").GetString());
        Assert.Equal("audio/pcm;rate=16000", audioPart.GetProperty("mimeType").GetString());
        await pump.SendAudioAsync(new byte[] { 5, 6 }, CancellationToken.None).WaitAsync(s_timeout);
        Assert.Contains(
            "\"audio\"",
            Encoding.UTF8.GetString((await transport.NextSentAsync()).Payload.Span),
            StringComparison.Ordinal);

        transport.EnqueueText("""{"server_content":{"interim_input_transcription":{"text":" Hallo "}}}""");

        var finalize = pump.FinalizeAsync(CancellationToken.None);
        Assert.Equal(
            """{"realtimeInput":{"activityEnd":{}}}""",
            Encoding.UTF8.GetString((await transport.NextSentAsync()).Payload.Span));
        transport.EnqueueText("""{"serverContent":{"inputTranscription":{"text":" Hallo Welt "}}}""");
        transport.EnqueueText("""{"voiceActivity":{"type":"ACTIVITY_END","audioOffset":"0.000188s"}}""");
        await finalize.WaitAsync(s_timeout);

        Assert.Equal(
            [new StreamingTranscriptEvent("Hallo", false), new StreamingTranscriptEvent("Hallo Welt", true)],
            events);
    }

    [Fact]
    public async Task StreamingAdapter_CompletesOnlyWithTheFinalAndTheAcknowledgedEnd()
    {
        var adapter = new GeminiWebSocketAdapter(
            "key", "gemini-3.5-transcribe-live", [], [], GeminiTranscriptionMode.Smart);
        await adapter.EncodeAudioAsync(new byte[32000], CancellationToken.None);
        await adapter.BeginFinalizeAsync(CancellationToken.None);

        // A turn boundary is not the end of the transcription.
        var final = Handle(
            adapter,
            """{"serverContent":{"turnComplete":true,"inputTranscription":{"text":"Done."}}}""",
            WebSocketMessageType.Binary);
        Assert.Equal(WebSocketSessionSignal.None, final.Signals);
        Assert.Equal(new StreamingTranscriptEvent("Done.", true), Assert.Single(final.Transcripts));
        Assert.Same(WebSocketInboundResult.Empty, Handle(adapter, """{"serverContent":{"turnComplete":true}}"""));
        Assert.Same(
            WebSocketInboundResult.Empty,
            Handle(adapter, """{"serverContent":{"interimInputTranscription":{"text":"Late"}}}"""));
        Assert.Same(
            WebSocketInboundResult.Empty,
            Handle(adapter, """{"voiceActivity":{"type":"ACTIVITY_START","audioOffset":"0s"}}"""));

        var end = Handle(adapter, """{"voiceActivity":{"type":"ACTIVITY_END","audioOffset":"1s"}}""");
        Assert.Null(end.Fault);
        Assert.Equal(WebSocketSessionSignal.Terminal, end.Signals);
    }

    [Fact]
    public void StreamingAdapter_ReadsSignalValuesAndIgnoresMalformedContainers()
    {
        var adapter = new GeminiWebSocketAdapter(
            "key", "gemini-3.5-transcribe-live", [], [], GeminiTranscriptionMode.Smart);

        Assert.Same(WebSocketInboundResult.Empty, Handle(adapter, """{"serverContent":{}}"""));
        // A container that is null or a scalar where an object is documented is ignored.
        Assert.Same(WebSocketInboundResult.Empty, Handle(adapter, """{"serverContent":null}"""));
        Assert.Same(WebSocketInboundResult.Empty, Handle(adapter, """{"serverContent":"x"}"""));
        Assert.Same(
            WebSocketInboundResult.Empty,
            Handle(adapter, """{"serverContent":{"inputTranscription":"x"}}"""));
        Assert.Same(WebSocketInboundResult.Empty, Handle(adapter, """{"voiceActivity":null}"""));
        // A null error is not a failure.
        Assert.Null(Handle(adapter, """{"error":null}""").Fault);
        Assert.NotNull(Handle(adapter, "not json").Fault);
        Assert.Contains(
            "rejected",
            Handle(adapter, """{"error":{"message":"rejected"}}""").Fault!.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamingAdapter_AcceptsSnakeCaseReadiness()
    {
        var transport = new ScriptedWebSocketTransport();
        var starting = WebSocketSessionPump.StartConnectedAsync(
            new GeminiWebSocketAdapter(
                "key", "gemini-3.5-transcribe-live", [], [], GeminiTranscriptionMode.Smart),
            transport,
            CancellationToken.None);
        await transport.NextSentAsync();
        transport.EnqueueText("""{"setup_complete":{}}""");
        await using var pump = await starting.WaitAsync(s_timeout);

        Assert.Equal(WebSocketSessionState.Active, pump.State);
    }

    private static WebSocketInboundResult Handle(
        GeminiWebSocketAdapter adapter,
        string json,
        WebSocketMessageType type = WebSocketMessageType.Text) =>
        adapter.HandleMessage(type, Encoding.UTF8.GetBytes(json));

    [Fact]
    public async Task TranscribeStreamingWithLanguageHintsAsync_KeepsEveryHint()
    {
        string? interactionBody = null;
        using var client = new HttpClient(new GeminiCapturingHandler((request, body) =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri.EndsWith("/upload/v1beta/files", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.TryAddWithoutValidation(
                    "X-Goog-Upload-URL",
                    "https://upload.example/session-1");
                return response;
            }

            if (uri == "https://upload.example/session-1")
            {
                return JsonResponse(
                    """{"file":{"name":"files/audio-123","uri":"https://files.example/audio-123"}}""");
            }

            // ReSharper disable once InvertIf -- one branch per route; inverting the last one
            // would bury the route it answers under the fallback.
            if (uri.EndsWith("/v1beta/interactions", StringComparison.Ordinal))
            {
                interactionBody = body;
                return JsonResponse(
                    """{"status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"Hallo"}]}]}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        var host = new GeminiTestHost { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin(client);
        await sut.ActivateAsync(host);

        // The live-preview path routes multi-hint requests here; the SDK default would
        // collapse them to an explicit "de".
        var result = await sut.TranscribeStreamingWithLanguageHintsAsync(
            CreatePcm16Wav(),
            ["de", "en"],
            translate: false,
            prompt: null,
            _ => true,
            CancellationToken.None);

        Assert.Equal("Hallo", result.Text);
        Assert.Null(result.DetectedLanguage);
        using var payload = JsonDocument.Parse(Assert.IsType<string>(interactionBody));
        Assert.Equal(
            ["de", "en"],
            TranscriptionConfig(payload).GetProperty("language_codes")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task Transcribe_DeletesTheUploadWhenItsMetadataIsIncomplete()
    {
        List<(HttpMethod Method, string Uri)> requests = [];
        using var client = new HttpClient(new GeminiCapturingHandler((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            requests.Add((request.Method, uri));
            // ReSharper disable once InvertIf -- the positive form keeps the routes in the order
            // the client walks them, upload start first.
            if (uri.EndsWith("/upload/v1beta/files", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.TryAddWithoutValidation(
                    "X-Goog-Upload-URL",
                    "https://upload.example/session-1");
                return response;
            }

            // The API named the file and then described it incompletely.
            return uri == "https://upload.example/session-1"
                ? JsonResponse("""{"file":{"name":"files/audio-123"}}""")
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        var host = new GeminiTestHost { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin(client);
        await sut.ActivateAsync(host);

        var failure = await Assert.ThrowsAsync<PluginRequestException>(
            () => sut.TranscribeAsync(
                CreatePcm16Wav(), null, translate: false, prompt: null, CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.EmptyResponse, failure.FailureKind);
        Assert.Equal(HttpMethod.Delete, requests[^1].Method);
        Assert.EndsWith("/v1beta/files/audio-123", requests[^1].Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiKeyChange_RepairsTheTranscriptionSelectionAgainstTheFallbackCatalog()
    {
        var host = new GeminiTestHost { Secrets = { ["api-key"] = "first-key" } };
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);
        await sut.SetFetchedTranscriptionModelsAsync(
            [new GeminiFetchedTranscriptionModel("gemini-3.7-transcribe", null, "gemini-3.7-transcribe-live")]);
        Assert.Equal("gemini-3.7-transcribe", sut.SelectedModelId);

        await sut.SetApiKeyAsync("second-key");

        Assert.Empty(sut.FetchedTranscriptionModels);
        Assert.Equal(GeminiPlugin.DefaultTranscriptionModel, sut.SelectedModelId);
        // Persisted too: a stale id left in the setting would come back at the next activation,
        // and a later refresh that finds it unchanged would never rewrite it.
        Assert.Equal(
            GeminiPlugin.DefaultTranscriptionModel,
            host.GetSetting<string>("selectedTranscriptionModel"));
        Assert.Contains(sut.TranscriptionModels, model => model.Id == sut.SelectedModelId);
        Assert.True(sut.SupportsStreaming);

        // Replaying the stale id through the settings pane repairs it instead of throwing.
        await sut.SetSettingValueAsync("selectedTranscriptionModel", "gemini-3.7-transcribe");

        Assert.Equal(GeminiPlugin.DefaultTranscriptionModel, sut.SelectedModelId);
    }

    private static JsonElement TranscriptionConfig(JsonDocument payload) =>
        payload.RootElement.GetProperty("generation_config").GetProperty("transcription_config");

    private static byte[] CreatePcm16Wav(int sampleRate = 16_000, int sampleCount = 16_000)
    {
        const short channelCount = 1;
        const short bitsPerSample = 16;
        var dataSize = sampleCount * sizeof(short);
        using var stream = new MemoryStream(44 + dataSize);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channelCount);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channelCount * bitsPerSample / 8);
        writer.Write((short)(channelCount * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataSize);
        writer.Write(new byte[dataSize]);
        writer.Flush();
        return stream.ToArray();
    }

    private sealed class StubSession : IStreamingSession
    {
        public event Action<StreamingTranscriptEvent>? TranscriptReceived { add { } remove { } }
        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct) => Task.CompletedTask;
        public Task FinalizeAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class GeminiCapturingHandler(
        Func<HttpRequestMessage, string?, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request, body);
        }
    }

    private sealed class GeminiBlockingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
            return response;
        }
    }

    private sealed class GeminiTestHost : IPluginHostServices
    {
        private readonly Dictionary<string, JsonElement> _settings = [];
        public Dictionary<string, string?> Secrets { get; } = [];
        public List<(PluginLogLevel Level, string Message)> Logs { get; } = [];
        public Task StoreSecretAsync(string key, string value)
        {
            Secrets[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadSecretAsync(string key) =>
            Task.FromResult(Secrets.GetValueOrDefault(key));

        public Task DeleteSecretAsync(string key)
        {
            Secrets.Remove(key);
            return Task.CompletedTask;
        }

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value) ? value.Deserialize<T>(s_jsonOptions) : default;

        public void SetSetting<T>(string key, T value) =>
            _settings[key] = JsonSerializer.SerializeToElement(value, s_jsonOptions);

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new GeminiTestEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) => Logs.Add((level, message));
        public void NotifyCapabilitiesChanged() { }
        public IPluginLocalization Localization { get; } = new GeminiTestLocalization();
    }

    private sealed class GeminiTestLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => key + ": " + string.Join(", ", args);
    }

    private sealed class GeminiTestEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }

        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent =>
            new GeminiTestSubscription();
    }

    private sealed class GeminiTestSubscription : IDisposable
    {
        public void Dispose() { }
    }
}
