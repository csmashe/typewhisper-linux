using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Plugin.OpenAi;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

// The CapturingHandler lambdas assert on the outgoing request (method, URI,
// headers, body) and return a canned response. ReSharper reads xUnit asserts
// as precondition checks and concludes those parameters are only validated,
// never used — but asserting on the request is exactly what these tests
// verify, so the inspection is a false positive here.
// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local

namespace TypeWhisper.PluginSystem.Tests;

public class OpenAiPluginTests
{
    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifest = LoadManifest();
        var sut = new OpenAiPlugin();

        Assert.Equal(manifest.GetProperty("version").GetString(), sut.PluginVersion);
    }

    [Fact]
    public void Manifest_AdvertisesOpenAiPluginIdentity()
    {
        var manifest = LoadManifest();

        Assert.Equal("com.typewhisper.openai", manifest.GetProperty("id").GetString());
        Assert.Equal("OpenAI / ChatGPT", manifest.GetProperty("name").GetString());
        Assert.Equal(["transcription", "llm", "tts"], manifest.GetProperty("categories").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(
            "TypeWhisper.Plugin.OpenAi.dll",
            manifest.GetProperty("assemblyName").GetString()
        );
        Assert.Equal(
            "TypeWhisper.Plugin.OpenAi.OpenAiPlugin",
            manifest.GetProperty("pluginClass").GetString()
        );
    }

    [Fact]
    public async Task ActivateAsync_DefaultsToGpt55AndExposesTtsProvider()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-test" } };

        var sut = new OpenAiPlugin();
        await sut.ActivateAsync(host);

        Assert.IsType<ITtsProviderPlugin>(sut, exactMatch: false);
        Assert.True(sut.IsConfigured);
        Assert.True(sut.IsAvailable);
        Assert.Equal("gpt-5.5", sut.SupportedModels[0].Id);
        Assert.Equal("whisper-1", sut.SelectedModelId);
        Assert.Equal("marin", sut.SelectedVoiceId);
        // Default model is whisper-1 (non-streaming), so SupportsStreaming
        // is false even though the realtime model is now wired up
        // (C5 Phase 7). The flag flips true only when the user selects
        // gpt-realtime-whisper — see
        // SupportsStreaming_RequiresRealtimeModelAndApiKeyMode.
        Assert.False(sut.SupportsStreaming);
    }

    [Fact]
    public async Task LocalSelectionChanges_PersistWithoutRebuildingCapabilities()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-test" } };
        var sut = new OpenAiPlugin();
        await sut.ActivateAsync(host);

        sut.SelectVoice("nova");
        sut.SelectLlmModel("gpt-4o");

        Assert.Equal("nova", host.GetSetting<string>("selectedVoice"));
        Assert.Equal("gpt-4o", host.GetSetting<string>("selectedLLMModel"));
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public void UsesResponsesApi_RoutesGPT5AndOSeriesReasoningModelsToResponses()
    {
        Assert.True(OpenAiPlugin.UsesResponsesApi("gpt-5.5"));
        Assert.True(OpenAiPlugin.UsesResponsesApi("gpt-5.4-mini"));
        // o-series reasoning models cannot use /v1/chat/completions safely
        // (they reject the legacy temperature/max_tokens shape). They must be
        // routed to /v1/responses like GPT-5.
        Assert.True(OpenAiPlugin.UsesResponsesApi("o1-preview"));
        Assert.True(OpenAiPlugin.UsesResponsesApi("o3-mini"));
        Assert.True(OpenAiPlugin.UsesResponsesApi("o4-mini"));
        Assert.False(OpenAiPlugin.UsesResponsesApi("gpt-4o"));
        Assert.False(OpenAiPlugin.UsesResponsesApi("gpt-4.1-mini"));
    }

    [Fact]
    public void MapApiReasoningEffort_DemotesXHighToHighForResponsesApi()
    {
        // OpenAI's /v1/responses accepts low/medium/high; xhigh is a Codex CLI
        // internal value that would 400 on the public API.
        Assert.Equal("low", OpenAiPlugin.MapApiReasoningEffort("low"));
        Assert.Equal("medium", OpenAiPlugin.MapApiReasoningEffort("medium"));
        Assert.Equal("high", OpenAiPlugin.MapApiReasoningEffort("high"));
        Assert.Equal("high", OpenAiPlugin.MapApiReasoningEffort("xhigh"));
        Assert.Null(OpenAiPlugin.MapApiReasoningEffort(null));
    }

    [Fact]
    public async Task ProcessAsync_ApiKeyMode_DemotesXHighReasoningForResponsesApi()
    {
        string? capturedBody = null;
        var handler = new CapturingHandler((_, body) =>
        {
            capturedBody = body;
            return Task.FromResult(JsonResponse("""{"output_text":"OK"}"""));
        });

        using var httpClient = new HttpClient(handler);
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-test" } };
        host.SetSetting("reasoningEffort", "xhigh");
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        await sut.ProcessAsync("system", "user", "gpt-5.5", CancellationToken.None);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("high", doc.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task ProcessAsync_ChatGptMode_PreservesXHighReasoning()
    {
        string? capturedBody = null;
        var handler = new CapturingHandler((_, body) =>
        {
            capturedBody = body;
            return Task.FromResult(JsonResponse("""{"output_text":"OK"}"""));
        });

        using var httpClient = new HttpClient(handler);
        var host = new TestPluginHostServices();
        host.SetSetting("authMode", "chatgpt");
        host.Secrets["oauth-access-token"] = "access-token";
        host.Secrets["oauth-refresh-token"] = "refresh-token";
        host.SetSetting("oauthExpiresAt", DateTimeOffset.UtcNow.AddHours(1));
        host.SetSetting("reasoningEffort", "xhigh");
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        await sut.ProcessAsync("system", "user", "gpt-5.5", CancellationToken.None);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("xhigh", doc.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task ProcessAsync_OSeriesModel_RoutesToResponsesApiNotChatCompletions()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new CapturingHandler((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(JsonResponse("""{"output_text":"OK"}"""));
        });

        using var httpClient = new HttpClient(handler);
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-test" } };
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        await sut.ProcessAsync("system", "user", "o4-mini", CancellationToken.None);

        Assert.Equal("https://api.openai.com/v1/responses", capturedRequest?.RequestUri?.ToString());
    }

    [Fact]
    public void ResponsesRequestBody_UsesStoreFalseAndReasoning()
    {
        var body = OpenAiResponsesClient.CreateRequestBody(
            model: "gpt-5.5",
            systemPrompt: "Fix grammar",
            userText: "hello world",
            reasoningEffort: "medium");

        Assert.Equal("gpt-5.5", body["model"].GetString());
        Assert.False(body["store"].GetBoolean());
        Assert.Equal("Fix grammar", body["instructions"].GetString());
        Assert.Equal("medium", body["reasoning"].GetProperty("effort").GetString());
        Assert.Equal("user", body["input"][0].GetProperty("role").GetString());
    }

    [Fact]
    public void ResponsesParser_ExtractsOutputTextFromOutputArray()
    {
        const string json = """
                            {
                              "id": "resp_123",
                              "output": [
                                {
                                  "type": "message",
                                  "content": [
                                    { "type": "output_text", "text": "Cleaned transcript" }
                                  ]
                                }
                              ]
                            }
                            """;

        Assert.Equal("Cleaned transcript", OpenAiResponsesClient.ParseResponse(json));
    }

    [Fact]
    public void TtsConfiguration_UsesMiniTtsPcmAndDefaultVoice()
    {
        Assert.Equal("marin", OpenAiTtsConfiguration.DefaultVoiceId);
        Assert.Equal(13, OpenAiTtsConfiguration.AvailableVoices.Count);
        Assert.Contains(OpenAiTtsConfiguration.AvailableVoices, voice => voice.Id == "cedar");

        var body = OpenAiTtsConfiguration.CreateRequestBody(
            text: "Hallo Welt",
            voice: null,
            instructions: "Speak calmly.");

        Assert.Equal("gpt-4o-mini-tts", body["model"].GetString());
        Assert.Equal("marin", body["voice"].GetString());
        Assert.Equal("Hallo Welt", body["input"].GetString());
        Assert.Equal("Speak calmly.", body["instructions"].GetString());
        Assert.Equal("pcm", body["response_format"].GetString());
    }

    [Fact]
    public async Task ProcessAsync_UsesResponsesApiForGPT5Models()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new CapturingHandler(async (request, body) =>
        {
            capturedRequest = request;
            capturedBody = body;
            await Task.Yield();
            return JsonResponse("""{"output_text":"Cleaned transcript"}""");
        });

        using var httpClient = new HttpClient(handler);
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-live" } };
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync("Fix grammar", "hello world", "gpt-5.5", CancellationToken.None);

        Assert.Equal("Cleaned transcript", result);
        Assert.Equal(HttpMethod.Post, capturedRequest?.Method);
        Assert.Equal("https://api.openai.com/v1/responses", capturedRequest?.RequestUri?.ToString());
        Assert.Equal("Bearer", capturedRequest?.Headers.Authorization?.Scheme);
        Assert.Equal("sk-live", capturedRequest?.Headers.Authorization?.Parameter);
        Assert.NotNull(capturedBody);

        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.False(doc.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal("medium", doc.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task RefreshAvailableLlmModels_QueriesModelsEndpointFiltersChatModelsAndPersists()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new CapturingHandler((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(JsonResponse("""
            {
              "data": [
                { "id": "whisper-1", "owned_by": "openai" },
                { "id": "gpt-4o-mini-transcribe", "owned_by": "openai" },
                { "id": "gpt-4o-mini-transcribe-2025-03-20", "owned_by": "openai" },
                { "id": "gpt-4o-transcribe-diarize", "owned_by": "openai" },
                { "id": "gpt-4o-realtime-preview-2024-12-17", "owned_by": "openai" },
                { "id": "gpt-4o-search-preview", "owned_by": "openai" },
                { "id": "gpt-audio-2025-08-28", "owned_by": "openai" },
                { "id": "gpt-image-1", "owned_by": "openai" },
                { "id": "o4-mini", "owned_by": "openai" },
                { "id": "gpt-4.1-mini", "owned_by": "openai" },
                { "id": "tts-1", "owned_by": "openai" }
              ]
            }
            """));
        });

        using var httpClient = new HttpClient(handler);
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-live" } };
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var models = await sut.RefreshAvailableLlmModelsAsync(CancellationToken.None);

        Assert.Equal(["gpt-4.1-mini", "o4-mini"], models.Select(m => m.Id).ToArray());
        Assert.Equal(["gpt-4.1-mini", "o4-mini"], sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal("https://api.openai.com/v1/models", capturedRequest?.RequestUri?.ToString());
        Assert.Equal("Bearer", capturedRequest?.Headers.Authorization?.Scheme);
        Assert.Equal("sk-live", capturedRequest?.Headers.Authorization?.Parameter);
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);

        var cachedModels = host.GetSetting<List<OpenAiFetchedModel>>("fetchedLLMModels");
        Assert.NotNull(cachedModels);
        Assert.Equal(["gpt-4.1-mini", "o4-mini"], cachedModels.Select(m => m.Id).ToArray());
    }

    [Fact]
    public void IsChatModel_IncludesBareOSeriesGaModelIds()
    {
        // OpenAI ships bare GA model IDs like `o1`, `o3` alongside dashed
        // variants. Upstream's verbatim filter required the trailing hyphen
        // and would drop these from the fetched catalog even though
        // UsesResponsesApi already routes them correctly.
        Assert.True(OpenAiPlugin.IsChatModel("o1"));
        Assert.True(OpenAiPlugin.IsChatModel("o3"));
        Assert.True(OpenAiPlugin.IsChatModel("o1-mini"));
        Assert.True(OpenAiPlugin.IsChatModel("o3-mini"));
        Assert.True(OpenAiPlugin.IsChatModel("o4-mini"));
        Assert.True(OpenAiPlugin.IsChatModel("gpt-4.1-mini"));
        Assert.True(OpenAiPlugin.IsChatModel("chatgpt-4o-latest"));
        // Still exclude non-chat capabilities.
        Assert.False(OpenAiPlugin.IsChatModel("whisper-1"));
        Assert.False(OpenAiPlugin.IsChatModel("o4-mini-transcribe"));
        Assert.False(OpenAiPlugin.IsChatModel("gpt-4o-realtime-preview"));
        Assert.False(OpenAiPlugin.IsChatModel("tts-1"));
    }

    [Fact]
    public async Task RefreshAvailableLlmModels_NormalizesSelectedModelWhenItIsNotInFetchedCatalog()
    {
        // Upstream's RefreshAvailableLlmModelsAsync did not re-normalize the
        // selected model after replacing the fallback catalog with fetched
        // ones, so a previously persisted "gpt-5.5" would dangle when the API
        // catalog only contained other models — sending the dangling ID would
        // 404 at runtime.
        var handler = new CapturingHandler((_, _) => Task.FromResult(JsonResponse("""
        {
          "data": [
            { "id": "gpt-4.1-mini", "owned_by": "openai" },
            { "id": "o4-mini", "owned_by": "openai" }
          ]
        }
        """)));

        using var httpClient = new HttpClient(handler);
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-test" } };
        host.SetSetting("selectedLLMModel", "gpt-5.5");
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        await sut.RefreshAvailableLlmModelsAsync(CancellationToken.None);

        Assert.Equal("gpt-4.1-mini", sut.SelectedLlmModelId);
        Assert.Equal("gpt-4.1-mini", host.GetSetting<string>("selectedLLMModel"));
    }

    [Fact]
    public async Task ChatGptAuthMode_IsAvailableWithBrowserLoginTokensWithoutApiKey()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("authMode", "chatgpt");
        host.Secrets["oauth-access-token"] = "access-token";
        host.Secrets["oauth-refresh-token"] = "refresh-token";
        host.SetSetting("oauthAccountID", "acct_123");
        host.SetSetting("oauthExpiresAt", DateTimeOffset.UtcNow.AddHours(1));

        var sut = new OpenAiPlugin(new HttpClient(new CapturingHandler((_, _) => Task.FromResult(JsonResponse("{}")))));
        await sut.ActivateAsync(host);

        Assert.Equal(OpenAiAuthMode.ChatGpt, sut.AuthMode);
        Assert.False(sut.IsConfigured);
        Assert.True(sut.IsAvailable);
        Assert.Equal("gpt-5.5", sut.SupportedModels[0].Id);
    }

    [Fact]
    public void ChatGptAuthorizeUri_UsesPkceLoopbackAndOpenAiIssuer()
    {
        var uri = OpenAiOAuthClient.BuildAuthorizeUri(
            state: "state_123",
            pkce: new OpenAiPkceCodes("verifier", "challenge"));
        var query = ParseQuery(uri);

        Assert.Equal("https", uri.Scheme);
        Assert.Equal("auth.openai.com", uri.Host);
        Assert.Equal("/oauth/authorize", uri.AbsolutePath);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(OpenAiOAuthClient.ClientId, query["client_id"]);
        Assert.Equal(OpenAiOAuthClient.RedirectUri, query["redirect_uri"]);
        Assert.Equal("challenge", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("state_123", query["state"]);
    }

    [Fact]
    public void LoopbackOAuthServer_ParsesCallbackRequestLineAndRejectsWrongState()
    {
        var code = OpenAiLoopbackOAuthServer.ParseAuthorizationCode(
            "GET /auth/callback?code=abc123&state=expected HTTP/1.1",
            "expected");

        Assert.Equal("abc123", code);
        Assert.Throws<InvalidOperationException>(() =>
            OpenAiLoopbackOAuthServer.ParseAuthorizationCode(
                "GET /auth/callback?code=abc123&state=wrong HTTP/1.1",
                "expected"));
    }

    [Fact]
    public async Task ProcessAsync_UsesChatGptEndpointWhenChatGptAuthModeIsSelected()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new CapturingHandler((request, body) =>
        {
            capturedRequest = request;
            capturedBody = body;
            return Task.FromResult(JsonResponse("""{"output_text":"Cleaned with ChatGPT"}"""));
        });
        var host = new TestPluginHostServices();
        host.SetSetting("authMode", "chatgpt");
        host.SetSetting("oauthAccountID", "acct_123");
        host.SetSetting("oauthExpiresAt", DateTimeOffset.UtcNow.AddHours(1));
        host.Secrets["oauth-access-token"] = "access-token";
        host.Secrets["oauth-refresh-token"] = "refresh-token";

        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var result = await sut.ProcessAsync("Fix grammar", "hello world", "gpt-5.5", CancellationToken.None);

        Assert.Equal("Cleaned with ChatGPT", result);
        Assert.Equal("https://chatgpt.com/backend-api/codex/responses", capturedRequest?.RequestUri?.ToString());
        Assert.Equal("Bearer", capturedRequest?.Headers.Authorization?.Scheme);
        Assert.Equal("access-token", capturedRequest?.Headers.Authorization?.Parameter);
        Assert.Equal("acct_123", capturedRequest?.Headers.GetValues("ChatGPT-Account-Id").Single());
        Assert.Equal("text/event-stream", capturedRequest?.Headers.Accept.Single().MediaType);

        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("gpt-5.5", doc.RootElement.GetProperty("model").GetString());
        Assert.False(doc.RootElement.GetProperty("store").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public void ChatGptResponseParser_ExtractsServerSentEventText()
    {
        const string stream = """
                              event: response.output_text.delta
                              data: {"type":"response.output_text.delta","delta":"Hello"}
                              event: response.output_text.delta
                              data: {"type":"response.output_text.delta","delta":" world"}
                              data: [DONE]

                              """;

        Assert.Equal("Hello world", OpenAiChatGptClient.ParseResponseText(stream));
    }

    [Fact]
    public async Task ChatGptRefresh_PreservesExistingRefreshTokenWhenResponseOmitsRefreshToken()
    {
        // RFC 6749 §6: a refresh response is allowed to omit `refresh_token`,
        // meaning the previously issued refresh token is still valid. The
        // upstream code unconditionally overwrote `_oauthRefreshToken` from
        // the response, which would null out the only usable refresh token.
        HttpRequestMessage? capturedTokenRequest = null;
        var handler = new CapturingHandler((request, _) =>
        {
            if (request.RequestUri?.AbsoluteUri != "https://auth.openai.com/oauth/token")
            {
                return Task.FromResult(JsonResponse("""{"output_text":"OK"}"""));
            }

            capturedTokenRequest = request;
            return Task.FromResult(JsonResponse(
                """{"access_token":"new-access-token","expires_in":3600}"""));

        });

        var host = new TestPluginHostServices();
        host.SetSetting("authMode", "chatgpt");
        host.SetSetting("oauthExpiresAt", DateTimeOffset.UtcNow.AddMinutes(-5));
        host.Secrets["oauth-access-token"] = "old-access-token";
        host.Secrets["oauth-refresh-token"] = "original-refresh-token";

        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        await sut.ProcessAsync("system", "user", "gpt-5.5", CancellationToken.None);

        Assert.NotNull(capturedTokenRequest);
        Assert.Equal("original-refresh-token", host.Secrets["oauth-refresh-token"]);
        Assert.Equal("new-access-token", host.Secrets["oauth-access-token"]);
    }

    [Fact]
    public async Task ConcurrentChatGptRequests_RefreshOnceAndUseOneCoherentCredentialSnapshot()
    {
        const long expiresAtUnixSeconds = 4_102_444_800;
        var firstAccessToken = CreateJwt("""
            {
              "exp": 4102444800
            }
            """);
        var firstIdToken = CreateJwt("""
            {
              "chatgpt_account_id": "acct_single_refresh",
              "chatgpt_plan_type": "pro"
            }
            """);
        var secondAccessToken = CreateJwt("""
            {
              "exp": 4102444800,
              "jti": "duplicate"
            }
            """);
        var secondIdToken = CreateJwt("""
            {
              "chatgpt_account_id": "acct_duplicate_refresh",
              "chatgpt_plan_type": "free"
            }
            """);
        var firstRefreshResponse = JsonSerializer.Serialize(new
        {
            access_token = firstAccessToken,
            refresh_token = "rotated-refresh-token",
            id_token = firstIdToken,
            expires_in = 3600,
        });
        var duplicateRefreshResponse = JsonSerializer.Serialize(new
        {
            access_token = secondAccessToken,
            refresh_token = "duplicate-rotated-refresh-token",
            id_token = secondIdToken,
            expires_in = 3600,
        });
        var firstRefreshStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRefresh = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var downstreamAccessTokens = new List<string?>();
        var tokenPostCount = 0;
        var handler = new CapturingHandler(async (request, _) =>
        {
            if (request.RequestUri?.AbsoluteUri == "https://auth.openai.com/oauth/token")
            {
                // ReSharper disable once AccessToModifiedClosure -- intentional shared counter across handler invocations; Interlocked.Increment coordinates the concurrent-refresh dedup this test asserts.
                var refreshNumber = Interlocked.Increment(ref tokenPostCount);
                // ReSharper disable once InvertIf -- the positive form states the first-refresh case this test coordinates.
                if (refreshNumber == 1)
                {
                    firstRefreshStarted.TrySetResult(true);
                    await releaseFirstRefresh.Task;
                    return JsonResponse(firstRefreshResponse);
                }

                return JsonResponse(duplicateRefreshResponse);
            }

            lock (downstreamAccessTokens)
            {
                downstreamAccessTokens.Add(request.Headers.Authorization?.Parameter);
            }

            return JsonResponse("""{"output_text":"OK"}""");
        });
        var host = new TestPluginHostServices();
        host.SetSetting("authMode", "chatgpt");
        host.SetSetting("oauthExpiresAt", DateTimeOffset.UtcNow.AddMinutes(-5));
        host.Secrets["oauth-access-token"] = "expired-access-token";
        host.Secrets["oauth-refresh-token"] = "original-refresh-token";

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var firstRequest = sut.ProcessAsync(
            "system",
            "first",
            "gpt-5.5",
            timeoutCts.Token);
        await firstRefreshStarted.Task.WaitAsync(timeoutCts.Token);
        var secondRequest = sut.ProcessAsync(
            "system",
            "second",
            "gpt-5.5",
            timeoutCts.Token);

        releaseFirstRefresh.TrySetResult(true);
        await Task.WhenAll(firstRequest, secondRequest).WaitAsync(timeoutCts.Token);

        Assert.Equal(1, Volatile.Read(ref tokenPostCount));
        lock (downstreamAccessTokens)
        {
            Assert.Equal(2, downstreamAccessTokens.Count);
            Assert.All(
                downstreamAccessTokens,
                accessToken => Assert.Equal(firstAccessToken, accessToken));
        }
        Assert.Equal(firstAccessToken, host.Secrets["oauth-access-token"]);
        Assert.Equal("rotated-refresh-token", host.Secrets["oauth-refresh-token"]);
        Assert.Equal(firstIdToken, host.Secrets["oauth-id-token"]);
        Assert.Equal("acct_single_refresh", host.GetSetting<string>("oauthAccountID"));
        Assert.Equal("pro", host.GetSetting<string>("oauthPlanType"));
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(expiresAtUnixSeconds),
            host.GetSetting<DateTimeOffset?>("oauthExpiresAt"));
    }

    [Fact]
    public async Task ChatGptRefresh_FailureReleasesCredentialGateForWaitingRequest()
    {
        var firstRefreshStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRefresh = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenPostCount = 0;
        var handler = new CapturingHandler(async (request, _) =>
        {
            if (request.RequestUri?.AbsoluteUri != "https://auth.openai.com/oauth/token")
                return JsonResponse("""{"output_text":"OK"}""");

            // ReSharper disable once AccessToModifiedClosure -- intentional shared counter across handler invocations; Interlocked.Increment coordinates the concurrent-refresh dedup this test asserts.
            var refreshNumber = Interlocked.Increment(ref tokenPostCount);
            // ReSharper disable once InvertIf -- the positive form states the first-refresh case this test coordinates.
            if (refreshNumber == 1)
            {
                firstRefreshStarted.TrySetResult(true);
                await releaseFirstRefresh.Task;
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"error":"rejected refresh token"}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            return JsonResponse(
                """{"access_token":"recovered-access-token","refresh_token":"recovered-refresh-token","expires_in":3600}""");
        });
        var host = new TestPluginHostServices();
        host.SetSetting("authMode", "chatgpt");
        host.SetSetting("oauthExpiresAt", DateTimeOffset.UtcNow.AddMinutes(-5));
        host.Secrets["oauth-access-token"] = "expired-access-token";
        host.Secrets["oauth-refresh-token"] = "original-refresh-token";

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var failingRequest = sut.ProcessAsync(
            "system",
            "first",
            "gpt-5.5",
            timeoutCts.Token);
        await firstRefreshStarted.Task.WaitAsync(timeoutCts.Token);
        var waitingRequest = sut.ProcessAsync(
            "system",
            "second",
            "gpt-5.5",
            timeoutCts.Token);

        try
        {
            for (var i = 0; i < 10; i++)
                await Task.Yield();
            Assert.Equal(1, Volatile.Read(ref tokenPostCount));

            releaseFirstRefresh.TrySetResult(true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => failingRequest);
            Assert.Equal("OK", await waitingRequest.WaitAsync(timeoutCts.Token));
            Assert.Equal(2, Volatile.Read(ref tokenPostCount));
            Assert.Equal("recovered-access-token", host.Secrets["oauth-access-token"]);
            Assert.Equal("recovered-refresh-token", host.Secrets["oauth-refresh-token"]);
        }
        finally
        {
            releaseFirstRefresh.TrySetResult(true);
        }
    }

    [Fact]
    public async Task ChatGptRefresh_CancellationReleasesCredentialGateForWaitingRequest()
    {
        var firstRefreshStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenPostCount = 0;
        var handler = new CapturingHandler(async (request, _, cancellationToken) =>
        {
            if (request.RequestUri?.AbsoluteUri != "https://auth.openai.com/oauth/token")
                return JsonResponse("""{"output_text":"OK"}""");

            // ReSharper disable once AccessToModifiedClosure -- intentional shared counter across handler invocations; Interlocked.Increment coordinates the concurrent-refresh dedup this test asserts.
            var refreshNumber = Interlocked.Increment(ref tokenPostCount);
            // ReSharper disable once InvertIf -- the positive form states the first-refresh case this test coordinates.
            if (refreshNumber == 1)
            {
                firstRefreshStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return JsonResponse(
                """{"access_token":"recovered-access-token","refresh_token":"recovered-refresh-token","expires_in":3600}""");
        });
        var host = new TestPluginHostServices();
        host.SetSetting("authMode", "chatgpt");
        host.SetSetting("oauthExpiresAt", DateTimeOffset.UtcNow.AddMinutes(-5));
        host.Secrets["oauth-access-token"] = "expired-access-token";
        host.Secrets["oauth-refresh-token"] = "original-refresh-token";

        using var firstRequestCts = new CancellationTokenSource();
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var canceledRequest = sut.ProcessAsync(
            "system",
            "first",
            "gpt-5.5",
            firstRequestCts.Token);
        await firstRefreshStarted.Task.WaitAsync(timeoutCts.Token);
        var waitingRequest = sut.ProcessAsync(
            "system",
            "second",
            "gpt-5.5",
            timeoutCts.Token);

        try
        {
            for (var i = 0; i < 10; i++)
                await Task.Yield();
            Assert.Equal(1, Volatile.Read(ref tokenPostCount));

            // ReSharper disable once MethodHasAsyncOverload -- synchronous Cancel must trip the token before the assertion; CancelAsync would defer it.
            firstRequestCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRequest);
            Assert.Equal("OK", await waitingRequest.WaitAsync(timeoutCts.Token));
            Assert.Equal(2, Volatile.Read(ref tokenPostCount));
            Assert.Equal("recovered-access-token", host.Secrets["oauth-access-token"]);
            Assert.Equal("recovered-refresh-token", host.Secrets["oauth-refresh-token"]);
        }
        finally
        {
            // ReSharper disable once MethodHasAsyncOverload -- Cancel() is fine in this teardown path; CancelAsync() only defers callbacks, with no benefit here.
            firstRequestCts.Cancel();
        }
    }

    [Fact]
    public async Task ImportExistingLogin_LoadsTokensFromCodexAuthFile()
    {
        var tempDir = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var authPath = Path.Join(tempDir, "auth.json");
        await File.WriteAllTextAsync(authPath, """
        {
          "tokens": {
            "access_token": "access-token",
            "refresh_token": "refresh-token",
            "id_token": null,
            "account_id": "acct_from_file"
          }
        }
        """);
        var host = new TestPluginHostServices();
        var sut = new OpenAiPlugin(new HttpClient(new CapturingHandler((_, _) => Task.FromResult(JsonResponse("{}")))));
        await sut.ActivateAsync(host);

        try
        {
            await sut.ImportExistingLoginAsync(authPath);

            Assert.Equal("access-token", host.Secrets["oauth-access-token"]);
            Assert.Equal("refresh-token", host.Secrets["oauth-refresh-token"]);
            Assert.Equal("acct_from_file", host.GetSetting<string>("oauthAccountID"));
            Assert.True(sut.HasChatGptCredentials);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetSettingDefinitions_ExposesAuthModeKeyModelsVoiceAndForgetToggle()
    {
        var host = new TestPluginHostServices();
        var sut = new OpenAiPlugin();
        await sut.ActivateAsync(host);

        var keys = sut.GetSettingDefinitions().Select(d => d.Key).ToArray();

        Assert.Equal(
            [
                "authMode",
                "api-key",
                "selectedModel",
                "selectedLLMModel",
                "reasoningEffort",
                "llmTemperatureMode",
                "llmTemperatureValue",
                "streamResponses",
                "selectedVoice",
                "ttsInstructions",
                "forgetChatGptLogin",
            ],
            keys);
    }

    [Fact]
    public async Task SetSettingValueAsync_RoundTripsAuthModeModelVoiceAndInstructions()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-test" } };
        var sut = new OpenAiPlugin();
        await sut.ActivateAsync(host);

        await sut.SetSettingValueAsync("authMode", "chatgpt");
        await sut.SetSettingValueAsync("selectedModel", "gpt-4o-transcribe");
        await sut.SetSettingValueAsync("reasoningEffort", "high");
        await sut.SetSettingValueAsync("selectedVoice", "nova");
        await sut.SetSettingValueAsync("ttsInstructions", "Speak calmly.");
        await sut.SetSettingValueAsync("forgetChatGptLogin", "true");

        Assert.Equal("chatgpt", await sut.GetSettingValueAsync("authMode"));
        Assert.Equal(OpenAiAuthMode.ChatGpt, sut.AuthMode);
        Assert.Equal("gpt-4o-transcribe", await sut.GetSettingValueAsync("selectedModel"));
        Assert.Equal("high", await sut.GetSettingValueAsync("reasoningEffort"));
        Assert.Equal("nova", await sut.GetSettingValueAsync("selectedVoice"));
        Assert.Equal("nova", sut.SelectedVoiceId);
        Assert.Equal("Speak calmly.", await sut.GetSettingValueAsync("ttsInstructions"));
        Assert.Equal("true", await sut.GetSettingValueAsync("forgetChatGptLogin"));
    }

    [Fact]
    public async Task ValidateAsync_ApiKeyMode_FetchesAndCachesModelCatalog()
    {
        var handler = new CapturingHandler((request, _) =>
        {
            Assert.Equal("https://api.openai.com/v1/models", request.RequestUri?.ToString());
            return Task.FromResult(JsonResponse("""
            {
              "data": [
                { "id": "gpt-4.1-mini", "owned_by": "openai" },
                { "id": "o4-mini", "owned_by": "openai" },
                { "id": "whisper-1", "owned_by": "openai" }
              ]
            }
            """));
        });

        using var httpClient = new HttpClient(handler);
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-live" } };
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var result = await sut.ValidateAsync();

        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.Contains("2", result.Message);
        Assert.Equal(["gpt-4.1-mini", "o4-mini"], sut.SupportedModels.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task ValidateAsync_ChatGptMode_ReportsConnectedPlanWhenCredentialsPresent()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("authMode", "chatgpt");
        host.Secrets["oauth-access-token"] = "access-token";
        host.Secrets["oauth-refresh-token"] = "refresh-token";
        host.SetSetting("oauthPlanType", "plus");
        host.SetSetting("oauthExpiresAt", DateTimeOffset.UtcNow.AddHours(1));

        var sut = new OpenAiPlugin(new HttpClient(new CapturingHandler((_, _) => Task.FromResult(JsonResponse("{}")))));
        await sut.ActivateAsync(host);

        var result = await sut.ValidateAsync();

        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.Contains("plus", result.Message);
    }

    [Fact]
    public async Task ValidateAsync_ChatGptMode_ForgetToggleClearsStoredLogin()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("authMode", "chatgpt");
        host.Secrets["oauth-access-token"] = "access-token";
        host.Secrets["oauth-refresh-token"] = "refresh-token";
        host.SetSetting("oauthExpiresAt", DateTimeOffset.UtcNow.AddHours(1));

        var sut = new OpenAiPlugin(new HttpClient(new CapturingHandler((_, _) => Task.FromResult(JsonResponse("{}")))));
        await sut.ActivateAsync(host);
        await sut.SetSettingValueAsync("forgetChatGptLogin", "true");

        var result = await sut.ValidateAsync();

        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.Contains("removed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(sut.HasChatGptCredentials);
        Assert.False(host.Secrets.ContainsKey("oauth-access-token"));
        Assert.Equal("false", await sut.GetSettingValueAsync("forgetChatGptLogin"));
    }

    [Fact]
    public async Task SpeakAsync_PostsAudioSpeechRequestAndUsesPlaybackFactory()
    {
        byte[]? playbackBytes = null;
        var handler = new CapturingHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.openai.com/v1/audio/speech", request.RequestUri?.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("sk-live", request.Headers.Authorization?.Parameter);

            using var doc = JsonDocument.Parse(body ?? throw new InvalidOperationException("Missing body"));
            var root = doc.RootElement;
            Assert.Equal("gpt-4o-mini-tts", root.GetProperty("model").GetString());
            Assert.Equal("nova", root.GetProperty("voice").GetString());
            Assert.Equal("Read this", root.GetProperty("input").GetString());
            Assert.Equal("pcm", root.GetProperty("response_format").GetString());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0, 1, 2, 3]),
            });
        });

        using var httpClient = new HttpClient(handler);
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-live" } };
        var sut = new OpenAiPlugin(
            httpClient,
            pcm =>
            {
                playbackBytes = pcm;
                return new FakeTtsPlaybackSession();
            },
            ttsPlaybackAvailableProbe: () => true);
        await sut.ActivateAsync(host);
        sut.SelectVoice("nova");

        var session = await sut.SpeakAsync(new TtsSpeakRequest("Read this", "en"), CancellationToken.None);

        Assert.NotNull(session);
        Assert.Equal([0, 1, 2, 3], playbackBytes);
    }

    [Fact]
    public async Task SpeakAsync_SkipsNetworkRequestWhenNoPlayerAvailable()
    {
        var requestCount = 0;
        var handler = new CapturingHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0, 1, 2, 3]),
            });
        });

        using var httpClient = new HttpClient(handler);
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-live" } };
        var sut = new OpenAiPlugin(httpClient, ttsPlaybackAvailableProbe: () => false);
        await sut.ActivateAsync(host);

        var session = await sut.SpeakAsync(new TtsSpeakRequest("Read this", "en"), CancellationToken.None);

        Assert.False(session.IsActive);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task ProcessAsync_UsesCustomTemperatureForApiKeyChatCompletions()
    {
        // GPT-4o routes through /v1/chat/completions (not the Responses API),
        // so the temperature/max-tokens dictionary built in OpenAiChatHelper is
        // observable in the outgoing body. Setting llmTemperatureMode=custom
        // pins the user's chosen sampling temperature into that body.
        string? capturedBody = null;
        var handler = new CapturingHandler((_, body) =>
        {
            capturedBody = body;
            return Task.FromResult(JsonResponse(
                """{"choices":[{"message":{"content":"OK"}}]}"""));
        });

        using var httpClient = new HttpClient(handler);
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-test" } };
        host.SetSetting("llmTemperatureMode", "custom");
        host.SetSetting("llmTemperatureValue", 0.7);
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        await sut.ProcessAsync("system", "user", "gpt-4o", CancellationToken.None);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal(0.7, doc.RootElement.GetProperty("temperature").GetDouble(), 5);
        Assert.Equal(2048, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task ProcessAsync_AppliesReasoningEffortToOSeriesChatCompletions()
    {
        // o-series models route to /v1/responses (UsesResponsesApi covers o4),
        // and the Responses body must include reasoning.effort so the model
        // actually exercises its reasoning channel. Regression test for the
        // o-series path B4 introduced.
        string? capturedBody = null;
        HttpRequestMessage? capturedRequest = null;
        var handler = new CapturingHandler((request, body) =>
        {
            capturedRequest = request;
            capturedBody = body;
            return Task.FromResult(JsonResponse("""{"output_text":"OK"}"""));
        });

        using var httpClient = new HttpClient(handler);
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-test" } };
        host.SetSetting("reasoningEffort", "high");
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        await sut.ProcessAsync("system", "user", "o4-mini", CancellationToken.None);

        Assert.Equal("https://api.openai.com/v1/responses", capturedRequest?.RequestUri?.ToString());
        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("high", doc.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task ResolvedTemperature_ReturnsNullForGPT5WithReasoning()
    {
        // Pins the rule: GPT-5 with reasoning_effort set rejects the
        // temperature field, so ResolvedTemperature must return null even
        // when the user explicitly chose Custom mode.
        var host = new TestPluginHostServices();
        host.SetSetting("reasoningEffort", "medium");
        host.SetSetting("llmTemperatureMode", "custom");
        host.SetSetting("llmTemperatureValue", 0.7);
        var sut = new OpenAiPlugin();
        await sut.ActivateAsync(host);

        Assert.Null(sut.ResolvedTemperature("gpt-5.5"));
        // Non-reasoning model in Custom mode honors the user's value.
        Assert.Equal(0.7, sut.ResolvedTemperature("gpt-4o"));
    }

    [Fact]
    public async Task SetSettingValueAsync_RejectsNonFiniteTemperatureValues()
    {
        // double.TryParse(NumberStyles.Float, …) accepts "NaN" / "Infinity" /
        // "-Infinity", and Math.Clamp(NaN, …) returns NaN. Persisting NaN
        // would throw inside System.Text.Json on the next chat-completion or
        // activate. The plugin must drop non-finite inputs and keep the
        // current value (which itself was normalized to a finite default).
        var host = new TestPluginHostServices();
        var sut = new OpenAiPlugin();
        await sut.ActivateAsync(host);

        await sut.SetSettingValueAsync("llmTemperatureValue", "NaN");
        Assert.Equal(0.3, sut.TemperatureValue);

        await sut.SetSettingValueAsync("llmTemperatureValue", "Infinity");
        Assert.Equal(0.3, sut.TemperatureValue);

        await sut.SetSettingValueAsync("llmTemperatureValue", "-Infinity");
        Assert.Equal(0.3, sut.TemperatureValue);

        // Sanity: a finite value still goes through and clamps to range.
        await sut.SetSettingValueAsync("llmTemperatureValue", "9.5");
        Assert.Equal(2.0, sut.TemperatureValue);
    }

    [Fact]
    public async Task RefreshAvailableLlmModels_ChatGptMode_ReturnsStaticCatalogWithoutHttp()
    {
        // ChatGPT-login mode has no /v1/models endpoint to query — the catalog
        // is the static ChatGptModels list. RefreshAvailableLlmModelsAsync must
        // short-circuit, otherwise it would call /v1/models with an OAuth
        // bearer token and 401.
        var requestCount = 0;
        var handler = new CapturingHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(JsonResponse("{}"));
        });

        using var httpClient = new HttpClient(handler);
        var host = new TestPluginHostServices();
        host.SetSetting("authMode", "chatgpt");
        host.Secrets["oauth-access-token"] = "access-token";
        host.Secrets["oauth-refresh-token"] = "refresh-token";
        host.SetSetting("oauthExpiresAt", DateTimeOffset.UtcNow.AddHours(1));
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var models = await sut.RefreshAvailableLlmModelsAsync(CancellationToken.None);

        Assert.Equal(0, requestCount);
        Assert.NotEmpty(models);
        Assert.Equal("gpt-5.5", models[0].Id);
    }

    // C5 Phase 7 — realtime streaming session
    // ----------------------------------------
    // Pure-function tests exercise the protocol payloads and collector.
    // Transport-backed tests below cover finalize ordering without network
    // access, plus the fork-specific model + auth-mode gating.

    [Fact]
    public void RealtimeUri_UsesGAEndpointWithoutBetaHeader()
    {
        var headers = OpenAiRealtimeStreamingSession.CreateRealtimeHeaders("sk-test");
        var uri = OpenAiRealtimeStreamingSession.BuildRealtimeUri();

        Assert.Equal("wss://api.openai.com/v1/realtime?intent=transcription", uri.AbsoluteUri);
        Assert.Equal("Bearer sk-test", headers["Authorization"]);
        Assert.DoesNotContain(
            headers.Keys,
            header => header.Equals("OpenAI-Beta", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RealtimeSessionUpdatePayload_BatchMode_DisablesTurnDetection()
    {
        // Batch (TranscribeWavAsync) sends an explicit input_audio_buffer.commit
        // at end. turn_detection must be null so the server doesn't auto-commit
        // on internal silences and return early before all audio is processed.
        var json = OpenAiRealtimeStreamingSession.CreateSessionUpdatePayload(
            "de", "TypeWhisper, OpenAI", useServerVad: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var session = root.GetProperty("session");
        var input = session.GetProperty("audio").GetProperty("input");
        var transcription = input.GetProperty("transcription");

        Assert.Equal("session.update", root.GetProperty("type").GetString());
        Assert.Equal("transcription", session.GetProperty("type").GetString());
        Assert.Equal("audio/pcm", input.GetProperty("format").GetProperty("type").GetString());
        Assert.Equal(24000, input.GetProperty("format").GetProperty("rate").GetInt32());
        Assert.Equal("gpt-realtime-whisper", transcription.GetProperty("model").GetString());
        Assert.Equal("de", transcription.GetProperty("language").GetString());
        // Caller-supplied prompt forwards to the server so realtime gets the
        // same guidance the batch whisper path uses — the HTTP transcription
        // API and dictation pipeline both merge prompt + dictionary terms
        // before calling TranscribeAsync.
        Assert.Equal("TypeWhisper, OpenAI", transcription.GetProperty("prompt").GetString());
        Assert.Equal(JsonValueKind.Null, input.GetProperty("turn_detection").ValueKind);
    }

    [Fact]
    public void RealtimeSessionUpdatePayload_NullPrompt_OmitsPromptField()
    {
        // When no prompt is supplied (e.g. live-streaming path with
        // prompt: null), the field is omitted entirely rather than sent
        // as null or empty — keeps the session.update minimal.
        var json = OpenAiRealtimeStreamingSession.CreateSessionUpdatePayload(
            "en", prompt: null, useServerVad: true);

        using var doc = JsonDocument.Parse(json);
        var transcription = doc.RootElement
            .GetProperty("session")
            .GetProperty("audio")
            .GetProperty("input")
            .GetProperty("transcription");

        Assert.False(transcription.TryGetProperty("prompt", out _));
    }

    [Fact]
    public void RealtimeSessionUpdatePayload_StreamingMode_EnablesServerVad()
    {
        // Live-streaming relies on server VAD to auto-commit per utterance —
        // without it the server buffers audio until our FinalizeAsync sends
        // commit, which happens only when the user stops dictating, so the
        // live coordinator would receive zero partials/finals during a
        // multi-second dictation.
        var json = OpenAiRealtimeStreamingSession.CreateSessionUpdatePayload(
            "en", prompt: null, useServerVad: true);

        using var doc = JsonDocument.Parse(json);
        var input = doc.RootElement.GetProperty("session").GetProperty("audio").GetProperty("input");
        var turnDetection = input.GetProperty("turn_detection");

        Assert.Equal(JsonValueKind.Object, turnDetection.ValueKind);
        Assert.Equal("server_vad", turnDetection.GetProperty("type").GetString());
    }

    [Fact]
    public void RealtimeAudioPayload_Resamples16kPcmTo24kPcm()
    {
        var oneSecond16KPcm = new byte[16_000 * sizeof(short)];

        var payload = OpenAiRealtimeStreamingSession.CreateAudioAppendPayload(oneSecond16KPcm);

        using var doc = JsonDocument.Parse(payload);
        var bytes = Convert.FromBase64String(doc.RootElement.GetProperty("audio").GetString()!);
        Assert.Equal("input_audio_buffer.append", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(24_000 * sizeof(short), bytes.Length);
    }

    [Fact]
    public void RealtimeTranscriptCollector_PublishesDeltaAndCompletedText()
    {
        var collector = new OpenAiRealtimeTranscriptCollector();

        var delta = collector.ApplyEvent(
            """{"type":"conversation.item.input_audio_transcription.delta","item_id":"item_1","delta":"Hello"}""",
            out var deltaEvent);
        var completed = collector.ApplyEvent(
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"item_1","transcript":"Hello world"}""",
            out var completedEvent);

        Assert.True(delta);
        Assert.Equal(new StreamingTranscriptEvent("Hello", false), deltaEvent);
        Assert.True(completed);
        Assert.Equal(new StreamingTranscriptEvent("Hello world", true), completedEvent);
        Assert.Equal("Hello world", collector.CurrentText);
    }

    [Fact]
    public void RealtimeTranscriptCollector_MultipleCompletedItems_EmitsPerSegmentFinals()
    {
        // The fork's StreamingTranscriptionCoordinator appends each IsFinal
        // event's text to _finalSegments separated by newlines. If this
        // collector emitted cumulative CurrentText on each completed item,
        // two segments "hello" then "world" would become "hello\nhello world"
        // in the host's final transcript. Per-segment emission keeps the
        // host's final output as "hello\nworld" while CurrentText still
        // exposes the cumulative "hello world" for TranscribeWavAsync.
        var collector = new OpenAiRealtimeTranscriptCollector();

        collector.ApplyEvent(
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"item_1","transcript":"hello"}""",
            out var firstFinal);
        collector.ApplyEvent(
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"item_2","transcript":"world"}""",
            out var secondFinal);

        Assert.Equal(new StreamingTranscriptEvent("hello", true), firstFinal);
        Assert.Equal(new StreamingTranscriptEvent("world", true), secondFinal);
        // CurrentText stays cumulative so batch TranscribeWavAsync returns
        // the joined transcript.
        Assert.Equal("hello world", collector.CurrentText);
    }

    [Fact]
    public void RealtimeTranscriptCollector_PartialDelta_IsPerItemNotCumulative()
    {
        // After one completed item, an interim delta on the NEXT item must
        // emit only that item's running text — not the prior completed item
        // concatenated with the new delta. Otherwise, the orchestrator's
        // StreamingTranscriptState would treat the next utterance as an
        // extension of the prior finalized one and corrupt the live UI.
        var collector = new OpenAiRealtimeTranscriptCollector();

        collector.ApplyEvent(
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"item_1","transcript":"hello"}""",
            out _);
        collector.ApplyEvent(
            """{"type":"conversation.item.input_audio_transcription.delta","item_id":"item_2","delta":"wo"}""",
            out var partial);
        collector.ApplyEvent(
            """{"type":"conversation.item.input_audio_transcription.delta","item_id":"item_2","delta":"rld"}""",
            out var partial2);

        Assert.Equal(new StreamingTranscriptEvent("wo", false), partial);
        Assert.Equal(new StreamingTranscriptEvent("world", false), partial2);
    }

    [Fact]
    public void RealtimeExtractPcm16Data_HandlesOddSizedChunkBeforeData()
    {
        // RIFF spec: odd-sized chunks are followed by a 1-byte pad so the
        // next chunk header lands on a word boundary. The verbatim upstream
        // ExtractPcm16Data didn't account for this, so a WAV with an odd
        // 'INFO'/'bext'/etc. chunk before 'data' would miss the data chunk
        // and fall back to wavAudio[44..] — sending header/metadata bytes
        // as PCM to the realtime endpoint.
        //
        // Construct a minimal RIFF: header (12) + fmt (8+16) + LIST chunk
        // with odd size 3 + pad (1) + data chunk with 4 PCM bytes.
        var fmtData = new byte[]
        {
            1, 0,      // format = PCM
            1, 0,      // channels = 1
            0x80, 0x3e, 0, 0,  // sample rate 16000
            0, 0x7d, 0, 0,     // byte rate
            2, 0,      // block align
            16, 0, // bits per sample
        };
        var listData = "INFO"u8.ToArray();  // 4 bytes ("INFO")
        var oddListPayload = new byte[] { 1, 2, 3 };  // odd size triggers pad
        var pcmPayload = new byte[] { 0x11, 0x22, 0x33, 0x44 };

        var wav = new List<byte>();
        wav.AddRange("RIFF"u8.ToArray());
        wav.AddRange(BitConverter.GetBytes(0));   // placeholder file size
        wav.AddRange("WAVE"u8.ToArray());
        wav.AddRange("fmt "u8.ToArray());
        wav.AddRange(BitConverter.GetBytes(fmtData.Length));
        wav.AddRange(fmtData);
        wav.AddRange("LIST"u8.ToArray());
        wav.AddRange(BitConverter.GetBytes(listData.Length + oddListPayload.Length));
        wav.AddRange(listData);
        wav.AddRange(oddListPayload);
        wav.Add(0x00);  // RIFF pad byte for odd chunk size
        wav.AddRange("data"u8.ToArray());
        wav.AddRange(BitConverter.GetBytes(pcmPayload.Length));
        wav.AddRange(pcmPayload);

        var pcm = OpenAiRealtimeStreamingSession.ExtractPcm16Data(wav.ToArray());

        Assert.Equal(pcmPayload, pcm);
    }

    [Fact]
    public void RealtimeExtractPcm16Data_NegativeChunkSize_DoesNotSpin()
    {
        // Malformed RIFF with a negative chunkSize would, without
        // bounds validation, drive the offset-advance loop to either
        // stand still or go backwards — hanging transcription. Verify
        // the parser breaks out and falls through to the header-skip
        // fallback in bounded time.
        var wav = new List<byte>();
        wav.AddRange("RIFF"u8.ToArray());
        wav.AddRange(BitConverter.GetBytes(0));
        wav.AddRange("WAVE"u8.ToArray());
        // First chunk header with chunkSize = -8 (the value that would
        // pin offset at the same byte under the previous formula).
        wav.AddRange("LIST"u8.ToArray());
        wav.AddRange(BitConverter.GetBytes(-8));
        // Padding so the file is > 44 bytes (the early-return guard).
        wav.AddRange(new byte[40]);

        var pcm = OpenAiRealtimeStreamingSession.ExtractPcm16Data(wav.ToArray());

        // Header-skip fallback returns wavAudio[44..]; the test passes
        // by virtue of completing instead of spinning.
        Assert.NotNull(pcm);
    }

    [Fact]
    public void RealtimeExtractPcm16Data_OversizedChunkSize_DoesNotThrow()
    {
        // chunkSize = int.MaxValue overflows `dataStart + chunkSize`
        // to a large negative, sneaking past the `<=` bounds check
        // and crashing wavAudio[dataStart..(dataStart + chunkSize)]
        // with ArgumentOutOfRangeException. Fixed by clamping against
        // the buffer remainder before indexing.
        var wav = new List<byte>();
        wav.AddRange("RIFF"u8.ToArray());
        wav.AddRange(BitConverter.GetBytes(0));
        wav.AddRange("WAVE"u8.ToArray());
        wav.AddRange("data"u8.ToArray());
        wav.AddRange(BitConverter.GetBytes(int.MaxValue));
        wav.AddRange(new byte[40]);

        var pcm = OpenAiRealtimeStreamingSession.ExtractPcm16Data(wav.ToArray());

        // Header-skip fallback is fine; the contract is "doesn't throw".
        Assert.NotNull(pcm);
    }

    [Fact]
    public void RealtimeTranscriptCollector_EmptyCompletion_MarksHasCompletedTranscript()
    {
        // Silent audio: OpenAI emits a completed event with an empty
        // transcript. The collector must still record the completion so
        // TranscribeWavAsync's WaitForCompletedTranscriptAsync poll
        // observes HasCompletedTranscript and returns immediately —
        // otherwise batch transcription of silence hangs for the full
        // 10s timeout and then throws.
        var collector = new OpenAiRealtimeTranscriptCollector();

        var applied = collector.ApplyEvent(
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"item_1","transcript":""}""",
            out var evt);

        Assert.False(applied);
        Assert.Null(evt);
        Assert.True(collector.HasCompletedTranscript);
        Assert.Equal("", collector.CurrentText);
    }

    [Fact]
    public void RealtimeTranscriptCollector_ErrorEvent_CapturesErrorMessage()
    {
        // Provider error events set Error and return false — the receiveing
        // loop promotes Error to a captured fault that SendAudioAsync /
        // FinalizeAsync then throw, triggering batch fallback in the
        // orchestrator. Without this contract a server-side error after a
        // good final segment would silently ship a truncated transcript.
        var collector = new OpenAiRealtimeTranscriptCollector();

        var applied = collector.ApplyEvent(
            """{"type":"error","error":{"message":"invalid_audio_format"}}""",
            out var evt);

        Assert.False(applied);
        Assert.Null(evt);
        Assert.Equal("invalid_audio_format", collector.Error);
    }

    [Fact]
    public async Task RealtimeFinalize_AppendAfterEarlierCompletedItem_CommitsAndWaitsForTailItem()
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var socket = new FakeRealtimeWebSocket();
        await using var session =
            await OpenAiRealtimeStreamingSession.CreateConnectedSessionForTests(socket);

        await session.SendAudioAsync(new byte[] { 1, 0, 2, 0 }, timeoutCts.Token);

        // Synchronize through a later transcript delta: receive ordering
        // guarantees committed-A was applied before this callback fires.
        var firstDelta = WaitForTranscriptAsync(
            session,
            new StreamingTranscriptEvent("a", false),
            timeoutCts.Token);
        socket.QueueTextMessage(
            """{"type":"input_audio_buffer.committed","item_id":"item_a"}""");
        socket.QueueTextMessage(
            """{"type":"conversation.item.input_audio_transcription.delta","item_id":"item_a","delta":"a"}""");
        await firstDelta;

        await session.SendAudioAsync(new byte[] { 3, 0, 4, 0 }, timeoutCts.Token);

        var firstCompleted = WaitForTranscriptAsync(
            session,
            new StreamingTranscriptEvent("utterance A", true),
            timeoutCts.Token);
        socket.QueueTextMessage(
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"item_a","transcript":"utterance A"}""");
        await firstCompleted;

        var finalizeTask = session.FinalizeAsync(timeoutCts.Token);
        await socket.WaitForSentMessageTypeAsync(
            "input_audio_buffer.commit",
            timeoutCts.Token);

        Assert.False(finalizeTask.IsCompleted);
        Assert.Equal(1, socket.CountSentMessages("input_audio_buffer.commit"));

        // The explicit commit must bind finalize to item B. A committed
        // acknowledgement alone is not enough; its transcription result
        // is the terminal event finalize is waiting for.
        var secondDelta = WaitForTranscriptAsync(
            session,
            new StreamingTranscriptEvent("b", false),
            timeoutCts.Token);
        socket.QueueTextMessage(
            """{"type":"input_audio_buffer.committed","item_id":"item_b"}""");
        socket.QueueTextMessage(
            """{"type":"conversation.item.input_audio_transcription.delta","item_id":"item_b","delta":"b"}""");
        await secondDelta;

        Assert.False(finalizeTask.IsCompleted);

        socket.QueueTextMessage(
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"item_b","transcript":"utterance B"}""");
        await finalizeTask;
    }

    [Fact]
    public async Task RealtimeFinalize_AllAudioAlreadyServerCommitted_SendsNoCommitAndWaitsForTranscription()
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var socket = new FakeRealtimeWebSocket();
        await using var session =
            await OpenAiRealtimeStreamingSession.CreateConnectedSessionForTests(socket);

        await session.SendAudioAsync(new byte[] { 1, 0, 2, 0 }, timeoutCts.Token);

        var delta = WaitForTranscriptAsync(
            session,
            new StreamingTranscriptEvent("ready", false),
            timeoutCts.Token);
        socket.QueueTextMessage(
            """{"type":"input_audio_buffer.committed","item_id":"item_a"}""");
        socket.QueueTextMessage(
            """{"type":"conversation.item.input_audio_transcription.delta","item_id":"item_a","delta":"ready"}""");
        await delta;

        var finalizeTask = session.FinalizeAsync(timeoutCts.Token);

        Assert.Equal(0, socket.CountSentMessages("input_audio_buffer.commit"));
        Assert.False(finalizeTask.IsCompleted);

        socket.QueueTextMessage(
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"item_a","transcript":"ready"}""");
        await finalizeTask;

        Assert.Equal(0, socket.CountSentMessages("input_audio_buffer.commit"));
    }

    [Fact]
    public async Task RealtimeFinalize_ManualCommitMode_SendsOneCommitAndWaitsForTranscription()
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var socket = new FakeRealtimeWebSocket();
        await using var session =
            await OpenAiRealtimeStreamingSession.CreateConnectedSessionForTests(socket);

        // No server-VAD commit arrives in manual mode. Finalize retains the
        // existing batch behavior of sending exactly one explicit commit.
        await session.SendAudioAsync(new byte[] { 1, 0, 2, 0 }, timeoutCts.Token);

        var finalizeTask = session.FinalizeAsync(timeoutCts.Token);
        await socket.WaitForSentMessageTypeAsync(
            "input_audio_buffer.commit",
            timeoutCts.Token);

        Assert.Equal(1, socket.CountSentMessages("input_audio_buffer.commit"));
        Assert.False(finalizeTask.IsCompleted);

        var delta = WaitForTranscriptAsync(
            session,
            new StreamingTranscriptEvent("batch", false),
            timeoutCts.Token);
        socket.QueueTextMessage(
            """{"type":"input_audio_buffer.committed","item_id":"item_batch"}""");
        socket.QueueTextMessage(
            """{"type":"conversation.item.input_audio_transcription.delta","item_id":"item_batch","delta":"batch"}""");
        await delta;

        Assert.False(finalizeTask.IsCompleted);

        socket.QueueTextMessage(
            """{"type":"conversation.item.input_audio_transcription.completed","item_id":"item_batch","transcript":"batch"}""");
        await finalizeTask;

        Assert.Equal(1, socket.CountSentMessages("input_audio_buffer.commit"));
    }

    [Fact]
    public async Task RealtimeFinalize_CancellationWhileWaitingForCommitAcknowledgement_Throws()
    {
        using var testTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var finalizeCts = new CancellationTokenSource();
        var socket = new FakeRealtimeWebSocket();
        await using var session =
            await OpenAiRealtimeStreamingSession.CreateConnectedSessionForTests(socket);

        await session.SendAudioAsync(new byte[] { 1, 0, 2, 0 }, testTimeoutCts.Token);

        var finalizeTask = session.FinalizeAsync(finalizeCts.Token);
        await socket.WaitForSentMessageTypeAsync(
            "input_audio_buffer.commit",
            testTimeoutCts.Token);
        Assert.False(finalizeTask.IsCompleted);

        // ReSharper disable once MethodHasAsyncOverload -- synchronous Cancel must trip the token before the assertion; CancelAsync would defer it.
        finalizeCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await finalizeTask);
    }

    [Fact]
    public async Task SupportsStreaming_RequiresRealtimeModelAndApiKeyMode()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-test" } };
        var sut = new OpenAiPlugin();
        await sut.ActivateAsync(host);

        // Default model is whisper-1 — non-streaming.
        Assert.Equal("whisper-1", sut.SelectedModelId);
        Assert.False(sut.SupportsStreaming);

        sut.SelectModel("gpt-realtime-whisper");
        Assert.True(sut.SupportsStreaming);

        // ChatGPT-OAuth mode can't authenticate the realtime endpoint —
        // streaming must report unavailable even with the realtime model
        // picked, so the orchestrator falls through to polling rather
        // than surfacing a 401 at connect time.
        await sut.SetSettingValueAsync("authMode", "chatgpt");
        Assert.False(sut.SupportsStreaming);
    }

    [Fact]
    public async Task StartStreamingAsync_ThrowsWhenModelOrAuthModeIsWrong()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-test" } };
        var sut = new OpenAiPlugin();
        await sut.ActivateAsync(host);

        // Non-realtime model selected → NotSupportedException with the
        // actionable "select GPT Realtime Whisper" message.
        var modelEx = await Assert.ThrowsAsync<NotSupportedException>(
            () => sut.StartStreamingAsync("en", CancellationToken.None));
        Assert.Contains("GPT Realtime Whisper", modelEx.Message);

        // ChatGPT-OAuth mode → InvalidOperationException with the API-key
        // requirement, even if the realtime model is selected.
        sut.SelectModel("gpt-realtime-whisper");
        await sut.SetSettingValueAsync("authMode", "chatgpt");
        var authEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartStreamingAsync("en", CancellationToken.None));
        Assert.Contains("API key", authEx.Message);
    }

    private static async Task WaitForTranscriptAsync(
        OpenAiRealtimeStreamingSession session,
        StreamingTranscriptEvent expected,
        CancellationToken ct)
    {
        var received = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        session.TranscriptReceived += OnTranscript;
        try
        {
            await received.Task.WaitAsync(ct);
        }
        finally
        {
            session.TranscriptReceived -= OnTranscript;
        }

        return;

        void OnTranscript(StreamingTranscriptEvent transcriptEvent)
        {
            if (transcriptEvent == expected)
                received.TrySetResult(true);
        }
    }

    private static JsonElement LoadManifest()
    {
        var basePath = Path.GetFullPath(AppContext.BaseDirectory);
        var relativeManifestPath = Path.Join(
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.OpenAi", "manifest.json");
        var manifestPath = Path.GetFullPath(relativeManifestPath, basePath);
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, string> ParseQuery(Uri uri)
    {
        var query = uri.Query.TrimStart('?');
        return query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace("+", " ")) : "");
    }

    [Fact]
    public async Task ProcessStreamingAsync_ChatCompletionsModel_StreamsDeltas()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\" world\"}}]}",
            "",
            "data: [DONE]",
            "");
        var handler = new CapturingHandler((request, body) =>
        {
            capturedRequest = request;
            capturedBody = body;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            });
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-test" } };
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (var chunk in sut.ProcessStreamingAsync("sys", "user", "gpt-4o", CancellationToken.None))
            chunks.Add(chunk);

        Assert.Equal(["Hello", " world"], chunks);
        Assert.Equal("https://api.openai.com/v1/chat/completions", capturedRequest?.RequestUri?.ToString());
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task ProcessStreamingAsync_ToggleOff_YieldsSingleBulkChunk()
    {
        var handler = new CapturingHandler((_, _) => Task.FromResult(JsonResponse(
            """{"choices":[{"message":{"content":"bulk result"}}]}""")));

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-test" } };
        host.SetSetting("streamResponses", false);
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiPlugin(httpClient, _ => new FakeTtsPlaybackSession());
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (var chunk in sut.ProcessStreamingAsync("sys", "user", "gpt-4o", CancellationToken.None))
            chunks.Add(chunk);

        Assert.Single(chunks);
        Assert.Equal("bulk result", chunks[0]);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static string CreateJwt(string payload)
    {
        var encodedPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"e30.{encodedPayload}.signature";
    }

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, string?, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        public CapturingHandler(
            Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> responder)
            : this((request, body, _) => responder(request, body))
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return await responder(request, body, cancellationToken);
        }
    }

    private sealed class FakeRealtimeWebSocket : WebSocket
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private readonly List<string> _sentMessages = [];
        private readonly SemaphoreSlim _sentSignal = new(0);
        private readonly Lock _sentLock = new();
        private int _state = (int)WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeStatusDescription;

        // ReSharper disable once ConvertToAutoPropertyWithPrivateSetter -- WebSocket declares these get-only, so an override cannot add a private setter.
        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        // ReSharper disable once ConvertToAutoPropertyWithPrivateSetter -- WebSocket declares these get-only, so an override cannot add a private setter.
        public override string? CloseStatusDescription => _closeStatusDescription;
        public override WebSocketState State => (WebSocketState)Volatile.Read(ref _state);
        public override string? SubProtocol => null;

        public void QueueTextMessage(string json)
        {
            if (!_incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(json)))
                throw new InvalidOperationException("The fake WebSocket receive queue is closed.");
        }

        public int CountSentMessages(string messageType)
        {
            lock (_sentLock)
            {
                return _sentMessages.Count(message => GetMessageType(message) == messageType);
            }
        }

        public async Task WaitForSentMessageTypeAsync(
            string messageType,
            CancellationToken ct)
        {
            while (true)
            {
                lock (_sentLock)
                {
                    if (_sentMessages.Any(message => GetMessageType(message) == messageType))
                        return;
                }

                await _sentSignal.WaitAsync(ct);
            }
        }

        public override void Abort()
        {
            Interlocked.Exchange(ref _state, (int)WebSocketState.Aborted);
            _incoming.Writer.TryComplete();
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            Interlocked.Exchange(ref _state, (int)WebSocketState.Closed);
            _incoming.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) =>
            CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose()
        {
            Interlocked.Exchange(ref _state, (int)WebSocketState.Closed);
            _incoming.Writer.TryComplete();
            _sentSignal.Dispose();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            var message = await _incoming.Reader.ReadAsync(cancellationToken);
            if (message.Length > buffer.Count)
                throw new InvalidOperationException("The fake WebSocket receive buffer is too small.");

            message.CopyTo(buffer.Array!.AsSpan(buffer.Offset, buffer.Count));
            return new WebSocketReceiveResult(
                message.Length,
                WebSocketMessageType.Text,
                endOfMessage: true);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (messageType != WebSocketMessageType.Text || !endOfMessage)
                throw new InvalidOperationException("The fake WebSocket only accepts complete text messages.");

            var message = Encoding.UTF8.GetString(
                buffer.Array!,
                buffer.Offset,
                buffer.Count);
            lock (_sentLock)
            {
                _sentMessages.Add(message);
            }

            _sentSignal.Release();
            return Task.CompletedTask;
        }

        private static string? GetMessageType(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("type").GetString();
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

    // Resolve from the plugin's real en.json (source tree) so validation
    // messages come back in English with format args applied — mirroring the
    // host's PluginLocalization in production, instead of echoing raw keys.
    private sealed class TestPluginLocalization : IPluginLocalization
    {
        private static readonly PluginLocalization s_en = new(
            Path.GetFullPath(
                Path.Join("..", "..", "..", "..", "..", "plugins", "TypeWhisper.Plugin.OpenAi"),
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

    private sealed class FakeTtsPlaybackSession : ITtsPlaybackSession
    {
        public bool IsActive => false;

        public event EventHandler? Completed
        {
            add { value?.Invoke(this, EventArgs.Empty); }
            remove { }
        }

        public void Stop() { }
    }
}
