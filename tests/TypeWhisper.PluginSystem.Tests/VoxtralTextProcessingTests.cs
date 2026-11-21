using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Voxtral;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

// The CapturingHandler lambdas assert on the outgoing request and return a canned response;
// ReSharper reads those asserts as precondition-only parameter use.
// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local

namespace TypeWhisper.PluginSystem.Tests;

public sealed class VoxtralTextProcessingTests
{
    private const string Key = "mistral-account-a";

    private static readonly string[] s_fallbackIds =
    [
        "mistral-small-2603", "mistral-medium-2604", "mistral-large-4",
        "ministral-14b-2512", "ministral-8b-2512", "ministral-3b-2512",
    ];

    private const string Answer =
        """{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""";

    private const string AccountCatalog = """
        {"object":"list","data":[
          {"id":"voxtral-mini-2507","object":"model","capabilities":{"completion_chat":false,"audio_transcription":true}},
          {"id":"mistral-large-2512","capabilities":{"completion_chat":true,"function_calling":true}},
          {"id":"mistral-small-latest","capabilities":{"completion_chat":true},"aliases":["mistral-small-2603"]},
          {"id":"mistral-embed","capabilities":{"completion_chat":false}},
          {"id":"ft:open-mistral-7b:old","capabilities":{"completion_chat":true},"archived":true},
          {"id":"codestral-latest","capabilities":{"completion_chat":true,"completion_fim":true}},
          {"id":"mistral-small-latest","capabilities":{"completion_chat":true}}
        ]}
        """;

    // Shaped like Mistral's listing: aliases appear as their own entries, successors next to retired models.
    private const string AliasCatalog = """
        {"object":"list","data":[
          {"id":"mistral-large-2512","name":"mistral-large-2512","capabilities":{"completion_chat":true},"aliases":["mistral-large-latest"]},
          {"id":"mistral-large-latest","capabilities":{"completion_chat":true},"aliases":["mistral-large-2512"]},
          {"id":"mistral-large-4","capabilities":{"completion_chat":true},"aliases":["mistral-large-4-0"]},
          {"id":"mistral-small-latest","capabilities":{"completion_chat":true},"aliases":["mistral-small-2603"]},
          {"id":"mistral-small-2603","capabilities":{"completion_chat":true},"aliases":["mistral-small-latest"]},
          {"id":"magistral-medium-2509","name":"Magistral Medium 1.2","capabilities":{"completion_chat":true},"aliases":["magistral-medium-latest"]},
          {"id":"magistral-medium-latest","capabilities":{"completion_chat":true},"aliases":["magistral-medium-2509"]},
          {"id":"labs-devstral-small-2512","capabilities":{"completion_chat":true}},
          {"id":"voxtral-small-2507","capabilities":{"completion_chat":true,"audio":true}},
          {"id":"voxtral-mini-tts-2603","capabilities":{"completion_chat":true,"audio_speech":true}},
          {"id":"codestral-2508","capabilities":{"completion_chat":true,"completion_fim":true},"aliases":["codestral-latest"]},
          {"id":"mistral-embed","capabilities":{"completion_chat":false}}
        ]}
        """;

