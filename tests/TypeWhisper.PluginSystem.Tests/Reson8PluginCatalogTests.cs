using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Reson8;
using TypeWhisper.PluginSDK;

// [0x00, 0x00] is a PCM silence sample, not text; see Reson8PluginTests.cs.
// ReSharper disable UseUtf8StringLiteral

namespace TypeWhisper.PluginSystem.Tests;

public partial class Reson8PluginTests
{
    private static readonly JsonSerializerOptions s_caseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };

    private const string CatalogJson = """[{ "id": "domain-model", "name": "Domain Model" }]""";

    [Fact]
    public async Task ValidateAsync_LoadsCatalogAndDropsSelectionTheAccountNoLongerHas()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        using var httpClient = new HttpClient(new CapturingHandler((request, _) =>
            request.Method == HttpMethod.Post
                ? JsonResponse("{}", HttpStatusCode.BadRequest)
                : JsonResponse("""[{ "id": "other-model", "name": "Other", "description": null, "phraseCount": 3 }]""")));
        var sut = await ActivateWithCatalogAsync(host, httpClient);

        var result = await sut.ValidateAsync();

        Assert.True(result!.IsSuccess);
        Assert.Equal("Settings.ApiKeyValidWithModels", result.Message);
        Assert.Equal("other-model", Assert.Single(sut.FetchedCustomModels).Id);
        Assert.Equal(Reson8Plugin.DefaultModelId, sut.SelectedModelId);
        Assert.Equal(Reson8Plugin.DefaultModelId, host.GetSetting<string>("selectedModel"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "[]")]
    [InlineData(HttpStatusCode.NotFound, "[]")]
    [InlineData(HttpStatusCode.TooManyRequests, "[]")]
    [InlineData(HttpStatusCode.Unauthorized, "[]")]
    [InlineData(HttpStatusCode.Found, "[]")]
    [InlineData(HttpStatusCode.OK, "invalid-json")]
    [InlineData(HttpStatusCode.OK, """{ "models": [] }""")]
    [InlineData(HttpStatusCode.OK, "[null]")]
    [InlineData(HttpStatusCode.OK, """[{ "name": "Missing ID" }]""")]
    [InlineData(HttpStatusCode.OK, """[{ "id": "missing-name" }]""")]
    [InlineData(HttpStatusCode.OK, """[{ "id": " ", "name": "Blank" }]""")]
    [InlineData(HttpStatusCode.OK, """[{ "id": 7, "name": "Numeric ID" }]""")]
    [InlineData(HttpStatusCode.OK, """[{ "id": "__default__", "name": "Collision" }]""")]
    [InlineData(HttpStatusCode.OK, """[{ "id": "a", "name": "A" }, { "id": "a", "name": "B" }]""")]
    [InlineData(HttpStatusCode.OK, """[{ "id": "a", "name": "Line\nBreak" }]""")]
    [InlineData(HttpStatusCode.OK, """[{ "id": "a", "name": "A", "description": 5 }]""")]
    [InlineData(HttpStatusCode.OK, """[{ "id": "a", "name": "A", "phraseCount": -1 }]""")]
    [InlineData(HttpStatusCode.OK, """[{ "id": "a", "name": "A", "phraseCount": 1.5 }]""")]
    public async Task ValidateAsync_FailedRefreshKeepsCatalogAndSelection(HttpStatusCode status, string json)
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        using var httpClient = new HttpClient(new CapturingHandler((request, _) =>
            request.Method == HttpMethod.Post ? JsonResponse("{}", HttpStatusCode.BadRequest) : JsonResponse(json, status)));
        var sut = await ActivateWithCatalogAsync(host, httpClient);

        var result = await sut.ValidateAsync();

        Assert.True(result!.IsSuccess);
        Assert.Equal("Settings.CustomModelsRefreshFailed", result.Message);
        await AssertCatalogKeptAsync(sut, host);
    }

    [Fact]
    public async Task ValidateAsync_OversizedCatalogIsRejected()
    {
        var description = new string('d', 200_000);
        var json = "[" + string.Join(",", Enumerable.Range(0, 6).Select(i => $$"""{ "id": "m{{i}}", "name": "M", "description": "{{description}}" }""")) + "]";
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        using var httpClient = new HttpClient(new CapturingHandler((request, _) =>
            request.Method == HttpMethod.Post ? JsonResponse("{}", HttpStatusCode.BadRequest) : JsonResponse(json)));
        var sut = await ActivateWithCatalogAsync(host, httpClient);

        var result = await sut.ValidateAsync();

        Assert.Equal("Settings.CustomModelsRefreshFailed", result!.Message);
        await AssertCatalogKeptAsync(sut, host);
    }

    [Fact]
    public void ParseCatalogResponse_RejectsMoreThanTheModelLimit()
    {
        Assert.Equal(1000, Reson8Plugin.ParseCatalogResponse(Catalog(1000))!.Count);
        Assert.Null(Reson8Plugin.ParseCatalogResponse(Catalog(1001)));
        return;

        static byte[] Catalog(int count) => Encoding.UTF8.GetBytes(
            "[" + string.Join(",", Enumerable.Range(0, count).Select(i => $$"""{ "id": "m{{i}}", "name": "M" }""")) + "]");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Settings.InvalidApiKey")]
    [InlineData(HttpStatusCode.MovedPermanently, "Settings.ServerRedirected")]
    [InlineData(HttpStatusCode.TemporaryRedirect, "Settings.ServerRedirected")]
    public async Task ValidateAsync_RejectedKeyCheckKeepsCatalogAndSkipsRefresh(HttpStatusCode status, string message)
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        var catalogRequests = 0;
        using var httpClient = new HttpClient(new CapturingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
                catalogRequests++;
            return JsonResponse("{}", status);
        }));
        var sut = await ActivateWithCatalogAsync(host, httpClient);

        var result = await sut.ValidateAsync();

        Assert.False(result!.IsSuccess);
        Assert.Equal(message, result.Message);
        Assert.Equal(0, catalogRequests);
        await AssertCatalogKeptAsync(sut, host);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ValidateAsync_UnreachableServerIsNotReportedAsInvalidKey(bool timeout)
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        using var httpClient = new HttpClient(new CapturingHandler((_, _) =>
            throw (timeout ? new TaskCanceledException("timed out") : new HttpRequestException("connection refused"))));
        var sut = await ActivateWithCatalogAsync(host, httpClient);

        var result = await sut.ValidateAsync();

        Assert.False(result!.IsSuccess);
        Assert.Equal("Settings.ServerUnreachable", result.Message);
        await AssertCatalogKeptAsync(sut, host);
    }

    [Fact]
    public async Task ValidateAsync_CatalogBodyCutOffKeepsCatalog()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        using var httpClient = new HttpClient(new CapturingHandler((request, _) =>
            request.Method == HttpMethod.Post
                ? JsonResponse("{}", HttpStatusCode.BadRequest)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream()) }));
        var sut = await ActivateWithCatalogAsync(host, httpClient);

        var result = await sut.ValidateAsync();

        Assert.Equal("Settings.CustomModelsRefreshFailed", result!.Message);
        await AssertCatalogKeptAsync(sut, host);
    }

    [Fact]
    public async Task ValidateAsync_UnreachableCatalogAfterKeyCheckKeepsCatalog()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        using var httpClient = new HttpClient(new CapturingHandler((request, _) =>
            request.Method == HttpMethod.Post
                ? JsonResponse("{}", HttpStatusCode.BadRequest)
                : throw new HttpRequestException("connection reset")));
        var sut = await ActivateWithCatalogAsync(host, httpClient);

        var result = await sut.ValidateAsync();

        Assert.True(result!.IsSuccess);
        Assert.Equal("Settings.CustomModelsRefreshFailed", result.Message);
        await AssertCatalogKeptAsync(sut, host);
    }

    [Fact]
    public async Task ValidateAsync_CallerCancellationPropagates()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        using var cts = new CancellationTokenSource();
        // ReSharper disable AccessToDisposedClosure -- the handler runs inside ValidateAsync, before cts is disposed.
        using var httpClient = new HttpClient(new CapturingHandler((_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }));
        // ReSharper restore AccessToDisposedClosure
        var sut = await ActivateWithCatalogAsync(host, httpClient);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.ValidateAsync(cts.Token));
        await AssertCatalogKeptAsync(sut, host);
    }

    [Fact]
    public async Task ValidateAsync_DiscardsCatalogLoadedBeforeAConnectionChange()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        Reson8Plugin? sut = null;
        using var httpClient = new HttpClient(new CapturingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
                return JsonResponse("{}", HttpStatusCode.BadRequest);

            // ReSharper disable once AccessToModifiedClosure -- assigned before the first request is sent.
            sut!.SetCustomBaseUrl("https://proxy.example.test");
            return JsonResponse("""[{ "id": "old-server-model", "name": "Old" }]""");
        }));
        sut = await ActivateWithCatalogAsync(host, httpClient);

        var result = await sut.ValidateAsync();

        Assert.False(result!.IsSuccess);
        Assert.Equal("Settings.SettingsChangedDuringValidation", result.Message);
        Assert.Empty(sut.FetchedCustomModels);
        sut.SetCustomBaseUrl(null);
        Assert.Equal("domain-model", Assert.Single(sut.FetchedCustomModels).Id);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("url")]
    [InlineData("header")]
    public async Task ConnectionChange_HidesCatalogUntilRevertedAndRefusesItsModel(string change)
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        var requests = 0;
        using var httpClient = new HttpClient(new CapturingHandler((_, _) =>
        {
            requests++;
            return JsonResponse("""{ "text": "ok" }""");
        }));
        var sut = await ActivateWithCatalogAsync(host, httpClient);
        var notifications = host.NotifyCapabilitiesChangedCount;

        await ApplyConnectionChangeAsync(sut, change, revert: false);

        Assert.Equal(notifications + 1, host.NotifyCapabilitiesChangedCount);
        Assert.Empty(sut.FetchedCustomModels);
        Assert.Equal([Reson8Plugin.DefaultModelId], sut.TranscriptionModels.Select(m => m.Id).ToArray());
        Assert.Equal("domain-model", sut.SelectedModelId);
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.TranscribeAsync(BuildPcm16Wav([0x00, 0x00]), null, false, null, CancellationToken.None));
        Assert.Equal(PluginRequestFailureKind.Configuration, ex.FailureKind);
        Assert.Equal("Settings.SelectedModelUnavailable", ex.Message);
        await Assert.ThrowsAsync<PluginRequestException>(() => sut.StartStreamingAsync(null, CancellationToken.None));
        Assert.Equal(0, requests);

        // A restart keeps the catalog with the connection it came from.
        await sut.DeactivateAsync();
        await sut.ActivateAsync(host);
        Assert.Empty(sut.FetchedCustomModels);

        await ApplyConnectionChangeAsync(sut, change, revert: true);
        Assert.Equal(notifications + 2, host.NotifyCapabilitiesChangedCount);
        Assert.Equal("domain-model", Assert.Single(sut.FetchedCustomModels).Id);
        await sut.TranscribeAsync(BuildPcm16Wav([0x00, 0x00]), null, false, null, CancellationToken.None);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task ConnectionSettingsWithTheSameMeaningKeepTheCatalog()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        var sut = await ActivateWithCatalogAsync(host, new HttpClient());
        var notifications = host.NotifyCapabilitiesChangedCount;

        sut.SetCustomBaseUrl(" https://api.reson8.dev/ ");
        sut.SetCustomAuthHeader("authorization");
        await sut.SetSettingValueAsync("api-key", " reson-key ");

        Assert.Equal(notifications, host.NotifyCapabilitiesChangedCount);
        Assert.Equal("domain-model", Assert.Single(sut.FetchedCustomModels).Id);
    }

    [Fact]
    public async Task ApiKeyRemovalHidesCatalogWithOneNotification()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        var sut = await ActivateWithCatalogAsync(host, new HttpClient());
        var notifications = host.NotifyCapabilitiesChangedCount;

        await sut.SetSettingValueAsync("api-key", "");

        Assert.Equal(notifications + 1, host.NotifyCapabilitiesChangedCount);
        Assert.False(sut.IsConfigured);
        Assert.Empty(sut.FetchedCustomModels);
    }

    [Fact]
    public async Task SettingsDropdown_ListsHiddenSelectionAsUnavailable()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        var sut = await ActivateWithCatalogAsync(host, new HttpClient());

        sut.SetCustomAuthHeader("X-Api-Key");

        var options = sut.GetSettingDefinitions().Single(d => d.Key == "selectedModel").Options!;
        Assert.Equal([Reson8Plugin.DefaultModelId, "domain-model"], options.Select(o => o.Value).ToArray());
        Assert.Equal("Settings.UnavailableModelOption", options[1].Label);
        Assert.Equal("domain-model", await sut.GetSettingValueAsync("selectedModel"));
    }

    [Theory]
    [InlineData("customBaseURL", "relative/path", "Settings.InvalidBaseUrl")]
    [InlineData("customBaseURL", "ftp://example.test", "Settings.InvalidBaseUrl")]
    [InlineData("customBaseURL", "wss://example.test", "Settings.InvalidBaseUrl")]
    [InlineData("customBaseURL", "https://example.test?key=value", "Settings.InvalidBaseUrl")]
    [InlineData("customBaseURL", "https://example.test#fragment", "Settings.InvalidBaseUrl")]
    [InlineData("customBaseURL", "https://user:password@example.test", "Settings.InvalidBaseUrl")]
    [InlineData("customAuthHeader", "X API Key", "Settings.InvalidAuthHeader")]
    [InlineData("customAuthHeader", "Authorization:", "Settings.InvalidAuthHeader")]
    [InlineData("customAuthHeader", "X-Äpi-Key", "Settings.InvalidAuthHeader")]
    [InlineData("api-key", "bad\r\nkey", "Settings.InvalidApiKeyCharacters")]
    [InlineData("api-key", "bad\tkey", "Settings.InvalidApiKeyCharacters")]
    [InlineData("api-key", "bad\0key", "Settings.InvalidApiKeyCharacters")]
    public async Task InvalidConnectionSettingsAreKeptAndReportedWithoutRequests(string key, string value, string message)
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        var requests = 0;
        using var httpClient = new HttpClient(new CapturingHandler((_, _) =>
        {
            requests++;
            return JsonResponse("{}");
        }));
        var sut = await ActivateWithCatalogAsync(host, httpClient);
        sut.SelectModel(Reson8Plugin.DefaultModelId);

        await sut.SetSettingValueAsync(key, value);

        Assert.Equal(value, await sut.GetSettingValueAsync(key));
        var result = await sut.ValidateAsync();
        Assert.False(result!.IsSuccess);
        Assert.Equal(message, result.Message);
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.TranscribeAsync(BuildPcm16Wav([0x00, 0x00]), null, false, null, CancellationToken.None));
        Assert.Equal(PluginRequestFailureKind.Configuration, ex.FailureKind);
        Assert.Equal(message, ex.Message);
        await Assert.ThrowsAsync<PluginRequestException>(() => sut.StartStreamingAsync(null, CancellationToken.None));
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task ProxyBaseUrlPathAndPortAreKept()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        var paths = new List<string>();
        using var httpClient = new HttpClient(new CapturingHandler((request, _) =>
        {
            paths.Add(request.RequestUri!.GetLeftPart(UriPartial.Path));
            return request.Method == HttpMethod.Post ? JsonResponse("{}", HttpStatusCode.BadRequest) : JsonResponse(CatalogJson);
        }));
        var sut = new Reson8Plugin(httpClient);
        await sut.ActivateAsync(host);

        sut.SetCustomBaseUrl("https://proxy.example.test:8443/reson8/");
        await sut.ValidateAsync();

        Assert.Equal(
            ["https://proxy.example.test:8443/reson8/v1/speech-to-text/prerecorded", "https://proxy.example.test:8443/reson8/v1/custom-model"],
            paths);
        Assert.Equal("/reson8/v1/speech-to-text/realtime", Reson8StreamingSession.BuildRealtimeUri(sut.CustomBaseUrl, null, null).AbsolutePath);
    }

    [Fact]
    public async Task FailedCatalogWriteKeepsCatalogAndSelection()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        var sut = await ActivateWithCatalogAsync(host, new HttpClient());
        host.FailSettingName = "customModelCatalog";

        Assert.Throws<IOException>(() => sut.SetFetchedCustomModels([]));

        host.FailSettingName = null;
        await AssertCatalogKeptAsync(sut, host);
    }

    [Theory]
    [InlineData("selectedModel")]
    [InlineData("customModelCatalog")]
    public async Task FailedRefreshWriteThatDropsTheSelectionKeepsBoth(string setting)
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        var sut = await ActivateWithCatalogAsync(host, new HttpClient());
        var notifications = host.NotifyCapabilitiesChangedCount;
        host.FailSettingName = setting;

        Assert.Throws<IOException>(() => sut.SetFetchedCustomModels([new Reson8CustomModel("other-model", "Other", null, null)]));

        Assert.Equal(notifications, host.NotifyCapabilitiesChangedCount);
        host.FailSettingName = null;
        await AssertCatalogKeptAsync(sut, host);
    }

    [Theory]
    [InlineData("customBaseURL")]
    [InlineData("customAuthHeader")]
    public async Task FailedConnectionWriteKeepsConnectionAndCatalog(string setting)
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        var sut = await ActivateWithCatalogAsync(host, new HttpClient());
        host.FailSettingName = setting;

        await Assert.ThrowsAsync<IOException>(() => sut.SetSettingValueAsync(setting, "X-Other"));

        Assert.Equal(Reson8Plugin.DefaultBaseUrl, sut.CustomBaseUrl);
        Assert.Equal(Reson8Plugin.DefaultAuthHeader, sut.CustomAuthHeader);
        host.FailSettingName = null;
        await AssertCatalogKeptAsync(sut, host);
    }

    [Fact]
    public async Task UnreadableStoredCatalogIsIgnoredAndLegacyListNotAdopted()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        host.SetSetting("customModelCatalog", """{ "owner": "x", "models": [{ "id": "__default__", "name": "Bad" }] }""");
        host.SetSetting("fetchedCustomModels", new[] { new Reson8CustomModel("legacy", "Legacy", null, null) });
        var sut = new Reson8Plugin();

        await sut.ActivateAsync(host);

        Assert.Empty(sut.FetchedCustomModels);
        Assert.Contains("Ignoring unreadable cached Reson8 custom models.", host.LogMessages);
    }

    [Fact]
    public async Task InvalidLegacyListIsNotAdopted()
    {
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "reson-key" } };
        host.SetSetting("fetchedCustomModels", new[] { new Reson8CustomModel(" ", "Blank", null, null) });
        var sut = new Reson8Plugin();

        await sut.ActivateAsync(host);

        Assert.Empty(sut.FetchedCustomModels);
        Assert.Null(host.GetSetting<string>("customModelCatalog"));
    }

    private static async Task<Reson8Plugin> ActivateWithCatalogAsync(TestPluginHostServices host, HttpClient httpClient)
    {
        var sut = new Reson8Plugin(httpClient);
        await sut.ActivateAsync(host);
        sut.SetFetchedCustomModels([new Reson8CustomModel("domain-model", "Domain Model", null, null)]);
        sut.SelectModel("domain-model");
        return sut;
    }

    private static async Task AssertCatalogKeptAsync(Reson8Plugin sut, TestPluginHostServices host)
    {
        Assert.Equal("domain-model", Assert.Single(sut.FetchedCustomModels).Id);
        Assert.Equal("domain-model", sut.SelectedModelId);
        await sut.DeactivateAsync();
        await sut.ActivateAsync(host);
        Assert.Equal("domain-model", Assert.Single(sut.FetchedCustomModels).Id);
        Assert.Equal("domain-model", sut.SelectedModelId);
    }

    private static async Task ApplyConnectionChangeAsync(Reson8Plugin sut, string change, bool revert)
    {
        switch (change)
        {
            case "key":
                await sut.SetSettingValueAsync("api-key", revert ? "reson-key" : "other-key");
                break;
            case "url":
                await sut.SetSettingValueAsync("customBaseURL", revert ? null : "https://proxy.example.test/reson8");
                break;
            default:
                await sut.SetSettingValueAsync("customAuthHeader", revert ? null : "X-Api-Key");
                break;
        }
    }

    private sealed class FailingStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("connection closed");

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection closed");
    }

    private static Reson8CustomModelCatalog StoredCatalog(TestPluginHostServices host) =>
        JsonSerializer.Deserialize<Reson8CustomModelCatalog>(
            host.GetSetting<string>("customModelCatalog")!,
            s_caseInsensitiveJson)!;
}
