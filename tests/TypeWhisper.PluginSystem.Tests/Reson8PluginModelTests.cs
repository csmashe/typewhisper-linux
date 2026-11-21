using System.Collections.Specialized;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using TypeWhisper.Plugin.Reson8;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

// [0x00, 0x00] is a PCM silence sample, not text; see Reson8PluginTests.cs.
// ReSharper disable UseUtf8StringLiteral

namespace TypeWhisper.PluginSystem.Tests;

public partial class Reson8PluginTests
{
    private const string ModelListJson = """
        [
          {"id":"resonant-1","displayName":"Resonant-1","endpoints":["prerecorded","realtime","turns"],"languages":["de","en","es","fr","it","nl","pl","pt","sv"],"isDefault":true},
          {"id":"rumble-2-flash","displayName":"Rumble-2-Flash","endpoints":["prerecorded"],"languages":["de","en","ja","nb","zh"],"isDefault":false},
          {"id":"../bad","displayName":"Bad"},
          {"id":"no-name"}
        ]
        """;

    private static readonly Reson8Model s_resonant =
        new("resonant-1", "Resonant-1", ["prerecorded", "realtime"], ["de", "en", "nl"], IsDefault: true);

    private static readonly Reson8Model s_rumble =
        new("rumble-2-flash", "Rumble-2-Flash", ["prerecorded"], ["de", "en", "ja"], IsDefault: false);

    [Fact]
    public void ParseModelListResponse_KeepsValidModelsWithTheirLanguagesAndEndpoints()
    {
        var models = Reson8Models.ParseModelListResponse(Encoding.UTF8.GetBytes(ModelListJson))!;

        Assert.Equal(["resonant-1", "rumble-2-flash"], models.Select(m => m.Id).ToArray());
        Assert.True(models[0].IsDefault);
        Assert.True(models[0].Streams);
        Assert.False(models[1].Streams);
        Assert.Equal(["de", "en", "ja", "nb", "zh"], models[1].Languages);
        Assert.Null(Reson8Models.ParseModelListResponse("""{ "models": [] }"""u8.ToArray()));
        Assert.Null(Reson8Models.ParseModelListResponse("[]"u8.ToArray()));
        Assert.Null(Reson8Models.ParseModelListResponse("not json"u8.ToArray()));
    }

    [Fact]
    public async Task ValidateAsync_LoadsReson8ModelsNextToCustomModels()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        using var httpClient = new HttpClient(ModelListHandler(ModelListJson));
        var sut = new Reson8Plugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ValidateAsync();

        Assert.True(result!.IsSuccess);
        Assert.Equal("Settings.ApiKeyValidWithModels", result.Message);
        Assert.Equal(
            [(Reson8Plugin.DefaultModelId, "Resonant-1"), ("rumble-2-flash", "Rumble-2-Flash"), ("domain-model", "Domain Model")],
            sut.TranscriptionModels.Select(m => (m.Id, m.DisplayName)).ToArray());
        Assert.Equal(5, sut.TranscriptionModels[1].LanguageCount);

