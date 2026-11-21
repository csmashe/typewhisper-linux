using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Linux.Services;
using TypeWhisper.Plugin.Groq;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

// The CapturingHandler lambdas assert on the outgoing request (method, URI,
// headers, body) and return a canned response. ReSharper reads xUnit asserts
// as precondition checks and concludes those parameters are only validated,
// never used — but asserting on the request is exactly what these tests
// verify, so the inspection is a false positive here.
// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local

namespace TypeWhisper.PluginSystem.Tests;

public class GroqPluginTests
{
    [Fact]
    public Task RequestFailures_AreClassified() =>
        ProviderFailureAssertions.VerifyAsync<GroqPlugin>();


    private static readonly JsonSerializerOptions s_manifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifestPath = Path.GetFullPath(
            Path.Join(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "plugins",
                "TypeWhisper.Plugin.Groq",
                "manifest.json"
            )
        );
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            s_manifestJsonOptions
        );

        var sut = new GroqPlugin();

        Assert.NotNull(manifest);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    [Fact]
    public void TranscriptionModels_RemainCuratedWhisperModels()
    {
        var sut = new GroqPlugin();

        var ids = sut.TranscriptionModels.Select(m => m.Id).ToArray();

        Assert.Equal(["whisper-large-v3", "whisper-large-v3-turbo"], ids);
    }

    [Fact]
    public void SupportedLanguages_AreWhisperLanguageCodes()
    {
        using var groq = new GroqPlugin();

        Assert.Contains("en", groq.SupportedLanguages);
        Assert.Contains("de", groq.SupportedLanguages);
        Assert.Contains("yue", groq.SupportedLanguages);
        Assert.Contains("haw", groq.SupportedLanguages);
        Assert.DoesNotContain("de-DE", groq.SupportedLanguages);
        Assert.All(groq.TranscriptionModels, model =>
            Assert.Equal(groq.SupportedLanguages.Count, model.LanguageCount));
    }

    [Fact]
    public void ExplicitRegionalTag_FoldsToBaseCodeBeforeUpload()
    {
        using var groq = new GroqPlugin();

        // Groq lists base codes only, so regional tags fold; unknown bases still throw.
        Assert.Equal("de", groq.ToLegacyLanguage(LanguageSelection.Explicit("de-DE")));
        Assert.Equal("de", groq.ToLegacyLanguage(LanguageSelection.Explicit("de")));
        Assert.Throws<TranscriptionLanguageNotSupportedException>(() =>
            groq.ToLegacyLanguage(LanguageSelection.Explicit("xx-YY")));
        Assert.Null(groq.ToLegacyLanguage(LanguageSelection.Automatic));
    }

    [Theory]
    [InlineData("whisper-large-v3", false)]
    [InlineData("distil-whisper-large-v3-en", false)]
    [InlineData("meta-llama/llama-3.3-70b-versatile-tool-use-preview", false)]
    [InlineData("playai-tts", false)]
    [InlineData("orpheus-tts-1", false)]
    [InlineData("llama-guard-prompt-guard", false)]
    [InlineData("openai/gpt-oss-safeguard-20b", false)]
    [InlineData("llama-3.1-8b-instant", true)]
    [InlineData("openai/gpt-oss-120b", true)]
    public void IsLlmModel_FiltersGroqModelIds(string modelId, bool expected)
    {
        Assert.Equal(expected, GroqPlugin.IsLlmModel(modelId));
    }

    [Fact]
    public async Task ActivateAsync_RestoresFetchedModelsAndFallsBackFromUnofferedSelectedLlmModel()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };
        host.SetSetting(
            "fetchedLlmModels",
            new List<FetchedLlmModel>
            {
                new("allam-2-7b", "SDAIA"),
                new("openai/gpt-oss-120b", "OpenAI"),
                new("openai/gpt-oss-20b", "OpenAI"),
            }
        );
        host.SetSetting("fetchedLlmModelsAtUtc", DateTimeOffset.UtcNow);
        host.SetSetting("selectedLlmModel", "whisper-large-v3");

        using var sut = new GroqPlugin(new HttpClient());
        await sut.ActivateAsync(host);

        Assert.Equal(GroqPlugin.DefaultLlmModelId, sut.SelectedLlmModelId);
        Assert.Equal(
            ["openai/gpt-oss-120b", "allam-2-7b", "openai/gpt-oss-20b"],
            sut.SupportedModels.Select(m => m.Id).ToArray()
        );
        // The saved choice stays so a later list that offers it can apply it again.
        Assert.Equal("whisper-large-v3", host.GetSetting<string>("selectedLlmModel"));
    }

    [Fact]
    public async Task ActivateAsync_UsesGptOss120BWhenSelectedLlmModelUnset()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };

        using var sut = new GroqPlugin(new HttpClient());
        await sut.ActivateAsync(host);

        Assert.Equal("openai/gpt-oss-120b", sut.SelectedLlmModelId);
        Assert.Equal("openai/gpt-oss-120b", sut.SupportedModels[0].Id);
    }

    [Fact]
    public async Task ActivateAsync_SavedRetiredLlamaModelFallsBackToGptOss120B()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };
        host.SetSetting("selectedLlmModel", "llama-3.3-70b-versatile");

        using var sut = new GroqPlugin(new HttpClient());
        await sut.ActivateAsync(host);

        Assert.Equal(GroqPlugin.DefaultLlmModelId, sut.SelectedLlmModelId);
        Assert.DoesNotContain(
            sut.SupportedModels,
            m => m.Id is "llama-3.3-70b-versatile" or "llama-3.1-8b-instant" or "moonshotai/kimi-k2-instruct-0905"
        );
    }

    [Fact]
    public async Task ActivateAsync_RefreshesFetchedListWithoutFetchDate()
    {
        var handler = new CapturingHandler((_, _) =>
            JsonResponse("""{"data":[{"id":"openai/gpt-oss-120b"},{"id":"qwen/qwen3.8-27b"}]}"""));
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };
        host.SetSetting(
            "fetchedLlmModels",
            new List<FetchedLlmModel> { new("llama-3.3-70b-versatile", "Meta") }
        );
        host.SetSetting("selectedLlmModel", "llama-3.3-70b-versatile");

        using var sut = new GroqPlugin(new HttpClient(handler), refreshLlmModelsAutomatically: true);
        await sut.ActivateAsync(host);
        await sut.LlmModelRefreshTask;

        Assert.Equal(["openai/gpt-oss-120b", "qwen/qwen3.8-27b"], sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.NotNull(sut.FetchedLlmModelsAt);
        Assert.Equal(GroqPlugin.DefaultLlmModelId, sut.SelectedLlmModelId);
    }

    [Fact]
    public async Task ActivateAsync_KeepsUndatedListWhenItsRefreshFails()
    {
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };
        host.SetSetting(
            "fetchedLlmModels",
            new List<FetchedLlmModel> { new("openai/gpt-oss-120b", "OpenAI"), new("qwen/qwen3-32b", "Alibaba") }
        );
        host.SetSetting("selectedLlmModel", "qwen/qwen3-32b");

        using var sut = new GroqPlugin(new HttpClient(handler), refreshLlmModelsAutomatically: true);
        await sut.ActivateAsync(host);
        await sut.LlmModelRefreshTask;

        // A model outside the fallback list keeps working while Groq's model list is unreachable.
        Assert.Equal("qwen/qwen3-32b", sut.SelectedLlmModelId);
        Assert.Contains(sut.SupportedModels, m => m.Id == "qwen/qwen3-32b");
        Assert.Null(sut.FetchedLlmModelsAt);
    }

    [Fact]
    public async Task ProcessAsync_RefreshesAListThatExpiredDuringTheSession()
    {
        var clock = new ManualClock { Now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero) };
        var modelRequests = 0;
        var handler = new CapturingHandler((request, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
                return JsonResponse("""{"choices":[{"message":{"content":"done"}}]}""");

            modelRequests++;
            return JsonResponse("""{"data":[{"id":"openai/gpt-oss-120b"}]}""");
        });
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };
        host.SetSetting("fetchedLlmModels", new List<FetchedLlmModel> { new("openai/gpt-oss-20b", "OpenAI") });
        host.SetSetting("fetchedLlmModelsAtUtc", clock.Now - TimeSpan.FromHours(1));

        using var sut = new GroqPlugin(new HttpClient(handler), refreshLlmModelsAutomatically: true, clock);
        await sut.ActivateAsync(host);
        await sut.ProcessAsync("system", "user", "", CancellationToken.None);
        await sut.LlmModelRefreshTask;
        Assert.Equal(0, modelRequests);

        clock.Now += TimeSpan.FromHours(24);
        await sut.ProcessAsync("system", "user", "", CancellationToken.None);
        await sut.LlmModelRefreshTask;

        Assert.Equal(1, modelRequests);
        Assert.Equal(["openai/gpt-oss-120b"], sut.SupportedModels.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task ProcessAsync_RetriesAFailedRefreshOnlyAfterTheRetryDelay()
    {
        var clock = new ManualClock { Now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero) };
        var modelRequests = 0;
        var modelsAvailable = false;
        var handler = new CapturingHandler((request, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
                return JsonResponse("""{"choices":[{"message":{"content":"done"}}]}""");

            modelRequests++;
            // ReSharper disable once AccessToModifiedClosure -- the test flips it to simulate Groq recovering.
            return modelsAvailable
                ? JsonResponse("""{"data":[{"id":"openai/gpt-oss-120b"}]}""")
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };

        using var sut = new GroqPlugin(new HttpClient(handler), refreshLlmModelsAutomatically: true, clock);
        await sut.ActivateAsync(host);
        await sut.LlmModelRefreshTask;
        Assert.Equal(1, modelRequests);

        clock.Now += GroqPlugin.s_llmModelRefreshRetryDelay - TimeSpan.FromMinutes(1);
        await sut.ProcessAsync("system", "user", "", CancellationToken.None);
        await sut.LlmModelRefreshTask;
        Assert.Equal(1, modelRequests);

        modelsAvailable = true;
        clock.Now += TimeSpan.FromMinutes(1);
        await sut.ProcessAsync("system", "user", "", CancellationToken.None);
        await sut.LlmModelRefreshTask;

        Assert.Equal(2, modelRequests);
        Assert.Equal(clock.Now, sut.FetchedLlmModelsAt);
    }

    [Fact]
    public async Task SetFetchedLlmModels_AppliesSavedModelOnceTheListOffersIt()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };
        host.SetSetting("selectedLlmModel", "qwen/qwen3-32b");

        using var sut = new GroqPlugin(new HttpClient());
        await sut.ActivateAsync(host);
        Assert.Equal(GroqPlugin.DefaultLlmModelId, sut.SelectedLlmModelId);

        sut.SetFetchedLlmModels(
            [new FetchedLlmModel("openai/gpt-oss-120b", "OpenAI"), new FetchedLlmModel("qwen/qwen3-32b", "Alibaba")]
        );

        Assert.Equal("qwen/qwen3-32b", sut.SelectedLlmModelId);
    }

    [Fact]
    public async Task ActivateAsync_RefreshesListOlderThanADayInTheBackground()
    {
        var clock = new ManualClock { Now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero) };
        var requests = 0;
        var handler = new CapturingHandler((request, _) =>
        {
            requests++;
            Assert.Equal("https://api.groq.com/openai/v1/models", request.RequestUri?.ToString());
            return JsonResponse(
                """{"data":[{"id":"openai/gpt-oss-20b"},{"id":"openai/gpt-oss-120b"},{"id":"whisper-large-v3"}]}"""
            );
        });
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };
        host.SetSetting("fetchedLlmModels", new List<FetchedLlmModel> { new("llama-3.3-70b-versatile", "Meta") });
        host.SetSetting("fetchedLlmModelsAtUtc", clock.Now - TimeSpan.FromHours(25));

        using var sut = new GroqPlugin(new HttpClient(handler), refreshLlmModelsAutomatically: true, clock);
        await sut.ActivateAsync(host);
        await sut.LlmModelRefreshTask;

        Assert.Equal(1, requests);
        Assert.Equal(["openai/gpt-oss-120b", "openai/gpt-oss-20b"], sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal(clock.Now, sut.FetchedLlmModelsAt);
        Assert.Equal(clock.Now, host.GetSetting<DateTimeOffset?>("fetchedLlmModelsAtUtc"));
        Assert.Equal(GroqPlugin.DefaultLlmModelId, sut.SelectedLlmModelId);
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task ActivateAsync_KeepsListFetchedWithinTheLastDay()
    {
        var clock = new ManualClock { Now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero) };
        var requests = 0;
        var handler = new CapturingHandler((_, _) =>
        {
            requests++;
            return JsonResponse("""{"data":[]}""");
        });
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };
        host.SetSetting("fetchedLlmModels", new List<FetchedLlmModel> { new("openai/gpt-oss-20b", "OpenAI") });
        host.SetSetting("fetchedLlmModelsAtUtc", clock.Now - TimeSpan.FromHours(23));

        using var sut = new GroqPlugin(new HttpClient(handler), refreshLlmModelsAutomatically: true, clock);
        await sut.ActivateAsync(host);
        await sut.LlmModelRefreshTask;

        Assert.Equal(0, requests);
        Assert.Equal(["openai/gpt-oss-20b"], sut.SupportedModels.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task BackgroundRefresh_KeepsCurrentListWhenGroqFails()
    {
        var clock = new ManualClock { Now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero) };
        var fetchedAt = clock.Now - TimeSpan.FromDays(3);
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };
        host.SetSetting("fetchedLlmModels", new List<FetchedLlmModel> { new("openai/gpt-oss-20b", "OpenAI") });
        host.SetSetting("fetchedLlmModelsAtUtc", fetchedAt);

        using var sut = new GroqPlugin(new HttpClient(handler), refreshLlmModelsAutomatically: true, clock);
        await sut.ActivateAsync(host);
        await sut.LlmModelRefreshTask;

        Assert.Equal(["openai/gpt-oss-20b"], sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal(fetchedAt, sut.FetchedLlmModelsAt);
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task BackgroundRefresh_DropsAListThatArrivesAfterTheKeyChanged()
    {
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var handler = new BlockingHandler(started, release);
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "old-key" } };

        using var sut = new GroqPlugin(new HttpClient(handler), refreshLlmModelsAutomatically: true);
        await sut.ActivateAsync(host);
        var oldKeyRefresh = sut.LlmModelRefreshTask;
        await started.Task;

        await sut.SetApiKeyAsync("");
        release.SetResult();
        await oldKeyRefresh;

        Assert.Empty(sut.FetchedLlmModels);
        Assert.Null(sut.FetchedLlmModelsAt);
    }

    [Fact]
    public async Task FetchLlmModelsAsync_FiltersAndSortsGroqResults()
    {
        var handler = new CapturingHandler((request, _) =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("Bearer groq-key", request.Headers.Authorization?.ToString());

                return JsonResponse(
                    """
                    {
                      "data": [
                        { "id": "whisper-large-v3", "owned_by": "OpenAI" },
                        { "id": "openai/gpt-oss-120b", "owned_by": "OpenAI" },
                        { "id": "llama-3.1-8b-instant", "owned_by": "Meta" },
                        { "id": "orpheus-tts-1", "owned_by": "Groq" }
                      ]
                    }
                    """
                );
            }
        );

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };

        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new GroqPlugin(httpClient);
        await sut.ActivateAsync(host);

        var models = await sut.FetchLlmModelsAsync();

        Assert.NotNull(models);
        Assert.Equal(
            ["llama-3.1-8b-instant", "openai/gpt-oss-120b"],
            models.Select(m => m.Id).ToArray()
        );
    }

    [Fact]
    public async Task ProcessAsync_UsesSelectedLlmModelWhenCallerDoesNotOverride()
    {
        var handler = new CapturingHandler((_, body) =>
            {
                using var doc = JsonDocument.Parse(
                    body ?? throw new InvalidOperationException("Missing request body.")
                );
                Assert.Equal(
                    "openai/gpt-oss-120b",
                    doc.RootElement.GetProperty("model").GetString()
                );

                return JsonResponse(
                    """
                    {
                      "choices": [
                        {
                          "message": {
                            "content": "done"
                          }
                        }
                      ]
                    }
                    """
                );
            }
        );

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };
        host.SetSetting("selectedLlmModel", "openai/gpt-oss-120b");

        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new GroqPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync("system", "user", "", CancellationToken.None);

        Assert.Equal("done", result);
    }

    [Fact]
    public async Task ProcessAsync_HidesReasoningForQwenModels()
    {
        var handler = new CapturingHandler((_, body) =>
            {
                using var doc = JsonDocument.Parse(
                    body ?? throw new InvalidOperationException("Missing request body.")
                );
                Assert.Equal("qwen/qwen3-32b", doc.RootElement.GetProperty("model").GetString());
                Assert.Equal("hidden", doc.RootElement.GetProperty("reasoning_format").GetString());

                return JsonResponse("""{"choices":[{"message":{"content":"final answer"}}]}""");
            }
        );

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };

        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new GroqPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync("system", "user", "qwen/qwen3-32b", CancellationToken.None);

        Assert.Equal("final answer", result);
    }

    [Fact]
    public async Task ProcessAsync_DoesNotSendReasoningFormatForOtherModels()
    {
        var handler = new CapturingHandler((request, body) =>
            {
                using var doc = JsonDocument.Parse(
                    body ?? throw new InvalidOperationException("Missing request body.")
                );
                Assert.Equal("llama-3.3-70b-versatile", doc.RootElement.GetProperty("model").GetString());
                Assert.False(doc.RootElement.TryGetProperty("reasoning_format", out _));

                return JsonResponse("""{"choices":[{"message":{"content":"final answer"}}]}""");
            }
        );

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };

        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new GroqPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync(
            "system", "user", "llama-3.3-70b-versatile", CancellationToken.None
        );

        Assert.Equal("final answer", result);
    }

    [Fact]
    public async Task ProcessStreamingAsync_HidesReasoningForQwenModels()
    {
        var sse = string.Join(
            "\n",
            """data: {"choices":[{"delta":{"content":"Hel"}}]}""",
            "",
            """data: {"choices":[{"delta":{"content":"lo"}}]}""",
            "",
            "data: [DONE]",
            "",
            ""
        );
        var handler = new CapturingHandler((_, body) =>
            {
                using var doc = JsonDocument.Parse(
                    body ?? throw new InvalidOperationException("Missing request body.")
                );
                Assert.Equal("qwen/qwen3-32b", doc.RootElement.GetProperty("model").GetString());
                Assert.Equal("hidden", doc.RootElement.GetProperty("reasoning_format").GetString());
                Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
                };
            }
        );

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };

        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new GroqPlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (
            var chunk in sut.ProcessStreamingAsync("system", "user", "qwen/qwen3-32b", CancellationToken.None)
        )
            chunks.Add(chunk);

        Assert.Equal(["Hel", "lo"], chunks);
    }

    [Fact]
    public async Task ProcessAsync_UsesFallbackLlmModelWhenSelectionMissing()
    {
        var handler = new CapturingHandler((_, body) =>
            {
                using var doc = JsonDocument.Parse(
                    body ?? throw new InvalidOperationException("Missing request body.")
                );
                Assert.Equal(
                    "openai/gpt-oss-120b",
                    doc.RootElement.GetProperty("model").GetString()
                );

                return JsonResponse(
                    """
                    {
                      "choices": [
                        {
                          "message": {
                            "content": "fallback"
                          }
                        }
                      ]
                    }
                    """
                );
            }
        );

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };

        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new GroqPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync("system", "user", "", CancellationToken.None);

        Assert.Equal("fallback", result);
    }

    [Fact]
    public async Task SetApiKeyAsync_DoesNotNotifyWhenPrefilledValueIsWrittenBack()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };

        using var sut = new GroqPlugin(new HttpClient());
        await sut.ActivateAsync(host);

        await sut.SetApiKeyAsync("groq-key");

        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SetApiKeyAsync_NotifiesWhenConfigurationStateChanges()
    {
        var host = new TestPluginHostServices();
        using var sut = new GroqPlugin(new HttpClient());
        await sut.ActivateAsync(host);

        await sut.SetApiKeyAsync("groq-key");
        await sut.SetApiKeyAsync("");

        Assert.Equal(2, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SetApiKeyAsync_ClearsFetchedLlmModelsAndSelectionWhenKeyChanges()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "old-key" } };
        host.SetSetting(
            "fetchedLlmModels",
            new List<FetchedLlmModel> { new("custom-model-from-old-account", "Old") }
        );
        host.SetSetting("fetchedLlmModelsAtUtc", DateTimeOffset.UtcNow);
        host.SetSetting("selectedLlmModel", "custom-model-from-old-account");

        using var sut = new GroqPlugin(new HttpClient());
        await sut.ActivateAsync(host);
        Assert.Equal("custom-model-from-old-account", sut.SelectedLlmModelId);

        await sut.SetApiKeyAsync("new-key");

        Assert.Empty(sut.FetchedLlmModels);
        Assert.Null(sut.FetchedLlmModelsAt);
        Assert.Equal(GroqPlugin.DefaultLlmModelId, sut.SelectedLlmModelId);
        Assert.Empty(host.GetSetting<List<FetchedLlmModel>>("fetchedLlmModels")!);
        Assert.Null(host.GetSetting<string>("selectedLlmModel"));
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task ProcessStreamingAsync_StreamsDeltasInOrder_UsingSelectedModel()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var sse = string.Join(
            "\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}",
            "",
            "data: [DONE]",
            "",
            ""
        );
        var handler = new CapturingHandler((request, body) =>
            {
                capturedRequest = request;
                capturedBody = body;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
                };
            }
        );

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };
        host.SetSetting("selectedLlmModel", "openai/gpt-oss-120b");

        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new GroqPlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (
            var chunk in sut.ProcessStreamingAsync("system", "user", "", CancellationToken.None)
        )
            chunks.Add(chunk);

        Assert.Equal(["Hel", "lo"], chunks);
        Assert.Equal(
            "https://api.groq.com/openai/v1/chat/completions",
            capturedRequest?.RequestUri?.ToString()
        );
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("openai/gpt-oss-120b", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ProcessStreamingAsync_ToggleOff_YieldsSingleBulkChunk()
    {
        var handler = new CapturingHandler(
            (_, _) => JsonResponse("""{"choices":[{"message":{"content":"bulk"}}]}""")
        );

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };
        host.SetSetting("selectedLlmModel", "openai/gpt-oss-120b");
        host.SetSetting("streamResponses", false);

        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new GroqPlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (
            var chunk in sut.ProcessStreamingAsync("system", "user", "", CancellationToken.None)
        )
            chunks.Add(chunk);

        Assert.Single(chunks);
        Assert.Equal("bulk", chunks[0]);
    }

    [Fact]
    public async Task TranscribeAsync_TranslateWithNonTranslatingModel_FailsBeforeUpload()
    {
        var requestCount = 0;
        var handler = new CapturingHandler((_, _) =>
        {
            requestCount++;
            return JsonResponse("""{"text":"ok"}""");
        });
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };
        using var httpClient = new HttpClient(handler);
        var sut = new GroqPlugin(httpClient);
        await sut.ActivateAsync(host);
        sut.SelectModel("whisper-large-v3-turbo");

        var exception = await Assert.ThrowsAsync<PluginRequestException>(
            () => sut.TranscribeAsync([1, 2, 3], "de", true, null, CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.InvalidRequest, exception.FailureKind);
        Assert.Contains("Settings.TranslationUnsupported", exception.Message);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task TranscribeAsync_TranslateWithWhisperLargeV3_OmitsSourceLanguage()
    {
        Uri? requestUri = null;
        string? requestBody = null;
        var handler = new CapturingHandler((request, body) =>
        {
            requestUri = request.RequestUri;
            requestBody = body;
            return JsonResponse("""{"text":"ok"}""");
        });
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };
        using var httpClient = new HttpClient(handler);
        var sut = new GroqPlugin(httpClient);
        await sut.ActivateAsync(host);
        sut.SelectModel("whisper-large-v3");

        await sut.TranscribeAsync([1, 2, 3], "de", true, null, CancellationToken.None);

        Assert.Equal("https://api.groq.com/openai/v1/audio/translations", requestUri?.ToString());
        Assert.NotNull(requestBody);
        Assert.DoesNotContain("name=language", requestBody);
        Assert.DoesNotContain("name=\"language\"", requestBody);
    }

    [Fact]
    public async Task TranscribeAsync_TranscribeSendsSourceLanguage()
    {
        Uri? requestUri = null;
        string? requestBody = null;
        var handler = new CapturingHandler((request, body) =>
        {
            requestUri = request.RequestUri;
            requestBody = body;
            return JsonResponse("""{"text":"ok"}""");
        });
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };
        using var httpClient = new HttpClient(handler);
        var sut = new GroqPlugin(httpClient);
        await sut.ActivateAsync(host);
        sut.SelectModel("whisper-large-v3");

        await sut.TranscribeAsync([1, 2, 3], "de", false, null, CancellationToken.None);

        Assert.Equal("https://api.groq.com/openai/v1/audio/transcriptions", requestUri?.ToString());
        Assert.Contains("name=language\r\n\r\nde\r\n", requestBody);
    }

    [Fact]
    public async Task TranscribeAsync_ReportsPerSegmentNoSpeechProbability()
    {
        var handler = new CapturingHandler((_, body) =>
        {
            Assert.Contains("verbose_json", body);
            return JsonResponse("""
                {
                    "text": "Please send the updated draft. Thank you.",
                    "language": "en",
                    "duration": 8.0,
                    "segments": [
                        { "text": " Please send the updated draft.", "start": 0.0, "end": 5.0, "no_speech_prob": 0.02 },
                        { "text": " Thank you.", "start": 5.0, "end": 8.0, "no_speech_prob": 0.95 }
                    ]
                }
                """);
        });
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "groq-key" } };
        using var httpClient = new HttpClient(handler);
        var sut = new GroqPlugin(httpClient);
        await sut.ActivateAsync(host);
        sut.SelectModel("whisper-large-v3");

        var result = await sut.TranscribeAsync([4, 5, 6], "en", false, null, CancellationToken.None);

        Assert.Equal("Please send the updated draft. Thank you.", result.Text);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(0.95f, result.Segments[1].NoSpeechProbability);
        Assert.Equal(0.02f, result.NoSpeechProbability);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, string?, HttpResponseMessage> responder
    ) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request, body);
        }
    }

    private sealed class BlockingHandler(TaskCompletionSource started, TaskCompletionSource release)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            started.TrySetResult();
            // Ignores cancellation on purpose: the result must arrive after the key changed.
            await release.Task;
            return JsonResponse("""{"data":[{"id":"openai/gpt-oss-120b"}]}""");
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
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

        public Task StoreSecretAsync(string key, string value)
        {
            Secrets[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadSecretAsync(string key)
        {
            return Task.FromResult(Secrets.GetValueOrDefault(key));
        }

        public Task DeleteSecretAsync(string key)
        {
            Secrets.Remove(key);
            return Task.CompletedTask;
        }

        public T? GetSetting<T>(string key)
        {
            return _settings.TryGetValue(key, out var value) ? value.Deserialize<T>(s_jsonOptions) : default;
        }

        public void SetSetting<T>(string key, T value)
        {
            _settings[key] = JsonSerializer.SerializeToElement(value, s_jsonOptions);
        }

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];

        public void Log(PluginLogLevel level, string message) { }

        public void NotifyCapabilitiesChanged()
        {
            NotifyCapabilitiesChangedCount++;
        }

        public IPluginLocalization Localization { get; } = new TestPluginLocalization();
    }

    private sealed class TestPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];

        public string GetString(string key)
        {
            return key;
        }

        public string GetString(string key, params object[] args)
        {
            return string.Format(key, args);
        }
    }

    private sealed class TestPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent)
            where T : PluginEvent
        {
        }

        public IDisposable Subscribe<T>(Func<T, Task> handler)
            where T : PluginEvent
        {
            return new NoOpDisposable();
        }
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
