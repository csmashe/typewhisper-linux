using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Plugin.OpenRouter;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class OpenRouterPluginTests
{
    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifest = LoadManifest();
        var sut = new OpenRouterPlugin();

        Assert.Equal(manifest.GetProperty("version").GetString(), sut.PluginVersion);
    }

    [Fact]
    public void Manifest_AdvertisesOpenRouterIdentity()
    {
        var manifest = LoadManifest();

        Assert.Equal("com.typewhisper.openrouter", manifest.GetProperty("id").GetString());
        Assert.Equal("OpenRouter", manifest.GetProperty("name").GetString());
        Assert.Equal("llm", manifest.GetProperty("category").GetString());
        Assert.Equal(
            "TypeWhisper.Plugin.OpenRouter.dll",
            manifest.GetProperty("assemblyName").GetString());
        Assert.Equal(
            "TypeWhisper.Plugin.OpenRouter.OpenRouterPlugin",
            manifest.GetProperty("pluginClass").GetString());
    }

    [Fact]
    public async Task ActivateAsync_ExposesOpenRouterAsTranscriptionEngineWithDefaultModel()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";

        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        Assert.IsAssignableFrom<ITranscriptionEnginePlugin>(sut);
        Assert.IsAssignableFrom<ILlmProviderPlugin>(sut);
        Assert.IsAssignableFrom<IPluginSettingsProvider>(sut);
        Assert.Equal("openrouter", sut.ProviderId);
        Assert.Equal("OpenRouter", sut.ProviderDisplayName);
        Assert.True(sut.IsConfigured);
        Assert.False(sut.SupportsTranslation);
        Assert.False(((ITranscriptionEnginePlugin)sut).SupportsStreaming);
        Assert.Equal("openai/whisper-large-v3-turbo", sut.SelectedModelId);
        Assert.Contains(sut.TranscriptionModels, model => model.Id == "openai/gpt-4o-mini-transcribe");
        Assert.Contains(sut.TranscriptionModels, model => model.Id == "openai/whisper-large-v3-turbo");
    }

    [Fact]
    public async Task ActivateAsync_RestoresFetchedTranscriptionModelsAndNormalizesStaleSelection()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        host.SetSetting("fetchedTranscriptionModels", new List<OpenRouterFetchedModel>
        {
            new("z/stt", "Zulu STT", "0.000002", "0"),
            new("a/stt", "Alpha STT", "0", "0"),
        });
        host.SetSetting("selectedTranscriptionModel", "missing/stt");

        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("a/stt", sut.SelectedModelId);
        Assert.Equal(["a/stt", "z/stt"], sut.TranscriptionModels.Select(m => m.Id).ToArray());
        Assert.Equal("a/stt", host.GetSetting<string>("selectedTranscriptionModel"));
    }

    [Fact]
    public async Task ActivateAsync_UsesOpenRouterFreeAsDefaultWhenSelectionUnset()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";

        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("openrouter/free", sut.SelectedLlmModelId);
        Assert.Equal("openrouter/free", sut.SupportedModels.First().Id);
        Assert.Equal("openrouter/free", host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task ActivateAsync_MigratesLegacyFallbackDefaultToOpenRouterFree()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        host.SetSetting("selectedLlmModel", "openai/gpt-4o");

        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("openrouter/free", sut.SelectedLlmModelId);
        Assert.Equal("openrouter/free", host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task ActivateAsync_PreservesUnmarkedSavedSelectionAndBackfillsUserFlag()
    {
        // Fork-specific deviation from upstream `4865959`: upstream's
        // verbatim guard migrated any saved selection that lacked the new
        // userSelectedLlmModel marker, which would silently downgrade a
        // pre-1.1.0 fork user's explicit choice (anthropic/claude-sonnet-4,
        // google/gemini-2.5-flash, meta-llama/llama-4-scout — the entire
        // catalog the pre-1.1.0 plugin offered) to openrouter/free. The
        // fork only migrates null/blank or the explicit legacy openai/
        // gpt-4o default; everything else is preserved and the user-flag
        // is backfilled so the next activate doesn't re-migrate it.
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        host.SetSetting("selectedLlmModel", "openrouter/owl-alpha");

        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("openrouter/owl-alpha", sut.SelectedLlmModelId);
        Assert.Equal("openrouter/owl-alpha", host.GetSetting<string>("selectedLlmModel"));
        Assert.True(host.GetSetting<bool?>("userSelectedLlmModel"));
    }

    [Theory]
    [InlineData("anthropic/claude-sonnet-4")]
    [InlineData("google/gemini-2.5-flash")]
    [InlineData("meta-llama/llama-4-scout")]
    public async Task ActivateAsync_PreservesPreviousForkCatalogSelectionOnUpgrade(string savedSelection)
    {
        // The three models the fork's pre-1.1.0 OpenRouter plugin
        // hard-coded. Any of these can be a real saved selection from an
        // earlier install; they must survive the catalog/migration
        // rewrite untouched.
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        host.SetSetting("selectedLlmModel", savedSelection);

        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal(savedSelection, sut.SelectedLlmModelId);
        Assert.Equal(savedSelection, host.GetSetting<string>("selectedLlmModel"));
        Assert.True(host.GetSetting<bool?>("userSelectedLlmModel"));
    }

    [Fact]
    public async Task ActivateAsync_RestoresFetchedModelsAndNormalizesStaleSelection()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        host.SetSetting("fetchedModels", new List<OpenRouterFetchedModel>
        {
            new("z/model", "Z Model", "0.000002", "0.000003"),
            new("a/model", "A Model", "0", "0"),
        });
        host.SetSetting("selectedLlmModel", "missing/model");

        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("openrouter/free", sut.SelectedLlmModelId);
        Assert.Equal(
            ["openrouter/free", "a/model", "z/model"],
            sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal("openrouter/free", host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task ActivateAsync_PreservesMarkedUserSelection()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        host.SetSetting("selectedLlmModel", "selected/model");
        host.SetSetting("userSelectedLlmModel", true);

        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("selected/model", sut.SelectedLlmModelId);
        Assert.Equal("selected/model", host.GetSetting<string>("selectedLlmModel"));
    }

    [Theory]
    [InlineData("text->text", "openai/gpt-4o", true)]
    [InlineData("text+image->text", "anthropic/claude-sonnet-4", true)]
    [InlineData("text->image", "image/model", false)]
    [InlineData("", "openai/gpt-4o", true)]
    [InlineData("", "openai/text-embedding-3-small", false)]
    [InlineData("", "openai/whisper-1", false)]
    [InlineData("", "stability/stable-diffusion-xl", false)]
    public void IsTextLlm_FiltersByModalityAndModelId(string modality, string modelId, bool expected)
    {
        Assert.Equal(expected, OpenRouterPlugin.IsTextLlm(modality, modelId));
    }

    [Fact]
    public async Task ValidateApiKeyAsync_UsesOpenRouterAuthKeyEndpoint()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://openrouter.ai/api/v1/auth/key", request.RequestUri?.ToString());
            Assert.Equal("Bearer openrouter-key", request.Headers.Authorization?.ToString());
            return JsonResponse("""{ "data": { "limit_remaining": 12.5 } }""");
        });

        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);

        Assert.True(await sut.ValidateApiKeyAsync("openrouter-key"));
    }

    [Fact]
    public async Task FetchModelsAsync_FiltersTextModelsSortsByNameAndKeepsPricing()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://openrouter.ai/api/v1/models", request.RequestUri?.ToString());
            Assert.Equal("Bearer openrouter-key", request.Headers.Authorization?.ToString());
            return JsonResponse("""
                {
                  "data": [
                    {
                      "id": "openai/text-embedding-3-small",
                      "name": "Embedding",
                      "pricing": { "prompt": "0.00000002", "completion": "0" },
                      "architecture": { "modality": "text->text" }
                    },
                    {
                      "id": "z/model",
                      "name": "Zulu",
                      "pricing": { "prompt": "0.000002", "completion": "0.000003" },
                      "architecture": { "modality": "text->text" }
                    },
                    {
                      "id": "a/model",
                      "name": "Alpha",
                      "pricing": { "prompt": "0", "completion": "0" },
                      "architecture": { "modality": "text+image->text" }
                    },
                    {
                      "id": "image/model",
                      "name": "Image",
                      "pricing": { "prompt": "0", "completion": "0" },
                      "architecture": { "modality": "text->image" }
                    }
                  ]
                }
                """);
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        var models = await sut.FetchModelsAsync();

        Assert.Equal(["openrouter/free", "a/model", "z/model"], models.Select(m => m.Id).ToArray());
        Assert.Equal("OpenRouter: Free Models Router (free)", models[0].Name);
        Assert.Equal("Free", models[0].FormattedPricing("Free"));
        Assert.Equal("Alpha", models[1].Name);
        Assert.Equal("Free", models[1].FormattedPricing("Free"));
        Assert.Equal("$2.00/$3.00 per 1M", models[2].FormattedPricing("Free"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"data":null}""")]
    public async Task FetchModelsAsync_ReturnsEmptyOnDegradedResponseInsteadOfThrowing(string responseBody)
    {
        // A 200 with `{}` or `{"data":null}` (schema drift / error-shaped
        // body) deserializes into OpenRouterModelsResponse with a null
        // Data list. Without the explicit null guard the LINQ chain would
        // dereference null *outside* the caught JsonException path,
        // crashing the user's Validate click instead of degrading to the
        // cached/fallback catalog — exactly the failure mode this code
        // is trying to tolerate.
        var handler = new CapturingHandler((_, _) => JsonResponse(responseBody));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        var llmModels = await sut.FetchModelsAsync();
        var transcriptionModels = await sut.FetchTranscriptionModelsAsync();

        Assert.Empty(llmModels);
        Assert.Empty(transcriptionModels);
    }

    [Fact]
    public void FormattedPricing_TreatsEffectivelyZeroPricesAsFree()
    {
        var sut = new OpenRouterFetchedModel(
            "tiny/free",
            "Tiny Free",
            "0.0000000000000001",
            "0");

        Assert.Equal("Free", sut.FormattedPricing("Free"));
    }

    [Fact]
    public async Task FetchTranscriptionModelsAsync_UsesOutputModalitiesFilterAndSortsModels()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                "https://openrouter.ai/api/v1/models?output_modalities=transcription",
                request.RequestUri?.ToString());
            Assert.Equal("Bearer openrouter-key", request.Headers.Authorization?.ToString());
            return JsonResponse("""
                {
                  "data": [
                    {
                      "id": "openai/whisper-large-v3-turbo",
                      "name": "OpenAI: Whisper Large V3 Turbo",
                      "pricing": { "prompt": "0.000001", "completion": "0" },
                      "architecture": { "modality": "audio->text" }
                    },
                    {
                      "id": "google/chirp-3",
                      "name": "Google: Chirp 3",
                      "pricing": { "prompt": "0", "completion": "0" },
                      "architecture": { "modality": "audio->text" }
                    }
                  ]
                }
                """);
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        var models = await sut.FetchTranscriptionModelsAsync();

        Assert.Equal(["google/chirp-3", "openai/whisper-large-v3-turbo"], models.Select(m => m.Id).ToArray());
        Assert.Equal("Google: Chirp 3", models[0].Name);
        Assert.Equal("$1.00/$0.00 per 1M", models[1].FormattedPricing("Free"));
    }

    [Fact]
    public async Task FetchCreditsAsync_ParsesLimitMinusUsage()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal("https://openrouter.ai/api/v1/auth/key", request.RequestUri?.ToString());
            return JsonResponse("""{ "data": { "limit": 20.0, "usage": 7.25 } }""");
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        Assert.Equal(12.75, await sut.FetchCreditsAsync());
    }

    [Fact]
    public async Task FetchCreditsAsync_ParsesLimitRemaining()
    {
        var handler = new CapturingHandler((_, _) =>
            JsonResponse("""{ "data": { "limit_remaining": 4.5 } }"""));

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        Assert.Equal(4.5, await sut.FetchCreditsAsync());
    }

    [Fact]
    public async Task TranscribeAsync_PostsBase64WavToOpenRouterTranscriptionsEndpoint()
    {
        var handler = new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "https://openrouter.ai/api/v1/audio/transcriptions",
                request.RequestUri?.ToString());
            Assert.Equal("Bearer openrouter-key", request.Headers.Authorization?.ToString());
            Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);

            using var doc = JsonDocument.Parse(body ?? throw new InvalidOperationException("Missing body"));
            Assert.Equal("openai/whisper-large-v3-turbo", doc.RootElement.GetProperty("model").GetString());
            Assert.Equal("de", doc.RootElement.GetProperty("language").GetString());
            var audio = doc.RootElement.GetProperty("input_audio");
            Assert.Equal("wav", audio.GetProperty("format").GetString());
            Assert.Equal(Convert.ToBase64String([1, 2, 3]), audio.GetProperty("data").GetString());

            return JsonResponse("""{ "text": "Hallo Welt", "usage": { "seconds": 1.25 } }""");
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.TranscribeAsync(
            [1, 2, 3],
            "de",
            translate: false,
            prompt: "ignored",
            CancellationToken.None);

        Assert.Equal("Hallo Welt", result.Text);
        Assert.Null(result.DetectedLanguage);
        Assert.Equal(1.25, result.DurationSeconds);
    }

    [Fact]
    public async Task TranscribeAsync_OmitsAutoLanguageAndRejectsTranslation()
    {
        var handler = new CapturingHandler((request, body) =>
        {
            using var doc = JsonDocument.Parse(body ?? throw new InvalidOperationException("Missing body"));
            Assert.NotNull(request);
            Assert.False(doc.RootElement.TryGetProperty("language", out var _));
            return JsonResponse("""{ "text": "auto", "usage": { "seconds": 0.5 } }""");
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.TranscribeAsync([1], "auto", translate: false, prompt: null, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.TranscribeAsync([1], "en", translate: true, prompt: null, CancellationToken.None));
        Assert.Contains("does not support translation", ex.Message);
    }

    [Fact]
    public async Task ProcessAsync_UsesOpenRouterFreeDefaultWhenCallerDoesNotOverride()
    {
        var handler = new CapturingHandler((_, body) =>
        {
            using var doc = JsonDocument.Parse(body ?? throw new InvalidOperationException("Missing body"));
            Assert.Equal("openrouter/free", doc.RootElement.GetProperty("model").GetString());
            return JsonResponse("""{ "choices": [ { "message": { "content": "default" } } ] }""");
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync("system", "user", "", CancellationToken.None);

        Assert.Equal("default", result);
    }

    [Fact]
    public async Task ProcessAsync_UsesSelectedModelAndOmitsTemperatureForProviderDefault()
    {
        var handler = new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://openrouter.ai/api/v1/chat/completions", request.RequestUri?.ToString());
            using var doc = JsonDocument.Parse(body ?? throw new InvalidOperationException("Missing body"));
            Assert.Equal("selected/model", doc.RootElement.GetProperty("model").GetString());
            Assert.False(doc.RootElement.TryGetProperty("temperature", out _));
            return JsonResponse("""{ "choices": [ { "message": { "content": "done" } } ] }""");
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        host.SetSetting("selectedLlmModel", "selected/model");
        host.SetSetting("userSelectedLlmModel", true);
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync("system", "user", "", CancellationToken.None);

        Assert.Equal("done", result);
    }

    [Fact]
    public async Task ProcessAsync_UsesCallerModelAndCustomTemperature()
    {
        var handler = new CapturingHandler((_, body) =>
        {
            using var doc = JsonDocument.Parse(body ?? throw new InvalidOperationException("Missing body"));
            Assert.Equal("override/model", doc.RootElement.GetProperty("model").GetString());
            Assert.Equal(1.2, doc.RootElement.GetProperty("temperature").GetDouble(), precision: 3);
            return JsonResponse("""{ "choices": [ { "message": { "content": "custom" } } ] }""");
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        host.SetSetting("llmTemperatureMode", "custom");
        host.SetSetting("llmTemperatureValue", 1.2);
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync("system", "user", "override/model", CancellationToken.None);

        Assert.Equal("custom", result);
    }

    [Fact]
    public async Task ProcessStreamingAsync_StreamsDeltas_OmitsTemperatureForProviderDefault()
    {
        string? capturedBody = null;
        var sse = string.Join(
            "\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}",
            "",
            "data: [DONE]",
            "");
        var handler = new CapturingHandler((request, body) =>
        {
            capturedBody = body;
            Assert.Equal(
                "https://openrouter.ai/api/v1/chat/completions",
                request.RequestUri?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (var chunk in sut.ProcessStreamingAsync("system", "user", "", CancellationToken.None))
            chunks.Add(chunk);

        Assert.Equal(new[] { "Hel", "lo" }, chunks);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("openrouter/free", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(2048, doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task ProcessStreamingAsync_StreamsWithCustomTemperature()
    {
        string? capturedBody = null;
        var handler = new CapturingHandler((_, body) =>
        {
            capturedBody = body;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\n\ndata: [DONE]\n",
                    Encoding.UTF8, "text/event-stream"),
            };
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        host.SetSetting("llmTemperatureMode", "custom");
        host.SetSetting("llmTemperatureValue", 1.2);
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (var chunk in sut.ProcessStreamingAsync("system", "user", "override/model", CancellationToken.None))
            chunks.Add(chunk);

        Assert.Equal(new[] { "x" }, chunks);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("override/model", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(1.2, doc.RootElement.GetProperty("temperature").GetDouble(), precision: 3);
    }

    [Fact]
    public async Task ProcessStreamingAsync_ToggleOff_YieldsSingleBulkChunk()
    {
        var handler = new CapturingHandler((_, _) =>
            JsonResponse("""{ "choices": [ { "message": { "content": "bulk" } } ] }"""));

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        host.SetSetting("streamResponses", false);
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (var chunk in sut.ProcessStreamingAsync("system", "user", "", CancellationToken.None))
            chunks.Add(chunk);

        Assert.Single(chunks);
        Assert.Equal("bulk", chunks[0]);
    }

    // Fork-specific: IPluginSettingsProvider coverage (the WPF settings view
    // tests upstream had don't port — fork uses metadata-driven settings).

    [Fact]
    public async Task GetSettingDefinitions_ExposesApiKeyModelsAndTemperatureControls()
    {
        var host = new TestPluginHostServices();
        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        var keys = sut.GetSettingDefinitions().Select(d => d.Key).ToArray();

        Assert.Equal(
            [
                "api-key",
                "selectedTranscriptionModel",
                "selectedLlmModel",
                "llmTemperatureMode",
                "llmTemperatureValue",
                "streamResponses",
            ],
            keys);
    }

    [Fact]
    public async Task SetSettingValueAsync_PersistsLlmModelAndMarksUserSelection()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        await sut.SetSettingValueAsync("selectedLlmModel", "anthropic/claude-sonnet-4");

        Assert.Equal("anthropic/claude-sonnet-4", sut.SelectedLlmModelId);
        Assert.Equal("anthropic/claude-sonnet-4", await sut.GetSettingValueAsync("selectedLlmModel"));
        Assert.True(host.GetSetting<bool?>("userSelectedLlmModel"));
    }

    [Fact]
    public async Task SetSettingValueAsync_PersistsTemperatureModeAndValueWithInvariantCulture()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        await sut.SetSettingValueAsync("llmTemperatureMode", "custom");
        await sut.SetSettingValueAsync("llmTemperatureValue", "1.7");

        Assert.Equal("custom", sut.TemperatureMode);
        Assert.Equal(1.7, sut.TemperatureValue);
        Assert.Equal("custom", await sut.GetSettingValueAsync("llmTemperatureMode"));
        Assert.Equal("1.7", await sut.GetSettingValueAsync("llmTemperatureValue"));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public async Task SetSettingValueAsync_RejectsNonFiniteTemperatureValues(string value)
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        var initial = sut.TemperatureValue;
        await sut.SetSettingValueAsync("llmTemperatureValue", value);

        // Initial default is 0.3; the non-finite input must not have replaced it.
        Assert.Equal(initial, sut.TemperatureValue);
        Assert.False(double.IsNaN(sut.TemperatureValue));
        Assert.True(double.IsFinite(sut.TemperatureValue));
    }

    [Fact]
    public async Task SetSettingValueAsync_ClampsTemperatureToZeroTwoRange()
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        await sut.SetSettingValueAsync("llmTemperatureValue", "10");
        Assert.Equal(2.0, sut.TemperatureValue);

        await sut.SetSettingValueAsync("llmTemperatureValue", "-3");
        Assert.Equal(0.0, sut.TemperatureValue);
    }

    [Fact]
    public async Task ValidateAsync_ReportsFailureWhenApiKeyIsAbsent()
    {
        var host = new TestPluginHostServices();
        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        var result = await sut.ValidateAsync();

        Assert.NotNull(result);
        Assert.False(result!.IsSuccess);
        Assert.Contains("API key", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_FetchesCatalogsAndCreditsAndPersistsSelection()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            var url = request.RequestUri?.ToString();
            return url switch
            {
                "https://openrouter.ai/api/v1/auth/key" =>
                    JsonResponse("""{ "data": { "limit": 10.0, "usage": 2.5 } }"""),
                "https://openrouter.ai/api/v1/models" =>
                    JsonResponse("""
                        {
                          "data": [
                            {
                              "id": "anthropic/claude-sonnet-4",
                              "name": "Claude Sonnet 4",
                              "pricing": { "prompt": "0.000003", "completion": "0.000015" },
                              "architecture": { "modality": "text->text" }
                            }
                          ]
                        }
                        """),
                "https://openrouter.ai/api/v1/models?output_modalities=transcription" =>
                    JsonResponse("""
                        {
                          "data": [
                            {
                              "id": "openai/whisper-large-v3-turbo",
                              "name": "OpenAI: Whisper Large V3 Turbo",
                              "pricing": { "prompt": "0", "completion": "0" },
                              "architecture": { "modality": "audio->text" }
                            }
                          ]
                        }
                        """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new OpenRouterPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ValidateAsync();

        Assert.NotNull(result);
        Assert.True(result!.IsSuccess);
        Assert.Contains("Fetched 2 LLM model(s)", result.Message, StringComparison.Ordinal);
        Assert.Contains("Fetched 1 transcription model(s)", result.Message, StringComparison.Ordinal);
        Assert.Contains("$7.50", result.Message, StringComparison.Ordinal);
        Assert.Equal(
            ["openrouter/free", "anthropic/claude-sonnet-4"],
            sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal(
            ["openai/whisper-large-v3-turbo"],
            sut.TranscriptionModels.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task SetApiKeyAsync_NotifiesOnlyWhenConfigurationStateChanges()
    {
        var host = new TestPluginHostServices();
        var sut = new OpenRouterPlugin();
        await sut.ActivateAsync(host);

        await sut.SetApiKeyAsync("openrouter-key");
        await sut.SetApiKeyAsync("openrouter-key");
        await sut.SetApiKeyAsync("");

        Assert.Equal(2, host.NotifyCapabilitiesChangedCount);
        Assert.False(host.Secrets.ContainsKey("api-key"));
    }

    private static JsonElement LoadManifest()
    {
        var basePath = Path.GetFullPath(AppContext.BaseDirectory);
        var manifestPath = Path.GetFullPath(
            Path.Join("..", "..", "..", "..", "..",
                "plugins", "TypeWhisper.Plugin.OpenRouter", "manifest.json"),
            basePath);
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return doc.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, string?, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request, body);
        }
    }

    private sealed class TestPluginHostServices : IPluginHostServices
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly Dictionary<string, JsonElement> _settings = [];
        public Dictionary<string, string?> Secrets { get; } = [];
        public int NotifyCapabilitiesChangedCount { get; private set; }

        public Task StoreSecretAsync(string key, string value)
        {
            Secrets[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadSecretAsync(string key) =>
            Task.FromResult(Secrets.TryGetValue(key, out var value) ? value : null);

        public Task DeleteSecretAsync(string key)
        {
            Secrets.Remove(key);
            return Task.CompletedTask;
        }

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value)
                ? value.Deserialize<T>(JsonOptions)
                : default;

        public void SetSetting<T>(string key, T value) =>
            _settings[key] = JsonSerializer.SerializeToElement(value, JsonOptions);

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() => NotifyCapabilitiesChangedCount++;
        public IPluginLocalization Localization { get; } = new TestPluginLocalization();
    }

    // Resolve from the plugin's real en.json (source tree) so validation
    // messages come back in English with format args applied — mirroring the
    // host's PluginLocalization in production, instead of echoing raw keys.
    private sealed class TestPluginLocalization : IPluginLocalization
    {
        private static readonly IPluginLocalization s_en = new PluginLocalization(
            Path.GetFullPath(
                Path.Join("..", "..", "..", "..", "..", "plugins", "TypeWhisper.Plugin.OpenRouter"),
                AppContext.BaseDirectory),
            "en");

        public string CurrentLanguage => s_en.CurrentLanguage;
        public IReadOnlyList<string> AvailableLanguages => s_en.AvailableLanguages;
        public string GetString(string key) => s_en.GetString(key);
        public string GetString(string key, params object[] args) => s_en.GetString(key, args);
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
