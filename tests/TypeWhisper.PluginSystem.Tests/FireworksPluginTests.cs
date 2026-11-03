using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Fireworks;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

// The CapturingHandler lambdas assert on the outgoing request and return a canned response;
// ReSharper reads those asserts as precondition-only parameter use.
// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local

namespace TypeWhisper.PluginSystem.Tests;

public sealed class FireworksPluginTests
{
    private const string Key = "fw-account-a";

    private const string Models = "accounts/fireworks/models/";

    private static readonly string[] s_fallbackIds = [$"{Models}gpt-oss-120b", $"{Models}glm-5p3-flash"];

    private const string Answer =
        """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""";

    [Fact]
    public Task RequestFailures_AreClassified() =>
        ProviderFailureAssertions.VerifyAsync<FireworksPlugin>();

    [Fact]
    public void FallbackCatalog_ReplacesRetiredLlamaScoutDefault()
    {
        using var sut = new FireworksPlugin(new HttpClient(new CapturingHandler((_, _) =>
            throw new InvalidOperationException("Unexpected network request."))));

        Assert.Equal(s_fallbackIds, Ids(sut));
        Assert.Equal(["GPT-OSS 120B", "GLM 5.3 Flash"], sut.SupportedModels.Select(m => m.DisplayName));
        Assert.True(sut.SupportedModels[0].IsRecommended);
        Assert.DoesNotContain(sut.SupportedModels, m => m.Id.Contains("llama4-scout", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", Models + "gpt-oss-120b")]
    [InlineData("  ", Models + "gpt-oss-120b")]
    [InlineData(Models + "llama4-scout-instruct-basic", Models + "llama4-scout-instruct-basic")]
    [InlineData(" accounts/acme/models/custom ", "accounts/acme/models/custom")]
    public async Task ProcessAsync_SendsExplicitModelUnchangedAndDefaultsBlankModel(string model, string expected)
    {
        using var sut = await ActivatedAsync(new CapturingHandler((request, body) =>
        {
            Assert.Equal("https://api.fireworks.ai/inference/v1/chat/completions", request.RequestUri!.ToString());
            using var doc = JsonDocument.Parse(body!);
            Assert.Equal(expected, doc.RootElement.GetProperty("model").GetString());
            return Json(Answer);
        }));

        Assert.Equal("ok", await sut.ProcessAsync("system", "user", model, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Requests_KeepThePreviousFixedTemperatureByDefault(bool streaming)
    {
        JsonElement sent = default;
        using var sut = await ActivatedAsync(new CapturingHandler((_, body) =>
        {
            sent = JsonDocument.Parse(body!).RootElement.Clone();
            return streaming ? Sse("ok") : Json(Answer);
        }));

        await RunAsync(sut, Models + "gpt-oss-120b", streaming);

        Assert.Equal(0.1, sent.GetProperty("temperature").GetDouble());
        Assert.Equal("custom", await sut.GetSettingValueAsync("temperatureMode"));
        Assert.Equal("0.1", await sut.GetSettingValueAsync("temperature"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Requests_ProviderDefaultOmitsTemperatureAndCustomSendsIt(bool streaming)
    {
        var bodies = new List<JsonElement>();
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((_, body) =>
        {
            bodies.Add(JsonDocument.Parse(body!).RootElement.Clone());
            return streaming ? Sse("ok") : Json(Answer);
        }), host);

        await sut.SetSettingValueAsync("temperatureMode", "provider-default");
        await RunAsync(sut, Models + "gpt-oss-120b", streaming);
        await sut.SetSettingValueAsync("temperatureMode", "custom");
        await sut.SetSettingValueAsync("temperature", "1.25");
        await RunAsync(sut, Models + "gpt-oss-120b", streaming);

        Assert.False(bodies[0].TryGetProperty("temperature", out _));
        Assert.Equal(1.25, bodies[1].GetProperty("temperature").GetDouble());

        using var restarted = new FireworksPlugin(new HttpClient(new CapturingHandler((_, _) => Json(Answer))));
        await restarted.ActivateAsync(host);
        Assert.Equal("custom", await restarted.GetSettingValueAsync("temperatureMode"));
        Assert.Equal("1.25", await restarted.GetSettingValueAsync("temperature"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-0.1")]
    [InlineData("2.5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("0,5")]
    public async Task Temperature_InvalidValueIsRejectedWithoutSaving(string value)
    {
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json(Answer)), host);
        var writes = host.SettingWrites;

        var error = await Assert.ThrowsAsync<ArgumentException>(() => sut.SetSettingValueAsync("temperature", value));

        Assert.Equal("Settings.TemperatureInvalid", error.Message);
        Assert.Equal(writes, host.SettingWrites);
        Assert.Equal("0.1", await sut.GetSettingValueAsync("temperature"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("provider_default")]
    [InlineData("Custom")]
    public async Task TemperatureMode_UnknownValueIsRejectedWithoutSaving(string? value)
    {
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json(Answer)), host);
        var writes = host.SettingWrites;

        var error = await Assert.ThrowsAsync<ArgumentException>(() => sut.SetSettingValueAsync("temperatureMode", value));

        Assert.Equal("Settings.TemperatureModeInvalid", error.Message);
        Assert.Equal(writes, host.SettingWrites);
        Assert.Equal("custom", await sut.GetSettingValueAsync("temperatureMode"));
    }

    [Fact]
    public async Task Activate_IgnoresInvalidSavedTemperatureSettings()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("temperatureMode", "unknown");
        host.SetSetting("temperature", 7.5);
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json(Answer)), host);

        Assert.Equal("custom", await sut.GetSettingValueAsync("temperatureMode"));
        Assert.Equal("0.1", await sut.GetSettingValueAsync("temperature"));
    }

    [Theory]
    [InlineData(Models + "gpt-oss-120b", true)]
    [InlineData(Models + "gpt-oss-20b", true)]
    [InlineData(Models + "glm-5p3-flash", true)]
    [InlineData(Models + "glm-5p3", true)]
    [InlineData("accounts/acme/models/gpt-oss-120b-ft", true)]
    [InlineData("accounts/acme/models/glm-5p3-tuned", true)]
    [InlineData(Models + "glm-4p7", false)]
    [InlineData(Models + "minimax-m3", false)]
    [InlineData("accounts/acme/models/custom", false)]
    public async Task Requests_ReserveOutputForReasoningModels(string model, bool reasoning)
    {
        JsonElement sent = default;
        using var sut = await ActivatedAsync(new CapturingHandler((_, body) =>
        {
            sent = JsonDocument.Parse(body!).RootElement.Clone();
            return Json(Answer);
        }));

        await sut.ProcessAsync("system", "user", model, CancellationToken.None);

        Assert.False(sent.TryGetProperty("max_completion_tokens", out _));
        Assert.Equal(
            reasoning
                ? LlmOutputTokenBudget.CalculateWithReasoningReserve("system", "user")
                : LlmOutputTokenBudget.Calculate("system", "user"),
            sent.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task ProcessAsync_RejectsTruncatedAnswer()
    {
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json(
            """{"choices":[{"message":{"role":"assistant","content":"partial"},"finish_reason":"length"}]}""")));

        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.ProcessAsync("system", "user", Models + "gpt-oss-120b", CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.OutputTruncated, error.FailureKind);
    }

    [Fact]
    public async Task ProcessAsync_NeverReturnsReasoningAsTheAnswer()
    {
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json(
            """{"choices":[{"message":{"role":"assistant","content":null,"reasoning":"private thinking"},"finish_reason":"stop"}]}""")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ProcessAsync("system", "user", Models + "gpt-oss-120b", CancellationToken.None));

        Assert.DoesNotContain("private thinking", error.Message);
    }

    [Fact]
    public async Task ValidateAsync_CommitsTheAccountCatalogWithTheDefaultFirst()
    {
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.fireworks.ai/inference/v1/models?page_size=200", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(Key, request.Headers.Authorization.Parameter);
            return Json("""
                {"object":"list","data":[
                  {"id":"accounts/fireworks/models/kimi-k3","object":"model","owned_by":"fireworks","kind":"HF_BASE_MODEL","supports_chat":true},
                  {"id":"accounts/fireworks/models/gpt-oss-120b","object":"model"},
                  {"id":"accounts/acme/models/custom"},
                  {"id":"accounts/fireworks/models/gpt-oss-120b"}
                ]}
                """);
        }), host);
        var capabilityChanges = host.CapabilityChanges;

        var result = await sut.ValidateAsync();

        Assert.True(result!.IsSuccess);
        Assert.Equal("Settings.ApiKeyValidFetchedModels", result.Message);
        Assert.Equal([$"{Models}gpt-oss-120b", "accounts/acme/models/custom", $"{Models}kimi-k3"], Ids(sut));
        Assert.Equal(["GPT-OSS 120B", "accounts/acme/models/custom", "Kimi K3"], sut.SupportedModels.Select(m => m.DisplayName));
        Assert.Equal([true, false, false], sut.SupportedModels.Select(m => m.IsRecommended));
        Assert.Equal(capabilityChanges + 1, host.CapabilityChanges);

        using var restarted = await RestartedAsync(host);
        Assert.Equal(Ids(sut), Ids(restarted));
    }

    [Fact]
    public async Task ValidateAsync_CatalogWithoutDefaultIsSorted()
    {
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) =>
            Json("""{"data":[{"id":"accounts/fireworks/models/minimax-m3"},{"id":"accounts/fireworks/models/glm-5p3"}]}""")));

        Assert.True((await sut.ValidateAsync())!.IsSuccess);
        Assert.Equal([$"{Models}glm-5p3", $"{Models}minimax-m3"], Ids(sut));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"data":[42]}""")]
    [InlineData("""{"data":[{"name":"no id"}]}""")]
    [InlineData("""{"data":[{"id":42}]}""")]
    [InlineData("""{"data":[{"id":"accounts/fireworks/models/gpt-oss-120b"},{"id":"bad id"}]}""")]
    [InlineData("""{"data":[{"id":"accounts/fireworks/models/gpt-oss-120b"},{"id":""}]}""")]
    [InlineData("""{"data":[{"id":"accounts/fireworks/models/gpt-oss-120b"}],"next_page_token":42}""")]
    [InlineData("""{"data":[{"id":"accounts/fireworks/models/nomic-embed","kind":"EMBEDDING_MODEL"},{"id":"accounts/fireworks/models/whisper-v3"}]}""")]
    public async Task ValidateAsync_MalformedCatalogKeepsSavedModels(string body)
    {
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json(body)), host);
        var writes = host.SettingWrites;

        var result = await sut.ValidateAsync();

        Assert.False(result!.IsSuccess);
        Assert.Equal("Settings.ModelRefreshFailed", result.Message);
        Assert.Equal(writes, host.SettingWrites);
        Assert.Equal(s_fallbackIds, Ids(sut));
    }

    [Fact]
    public async Task FetchModelsAsync_FailureReasonsAreLocalized()
    {
        using var invalid = await ActivatedAsync(new CapturingHandler((_, _) => Json("{}")));
        var catalogError = await Assert.ThrowsAsync<PluginRequestException>(() =>
            invalid.FetchModelsAsync(Key, CancellationToken.None));
        Assert.Equal("Errors.ModelCatalogInvalid", catalogError.Message);

        var host = new TestPluginHostServices { Secrets = { ["apiKey"] = Key } };
        var plugin = new FireworksPlugin(new HttpClient(new AsyncHandler(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        }))) { ModelDiscoveryDeadline = TimeSpan.FromMilliseconds(50) };
        using var slow = plugin;
        await slow.ActivateAsync(host);
        var timeout = await Assert.ThrowsAsync<PluginRequestException>(() =>
            slow.FetchModelsAsync(Key, CancellationToken.None));
        Assert.Equal("Errors.ModelRefreshTimedOut", timeout.Message);
        Assert.Equal(PluginRequestFailureKind.Timeout, timeout.FailureKind);
    }

    [Fact]
    public async Task ValidateAsync_FollowsEveryPageOfTheCatalog()
    {
        var urls = new List<string>();
        using var sut = await ActivatedAsync(new CapturingHandler((request, _) =>
        {
            urls.Add(request.RequestUri!.AbsoluteUri);
            return urls.Count == 1
                ? Json("""{"data":[{"id":"accounts/fireworks/models/kimi-k3"}],"next_page_token":"page 2/="}""")
                : Json("""{"data":[{"id":"accounts/fireworks/models/glm-5p3"}],"next_page_token":""}""");
        }));

        Assert.True((await sut.ValidateAsync())!.IsSuccess);

        Assert.Equal(
            [
                "https://api.fireworks.ai/inference/v1/models?page_size=200",
                "https://api.fireworks.ai/inference/v1/models?page_size=200&page_token=page%202%2F%3D",
            ],
            urls);
        Assert.Equal([$"{Models}glm-5p3", $"{Models}kimi-k3"], Ids(sut));
    }

    [Fact]
    public async Task ValidateAsync_RepeatedPageTokenIsRejected()
    {
        var requests = 0;
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) =>
        {
            requests++;
            return Json("""{"data":[{"id":"accounts/fireworks/models/kimi-k3"}],"nextPageToken":"same"}""");
        }));

        var result = await sut.ValidateAsync();

        Assert.False(result!.IsSuccess);
        Assert.Equal("Settings.ModelRefreshFailed", result.Message);
        Assert.Equal(2, requests);
        Assert.Equal(s_fallbackIds, Ids(sut));
    }

    [Fact]
    public async Task ValidateAsync_EndlessPagingIsRejected()
    {
        var requests = 0;
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) =>
        {
            requests++;
            return Json($$"""{"data":[{"id":"accounts/fireworks/models/m-{{requests}}"}],"next_page_token":"t-{{requests}}"}""");
        }));

        Assert.False((await sut.ValidateAsync())!.IsSuccess);
        Assert.Equal(10, requests);
        Assert.Equal(s_fallbackIds, Ids(sut));
    }

    [Fact]
    public async Task ValidateAsync_OversizedCatalogAcrossPagesIsRejected()
    {
        var requests = 0;
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) =>
        {
            var page = requests++;
            var models = string.Join(",", Enumerable.Range(0, 200).Select(i => $$"""{"id":"m-{{page}}-{{i}}"}"""));
            return Json($$"""{"data":[{{models}}],"next_page_token":"t-{{page}}"}""");
        }));

        Assert.False((await sut.ValidateAsync())!.IsSuccess);
        Assert.Equal(6, requests);
        Assert.Equal(s_fallbackIds, Ids(sut));
    }

    [Fact]
    public async Task ValidateAsync_KeepsOnlyChatModels()
    {
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json("""
            {"data":[
              {"id":"accounts/fireworks/models/nomic-embed-text","kind":"EMBEDDING_MODEL","supports_chat":true},
              {"id":"accounts/fireworks/models/rerank-model","supports_chat":false},
              {"id":"accounts/fireworks/models/whisper-v3-turbo"},
              {"id":"accounts/fireworks/models/flux-1-dev"},
              {"id":"accounts/fireworks/models/qwen-image-chat","supports_chat":true},
              {"id":"accounts/fireworks/models/kimi-k3","kind":"HF_BASE_MODEL"},
              {"id":"accounts/typewhisper/models/custom-chat"}
            ]}
            """)));

        Assert.True((await sut.ValidateAsync())!.IsSuccess);
        Assert.Equal([$"{Models}kimi-k3", $"{Models}qwen-image-chat", "accounts/typewhisper/models/custom-chat"], Ids(sut));
    }

    [Fact]
    public async Task ValidateAsync_OversizedCatalogIsRejectedWhole()
    {
        var models = string.Join(",", Enumerable.Range(0, 1001).Select(i => $$"""{"id":"model-{{i}}"}"""));
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json($$"""{"data":[{{models}}]}""")));

        Assert.False((await sut.ValidateAsync())!.IsSuccess);
        Assert.Equal(s_fallbackIds, Ids(sut));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ValidateAsync_RejectedKeyIsReported(HttpStatusCode status)
    {
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) =>
            Json("""{"message":"Wrong API Key","type":"invalid_request_error"}""", status)));

        var result = await sut.ValidateAsync();

        Assert.False(result!.IsSuccess);
        Assert.Equal("Settings.ApiKeyRejected", result.Message);
    }

    [Fact]
    public async Task ValidateAsync_NetworkFailureIsNotReportedAsARejectedKey()
    {
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) =>
            throw new HttpRequestException("Connection refused")));

        var result = await sut.ValidateAsync();

        Assert.False(result!.IsSuccess);
        Assert.Equal("Settings.ModelRefreshFailed", result.Message);
    }

    [Fact]
    public async Task ValidateAsync_WithoutKeyStaysOffline()
    {
        using var sut = new FireworksPlugin(new HttpClient(new CapturingHandler((_, _) =>
            throw new InvalidOperationException("Unexpected network request."))));
        await sut.ActivateAsync(new TestPluginHostServices());

        var result = await sut.ValidateAsync();

        Assert.False(result!.IsSuccess);
        Assert.Equal("Settings.EnterApiKey", result.Message);
    }

    [Fact]
    public async Task ValidateAsync_FailedCatalogSaveLeavesLiveCatalogUnchanged()
    {
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) =>
            Json("""{"data":[{"id":"unsaved-model"}]}""")), host);
        var capabilityChanges = host.CapabilityChanges;
        host.FailSettingWrites = true;

        await Assert.ThrowsAsync<IOException>(() => sut.ValidateAsync());

        Assert.Equal(s_fallbackIds, Ids(sut));
        Assert.Equal(capabilityChanges, host.CapabilityChanges);
    }

    [Fact]
    public async Task ApiKeyChange_ForgetsTheOtherAccountsCatalog()
    {
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) =>
            Json("""{"data":[{"id":"account-a-model"}]}""")), host);
        Assert.True((await sut.ValidateAsync())!.IsSuccess);
        Assert.Equal(["account-a-model"], Ids(sut));

        await sut.SetSettingValueAsync("apiKey", $" {Key} ");
        Assert.Equal(["account-a-model"], Ids(sut));
        Assert.Equal(Key, host.Secrets["apiKey"]);

        await sut.SetSettingValueAsync("apiKey", "fw-account-b");
        Assert.Equal(s_fallbackIds, Ids(sut));
        using (var restarted = await RestartedAsync(host))
            Assert.Equal(s_fallbackIds, Ids(restarted));

        // The saved catalog still belongs to account A, so returning to that key restores it.
        await sut.SetSettingValueAsync("apiKey", Key);
        Assert.Equal(["account-a-model"], Ids(sut));
    }

    [Fact]
    public async Task ApiKeyChange_FailedSecretWriteKeepsOldKeyAndCatalogAcrossRestart()
    {
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) =>
            Json("""{"data":[{"id":"account-a-model"}]}""")), host);
        Assert.True((await sut.ValidateAsync())!.IsSuccess);
        host.FailSecretWrites = true;

        await Assert.ThrowsAsync<IOException>(() => sut.SetSettingValueAsync("apiKey", "fw-account-b"));

        Assert.Equal(Key, await sut.GetSettingValueAsync("apiKey"));
        Assert.Equal(["account-a-model"], Ids(sut));
        host.FailSecretWrites = false;
        using var restarted = await RestartedAsync(host);
        Assert.Equal(["account-a-model"], Ids(restarted));
    }

    [Fact]
    public async Task ApiKeyRemoval_DeletesTheSecretAndDisablesTheProvider()
    {
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json(Answer)), host);

        await sut.SetSettingValueAsync("apiKey", "  ");

        Assert.False(sut.IsAvailable);
        Assert.False(host.Secrets.ContainsKey("apiKey"));
    }

    [Fact]
    public async Task ApiKeyRemoval_DuringAnEarlierSaveLeavesTheKeyCleared()
    {
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json(Answer)), host);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SecretWriteDelay = release.Task;

        var save = sut.SetSettingValueAsync("apiKey", "fw-account-b");
        var removal = sut.SetSettingValueAsync("apiKey", "");
        host.SecretWriteDelay = null;
        release.SetResult();
        await Task.WhenAll(save, removal);

        Assert.False(sut.IsAvailable);
        Assert.False(host.Secrets.ContainsKey("apiKey"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidateAsync_RefreshOutlivedByKeyChangeOrDeactivationIsDiscarded(bool deactivate)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new AsyncHandler(async _ =>
        {
            entered.TrySetResult();
            await finish.Task;
            return Json("""{"data":[{"id":"stale-model"}]}""");
        }), host);

        var pending = sut.ValidateAsync();
        await entered.Task;
        if (deactivate)
            await sut.DeactivateAsync();
        else
            await sut.SetSettingValueAsync("apiKey", "fw-replacement");
        finish.SetResult();

        var result = await pending;
        Assert.False(result!.IsSuccess);
        Assert.Equal("Settings.ModelRefreshStale", result.Message);
        Assert.DoesNotContain(sut.SupportedModels, m => m.Id == "stale-model");
        Assert.DoesNotContain("stale-model", host.GetSetting<FireworksModelCatalog>("fetchedModels")?.Models ?? []);
    }

    [Fact]
    public async Task ValidateAsync_DiscoveryIsBoundedByDeadline()
    {
        var host = new TestPluginHostServices { Secrets = { ["apiKey"] = Key } };
        var plugin = new FireworksPlugin(new HttpClient(new AsyncHandler(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        }))) { ModelDiscoveryDeadline = TimeSpan.FromMilliseconds(50) };
        using var sut = plugin;
        await sut.ActivateAsync(host);

        var result = await sut.ValidateAsync();

        Assert.False(result!.IsSuccess);
        Assert.Equal("Settings.ModelRefreshFailed", result.Message);
        Assert.Equal(s_fallbackIds, Ids(sut));
    }

    [Fact]
    public async Task ValidateAsync_CallerCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        using var sut = await ActivatedAsync(new AsyncHandler(async ct =>
        {
            // ReSharper disable once AccessToDisposedClosure -- the handler only runs inside the awaited ValidateAsync below.
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.ValidateAsync(cts.Token));
        Assert.Equal(s_fallbackIds, Ids(sut));
    }

    [Fact]
    public async Task Activate_DropsInvalidPersistedModels()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("fetchedModels", new FireworksModelCatalog(
            FireworksPlugin.Fingerprint(Key), ["kept-model", "bad id", "kept-model", ""]));
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) =>
            throw new InvalidOperationException("Unexpected network request.")), host);

        Assert.Equal(["kept-model"], Ids(sut));
    }

    private static async Task RunAsync(FireworksPlugin sut, string model, bool streaming)
    {
        if (!streaming)
        {
            await sut.ProcessAsync("system", "user", model, CancellationToken.None);
            return;
        }

        await foreach (var _ in sut.ProcessStreamingAsync("system", "user", model, CancellationToken.None))
        {
            // Drain the stream so the request is sent.
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Sse(string text) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""data: {"choices":[{"delta":{"content":"{{text}}"},"finish_reason":"stop"}]}""" + "\n\ndata: [DONE]\n\n",
                Encoding.UTF8,
                "text/event-stream"),
        };

    private static async Task<FireworksPlugin> ActivatedAsync(
        HttpMessageHandler handler, TestPluginHostServices? host = null)
    {
        host ??= new TestPluginHostServices();
        host.Secrets["apiKey"] = Key;
        var sut = new FireworksPlugin(new HttpClient(handler));
        await sut.ActivateAsync(host);
        return sut;
    }

    [Fact]
    public async Task Activate_IgnoresACatalogSavedForAnotherKey()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("fetchedModels", new FireworksModelCatalog(FireworksPlugin.Fingerprint("fw-other"), ["other-model"]));
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) =>
            throw new InvalidOperationException("Unexpected network request.")), host);

        Assert.Equal(s_fallbackIds, Ids(sut));
    }

    private static async Task<FireworksPlugin> RestartedAsync(TestPluginHostServices host)
    {
        var restarted = new FireworksPlugin(new HttpClient(new CapturingHandler((_, _) =>
            throw new InvalidOperationException("Unexpected network request."))));
        await restarted.ActivateAsync(host);
        return restarted;
    }

    private static string[] Ids(FireworksPlugin plugin) => plugin.SupportedModels.Select(m => m.Id).ToArray();

    private sealed class AsyncHandler(
        Func<CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(cancellationToken);
    }

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
        private readonly Dictionary<string, JsonElement> _settings = [];
        public Dictionary<string, string?> Secrets { get; } = [];

        public async Task StoreSecretAsync(string key, string value)
        {
            if (FailSecretWrites)
                throw new IOException("Secret store unavailable.");
            Secrets[key] = value;
            if (SecretWriteDelay is { } delay)
                await delay;
        }

        public Task? SecretWriteDelay { get; set; }

        public Task<string?> LoadSecretAsync(string key) =>
            Task.FromResult(Secrets.GetValueOrDefault(key));

        public Task DeleteSecretAsync(string key)
        {
            Secrets.Remove(key);
            return Task.CompletedTask;
        }

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value) ? value.Deserialize<T>() : default;

        public void SetSetting<T>(string key, T value)
        {
            if (FailSettingWrites)
                throw new IOException("Settings store unavailable.");
            SettingWrites++;
            _settings[key] = JsonSerializer.SerializeToElement(value);
        }

        public int SettingWrites { get; private set; }
        public bool FailSettingWrites { get; set; }
        public bool FailSecretWrites { get; set; }
        public int CapabilityChanges { get; private set; }

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() => CapabilityChanges++;
        public IPluginLocalization Localization { get; } = new TestPluginLocalization();
    }

    private sealed class TestPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => key;
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
