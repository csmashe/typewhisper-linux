using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Tests;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Gemini;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class GeminiPluginTests
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifestPath = Path.Join(
            RepositoryRoot(),
            "plugins",
            "TypeWhisper.Plugin.Gemini",
            "manifest.json");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            s_jsonOptions);

        using var sut = new GeminiPlugin();

        Assert.NotNull(manifest);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    [Fact]
    public void SupportedModels_UsesCurrentAliasesWithExplicitDefault()
    {
        using var sut = new GeminiPlugin();

        Assert.Equal(
            ["gemini-flash-lite-latest", "gemini-flash-latest", "gemini-pro-latest",
                "gemini-2.5-flash", "gemini-2.5-pro", "gemini-2.5-flash-lite"],
            sut.SupportedModels.Select(model => model.Id).ToArray());
        Assert.Equal(GeminiPlugin.DefaultModel, Assert.Single(sut.SupportedModels, model => model.IsRecommended).Id);
    }

    [Theory]
    [InlineData("models/gemini-3.7-flash", true)]
    [InlineData("gemini-3.1-pro-preview", true)]
    [InlineData("gemma-4-31b-it", true)]
    [InlineData("gemini-3.1-flash-image", false)]
    [InlineData("gemini-embedding-2", false)]
    [InlineData("gemini-3.1-flash-tts-preview", false)]
    [InlineData("gemini-3.1-flash-live-preview", false)]
    [InlineData("gemini-3.5-transcribe", false)]
    [InlineData("gemini-3.5-transcribe-live", false)]
    [InlineData("gemini-omni-flash", false)]
    [InlineData("gemini-omni-flash-preview", false)]
    [InlineData("gemini-robotics-er-2-preview", false)]
    [InlineData("deep-research-preview", false)]
    [InlineData("veo-3.1-generate-preview", false)]
    [InlineData("imagen-4.0-generate-001", false)]
    [InlineData("aqa", false)]
    [InlineData("gemini-vision-only", false)]
    [InlineData("gemini-veo", false)]
    [InlineData("gemini-imagen", false)]
    [InlineData("gemini-aqa", false)]
    [InlineData("gemini-2.5-computer-use-preview", false)]
    [InlineData("gemini-2.5-flash-native-audio", false)]
    [InlineData("gemini-deep-research-preview", false)]
    [InlineData("models/GEMINI-3.7-FLASH", true)]
    [InlineData("gemma-3-27b-it", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsCompatibleChatModelId_FiltersCatalog(string modelId, bool expected)
    {
        Assert.Equal(expected, GeminiPlugin.IsCompatibleChatModelId(modelId));
    }

    [Fact]
    public async Task ActivateAsync_RestoresNormalizedCachedModels()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = " gemini-key " } };
        host.SetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2",
        [
            new GeminiFetchedModel("models/gemma-4-31b-it", " Gemma 4 31B IT "),
            new GeminiFetchedModel("models/gemini-flash-latest", "Gemini Flash Latest"),
            new GeminiFetchedModel("models/gemini-3.1-flash-image", "Nano Banana"),
        ]);
        using var sut = new GeminiPlugin();

        await sut.ActivateAsync(host);

        Assert.True(sut.IsAvailable);
        Assert.Equal(
            ["gemini-flash-latest", "gemma-4-31b-it"],
            sut.SupportedModels.Select(model => model.Id).ToArray());
        Assert.Equal("Gemma 4 31B IT", sut.SupportedModels[^1].DisplayName);
    }

    [Fact]
    public async Task FetchLlmModelsAsync_UsesCompatibleEndpointAndFiltersSparseResults()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new CapturingHandler((request, _) =>
        {
            capturedRequest = request;
            return JsonResponse("""
                {
                  "object": "list",
                  "data": [
                    null,
                    { "id": null, "display_name": "Missing ID" },
                    { "object": "model", "display_name": "No ID" },
                    { "id": "models/gemini-3.7-flash", "display_name": "Gemini 3.7 Flash" },
                    { "id": "models/gemma-4-31b-it", "display_name": "Gemma 4 31B IT" },
                    { "id": "gemini-3.7-flash", "display_name": "Duplicate" },
                    { "id": "models/gemini-3.1-flash-image", "display_name": "Nano Banana" },
                    { "id": "models/gemini-embedding-2", "display_name": "Embedding" },
                    { "id": "models/veo-3.1-generate-preview", "display_name": "Veo" }
                  ]
                }
                """);
        });
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "gemini-key" } };
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var models = await sut.FetchLlmModelsAsync();

        Assert.NotNull(models);
        Assert.Equal(
            ["gemini-3.7-flash", "gemma-4-31b-it"],
            models.Select(model => model.Id).ToArray());
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/models", capturedRequest?.RequestUri?.ToString());
        Assert.Equal("Bearer gemini-key", capturedRequest?.Headers.Authorization?.ToString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task FetchLlmModelsAsync_RejectsCatalogWithoutDataArray(string json)
    {
        var handler = new CapturingHandler((_, _) => JsonResponse(json));
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "gemini-key" } };
        host.SetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2",
        [
            new GeminiFetchedModel("gemini-3.7-flash", "Gemini 3.7 Flash"),
        ]);
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.FetchLlmModelsAsync();

        Assert.Null(result);
        Assert.Contains(sut.FetchedLlmModels, model => model.Id == "gemini-3.7-flash");
        Assert.Contains(host.Logs, entry =>
            entry.Level == PluginLogLevel.Warning
            && entry.Message.Contains("data array", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FetchLlmModelsAsync_AcceptsExplicitEmptyCatalog()
    {
        var handler = new CapturingHandler((_, _) => JsonResponse("""{"data":[]}"""));
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "gemini-key" } };
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.FetchLlmModelsAsync();

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task SetFetchedLlmModels_KeepsCurrentFetchedFlashModelFirstAndNotifiesHost()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);

        await sut.SetFetchedLlmModelsAsync(
        [
            new GeminiFetchedModel("gemini-3.7-flash", "Gemini 3.7 Flash"),
            new GeminiFetchedModel("gemma-4-26b-a4b-it", "Gemma 4 26B MoE IT"),
        ]);

        Assert.Equal(
            ["gemini-3.7-flash", "gemma-4-26b-a4b-it"],
            sut.SupportedModels.Select(model => model.Id).ToArray());
        Assert.True(sut.SupportedModels[0].IsRecommended);
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
        Assert.Equal(
            ["gemini-3.7-flash", "gemma-4-26b-a4b-it"],
            host.GetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2")!
                .Select(model => model.Id)
                .ToArray());
    }

    [Fact]
    public async Task SetFetchedLlmModels_DoesNotPersistOrNotifyForUnchangedCatalog()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);

        await sut.SetFetchedLlmModelsAsync([new GeminiFetchedModel("gemini-3.7-flash", "Gemini 3.7 Flash")]);
        await sut.SetFetchedLlmModelsAsync([new GeminiFetchedModel("models/gemini-3.7-flash", "Gemini 3.7 Flash")]);

        Assert.Equal(2, host.SetSettingCount);
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SetApiKeyAsync_ClearsCatalogOwnedByPreviousKey()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "first-key" } };
        host.SetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2",
        [
            new GeminiFetchedModel("gemini-3.7-flash", "Gemini 3.7 Flash"),
        ]);
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);
        host.ResetTracking();

        await sut.SetApiKeyAsync("second-key");

        Assert.Equal("second-key", host.Secrets["api-key"]);
        Assert.Equal(
            ["gemini-flash-lite-latest", "gemini-flash-latest", "gemini-pro-latest",
                "gemini-2.5-flash", "gemini-2.5-pro", "gemini-2.5-flash-lite"],
            sut.SupportedModels.Select(model => model.Id).ToArray());
        Assert.Empty(host.GetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2")!);
        Assert.Equal(1, host.SetSettingCount);
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SetApiKeyAsync_LeavesStateUnchangedWhenSecretWriteFails()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "first-key" } };
        host.SetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2",
        [
            new GeminiFetchedModel("gemini-3.7-flash", "Gemini 3.7 Flash"),
        ]);
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);
        host.ResetTracking();
        host.StoreSecretException = new InvalidOperationException("secret write failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.SetApiKeyAsync("second-key"));

        Assert.Equal("first-key", sut.ApiKey);
        Assert.Equal("first-key", host.Secrets["api-key"]);
        Assert.Contains(sut.FetchedLlmModels, model => model.Id == "gemini-3.7-flash");
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SetApiKeyAsync_RestoresSecretWhenCatalogWriteFails()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "first-key" } };
        host.SetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2",
        [
            new GeminiFetchedModel("gemini-3.7-flash", "Gemini 3.7 Flash"),
        ]);
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);
        host.ResetTracking();
        host.SetSettingExceptionAfterWrite = new InvalidOperationException("catalog write failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.SetApiKeyAsync("second-key"));

        Assert.Equal("first-key", sut.ApiKey);
        Assert.Equal("first-key", host.Secrets["api-key"]);
        Assert.Contains(sut.FetchedLlmModels, model => model.Id == "gemini-3.7-flash");
        Assert.Contains(
            host.GetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2")!,
            model => model.Id == "gemini-3.7-flash");
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SetFetchedLlmModelsAsync_LeavesStateUnchangedWhenSettingWriteFails()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "gemini-key" } };
        List<GeminiFetchedModel> previousCatalog = [new("gemini-2.5-flash", "Gemini 2.5 Flash")];
        host.SetSetting("fetchedLlmModels.v2", previousCatalog);
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);
        host.SetSettingExceptionAfterWrite = new InvalidOperationException("catalog write failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.SetFetchedLlmModelsAsync(
            [new GeminiFetchedModel("gemini-3.7-flash", "Gemini 3.7 Flash")]));

        Assert.Equal(previousCatalog, sut.FetchedLlmModels);
        Assert.Equal(previousCatalog, host.GetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2"));
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
        using var restored = new GeminiPlugin();
        await restored.ActivateAsync(host);
        Assert.Equal(previousCatalog, restored.FetchedLlmModels);
        Assert.Equal("gemini-2.5-flash", restored.SupportedModels[0].Id);
    }

    [Fact]
    public async Task ProcessAsync_UsesDocumentedGeminiChatEndpoint()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new CapturingHandler((request, body) =>
        {
            capturedRequest = request;
            capturedBody = body;
            return JsonResponse("""{"choices":[{"message":{"content":"  refreshed result  "}}]}""");
        });
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "gemini-key" } };
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync(
            "Clean up technical dictation",
            "hello world",
            "gemini-3.7-flash",
            CancellationToken.None);

        Assert.Equal("refreshed result", result);
        Assert.Equal(HttpMethod.Post, capturedRequest?.Method);
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/openai/v1/chat/completions",
            capturedRequest?.RequestUri?.ToString());
        Assert.Equal("Bearer gemini-key", capturedRequest?.Headers.Authorization?.ToString());

        using var body = JsonDocument.Parse(Assert.IsType<string>(capturedBody));
        Assert.Equal("gemini-3.7-flash", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(8192, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("low", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(0.1, body.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal("system", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("user", body.RootElement.GetProperty("messages")[1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task ProcessAsync_OmitsGeminiReasoningEffortForGemmaModels()
    {
        string? capturedBody = null;
        var handler = new CapturingHandler((_, body) =>
        {
            capturedBody = body;
            return JsonResponse("""{"choices":[{"message":{"content":"ok"}}]}""");
        });
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "gemini-key" } };
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.ProcessAsync("system", "user", "gemma-4-31b-it", CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.IsType<string>(capturedBody));
        Assert.Equal("gemma-4-31b-it", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task ProcessAsync_NormalizesModelsPrefixBeforeApplyingGeminiOptions()
    {
        string? capturedBody = null;
        var handler = new CapturingHandler((_, body) =>
        {
            capturedBody = body;
            return JsonResponse("""{"choices":[{"message":{"content":"ok"}}]}""");
        });
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "gemini-key" } };
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.ProcessAsync("system", "user", "models/gemini-3.7-flash", CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.IsType<string>(capturedBody));
        Assert.Equal("gemini-3.7-flash", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("low", body.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task ProcessAsync_ClassifiesMalformedSuccessResponse()
    {
        var handler = new CapturingHandler((_, _) => JsonResponse("not-json"));
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "gemini-key" } };
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => sut.ProcessAsync(
            "system",
            "user",
            "gemini-3.7-flash",
            CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.EmptyResponse, error.FailureKind);
        Assert.IsType<JsonException>(error.InnerException, exactMatch: false);
    }

    [Fact]
    public async Task ProcessAsync_UsesCuratedDefaultWhenModelIsBlank()
    {
        string? capturedBody = null;
        var handler = new CapturingHandler((_, body) =>
        {
            capturedBody = body;
            return JsonResponse("""{"choices":[{"message":{"content":"ok"}}]}""");
        });
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "gemini-key" } };
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.ProcessAsync("system", "user", "", CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.IsType<string>(capturedBody));
        Assert.Equal(GeminiPlugin.DefaultModel, body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ProcessAsync_UsesCurrentFetchedFlashModelWhenModelIsBlank()
    {
        string? capturedBody = null;
        var handler = new CapturingHandler((_, body) =>
        {
            capturedBody = body;
            return JsonResponse("""{"choices":[{"message":{"content":"ok"}}]}""");
        });
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "gemini-key" } };
        host.SetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2",
        [
            new GeminiFetchedModel("gemini-2.5-flash", "Gemini 2.5 Flash"),
            new GeminiFetchedModel("gemini-3.7-flash", "Gemini 3.7 Flash"),
            new GeminiFetchedModel("gemini-3.1-pro-preview", "Gemini 3.1 Pro Preview"),
        ]);
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        await sut.ProcessAsync("system", "user", "", CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.IsType<string>(capturedBody));
        Assert.Equal("gemini-3.7-flash", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("gemini-3.7-flash", sut.SupportedModels[0].Id);
        Assert.True(sut.SupportedModels[0].IsRecommended);
    }

    [Fact]
    public async Task SupportedModels_OrdersFetchedFlashVersionsNumerically()
    {
        using var sut = new GeminiPlugin();

        await sut.SetFetchedLlmModelsAsync(
        [
            new GeminiFetchedModel("gemini-3.7-flash", "Gemini 3.7 Flash"),
            new GeminiFetchedModel("gemini-3.10-flash", "Gemini 3.10 Flash"),
            new GeminiFetchedModel("gemini-3.11-flash-preview", "Gemini 3.11 Flash Preview"),
        ]);

        Assert.Equal("gemini-3.10-flash", sut.SupportedModels[0].Id);
        Assert.True(sut.SupportedModels[0].IsRecommended);
    }

    [Fact]
    public async Task FetchLlmModelsAsync_FailureKeepsCachedCatalog()
    {
        var handler = new CapturingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "gemini-key" } };
        host.SetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2",
        [
            new GeminiFetchedModel("gemini-3.7-flash", "Gemini 3.7 Flash"),
        ]);
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.FetchLlmModelsAsync();

        Assert.Null(result);
        Assert.Contains(sut.SupportedModels, model => model.Id == "gemini-3.7-flash");
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
        Assert.Contains(host.Logs, entry =>
            entry.Level == PluginLogLevel.Warning
            && entry.Message.Contains("503", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateApiKeyAsync_ReturnsFalseForUnauthorizedResponse()
    {
        var handler = new CapturingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);

        var result = await sut.ValidateApiKeyAsync("invalid-key");

        Assert.False(result);
    }

    [Fact]
    public async Task FetchLlmModelsAsync_RethrowsCallerCancellation()
    {
        var handler = new BlockingHandler(JsonResponse("""{"data":[]}"""));
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "gemini-key" } };
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);
        using var cts = new CancellationTokenSource();

        var fetchTask = sut.FetchLlmModelsAsync(cts.Token);
        // ReSharper disable once MethodSupportsCancellation -- fixed hang-guard; the only in-scope token is cts.Token (the token under test), which the next line cancels, so forwarding it here would abort this wait instead of guarding it.
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // ReSharper disable once MethodHasAsyncOverload -- synchronous Cancel must trip the token before the assertion below; CancelAsync would defer it.
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetchTask);

        Assert.Contains(host.Logs, entry =>
            entry.Level == PluginLogLevel.Debug
            && entry.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FetchLlmModelsAsync_DiscardsCatalogWhenApiKeyChangesInFlight()
    {
        var handler = new BlockingHandler(JsonResponse("""
            {"data":[{"id":"gemini-3.7-flash","display_name":"Gemini 3.7 Flash"}]}
            """));
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "first-key" } };
        host.SetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2",
        [
            new GeminiFetchedModel("gemini-2.5-flash", "Gemini 2.5 Flash"),
        ]);
        using var httpClient = new HttpClient(handler);
        using var sut = new GeminiPlugin(httpClient);
        await sut.ActivateAsync(host);

        var fetchTask = sut.FetchLlmModelsAsync();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await sut.SetApiKeyAsync("second-key");
        handler.Release();
        var result = await fetchTask;

        Assert.Null(result);
        Assert.Equal("second-key", sut.ApiKey);
        Assert.Empty(sut.FetchedLlmModels);
        Assert.Empty(host.GetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2")!);
        Assert.Contains(host.Logs, entry =>
            entry.Level == PluginLogLevel.Debug
            && entry.Message.Contains("previous API key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SetFetchedLlmModelsAsync_RejectsCatalogForPreviousApiKey()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "first-key" } };
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);
        await sut.SetApiKeyAsync("second-key");

        var applied = await sut.SetFetchedLlmModelsAsync(
            [new GeminiFetchedModel("gemini-3.7-flash", "Gemini 3.7 Flash")],
            expectedRevision: 0);

        Assert.False(applied);
        Assert.Empty(sut.FetchedLlmModels);
        Assert.Contains(host.Logs, entry =>
            entry.Level == PluginLogLevel.Debug
            && entry.Message.Contains("previous API key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SetApiKeyAsync_NotifiesOnlyWhenAvailabilityChanges()
    {
        var host = new TestPluginHostServices();
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);

        await sut.SetApiKeyAsync(" first-key ");
        await sut.SetApiKeyAsync("second-key");
        await sut.SetApiKeyAsync("");

        Assert.Equal(2, host.NotifyCapabilitiesChangedCount);
        Assert.DoesNotContain("api-key", host.Secrets.Keys);
    }

    [Fact]
    public void HttpClient_UsesFiveMinuteTimeout()
    {
        using var sut = new GeminiPlugin();
        var field = typeof(GeminiPlugin).GetField("_httpClient", BindingFlags.NonPublic | BindingFlags.Instance);
        var client = Assert.IsType<HttpClient>(field!.GetValue(sut));
        Assert.Equal(TimeSpan.FromMinutes(5), client.Timeout);
    }

    [Theory]
    [InlineData("gemini-2.5-flash-lite", "gemini-3.7-flash", "gemini-2.5-flash-lite")]
    [InlineData("gemini-3.7-flash-lite", "gemini-3.10-flash-lite", "gemini-3.10-flash-lite")]
    [InlineData("gemini-3.11-flash-lite-preview", "gemini-3.10-flash", "gemini-3.10-flash")]
    [InlineData("gemini-3.11-flash-lite-exp", "gemini-3.10-pro", "gemini-3.10-pro")]
    [InlineData("gemini-3.7-pro", "gemini-3.10-pro", "gemini-3.10-pro")]
    [InlineData("gemini-3.10-flash-lite", "gemini-flash-lite-latest", "gemini-flash-lite-latest")]
    [InlineData("gemma-3-27b-it", "gemini-3.10-pro", "gemini-3.10-pro")]
    public async Task SupportedModels_ResolvesStableDefaultByTierAndVersion(
        string first, string second, string expected)
    {
        using var sut = new GeminiPlugin();
        await sut.SetFetchedLlmModelsAsync([new GeminiFetchedModel(first, null), new GeminiFetchedModel(second, null)]);
        Assert.Equal(expected, sut.SupportedModels[0].Id);
        Assert.Equal(expected, Assert.Single(sut.SupportedModels, model => model.IsRecommended).Id);
    }

    [Fact]
    public async Task RefreshModelCatalogAsync_PersistsAndRestoresCatalog()
    {
        using var client = new HttpClient(new CapturingHandler((_, _) => JsonResponse(
            """{"data":[{"id":"models/gemma-3-27b-it","display_name":"Gemma 3"},{"id":"gemini-3.7-flash"}]}""")));
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin(client);
        await sut.ActivateAsync(host);
        Assert.Equal("Settings.LlmModelDescriptionDefault", sut.GetSettingDefinitions()
            .Single(setting => setting.Key == "selectedLLMModel").Description);
        await sut.RefreshModelCatalogAsync();
        await sut.RefreshModelCatalogAsync();
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
        using var restored = new GeminiPlugin();
        await restored.ActivateAsync(host);
        Assert.Equal(sut.SupportedModels, restored.SupportedModels);
        Assert.Equal("Settings.LlmModelDescriptionFetched: 2", restored.GetSettingDefinitions()
            .Single(setting => setting.Key == "selectedLLMModel").Description);
    }

    [Theory]
    [InlineData(false, "gemini-2.5-pro", "gemini-3.7-flash")]
    [InlineData(true, "gemini-2.5-pro", "gemini-3.7-flash")]
    [InlineData(false, "gemma-3-27b-it", "gemma-3-27b-it")]
    public async Task RefreshModelCatalogAsync_NormalizesAndPersistsCachedSelection(
        bool unchangedCatalog, string selectedModel, string expectedModel)
    {
        string? capturedBody = null;
        using var client = new HttpClient(new CapturingHandler((request, body) =>
        {
            if (request.Method == HttpMethod.Get)
                return JsonResponse("""{"data":[{"id":"gemma-3-27b-it"},{"id":"gemini-3.7-flash"}]}""");
            capturedBody = body;
            return JsonResponse("""{"choices":[{"message":{"content":"ok"}}]}""");
        }));
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "gemini-key" } };
        host.SetSetting("selectedLLMModel", selectedModel);
        host.SetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2", unchangedCatalog
            ? [new GeminiFetchedModel("gemma-3-27b-it", null), new GeminiFetchedModel("gemini-3.7-flash", null)]
            : [new GeminiFetchedModel(selectedModel, null)]);
        using var sut = new GeminiPlugin(client);
        await sut.ActivateAsync(host);

        await sut.RefreshModelCatalogAsync();

        Assert.Equal(expectedModel, await sut.GetSettingValueAsync("selectedLLMModel"));
        Assert.Equal(expectedModel, host.GetSetting<string>("selectedLLMModel"));
        await sut.ProcessAsync("system", "user", "", CancellationToken.None);
        using var body = JsonDocument.Parse(Assert.IsType<string>(capturedBody));
        Assert.Equal(expectedModel, body.RootElement.GetProperty("model").GetString());
        using var restored = new GeminiPlugin();
        await restored.ActivateAsync(host);
        Assert.Equal(expectedModel, await restored.GetSettingValueAsync("selectedLLMModel"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupportedModels_SelectionControlsHostDefaultAcrossRefresh(bool fetchedCatalog)
    {
        var requestedModels = new List<string>();
        using var client = new HttpClient(new CapturingHandler((request, body) =>
        {
            if (request.Method == HttpMethod.Get)
                return JsonResponse("""{"data":[{"id":"gemini-2.5-pro"},{"id":"gemini-3.7-flash-lite"}]}""");
            using var json = JsonDocument.Parse(body!);
            requestedModels.Add(json.RootElement.GetProperty("model").GetString()!);
            return JsonResponse("""{"choices":[{"message":{"content":"Hallo"}}]}""");
        }));
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin(client);
        await sut.ActivateAsync(host);
        if (fetchedCatalog)
            await sut.RefreshModelCatalogAsync();
        await sut.SetSettingValueAsync("selectedLLMModel", "gemini-2.5-pro");

        var tempDir = TestPaths.CreateTempDirectory("TypeWhisper.GeminiTranslationTests");
        try
        {
            var profiles = new Mock<IProfileService>();
            profiles.SetupGet(service => service.Profiles).Returns([]);
            var settings = new Mock<ISettingsService>();
            settings.SetupGet(service => service.Current).Returns(new AppSettings());
            using var manager = new PluginManager(
                new PluginLoader(Path.Join(tempDir, "PluginData")),
                new PluginEventBus(),
                Mock.Of<IActiveWindowService>(), profiles.Object, settings.Object, []);
            var providersField = typeof(PluginManager).GetField(
                "_llmProviders", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(providersField);
            providersField.SetValue(manager, new List<ILlmProviderRole> { sut });
            using var translation = new TranslationService(manager);

            AssertSelectedDefault();
            Assert.Equal("Hallo", await translation.TranslateAsync("Hello", "en", "de"));
            await sut.RefreshModelCatalogAsync();
            AssertSelectedDefault();
            Assert.Equal("Hallo", await translation.TranslateAsync("Hello", "en", "de"));
            await sut.ProcessAsync("system", "user", "gemini-3.7-flash-lite", CancellationToken.None);
            Assert.Equal(["gemini-2.5-pro", "gemini-2.5-pro", "gemini-3.7-flash-lite"], requestedModels);

            await sut.SetSettingValueAsync("selectedLLMModel", null);
            Assert.Equal("gemini-3.7-flash-lite", sut.SupportedModels[0].Id);
            Assert.Equal(sut.SupportedModels[0], Assert.Single(sut.SupportedModels, model => model.IsRecommended));
        }
        finally
        {
            TestPaths.DeleteDirectory(tempDir);
        }

        return;

        void AssertSelectedDefault()
        {
            Assert.Equal("gemini-2.5-pro", sut.SupportedModels[0].Id);
            Assert.Equal(sut.SupportedModels[0], Assert.Single(sut.SupportedModels, model => model.IsRecommended));
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"id\":\"imagen-4\"}]}")]
    [InlineData(null)]
    public async Task RefreshModelCatalogAsync_FailurePreservesCatalogAndSelection(string? response)
    {
        using var client = new HttpClient(new CapturingHandler((_, _) => response is null
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : JsonResponse(response)));
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin(client);
        await sut.ActivateAsync(host);
        await sut.SetFetchedLlmModelsAsync(
            [new GeminiFetchedModel("gemma-3-27b-it", "Gemma 3"), new GeminiFetchedModel("gemini-2.5-pro", "Gemini 2.5 Pro")]);
        await sut.SetSettingValueAsync("selectedLLMModel", "gemma-3-27b-it");
        host.ResetTracking();
        await sut.RefreshModelCatalogAsync();
        Assert.Equal(["gemma-3-27b-it", "gemini-2.5-pro"], sut.SupportedModels.Select(model => model.Id));
        Assert.Equal("gemma-3-27b-it", await sut.GetSettingValueAsync("selectedLLMModel"));
        Assert.Equal("gemma-3-27b-it", host.GetSetting<string>("selectedLLMModel"));
        Assert.Equal(0, host.SetSettingCount);
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SetSettingValueAsync_RepairsUnknownModelSelectionToRecommendedModel()
    {
        var host = new TestPluginHostServices();
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);
        await sut.SetFetchedLlmModelsAsync(
            [new GeminiFetchedModel("gemma-3-27b-it", "Gemma 3"), new GeminiFetchedModel("gemini-2.5-pro", "Gemini 2.5 Pro")]);

        await sut.SetSettingValueAsync("selectedLLMModel", "models/gemini-9-preview");

        Assert.Equal("gemini-2.5-pro", await sut.GetSettingValueAsync("selectedLLMModel"));
        Assert.Equal("gemini-2.5-pro", host.GetSetting<string>("selectedLLMModel"));
    }

    [Fact]
    public async Task RefreshModelCatalogAsync_DiscardsResponseAfterKeyChangesBack()
    {
        var handler = new BlockingHandler(JsonResponse("""{"data":[{"id":"gemini-3.7-flash"}]}"""));
        using var client = new HttpClient(handler);
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "first-key" } };
        using var sut = new GeminiPlugin(client);
        await sut.ActivateAsync(host);
        var refresh = sut.RefreshModelCatalogAsync();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await sut.SetApiKeyAsync("second-key");
        await sut.SetApiKeyAsync("first-key");
        handler.Release();
        await refresh;
        Assert.Empty(sut.FetchedLlmModels);
        Assert.Equal(0, host.SetSettingCount);
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SetApiKeyAsync_UnchangedKeyPreservesCatalog()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin();
        await sut.ActivateAsync(host);
        await sut.SetFetchedLlmModelsAsync([new GeminiFetchedModel("gemma-3-27b-it", null)]);
        host.ResetTracking();
        await sut.SetApiKeyAsync(" gemini-key ");
        Assert.Single(sut.FetchedLlmModels);
        Assert.Equal(0, host.SetSettingCount);
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidateAsync_FetchesBothCatalogsAndReportsAvailability(bool catalogAvailable)
    {
        List<string> paths = [];
        using var client = new HttpClient(new CapturingHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            if (!catalogAvailable && paths.Count > 1)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

            return path switch
            {
                // The first compat call is the key probe, the second the chat catalog.
                "/v1beta/openai/models" when paths.Count == 1 => JsonResponse("{}"),
                "/v1beta/openai/models" => JsonResponse(
                    """{"data":[{"id":"gemini-3.7-flash"},{"id":"gemma-3-27b-it"},{"id":"veo-3"}]}"""),
                _ => JsonResponse(
                    """{"models":[{"name":"models/gemini-3.5-transcribe"},{"name":"models/gemini-3.5-transcribe-live"}]}"""),
            };
        }));
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin(client);
        await sut.ActivateAsync(host);
        var result = await sut.ValidateAsync();
        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.Equal(
            catalogAvailable ? "Settings.ApiKeyValidFetched: 2, 1" : "Settings.ApiKeyValid",
            result.Message);
        Assert.Equal(
            ["/v1beta/openai/models", "/v1beta/openai/models", "/v1beta/models"],
            paths);
        Assert.Equal(catalogAvailable ? 2 : 0, sut.FetchedLlmModels.Count);
        Assert.Equal(catalogAvailable ? 1 : 0, sut.FetchedTranscriptionModels.Count);
    }

    [Theory]
    [InlineData(false, "models/gemini-2.5-flash", 8192)]
    [InlineData(true, "models/gemini-2.5-flash", 8192)]
    [InlineData(false, "models/gemma-3-27b-it", 4096)]
    [InlineData(true, "models/gemma-3-27b-it", 4096)]
    public async Task ChatRequests_ApplyModelSpecificReasoningAndBoundLongPromptBudget(
        bool streaming, string model, int expectedTokens)
    {
        string? captured = null;
        using var client = new HttpClient(new CapturingHandler((_, body) =>
        {
            captured = body;
            return streaming
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
                        Encoding.UTF8, "text/event-stream"),
                }
                : JsonResponse("""{"choices":[{"message":{"content":"ok"}}]}""");
        }));
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "gemini-key" } };
        using var sut = new GeminiPlugin(client);
        await sut.ActivateAsync(host);
        var input = new string('x', 100_000);
        if (streaming)
        {
            List<string> chunks = [];
            await foreach (var chunk in sut.ProcessStreamingAsync("system", input, model, CancellationToken.None))
                chunks.Add(chunk);
            Assert.Equal(["ok"], chunks);
        }
        else
        {
            Assert.Equal("ok", await sut.ProcessAsync("system", input, model, CancellationToken.None));
        }

        using var json = JsonDocument.Parse(Assert.IsType<string>(captured));
        var request = json.RootElement;
        Assert.Equal(model["models/".Length..], request.GetProperty("model").GetString());
        Assert.Equal(expectedTokens, request.GetProperty("max_tokens").GetInt32());
        if (model.Contains("gemini-", StringComparison.Ordinal))
            Assert.Equal("low", request.GetProperty("reasoning_effort").GetString());
        else
            Assert.False(request.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void Locales_HaveMatchingKeysAndTranslatedCatalogMessages()
    {
        var directory = Path.Join(RepositoryRoot(), "plugins", "TypeWhisper.Plugin.Gemini", "Localization");
        var english = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Join(directory, "en.json")))!;
        string[] languages = ["de", "es", "ru"];
        foreach (var language in languages)
        {
            var locale = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Join(directory, language + ".json")))!;
            Assert.Equal(english.Keys.Order(), locale.Keys.Order());
            string[] translatedKeys =
            [
                "Settings.ApiKeyValidFetched", "Settings.LlmModelDescriptionFetched",
                "Settings.LlmModelDescriptionDefault", "Manifest.Description", "Settings.LlmModel",
                "Settings.TranscriptionModel", "Settings.TranscriptionModelsFetched",
                "Settings.TranscriptionModelFallback", "Settings.TranscriptionMode",
                "Settings.TranscriptionModeHint", "Settings.ModeVerbatim",
            ];
            foreach (var key in translatedKeys)
            {
                Assert.False(string.IsNullOrWhiteSpace(locale[key]));
                Assert.NotEqual(english[key], locale[key]);
                // Every placeholder English uses must survive the translation, not just {0}.
                foreach (var placeholder in Placeholders(english[key]))
                    Assert.Contains(placeholder, locale[key], StringComparison.Ordinal);
            }
        }
    }

    private static IEnumerable<string> Placeholders(string text) =>
        Enumerable.Range(0, 10)
            .Select(index => $"{{{index}}}")
            .Where(placeholder => text.Contains(placeholder, StringComparison.Ordinal));

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
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request, body);
        }
    }

    private sealed class BlockingHandler(HttpResponseMessage response) : HttpMessageHandler
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

    private sealed class TestPluginHostServices : IPluginHostServices
    {
        private readonly Dictionary<string, JsonElement> _settings = [];
        public Dictionary<string, string?> Secrets { get; } = [];
        public int NotifyCapabilitiesChangedCount { get; private set; }
        public int SetSettingCount { get; private set; }
        public List<(PluginLogLevel Level, string Message)> Logs { get; } = [];
        public Exception? StoreSecretException { get; set; }
        private Exception? SetSettingException { get; set; }
        public Exception? SetSettingExceptionAfterWrite { get; set; }

        public Task StoreSecretAsync(string key, string value)
        {
            if (StoreSecretException is { } exception)
            {
                StoreSecretException = null;
                throw exception;
            }

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
            _settings.TryGetValue(key, out var value)
                ? value.Deserialize<T>(s_jsonOptions)
                : default;

        public void SetSetting<T>(string key, T value)
        {
            if (SetSettingException is { } exception)
            {
                SetSettingException = null;
                throw exception;
            }

            _settings[key] = JsonSerializer.SerializeToElement(value, s_jsonOptions);
            SetSettingCount++;
            // ReSharper disable once InvertIf -- guard-clause form states the injected-failure case; inverting would end the method on a bare return.
            if (SetSettingExceptionAfterWrite is { } afterWriteException)
            {
                SetSettingExceptionAfterWrite = null;
                throw afterWriteException;
            }
        }

        public void ResetTracking()
        {
            NotifyCapabilitiesChangedCount = 0;
            SetSettingCount = 0;
            Logs.Clear();
        }

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) => Logs.Add((level, message));
        public void NotifyCapabilitiesChanged() => NotifyCapabilitiesChangedCount++;
        public IPluginLocalization Localization { get; } = new TestPluginLocalization();
    }

    private sealed class TestPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => key + ": " + string.Join(", ", args);
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

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "TypeWhisper.slnx"))
                && Directory.Exists(Path.Join(directory.FullName, "plugins")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("TypeWhisper repository root not found.");
    }
    [Fact]
    public Task RequestFailures_AreClassified() =>
        ProviderFailureAssertions.VerifyAsync<GeminiPlugin>();

}
