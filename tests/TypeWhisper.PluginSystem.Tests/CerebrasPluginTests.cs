using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Cerebras;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

// The CapturingHandler lambdas assert on the outgoing request and return a canned response;
// ReSharper reads those asserts as precondition-only parameter use.
// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local

namespace TypeWhisper.PluginSystem.Tests;

public sealed class CerebrasPluginTests
{
    private const string Key = "csk-account-a";

    private static readonly string[] s_fallbackIds = ["gpt-oss-120b", "qwen-3.8-27b"];

    private const string Answer =
        """{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""";

    [Fact]
    public Task RequestFailures_AreClassified() =>
        ProviderFailureAssertions.VerifyAsync<CerebrasPlugin>();

    [Fact]
    public void FallbackCatalog_ReplacesRetiredLlamaDefault()
    {
        using var sut = new CerebrasPlugin(new HttpClient(new CapturingHandler((_, _) =>
            throw new InvalidOperationException("Unexpected network request."))));

        Assert.Equal(s_fallbackIds, Ids(sut));
        Assert.Equal(["GPT-OSS 120B", "Qwen 3.8 27B"], sut.SupportedModels.Select(m => m.DisplayName));
        Assert.DoesNotContain(sut.SupportedModels, m => m.Id == "llama-4-scout-17b-16e-instruct");
    }