    [Fact]
    public async Task TextRole_KeepsTheVoxtralIdentityAndSharesItsKey()
    {
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json(Answer)), host);

        Assert.Equal("com.typewhisper.voxtral", sut.PluginId);
        Assert.Equal("voxtral", sut.ProviderId);
        Assert.Equal("Voxtral", sut.ProviderDisplayName);
        Assert.Equal("Mistral", sut.ProviderName);
        Assert.True(sut.IsAvailable);
        Assert.Equal(s_fallbackIds, Ids(sut));
        Assert.Equal(
            ["Mistral Small 4", "Mistral Medium 3.5", "Mistral Large 4", "Ministral 3 14B", "Ministral 3 8B", "Ministral 3 3B"],
            sut.SupportedModels.Select(m => m.DisplayName));

        await sut.SetSettingValueAsync("api-key", " ");

        Assert.False(sut.IsAvailable);
        Assert.False(sut.IsConfigured);
        Assert.False(host.Secrets.ContainsKey("api-key"));
    }

    [Fact]
    public async Task ProcessAsync_WithoutKeyFailsAsConfigurationWithoutNetwork()
    {
        using var sut = new VoxtralPlugin(new HttpClient(new CapturingHandler((_, _) =>
            throw new InvalidOperationException("Unexpected network request."))));
        await sut.ActivateAsync(new TestPluginHostServices());

        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.ProcessAsync("system", "user", "", CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.Configuration, error.FailureKind);
    }

    [Theory]
    [InlineData("", "mistral-small-2603", "none")]
    [InlineData("  ", "mistral-small-2603", "none")]
    [InlineData("mistral-small-latest", "mistral-small-2603", "none")]
    [InlineData(" ministral-8b-latest ", "ministral-8b-2512", null)]
    [InlineData("ministral-8b-2410", "ministral-8b-2512", null)]
    [InlineData("open-mistral-nemo", "open-mistral-nemo", null)]
    public async Task ProcessAsync_SendsChatCompletionWithSharedKeyAndProviderDefaults(
        string model, string expected, string? effort)
    {
        using var sut = await ActivatedAsync(new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.mistral.ai/v1/chat/completions", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(Key, request.Headers.Authorization.Parameter);
            using var doc = JsonDocument.Parse(body!);
            var root = doc.RootElement;
            Assert.Equal(expected, root.GetProperty("model").GetString());
            Assert.Equal(
                ["system", "user"],
                root.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()));
            Assert.Equal(LlmOutputTokenBudget.Calculate("system", "user"), root.GetProperty("max_tokens").GetInt32());
            Assert.Equal(effort, root.TryGetProperty("reasoning_effort", out var sentEffort) ? sentEffort.GetString() : null);
            Assert.False(root.TryGetProperty("temperature", out _));
            Assert.False(root.TryGetProperty("stream", out _));
            return Json(Answer);
        }));

        Assert.Equal("ok", await sut.ProcessAsync("system", "user", model, CancellationToken.None));
    }

    [Theory]
    [InlineData("magistral-medium-2509", true)]
    [InlineData("mistral-small-latest", false)]
    [InlineData("mistral-large-latest", true)]
    [InlineData("open-mistral-7b", false)]
    public async Task ProcessAsync_ReservesReasoningOutputOnlyForModelsThatReason(string model, bool reasoning)
    {
        JsonElement sent = default;
        using var sut = await ActivatedAsync(new CapturingHandler((_, body) =>
        {
            sent = JsonDocument.Parse(body!).RootElement.Clone();
            return Json(Answer);
        }));

        await sut.ProcessAsync("system", "user", model, CancellationToken.None);

        Assert.Equal(
            reasoning
                ? LlmOutputTokenBudget.CalculateWithReasoningReserve("system", "user")
                : LlmOutputTokenBudget.Calculate("system", "user"),
            sent.GetProperty("max_tokens").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Requests_SendEachModelsFastestReasoningLevelUnlessHighIsChosen(bool streaming)
    {
        var sent = new List<(string Model, string? Effort)>();
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((_, body) =>
        {
            var root = JsonDocument.Parse(body!).RootElement;
            sent.Add((root.GetProperty("model").GetString()!,
                root.TryGetProperty("reasoning_effort", out var effort) ? effort.GetString() : null));
            return streaming ? Sse("ok") : Json(Answer);
        }), host);
        string[] models = ["mistral-large-4", "mistral-medium-2604", "mistral-small-2603", "ministral-3b-2512"];

        Assert.Equal("fastest", await sut.GetSettingValueAsync("reasoningEffort"));
        foreach (var model in models)
            await RunAsync(sut, streaming, model);
        await sut.SetSettingValueAsync("reasoningEffort", "high");
        foreach (var model in models)
            await RunAsync(sut, streaming, model);

        Assert.Equal(
            [
                ("mistral-large-4", "low"), ("mistral-medium-2604", "none"), ("mistral-small-2603", "none"), ("ministral-3b-2512", null),
                ("mistral-large-4", "high"), ("mistral-medium-2604", "high"), ("mistral-small-2603", "high"), ("ministral-3b-2512", null),
            ],
            sent);
        using (var restarted = await RestartedAsync(host))
            Assert.Equal("high", await restarted.GetSettingValueAsync("reasoningEffort"));

        await sut.SetSettingValueAsync("reasoningEffort", "maximum");
        Assert.Equal("fastest", await sut.GetSettingValueAsync("reasoningEffort"));
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

        Assert.Equal("provider-default", await sut.GetSettingValueAsync("temperatureMode"));
        Assert.Equal("0.3", await sut.GetSettingValueAsync("temperature"));
        await RunAsync(sut, streaming);
        await sut.SetSettingValueAsync("temperatureMode", "custom");
        await RunAsync(sut, streaming);
        await sut.SetSettingValueAsync("temperature", "1.25");
        await RunAsync(sut, streaming);

        Assert.False(bodies[0].TryGetProperty("temperature", out _));
        Assert.Equal(0.3, bodies[1].GetProperty("temperature").GetDouble());
        Assert.Equal(1.25, bodies[2].GetProperty("temperature").GetDouble());

        using var restarted = await RestartedAsync(host);
        Assert.Equal("custom", await restarted.GetSettingValueAsync("temperatureMode"));
        Assert.Equal("1.25", await restarted.GetSettingValueAsync("temperature"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-0.1")]
    [InlineData("1.6")]
    [InlineData("2")]
    [InlineData("NaN")]
    [InlineData("0,5")]
    public async Task Temperature_InvalidValueIsRejectedWithoutSaving(string value)
    {
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json(Answer)), host);
        var writes = host.SettingWrites;

        var error = await Assert.ThrowsAsync<ArgumentException>(() => sut.SetSettingValueAsync("temperature", value));

        Assert.Equal("Settings.TemperatureInvalid", error.Message);
        Assert.Equal(writes, host.SettingWrites);
        Assert.Equal("0.3", await sut.GetSettingValueAsync("temperature"));
    }

    [Fact]
    public async Task Activate_IgnoresInvalidSavedTemperatureSettings()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("temperatureMode", "unknown");
        host.SetSetting("temperature", 1.75);
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json(Answer)), host);

        Assert.Equal("provider-default", await sut.GetSettingValueAsync("temperatureMode"));
        Assert.Equal("0.3", await sut.GetSettingValueAsync("temperature"));
    }

    [Theory]
    [InlineData("length")]
    [InlineData("model_length")]
    public async Task ProcessAsync_RejectsTruncatedAnswer(string finishReason)
    {
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json(
            $$"""{"choices":[{"message":{"role":"assistant","content":"partial"},"finish_reason":"{{finishReason}}"}]}""")));

        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.ProcessAsync("system", "user", "", CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.OutputTruncated, error.FailureKind);
    }

    [Fact]
    public async Task ProcessAsync_ReturnsOnlyTextChunksFromReasoningContent()
    {
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json("""
            {"choices":[{"message":{"role":"assistant","content":[
              {"type":"thinking","thinking":[{"type":"text","text":"private thinking"}]},
              {"type":"text","text":"Corrected text."}
            ]},"finish_reason":"stop"}]}
            """)));

        Assert.Equal("Corrected text.", await sut.ProcessAsync("system", "user", "magistral-small-2509", CancellationToken.None));
    }

    [Fact]
    public async Task ProcessStreamingAsync_ToggleOffPersistsAndSendsOneRequest()
    {
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((_, body) =>
        {
            Assert.DoesNotContain("\"stream\"", body);
            return Json(Answer);
        }), host);

        await sut.SetSettingValueAsync("streamResponses", "false");
        var chunks = new List<string>();
        await foreach (var chunk in sut.ProcessStreamingAsync("system", "user", "", CancellationToken.None))
            chunks.Add(chunk);

        Assert.Equal(["ok"], chunks);
        using var restarted = await RestartedAsync(host);
        Assert.Equal("false", await restarted.GetSettingValueAsync("streamResponses"));
    }

    [Fact]
    public async Task SettingDefinitions_AddTextSettingsBesideTheTranscriptionModel()
    {
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json(Answer)));

        Assert.Equal(
            ["api-key", "selectedModel", "streamResponses", "reasoningEffort", "temperatureMode", "temperature"],
            sut.GetSettingDefinitions().Select(d => d.Key));
    }

    [Fact]
    public async Task ValidateAsync_CommitsTheAccountChatModelsWithTheDefaultFirst()
    {
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.mistral.ai/v1/models", request.RequestUri!.ToString());
            Assert.Equal(Key, request.Headers.Authorization!.Parameter);
            return Json(AccountCatalog);
        }), host);
        var capabilityChanges = host.CapabilityChanges;

        var result = await sut.ValidateAsync();

        Assert.True(result!.IsSuccess);
        Assert.Equal("Settings.ApiKeyValidFetchedTextModels", result.Message);
        // The alias group takes the known dated ID; code completion and archived models are not offered.
        Assert.Equal(["mistral-small-2603", "mistral-large-2512"], Ids(sut));
        Assert.Equal("Mistral Small 4", sut.SupportedModels[0].DisplayName);
        Assert.Equal("mistral-small-2603", sut.ResolveSavedModelId("mistral-small-latest"));
        Assert.Equal(capabilityChanges + 1, host.CapabilityChanges);

        using var restarted = await RestartedAsync(host);
        Assert.Equal(Ids(sut), Ids(restarted));
        Assert.Equal("mistral-small-2603", restarted.ResolveSavedModelId("mistral-small-latest"));
    }

    [Fact]
    public async Task ValidateAsync_GroupsAliasesHidesReplacedAndNonTextModels()
    {
        var bodies = new List<string>();
        using var sut = await ActivatedAsync(new CapturingHandler((request, body) =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(AliasCatalog);
            bodies.Add(JsonDocument.Parse(body!).RootElement.GetProperty("model").GetString()!);
            return Json(Answer);
        }));

        Assert.True((await sut.ValidateAsync())!.IsSuccess);

        Assert.Equal(["mistral-small-2603", "mistral-large-4", "magistral-medium-2509"], Ids(sut));
        Assert.Equal(
            ["Mistral Small 4", "Mistral Large 4", "Magistral Medium 1.2"],
            sut.SupportedModels.Select(m => m.DisplayName));
        foreach (var saved in new[] { "mistral-large-latest", "mistral-large-2512", "mistral-large-4-0" })
            Assert.Equal("mistral-large-4", sut.ResolveSavedModelId(saved));
        Assert.Equal("magistral-medium-2509", sut.ResolveSavedModelId("magistral-medium-latest"));
        Assert.Null(sut.ResolveSavedModelId("codestral-latest"));
        Assert.Null(sut.ResolveSavedModelId("labs-devstral-small-2512"));

        await sut.ProcessAsync("system", "user", "mistral-large-latest", CancellationToken.None);
        Assert.Equal(["mistral-large-4"], bodies);
    }

    [Fact]
    public async Task ValidateAsync_AliasesOfAHiddenModelResolveToItsSuccessor()
    {
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json("""
            {"data":[
              {"id":"mistral-small-3-2-25-06","name":"mistral-small-3-2-25-06","capabilities":{"completion_chat":true},
               "aliases":["mistral-small-2506","mistral-small-latest"]},
              {"id":"mistral-small-2603","capabilities":{"completion_chat":true}}
            ]}
            """)));

        Assert.True((await sut.ValidateAsync())!.IsSuccess);

        Assert.Equal(["mistral-small-2603"], Ids(sut));
        foreach (var saved in new[] { "mistral-small-latest", "mistral-small-2506", "mistral-small-3-2-25-06" })
            Assert.Equal("mistral-small-2603", sut.ResolveSavedModelId(saved));
    }

    [Fact]
    public async Task ResolveModelId_MovesOnlyFormerDefaultsUntilTheAccountCatalogIsValidated()
    {
        var bodies = new List<string>();
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((request, body) =>
        {
            if (request.Method == HttpMethod.Get)
                return Json("""{"data":[{"id":"mistral-large-2512","capabilities":{"completion_chat":true}}]}""");
            bodies.Add(JsonDocument.Parse(body!).RootElement.GetProperty("model").GetString()!);
            return Json(Answer);
        }), host);

        Assert.Equal("mistral-small-2603", sut.ResolveSavedModelId("mistral-small-latest"));
        Assert.Equal("mistral-large-4", sut.ResolveSavedModelId("mistral-large-latest"));
        // An account model is not renamed from the default lineup, though a request still uses the successor.
        Assert.Null(sut.ResolveSavedModelId("mistral-large-2512"));
        Assert.Null(sut.ResolveSavedModelId("ministral-8b-latest"));
        await sut.ProcessAsync("system", "user", "mistral-large-2512", CancellationToken.None);
        Assert.Equal(["mistral-large-4"], bodies);

        Assert.True((await sut.ValidateAsync())!.IsSuccess);
        Assert.Equal("mistral-large-2512", sut.ResolveSavedModelId("mistral-large-2512"));

        // Removing the key falls back to the default lineup without renaming the account's model.
        await sut.SetSettingValueAsync("api-key", "");
        Assert.Null(sut.ResolveSavedModelId("mistral-large-2512"));
    }

    [Fact]
    public async Task ValidateAsync_AnAccountWithOnlyNonTextModelsIsRejected()
    {
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json("""
            {"data":[
              {"id":"codestral-latest","capabilities":{"completion_chat":true,"completion_fim":true}},
              {"id":"labs-mistral-small-creative","capabilities":{"completion_chat":true}}
            ]}
            """)));

        Assert.False((await sut.ValidateAsync())!.IsSuccess);
        Assert.Equal(s_fallbackIds, Ids(sut));
    }

    [Fact]
    public async Task Activate_KeepsACatalogSavedByAnEarlierVersionUntilTheNextValidation()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = Key } };
        host.SetSetting("fetchedTextModels", new MistralTextModelCatalog(
            VoxtralPlugin.Fingerprint(Key), ["open-mistral-nemo", "mistral-large-latest", "mistral-small-latest"]));
        using var sut = await RestartedAsync(host);

        Assert.Equal(["mistral-small-latest", "mistral-large-latest", "open-mistral-nemo"], Ids(sut));
        Assert.Equal("Mistral Small 4", sut.SupportedModels[0].DisplayName);
        Assert.Equal("mistral-small-latest", sut.ResolveSavedModelId("mistral-small-latest"));
        Assert.Null(sut.ResolveSavedModelId("mistral-medium-latest"));
        // The account never confirmed which model its "-latest" key names, so no level is assumed.
        Assert.Null(sut.ReasoningEffortFor("mistral-small-latest"));
    }

    [Fact]
    public async Task ValidateAsync_KnownGeneralModelsLeadWhenTheDefaultIsAbsent()
    {
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json("""
            {"data":[
              {"id":"codestral-latest","capabilities":{"completion_chat":true}},
              {"id":"mistral-large-latest","capabilities":{"completion_chat":true}},
              {"id":"mistral-medium-latest","capabilities":{"completion_chat":true}}
            ]}
            """)));

        Assert.True((await sut.ValidateAsync())!.IsSuccess);
        Assert.Equal(["mistral-medium-latest", "mistral-large-latest", "codestral-latest"], Ids(sut));
        // Without aliases in the listing, no reasoning level is assumed for a "-latest" key.
        Assert.Null(sut.ReasoningEffortFor("mistral-medium-latest"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"data":[42]}""")]
    [InlineData("""{"data":[{"id":"mistral-small-latest"}]}""")]
    [InlineData("""{"data":[{"id":"mistral-small-latest","capabilities":null}]}""")]
    [InlineData("""{"data":[{"id":"bad id","capabilities":{"completion_chat":true}}]}""")]
    [InlineData("""{"data":[{"id":"mistral-embed","capabilities":{"completion_chat":false}}]}""")]
    [InlineData("""{"data":[{"id":"ft:old","capabilities":{"completion_chat":true},"archived":true}]}""")]
    public async Task ValidateAsync_MalformedOrChatlessCatalogKeepsSavedModels(string body)
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
    public async Task ValidateAsync_OversizedCatalogIsRejectedWhole()
    {
        var models = string.Join(",", Enumerable.Range(0, 1001)
            .Select(i => $$$"""{"id":"model-{{{i}}}","capabilities":{"completion_chat":true}}"""));
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
            Json("""{"message":"Unauthorized","request_id":"r"}""", status)));

        var result = await sut.ValidateAsync();

        Assert.False(result!.IsSuccess);
        Assert.Equal("Settings.ApiKeyInvalid", result.Message);
    }

    [Fact]
    public async Task ValidateAsync_NetworkFailureIsNotReportedAsAnInvalidKey()
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
        using var sut = new VoxtralPlugin(new HttpClient(new CapturingHandler((_, _) =>
            throw new InvalidOperationException("Unexpected network request."))));
        await sut.ActivateAsync(new TestPluginHostServices());

        var result = await sut.ValidateAsync();

        Assert.False(result!.IsSuccess);
        Assert.Equal("Settings.EnterApiKey", result.Message);
    }

    [Fact]
    public async Task ValidateAsync_DiscoveryIsBoundedByDeadline()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = Key } };
        var plugin = new VoxtralPlugin(new HttpClient(new AsyncHandler(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json("{}");
        }))) { ModelDiscoveryDeadline = TimeSpan.FromMilliseconds(50) };
        using var sut = plugin;
        await sut.ActivateAsync(host);

        var timeout = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.FetchTextModelsAsync(Key, CancellationToken.None));
        var result = await sut.ValidateAsync();

        Assert.Equal("Errors.ModelRefreshTimedOut", timeout.Message);
        Assert.Equal(PluginRequestFailureKind.Timeout, timeout.FailureKind);
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
    public async Task ValidateAsync_FailedCatalogSaveLeavesLiveCatalogUnchanged()
    {
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) => Json(AccountCatalog)), host);
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
            Json("""{"data":[{"id":"account-a-model","capabilities":{"completion_chat":true}}]}""")), host);
        Assert.True((await sut.ValidateAsync())!.IsSuccess);
        Assert.Equal(["account-a-model"], Ids(sut));

        await sut.SetSettingValueAsync("api-key", $" {Key} ");
        Assert.Equal(["account-a-model"], Ids(sut));
        Assert.Equal(Key, host.Secrets["api-key"]);

        await sut.SetSettingValueAsync("api-key", "mistral-account-b");
        Assert.Equal(s_fallbackIds, Ids(sut));
        using (var restarted = await RestartedAsync(host))
            Assert.Equal(s_fallbackIds, Ids(restarted));

        // The saved catalog still belongs to account A, so returning to that key restores it.
        await sut.SetSettingValueAsync("api-key", Key);
        Assert.Equal(["account-a-model"], Ids(sut));
    }

    [Fact]
    public async Task ApiKeyChange_FailedSecretWriteKeepsOldKeyAndCatalog()
    {
        var host = new TestPluginHostServices();
        using var sut = await ActivatedAsync(new CapturingHandler((_, _) =>
            Json("""{"data":[{"id":"account-a-model","capabilities":{"completion_chat":true}}]}""")), host);
        Assert.True((await sut.ValidateAsync())!.IsSuccess);
        host.FailSecretWrites = true;

        await Assert.ThrowsAsync<IOException>(() => sut.SetSettingValueAsync("api-key", "mistral-account-b"));

        Assert.Equal(Key, await sut.GetSettingValueAsync("api-key"));
        Assert.Equal(["account-a-model"], Ids(sut));
        host.FailSecretWrites = false;
        using var restarted = await RestartedAsync(host);
        Assert.Equal(["account-a-model"], Ids(restarted));
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
            return Json("""{"data":[{"id":"stale-model","capabilities":{"completion_chat":true}}]}""");
        }), host);

        var pending = sut.ValidateAsync();
        await entered.Task;
        if (deactivate)
            await sut.DeactivateAsync();
        else
            await sut.SetSettingValueAsync("api-key", "mistral-replacement");
        finish.SetResult();

        var result = await pending;
        Assert.False(result!.IsSuccess);
        Assert.Equal("Settings.ModelRefreshStale", result.Message);
        Assert.DoesNotContain(sut.SupportedModels, m => m.Id == "stale-model");
        Assert.DoesNotContain("stale-model", host.GetSetting<MistralTextModelCatalog>("fetchedTextModels")?.Models ?? []);
    }

    [Fact]
    public async Task Activate_IgnoresACatalogSavedForAnotherKeyAndDropsInvalidIds()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("fetchedTextModels", new MistralTextModelCatalog(VoxtralPlugin.Fingerprint("other"), ["other-model"]));
        using (var other = await ActivatedAsync(new CapturingHandler((_, _) =>
                   throw new InvalidOperationException("Unexpected network request.")), host))
            Assert.Equal(s_fallbackIds, Ids(other));

        host.SetSetting("fetchedTextModels", new MistralTextModelCatalog(
            VoxtralPlugin.Fingerprint(Key), ["kept-model", "bad id", "kept-model", ""]));
        using var sut = await RestartedAsync(host);
        Assert.Equal(["kept-model"], Ids(sut));
    }

    private static async Task RunAsync(VoxtralPlugin sut, bool streaming, string model = "")
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

    private static async Task<VoxtralPlugin> ActivatedAsync(
        HttpMessageHandler handler, TestPluginHostServices? host = null)
    {
        host ??= new TestPluginHostServices();
        host.Secrets["api-key"] = Key;
        var sut = new VoxtralPlugin(new HttpClient(handler));
        await sut.ActivateAsync(host);
        return sut;
    }

    private static async Task<VoxtralPlugin> RestartedAsync(TestPluginHostServices host)
    {
        var restarted = new VoxtralPlugin(new HttpClient(new CapturingHandler((_, _) =>
            throw new InvalidOperationException("Unexpected network request."))));
        await restarted.ActivateAsync(host);
        return restarted;
    }

    private static string[] Ids(VoxtralPlugin plugin) => plugin.SupportedModels.Select(m => m.Id).ToArray();

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