        // Kept with the connection, so a restart still offers them.
        await sut.DeactivateAsync();
        await sut.ActivateAsync(host);
        Assert.Equal(2, sut.FetchedListedModels.Count);
    }

    [Fact]
    public async Task SelectingRumble_TurnsOffLiveTextAndFollowsItsLanguages()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        var sut = await ActivateWithListedModelsAsync(host, new HttpClient());
        Assert.True(sut.SupportsStreaming);
        var notifications = host.NotifyCapabilitiesChangedCount;

        sut.SelectModel("rumble-2-flash");

        Assert.False(sut.SupportsStreaming);
        Assert.Equal(["de", "en", "ja"], sut.SupportedLanguages);
        Assert.Equal(notifications + 1, host.NotifyCapabilitiesChangedCount);
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() => sut.StartStreamingAsync(null, CancellationToken.None));
        Assert.Equal(PluginRequestFailureKind.Configuration, ex.FailureKind);
        await Assert.ThrowsAsync<PluginRequestException>(() => sut.StartStreamingWithLanguageHintsAndPromptAsync(
            ["de"], PluginTranscriptionPrompt.Encode(null, ["TypeWhisper"]), CancellationToken.None));

        sut.SelectModel(Reson8Plugin.DefaultModelId);
        Assert.True(sut.SupportsStreaming);
        Assert.Equal(["de", "en", "nl"], sut.SupportedLanguages);
    }

    [Fact]
    public async Task Rumble_GoesAsModelAndLeavesOutTheParametersItRejects()
    {
        var queries = new List<NameValueCollection>();
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        using var httpClient = new HttpClient(new CapturingHandler((request, _) =>
        {
            queries.Add(HttpUtility.ParseQueryString(request.RequestUri!.Query));
            return JsonResponse("""{ "text": "hallo", "language": "" }""");
        }));
        var streamingConnects = 0;
        var sut = new Reson8Plugin(httpClient)
        {
            StreamingSessionFactory = (_, _) =>
            {
                streamingConnects++;
                return Task.FromResult<IStreamingSession>(new FakeStreamingSession());
            },
        };
        await sut.ActivateAsync(host);
        sut.SetFetchedListedModels([s_resonant, s_rumble]);
        sut.SelectModel("rumble-2-flash");
        await sut.SetSettingValueAsync("fillerMode", "clean");
        await sut.SetSettingValueAsync("biasStrength", "0.70");
        var prompt = PluginTranscriptionPrompt.Encode(null, ["TypeWhisper"]);

        await sut.TranscribeAsync(BuildPcm16Wav([0x00, 0x00]), "de", false, prompt, CancellationToken.None);
        // Recorded audio goes straight to the batch endpoint, as the model has no live endpoint.
        await sut.TranscribeStreamingAsync(BuildPcm16Wav([0x00, 0x00]), "de", false, prompt, _ => true, CancellationToken.None);

        Assert.Equal(0, streamingConnects);
        Assert.Equal(2, queries.Count);
        foreach (var query in queries)
        {
            Assert.Equal("rumble-2-flash", query["model"]);
            Assert.Null(query["custom_model_id"]);
            Assert.Null(query["phrases"]);
            Assert.Null(query["filler_mode"]);
            Assert.Null(query["bias_strength"]);
            Assert.Null(query["include_words"]);
            Assert.Null(query["include_timestamps"]);
        }
    }

    [Fact]
    public async Task DefaultModel_SendsFillerModeBiasWithPhrasesAndAsksForWords()
    {
        var queries = new List<NameValueCollection>();
        var sut = await CreateBatchPluginAsync(request => queries.Add(HttpUtility.ParseQueryString(request.RequestUri!.Query)));
        await sut.SetSettingValueAsync("fillerMode", "Verbatim");
        await sut.SetSettingValueAsync("biasStrength", "0.60");

        await sut.TranscribeAsync(
            BuildPcm16Wav([0x00, 0x00]), null, false, PluginTranscriptionPrompt.Encode(null, ["TypeWhisper"]), CancellationToken.None);
        await sut.TranscribeAsync(BuildPcm16Wav([0x00, 0x00]), null, false, null, CancellationToken.None);

        Assert.Equal("verbatim", queries[0]["filler_mode"]);
        Assert.Equal("TypeWhisper", queries[0]["phrases"]);
        Assert.Equal("0.60", queries[0]["bias_strength"]);
        Assert.Equal("true", queries[0]["include_words"]);
        Assert.Equal("true", queries[0]["include_timestamps"]);
        Assert.Null(queries[0]["model"]);
        // Without phrases or a custom model nothing is biased, so the strength stays out.
        Assert.Null(queries[1]["bias_strength"]);
        Assert.Equal("verbatim", queries[1]["filler_mode"]);
    }

    [Fact]
    public async Task CustomModel_TakesTheBiasStrengthButNoPhrases()
    {
        NameValueCollection? query = null;
        var sut = await CreateBatchPluginAsync(
            request => query = HttpUtility.ParseQueryString(request.RequestUri!.Query),
            selectedModel: "domain-model");
        await sut.SetSettingValueAsync("biasStrength", "0.20");

        await sut.TranscribeAsync(
            BuildPcm16Wav([0x00, 0x00]), null, false, PluginTranscriptionPrompt.Encode(null, ["TypeWhisper"]), CancellationToken.None);

        Assert.Equal("domain-model", query!["custom_model_id"]);
        Assert.Null(query["phrases"]);
        Assert.Equal("0.20", query["bias_strength"]);
        Assert.Null(query["filler_mode"]);
    }

    [Fact]
    public async Task LiveSession_CarriesTheSelectionsOptionsWithoutWords()
    {
        var requests = new List<Reson8StreamingRequest>();
        var sut = await CreateStreamingPluginAsync(
            (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult<IStreamingSession>(new FakeStreamingSession());
            },
            () => { });
        sut.SetFillerMode("clean");
        sut.SetBiasStrength("0.90");

        await using (await sut.StartStreamingWithLanguageHintsAndPromptAsync(
            ["de"], PluginTranscriptionPrompt.Encode(null, ["TypeWhisper"]), CancellationToken.None)) { }

        var request = Assert.Single(requests);
        var uri = Reson8StreamingSession.BuildRealtimeUri(
            "https://api.reson8.dev", request.ModelId, request.Language, request.Phrases, request.Options);
        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("clean", query["filler_mode"]);
        Assert.Equal("0.90", query["bias_strength"]);
        Assert.Equal("TypeWhisper", query["phrases"]);
        Assert.Equal("true", query["include_interim"]);
        Assert.Null(query["include_words"]);
    }

    [Fact]
    public void RealtimeUri_SendsAListedModelAsModel()
    {
        var uri = Reson8StreamingSession.BuildRealtimeUri(
            "https://api.reson8.dev", "resonant-2", null, [], new Reson8QueryOptions(Model: "resonant-2"));

        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("resonant-2", query["model"]);
        Assert.Null(query["custom_model_id"]);
    }

    [Theory]
    [InlineData(null, "default", null)]
    [InlineData("0.45", "default", null)]
    [InlineData("1.5", "default", null)]
    [InlineData("-0.1", "default", null)]
    [InlineData("NaN", "default", null)]
    [InlineData("soon", "default", null)]
    [InlineData("0.60", "0.60", 0.6)]
    [InlineData("0", "0.00", 0.0)]
    [InlineData("1", "1.00", 1.0)]
    [InlineData("0.453", "default", null)]
    public async Task BiasStrength_KeepsOnlyAStrengthOtherThanTheDefault(string? value, string shown, double? stored)
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        var sut = new Reson8Plugin(new HttpClient());
        await sut.ActivateAsync(host);

        await sut.SetSettingValueAsync("biasStrength", value);

        Assert.Equal(stored, sut.BiasStrength);
        Assert.Equal(shown, await sut.GetSettingValueAsync("biasStrength"));
        await sut.DeactivateAsync();
        await sut.ActivateAsync(host);
        Assert.Equal(stored, sut.BiasStrength);
    }

    [Fact]
    public async Task FillerModeAndBias_AreDropdownsThatSurviveARestart()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        var sut = new Reson8Plugin(new HttpClient());
        await sut.ActivateAsync(host);

        await sut.SetSettingValueAsync("fillerMode", "clean");
        await sut.SetSettingValueAsync("biasStrength", "0.25");
        await sut.DeactivateAsync();
        await sut.ActivateAsync(host);

        Assert.Equal("clean", await sut.GetSettingValueAsync("fillerMode"));
        Assert.Equal("0.25", await sut.GetSettingValueAsync("biasStrength"));
        var definitions = sut.GetSettingDefinitions();
        Assert.Equal(
            ["natural", "clean", "verbatim"],
            definitions.Single(d => d.Key == "fillerMode").Options!.Select(o => o.Value).ToArray());
        var biasOptions = definitions.Single(d => d.Key == "biasStrength").Options!.Select(o => o.Value).ToArray();
        Assert.Equal("default", biasOptions[0]);
        Assert.Equal(21, biasOptions.Length);
        Assert.Contains("0.25", biasOptions);
        Assert.DoesNotContain("0.45", biasOptions);

        await sut.SetSettingValueAsync("fillerMode", "shouting");
        Assert.Equal("natural", sut.FillerMode);
    }

    [Fact]
    public async Task ValidateAsync_ServerWithoutAModelListRunsTheDefaultModel()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        using var httpClient = new HttpClient(ModelListHandler("{}", HttpStatusCode.NotFound));
        var sut = new Reson8Plugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.ValidateAsync();

        Assert.True(result!.IsSuccess);
        Assert.Equal("Settings.ApiKeyValidWithModels", result.Message);
        Assert.Empty(sut.FetchedListedModels);
        Assert.Equal("Settings.DefaultModel", sut.TranscriptionModels[0].DisplayName);
    }

    [Fact]
    public async Task ValidateAsync_ModelListThatTurnsUpMissingKeepsTheCachedList()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        using var httpClient = new HttpClient(ModelListHandler("{}", HttpStatusCode.NotFound));
        var sut = await ActivateWithListedModelsAsync(host, httpClient);
        sut.SelectModel("rumble-2-flash");

        var result = await sut.ValidateAsync();

        Assert.Equal("Settings.ApiKeyValidWithModels", result!.Message);
        Assert.Equal(["resonant-1", "rumble-2-flash"], sut.FetchedListedModels.Select(m => m.Id).ToArray());
        Assert.Equal("rumble-2-flash", sut.SelectedModelId);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "[]")]
    [InlineData(HttpStatusCode.OK, "[]")]
    [InlineData(HttpStatusCode.OK, "invalid-json")]
    public async Task ValidateAsync_FailedModelListKeepsTheListedModelsAndRefreshesCustomModels(HttpStatusCode status, string json)
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        using var httpClient = new HttpClient(ModelListHandler(json, status));
        var sut = await ActivateWithListedModelsAsync(host, httpClient);
        sut.SelectModel("rumble-2-flash");

        var result = await sut.ValidateAsync();

        Assert.True(result!.IsSuccess);
        Assert.Equal("Settings.CustomModelsRefreshFailed", result.Message);
        Assert.Equal(["resonant-1", "rumble-2-flash"], sut.FetchedListedModels.Select(m => m.Id).ToArray());
        Assert.Equal("domain-model", Assert.Single(sut.FetchedCustomModels).Id);
        Assert.Equal("rumble-2-flash", sut.SelectedModelId);
    }

    [Fact]
    public async Task ValidateAsync_DropsASelectedModelReson8NoLongerLists()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        using var httpClient = new HttpClient(ModelListHandler(
            """[{"id":"resonant-1","displayName":"Resonant-1","isDefault":true}]"""));
        var sut = await ActivateWithListedModelsAsync(host, httpClient);
        sut.SelectModel("rumble-2-flash");

        await sut.ValidateAsync();

        Assert.Equal(Reson8Plugin.DefaultModelId, sut.SelectedModelId);
        Assert.Equal(Reson8Plugin.DefaultModelId, host.GetSetting<string>("selectedModel"));
    }

    [Fact]
    public async Task ListedModels_FollowTheConnectionTheyWereLoadedWith()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        var requests = 0;
        using var httpClient = new HttpClient(new CapturingHandler((_, _) =>
        {
            requests++;
            return JsonResponse("""{ "text": "ok" }""");
        }));
        var sut = await ActivateWithListedModelsAsync(host, httpClient);
        sut.SelectModel("rumble-2-flash");
        var notifications = host.NotifyCapabilitiesChangedCount;

        await sut.SetSettingValueAsync("customBaseURL", "https://proxy.example.test/reson8");

        Assert.Equal(notifications + 1, host.NotifyCapabilitiesChangedCount);
        Assert.Empty(sut.FetchedListedModels);
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.TranscribeAsync(BuildPcm16Wav([0x00, 0x00]), null, false, null, CancellationToken.None));
        Assert.Equal("Settings.SelectedModelUnavailable", ex.Message);
        Assert.Equal(0, requests);

        await sut.SetSettingValueAsync("customBaseURL", null);
        Assert.Equal(2, sut.FetchedListedModels.Count);
        await sut.TranscribeAsync(BuildPcm16Wav([0x00, 0x00]), null, false, null, CancellationToken.None);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task StoredCatalogFromAnEarlierVersionLoadsWithoutListedModels()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        var sut = await ActivateWithCatalogAsync(host, new HttpClient());
        var stored = JsonNode.Parse(host.GetSetting<string>("customModelCatalog")!)!.AsObject();
        Assert.True(stored.Remove("ListedModels"));
        host.SetSetting("customModelCatalog", stored.ToJsonString());

        await sut.DeactivateAsync();
        await sut.ActivateAsync(host);

        Assert.Equal("domain-model", Assert.Single(sut.FetchedCustomModels).Id);
        Assert.Empty(sut.FetchedListedModels);
    }

    [Fact]
    public void ParseTranscriptionResponse_TurnsWordsIntoSentenceSegments()
    {
        const string json = """
            {"text":"Die Rechnung beträgt 1234 Euro. Danke dir sehr.","language":"de","words":[
              {"text":"Die","start_ms":1200,"duration_ms":160},{"text":"Rechnung","start_ms":1360,"duration_ms":400},
              {"text":"beträgt","start_ms":1840,"duration_ms":400},{"text":"1234","start_ms":2800,"duration_ms":1040},
              {"text":"Euro.","start_ms":4240,"duration_ms":160},{"text":"Danke","start_ms":4600,"duration_ms":200},
              {"text":"dir","start_ms":4800,"duration_ms":100},{"text":"sehr.","start_ms":4900,"duration_ms":300},
              {"text":"bad","start_ms":-5,"duration_ms":10},{"start_ms":1,"duration_ms":1}]}
            """;

        var result = Reson8Plugin.ParseTranscriptionResponse(json, null, 32000);

        Assert.Equal(
            [
                new PluginTranscriptionSegment("Die Rechnung beträgt 1234 Euro.", 1.2, 4.4),
                new PluginTranscriptionSegment("Danke dir sehr.", 4.6, 5.2),
            ],
            result.Segments);
        Assert.Empty(Reson8Plugin.ParseTranscriptionResponse("""{ "text": "hi" }""", null, 0).Segments);
    }

    [Fact]
    public void SentenceSegments_SplitOnALongPauseAndKeepShortSentencesTogether()
    {
        var segments = Reson8Transcript.SentenceSegments(
        [
            new Reson8Word("Yes.", 0, 0.3),
            new Reson8Word("I", 0.4, 0.5),
            new Reson8Word("agree", 0.5, 0.9),
            new Reson8Word("later", 2.5, 2.9),
        ]);

        Assert.Equal(["Yes. I agree", "later"], segments.Select(s => s.Text).ToArray());
    }

    [Fact]
    public void SentenceSegments_KeepTheTranscriptsSpacingForLanguagesWithoutSpaces()
    {
        var segments = Reson8Transcript.SentenceSegments(
            [new Reson8Word("今日は", 0, 0.5), new Reson8Word("いい", 0.5, 0.8), new Reson8Word("天気です。", 0.8, 1.4)],
            "今日はいい天気です。");

        Assert.Equal("今日はいい天気です。", Assert.Single(segments).Text);
    }

    [Fact]
    public async Task LiveAudio_GoesOutInFramesOfAtMost8KiB()
    {
        var adapter = new Reson8WebSocketAdapter("", "https://api.reson8.dev", Reson8Plugin.DefaultAuthHeader, null, null);

        var frames = await adapter.EncodeAudioAsync(new byte[20_000], CancellationToken.None);

        Assert.Equal([8192, 8192, 3616], frames.Select(f => f.Payload.Length).ToArray());
        Assert.All(frames, f => Assert.Equal(WebSocketMessageType.Binary, f.MessageType));
        Assert.Empty(await adapter.EncodeAudioAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None));
    }

    [Theory]
    [InlineData(404, "Unknown vocabulary: dict-1", PluginRequestFailureKind.InvalidRequest, "Reson8 refused the live connection (404): Unknown vocabulary: dict-1")]
    [InlineData(401, null, PluginRequestFailureKind.Authentication, "Reson8 refused the live connection (401).")]
    [InlineData(402, "Credit limit reached", PluginRequestFailureKind.Permission, "Reson8 refused the live connection (402): Credit limit reached")]
    [InlineData(429, "Too many connections", PluginRequestFailureKind.RateLimit, "Reson8 refused the live connection (429): Too many connections")]
    [InlineData(503, "down\r\nfor maintenance", PluginRequestFailureKind.ServerError, "Reson8 refused the live connection (503): downfor maintenance")]
    public void RejectionOf_NamesReson8sReason(int status, string? reason, PluginRequestFailureKind kind, string message)
    {
        IReadOnlyDictionary<string, IEnumerable<string>> headers = reason is null
            ? new Dictionary<string, IEnumerable<string>>()
            : new Dictionary<string, IEnumerable<string>> { ["x-error-message"] = [reason] };

        var rejection = Reson8StreamingSession.RejectionOf(status, headers, new WebSocketException("refused"))!;

        Assert.Equal(kind, rejection.FailureKind);
        Assert.Equal(status, rejection.HttpStatusCode);
        Assert.Equal(message, rejection.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(302)]
    public void RejectionOf_LeavesOtherConnectionFailuresAlone(int status) =>
        Assert.Null(Reson8StreamingSession.RejectionOf(status, null, new WebSocketException("reset")));

    [Fact]
    public async Task LiveConnection_RefusedByTheServerNamesXErrorMessage()
    {
        using var listener = new HttpListener();
        var port = FreeTcpPort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serve = Task.Run(async () =>
        {
            // ReSharper disable once AccessToDisposedClosure -- serve is awaited below, inside the listener's using-scope.
            var context = await listener.GetContextAsync();
            context.Response.StatusCode = 404;
            context.Response.Headers["X-Error-Message"] = "Unknown vocabulary: dict-1";
            context.Response.Close();
        });

        var ex = await Assert.ThrowsAsync<PluginRequestException>(() => Reson8StreamingSession.ConnectAsync(
            "reson-key", $"http://127.0.0.1:{port}", Reson8Plugin.DefaultAuthHeader, "dict-1", null, [], null, CancellationToken.None));

        await serve;
        Assert.Equal(404, ex.HttpStatusCode);
        Assert.Contains("Unknown vocabulary: dict-1", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "Reson8 rejected the request: include_words is not supported")]
    [InlineData(HttpStatusCode.PaymentRequired, "Reson8 credit limit reached: include_words is not supported")]
    [InlineData(HttpStatusCode.NotFound, "Reson8 model not found: include_words is not supported")]
    public async Task BatchErrors_NameReson8sDetail(HttpStatusCode status, string message)
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        using var httpClient = new HttpClient(new CapturingHandler((_, _) => JsonResponse(
            """{"status":400,"title":"Invalid Query Parameter","detail":"include_words is not supported"}""", status)));
        var sut = new Reson8Plugin(httpClient);
        await sut.ActivateAsync(host);

        var ex = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.TranscribeAsync(BuildPcm16Wav([0x00, 0x00]), null, false, null, CancellationToken.None));

        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public async Task BatchNotFound_NamesTheCustomModelWhenOneWasSent()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        using var httpClient = new HttpClient(new CapturingHandler((_, _) =>
            JsonResponse("""{"detail":"Unknown vocabulary"}""", HttpStatusCode.NotFound)));
        var sut = await ActivateWithCatalogAsync(host, httpClient);

        var ex = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.TranscribeAsync(BuildPcm16Wav([0x00, 0x00]), null, false, null, CancellationToken.None));

        Assert.Contains("Reson8 custom model not found: Unknown vocabulary", ex.Message);
    }

    private static async Task<Reson8Plugin> ActivateWithListedModelsAsync(TestPluginHostServices host, HttpClient httpClient)
    {
        var sut = await ActivateWithCatalogAsync(host, httpClient);
        sut.SelectModel(Reson8Plugin.DefaultModelId);
        sut.SetFetchedListedModels([s_resonant, s_rumble]);
        return sut;
    }

    // POST answers the key check; GET /v1/model answers with the given list, other GETs with the custom models.
    private static CapturingHandler ModelListHandler(string modelListJson, HttpStatusCode modelListStatus = HttpStatusCode.OK) =>
        new((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
                return JsonResponse("{}", HttpStatusCode.BadRequest);

            return request.RequestUri!.AbsolutePath.EndsWith("/v1/model", StringComparison.Ordinal)
                ? JsonResponse(modelListJson, modelListStatus)
                : JsonResponse(CatalogJson);
        });

    private static int FreeTcpPort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