    [Theory]
    [InlineData("", "gpt-oss-120b")]
    [InlineData("  ", "gpt-oss-120b")]
    [InlineData("llama-4-scout-17b-16e-instruct", "llama-4-scout-17b-16e-instruct")]
    [InlineData(" account-custom-model ", "account-custom-model")]
    public async Task ProcessAsync_SendsExplicitModelUnchangedAndDefaultsBlankModel(string model, string expected)
    {
        using var sut = await ActivatedAsync(new CapturingHandler((_, body) =>
        {
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

        await RunAsync(sut, "gpt-oss-120b", streaming);

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
        await RunAsync(sut, "gpt-oss-120b", streaming);
        await sut.SetSettingValueAsync("temperatureMode", "custom");
        await sut.SetSettingValueAsync("temperature", "1.25");
        await RunAsync(sut, "gpt-oss-120b", streaming);

        Assert.False(bodies[0].TryGetProperty("temperature", out _));
        Assert.Equal(1.25, bodies[1].GetProperty("temperature").GetDouble());

        using var restarted = new CerebrasPlugin(new HttpClient(new CapturingHandler((_, _) => Json(Answer))));
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
    [InlineData("gpt-oss-120b", true)]
    [InlineData("qwen-3.8-27b", true)]
    [InlineData("kimi-k2.7-code", true)]
    [InlineData("llama3.1-8b", false)]
    [InlineData("gemma-4-31b", false)]
    public async Task Requests_ReserveOutputForReasoningModels(string model, bool reasoning)
    {
        JsonElement sent = default;
        using var sut = await ActivatedAsync(new CapturingHandler((_, body) =>
        {
            sent = JsonDocument.Parse(body!).RootElement.Clone();
            return Json(Answer);
        }));

        await sut.ProcessAsync("system", "user", model, CancellationToken.None);

        Assert.False(sent.TryGetProperty("max_tokens", out _));
        Assert.Equal(
            reasoning
                ? LlmOutputTokenBudget.CalculateWithReasoningReserve("system", "user")
                : LlmOutputTokenBudget.Calculate("system", "user"),
            sent.GetProperty("max_completion_tokens").GetInt32());
    }

    [Fact]
    public async Task ProcessAsync_RejectsTruncatedAnswer()
    {
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json(
            """{"choices":[{"message":{"role":"assistant","content":"partial"},"finish_reason":"length"}]}""")));

        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.ProcessAsync("system", "user", "gpt-oss-120b", CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.OutputTruncated, error.FailureKind);
    }

    [Fact]
    public async Task ProcessAsync_NeverReturnsReasoningAsTheAnswer()
    {
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json(
            """{"choices":[{"message":{"role":"assistant","content":null,"reasoning":"private thinking"},"finish_reason":"stop"}]}""")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ProcessAsync("system", "user", "gpt-oss-120b", CancellationToken.None));

        Assert.DoesNotContain("private thinking", error.Message);
    }

    [Fact]
    public async Task ValidateAsync_CommitsTheAccountCatalogWithTheDefaultFirst()
    {
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.cerebras.ai/v1/models", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(Key, request.Headers.Authorization.Parameter);
            return Json("""
                {"object":"list","data":[
                  {"id":"zai-glm-4.7","object":"model","created":0,"owned_by":"Cerebras"},
                  {"id":"gpt-oss-120b","object":"model"},
                  {"id":"account-model"},
                  {"id":"gpt-oss-120b"}
                ]}
                """);
        }), host);
        var capabilityChanges = host.CapabilityChanges;

        var result = await sut.ValidateAsync();

        Assert.True(result!.IsSuccess);
        Assert.Equal("Settings.ApiKeyValidFetchedModels", result.Message);
        Assert.Equal(["gpt-oss-120b", "account-model", "zai-glm-4.7"], Ids(sut));
        Assert.Equal(["GPT-OSS 120B", "account-model", "ZAI GLM 4.7"], sut.SupportedModels.Select(m => m.DisplayName));
        Assert.Equal(capabilityChanges + 1, host.CapabilityChanges);

        using var restarted = await RestartedAsync(host);
        Assert.Equal(Ids(sut), Ids(restarted));
    }

    [Fact]
    public async Task ValidateAsync_CatalogWithoutDefaultIsSorted()
    {
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) =>
            Json("""{"data":[{"id":"qwen-3.8-27b"},{"id":"gemma-4-31b"}]}""")));

        Assert.True((await sut.ValidateAsync())!.IsSuccess);
        Assert.Equal(["gemma-4-31b", "qwen-3.8-27b"], Ids(sut));
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
    [InlineData("""{"data":[{"id":"gpt-oss-120b"},{"id":"bad id"}]}""")]
    [InlineData("""{"data":[{"id":"gpt-oss-120b"},{"id":""}]}""")]
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

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = Key } };
        var plugin = new CerebrasPlugin(new HttpClient(new AsyncHandler(async ct =>
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
        using var sut = new CerebrasPlugin(new HttpClient(new CapturingHandler((_, _) =>
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

        await sut.SetSettingValueAsync("api-key", $" {Key} ");
        Assert.Equal(["account-a-model"], Ids(sut));
        Assert.Equal(Key, host.Secrets["api-key"]);

        await sut.SetSettingValueAsync("api-key", "csk-account-b");
        Assert.Equal(s_fallbackIds, Ids(sut));
        using (var restarted = await RestartedAsync(host))
            Assert.Equal(s_fallbackIds, Ids(restarted));

        // The saved catalog still belongs to account A, so returning to that key restores it.
        await sut.SetSettingValueAsync("api-key", Key);
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

        await Assert.ThrowsAsync<IOException>(() => sut.SetSettingValueAsync("api-key", "csk-account-b"));

        Assert.Equal(Key, await sut.GetSettingValueAsync("api-key"));
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

        await sut.SetSettingValueAsync("api-key", "  ");

        Assert.False(sut.IsAvailable);
        Assert.False(host.Secrets.ContainsKey("api-key"));
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
            await sut.SetSettingValueAsync("api-key", "csk-replacement");
        finish.SetResult();

        var result = await pending;
        Assert.False(result!.IsSuccess);
        Assert.Equal("Settings.ModelRefreshStale", result.Message);
        Assert.DoesNotContain(sut.SupportedModels, m => m.Id == "stale-model");
        Assert.DoesNotContain("stale-model", host.GetSetting<CerebrasModelCatalog>("fetchedModels")?.Models ?? []);
    }

    [Fact]
    public async Task ValidateAsync_DiscoveryIsBoundedByDeadline()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = Key } };
        var plugin = new CerebrasPlugin(new HttpClient(new AsyncHandler(async ct =>
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
        host.SetSetting("fetchedModels", new CerebrasModelCatalog(
            CerebrasPlugin.Fingerprint(Key), ["kept-model", "bad id", "kept-model", ""]));
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) =>
            throw new InvalidOperationException("Unexpected network request.")), host);

        Assert.Equal(["kept-model"], Ids(sut));
    }

    private static async Task RunAsync(CerebrasPlugin sut, string model, bool streaming)
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

    private static async Task<CerebrasPlugin> ActivatedAsync(
        HttpMessageHandler handler, TestPluginHostServices? host = null)
    {
        host ??= new TestPluginHostServices();
        host.Secrets["api-key"] = Key;
        var sut = new CerebrasPlugin(new HttpClient(handler));
        await sut.ActivateAsync(host);
        return sut;
    }

    [Fact]
    public async Task Activate_IgnoresACatalogSavedForAnotherKey()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("fetchedModels", new CerebrasModelCatalog(CerebrasPlugin.Fingerprint("csk-other"), ["other-model"]));
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) =>
            throw new InvalidOperationException("Unexpected network request.")), host);

        Assert.Equal(s_fallbackIds, Ids(sut));
    }

    private static async Task<CerebrasPlugin> RestartedAsync(TestPluginHostServices host)
    {
        var restarted = new CerebrasPlugin(new HttpClient(new CapturingHandler((_, _) =>
            throw new InvalidOperationException("Unexpected network request."))));
        await restarted.ActivateAsync(host);
        return restarted;
    }

    private static string[] Ids(CerebrasPlugin plugin) => plugin.SupportedModels.Select(m => m.Id).ToArray();

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

        public Task StoreSecretAsync(string key, string value)
        {
            if (FailSecretWrites)
                throw new IOException("Secret store unavailable.");
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
