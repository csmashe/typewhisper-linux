using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting.Internal;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK.Processes;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class HttpApiUnixSocketTests
{
    private static readonly string[] s_helloWorld = ["Hello", "World"];

    [Fact]
    public async Task ModelLoadActivatesWithoutChangingSelection()
    {
        var engine = new DownloadableTranscriptionEngine();
        using var fixture = new ApiFixture(transcriptionEngine: engine);
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        using var content = new StringContent("""{"engine":"DOWNLOADABLE","model":"other"}""", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/models/load", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var fullId = ModelManagerService.GetPluginModelId(engine.PluginId, "other");
        Assert.Equal("ready", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(engine.ProviderId, json.RootElement.GetProperty("engine").GetString());
        Assert.Equal("other", json.RootElement.GetProperty("model").GetString());
        Assert.Equal(fullId, json.RootElement.GetProperty("fullId").GetString());
        Assert.Equal(1, engine.LoadCount);
        using var statusResponse = await client.GetAsync("/v1/status");
        using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
        Assert.Equal(fullId, status.RootElement.GetProperty("active_model").GetString());
        using var modelsResponse = await client.GetAsync("/v1/models");
        using var models = JsonDocument.Parse(await modelsResponse.Content.ReadAsStringAsync());
        Assert.Equal("test", Assert.Single(models.RootElement.GetProperty("models").EnumerateArray(),
            model => model.GetProperty("selected").GetBoolean()).GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("", HttpStatusCode.BadRequest)]
    [InlineData("{", HttpStatusCode.BadRequest)]
    [InlineData("{}", HttpStatusCode.BadRequest)]
    [InlineData("{\"engine\":\"\"}", HttpStatusCode.BadRequest)]
    [InlineData("[]", HttpStatusCode.BadRequest)]
    [InlineData("null", HttpStatusCode.BadRequest)]
    [InlineData("{\"engine\":123}", HttpStatusCode.BadRequest)]
    [InlineData("{\"engine\":\"downloadable\",\"model\":false}", HttpStatusCode.BadRequest)]
    [InlineData("{\"engine\":\"downloadable\",\"engine\":\"downloadable\"}", HttpStatusCode.BadRequest)]
    [InlineData("{\"engine\":\"missing\"}", HttpStatusCode.NotFound)]
    [InlineData("{\"engine\":\"downloadable\",\"model\":\"missing\"}", HttpStatusCode.NotFound)]
    public async Task ModelLoadRejectsInvalidRequests(string body, HttpStatusCode expected)
    {
        var engine = new DownloadableTranscriptionEngine();
        using var fixture = new ApiFixture(transcriptionEngine: engine);
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/models/load", content);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(0, engine.LoadCount);
    }

    [Theory]
    [InlineData("", HttpStatusCode.Conflict, 0)]
    [InlineData("?await_download=YES", HttpStatusCode.OK, 1)]
    [InlineData("?await_download=invalid", HttpStatusCode.BadRequest, 0)]
    public async Task ModelLoadOnlyDownloadsWhenRequested(string query, HttpStatusCode expected, int downloads)
    {
        var engine = new DownloadableTranscriptionEngine { Downloaded = false };
        using var fixture = new ApiFixture(transcriptionEngine: engine);
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        using var content = new StringContent("""{"engine":"downloadable"}""", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/models/load" + query, content);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(downloads, engine.DownloadCount);
        Assert.Equal(downloads, engine.LoadCount);
        if (expected == HttpStatusCode.Conflict)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("Model is not downloaded", json.RootElement.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task ModelUnloadThenDeleteRemovesOnlyUnloadedFiles()
    {
        var engine = new DownloadableTranscriptionEngine();
        using var fixture = new ApiFixture(transcriptionEngine: engine);
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        using var content = new StringContent("""{"engine":"downloadable","model":"other"}""", Encoding.UTF8, "application/json");
        using var load = await client.PostAsync("/v1/models/load", content);
        Assert.Equal(HttpStatusCode.OK, load.StatusCode);
        using var loadedDelete = await client.DeleteAsync("/v1/models?engine=downloadable&model=other");
        Assert.Equal(HttpStatusCode.Conflict, loadedDelete.StatusCode);
        using var error = JsonDocument.Parse(await loadedDelete.Content.ReadAsStringAsync());
        Assert.Equal("Unload the model first", error.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, engine.UnloadCount);
        Assert.Equal(0, engine.DeleteCount);
        using var unload = await client.PostAsync("/v1/models/unload", null);
        Assert.Equal(HttpStatusCode.OK, unload.StatusCode);
        using var unloaded = JsonDocument.Parse(await unload.Content.ReadAsStringAsync());
        Assert.Equal("unloaded", unloaded.RootElement.GetProperty("status").GetString());
        Assert.Equal("other", unloaded.RootElement.GetProperty("model").GetString());
        Assert.Equal(engine.ProviderId, unloaded.RootElement.GetProperty("engine").GetString());
        Assert.Equal(1, engine.UnloadCount);
        Assert.Null(fixture.Models.ActiveModelId);
        using var delete = await client.DeleteAsync("/v1/models?engine=downloadable&model=other");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        using var deleted = JsonDocument.Parse(await delete.Content.ReadAsStringAsync());
        Assert.Equal("deleted", deleted.RootElement.GetProperty("status").GetString());
        Assert.Equal("other", deleted.RootElement.GetProperty("model").GetString());
        Assert.Equal(engine.ProviderId, deleted.RootElement.GetProperty("engine").GetString());
        Assert.Equal(1, engine.DeleteCount);
    }

    [Theory]
    [InlineData("", HttpStatusCode.OK)]
    [InlineData("{}", HttpStatusCode.OK)]
    [InlineData("{\"engine\":\"downloadable\"}", HttpStatusCode.OK)]
    [InlineData("{\"engine\":\"missing\"}", HttpStatusCode.NotFound)]
    [InlineData("[]", HttpStatusCode.BadRequest)]
    [InlineData("{\"engine\":\"downloadable\",\"engine\":\"downloadable\"}", HttpStatusCode.BadRequest)]
    public async Task ModelUnloadWhenNothingLoaded(string body, HttpStatusCode expected)
    {
        using var fixture = new ApiFixture(transcriptionEngine: new DownloadableTranscriptionEngine());
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/models/unload", content);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("model").ValueKind);
            Assert.Equal("unloaded", json.RootElement.GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task ModelUnloadRejectsDifferentEngine()
    {
        var engine = new DownloadableTranscriptionEngine();
        var other = new SegmentedTranscriptionEngine();
        using var fixture = new ApiFixture(transcriptionEngines: [engine, other]);
        await fixture.Models.LoadModelAsync(ModelManagerService.GetPluginModelId(engine.PluginId, "test"));
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        using var content = new StringContent(JsonSerializer.Serialize(new { engine = other.ProviderId }), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/models/unload", content);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("That engine has no loaded model", json.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, engine.UnloadCount);
    }

    [Theory]
    [InlineData("?engine=downloadable&model=test", true, HttpStatusCode.Conflict, "The selected model cannot be deleted")]
    [InlineData("?engine=downloadable&model=other", false, HttpStatusCode.NotFound, "Downloaded model not found")]
    [InlineData("?engine=downloadable", true, HttpStatusCode.BadRequest, "Both 'engine' and 'model' are required.")]
    [InlineData("?model=other", true, HttpStatusCode.BadRequest, "Both 'engine' and 'model' are required.")]
    [InlineData("?engine=missing&model=other", true, HttpStatusCode.NotFound, "Unknown engine: missing")]
    [InlineData("?engine=downloadable&model=missing", true, HttpStatusCode.NotFound, "Unknown model for engine downloadable: missing")]
    public async Task ModelDeleteRejectsProtectedOrMissingModels(string query, bool downloaded, HttpStatusCode expected, string message)
    {
        var engine = new DownloadableTranscriptionEngine { Downloaded = downloaded };
        using var fixture = new ApiFixture(transcriptionEngine: engine);
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        using var response = await client.DeleteAsync("/v1/models" + query);
        Assert.Equal(expected, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(message, json.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, engine.DeleteCount);
    }

    [Theory]
    [InlineData("POST", "/v1/models/load")]
    [InlineData("POST", "/v1/models/unload")]
    [InlineData("DELETE", "/v1/models?engine=downloadable&model=other")]
    public async Task ModelOperationsReturnBusyWhileTranscriptionLeaseIsHeld(string method, string path)
    {
        var engine = new DownloadableTranscriptionEngine();
        using var fixture = new ApiFixture(transcriptionEngine: engine);
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        await using (await fixture.Models.AcquireTranscriptionAsync())
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            request.Content = new StringContent("""{"engine":"downloadable"}""", Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("A transcription or model operation is in progress", json.RootElement.GetProperty("error").GetString());
        }
        Assert.Equal(1, engine.LoadCount);
        Assert.Equal(0, engine.UnloadCount);
        Assert.Equal(0, engine.DeleteCount);
    }

    [Fact]
    public async Task ModelUnloadReportsFailedTeardownAndKeepsModelActive()
    {
        var engine = new DownloadableTranscriptionEngine { UnloadFails = true };
        using var fixture = new ApiFixture(transcriptionEngine: engine);
        var fullId = ModelManagerService.GetPluginModelId(engine.PluginId, "other");
        await fixture.Models.LoadModelAsync(fullId);
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        using var response = await client.PostAsync("/v1/models/unload", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("This engine cannot unload the model", json.RootElement.GetProperty("error").GetString());
        Assert.Equal(1, engine.UnloadCount);
        Assert.Equal(fullId, fixture.Models.ActiveModelId);
    }

    [Fact]
    public async Task ModelDeleteReportsFilesLeftBehind()
    {
        var engine = new DownloadableTranscriptionEngine { DeleteFails = true };
        using var fixture = new ApiFixture(transcriptionEngine: engine);
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        using var response = await client.DeleteAsync("/v1/models?engine=downloadable&model=other");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Model files could not be deleted", json.RootElement.GetProperty("error").GetString());
        Assert.Equal(1, engine.DeleteCount);
        Assert.True(fixture.Models.IsDownloaded(ModelManagerService.GetPluginModelId(engine.PluginId, "other")));
    }

    [Fact]
    public async Task ModelDeleteRejectsEngineWithoutFileManagement()
    {
        var engine = new SegmentedTranscriptionEngine();
        using var fixture = new ApiFixture(transcriptionEngines: [engine]);
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        using var response = await client.DeleteAsync($"/v1/models?engine={engine.ProviderId}&model=test");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("This engine does not support deleting model files", json.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("/v1/models/load")]
    [InlineData("/v1/models/unload")]
    public async Task ModelRequestsRespectJsonBodyLimit(string path)
    {
        using var fixture = new ApiFixture();
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        using var content = new StringContent(new string(' ', (int)HttpApiService.MaxJsonRequestBytes + 1), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(path, content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private sealed class DownloadableTranscriptionEngine : ITranscriptionEngineRole
    {
        public string PluginId => "test.downloadable";
        public string ProviderId => "downloadable";
        public string ProviderDisplayName => "Downloadable";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels => [new("test", "Test"), new("other", "Other")];
        public string SelectedModelId => "test";
        public bool SupportsTranslation => false;
        public bool SupportsModelDownload => true;
        public bool Downloaded { get; set; } = true;
        public bool UnloadFails { get; init; }
        public bool DeleteFails { get; init; }
        public int LoadCount { get; private set; }
        public int UnloadCount { get; private set; }
        public int DeleteCount { get; private set; }
        public int DownloadCount { get; private set; }
        public void SelectModel(string modelId) { }
        public bool IsModelDownloaded(string modelId) => Downloaded;
        public Task DownloadModelAsync(string modelId, IProgress<double>? progress, CancellationToken ct)
        {
            DownloadCount++;
            Downloaded = true;
            return Task.CompletedTask;
        }
        public Task LoadModelAsync(string modelId, CancellationToken ct)
        {
            LoadCount++;
            return Task.CompletedTask;
        }
        public Task UnloadModelAsync()
        {
            UnloadCount++;
            return UnloadFails ? Task.FromException(new InvalidOperationException("teardown failed")) : Task.CompletedTask;
        }
        public Task DeleteModelAsync(string modelId, CancellationToken ct)
        {
            DeleteCount++;
            // DeleteFails mirrors plugins that swallow filesystem errors and leave the file behind.
            if (!DeleteFails)
            {
                Downloaded = false;
            }
            return Task.CompletedTask;
        }
        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct) =>
            Task.FromResult(new PluginTranscriptionResult("test", "en", 1));
    }

    [Fact]
    public async Task VerboseTranslationPreservesSegmentTimingsAndReportsTargetLanguage()
    {
        var translation = CreateTranslationMock();
        using var fixture = CreateSegmentFixture(translation.Object);
        fixture.Start();
        using var client = fixture.CreateTcpClient(withBearer: true);
        using var content = CreateSegmentRequest(fixture, "verbose_json", "de");

        using var response = await client.PostAsync("/v1/transcribe/local-file", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Hallo Welt", json.RootElement.GetProperty("text").GetString());
        Assert.Equal("de", json.RootElement.GetProperty("language").GetString());
        AssertSegments(json.RootElement, "Hallo", "Welt");
        translation.Verify(service => service.TranslateSegmentsAsync(
            It.Is<IReadOnlyList<string>>(texts => texts.SequenceEqual(s_helloWorld)),
            "en", "de", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SegmentTranslationMismatchReturnsBadGateway()
    {
        var translation = CreateTranslationMock();
        translation.Setup(service => service.TranslateSegmentsAsync(
                It.IsAny<IReadOnlyList<string>>(), "en", "de", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SegmentTranslationMismatchException());
        using var fixture = CreateSegmentFixture(translation.Object);
        fixture.Start();
        using var client = fixture.CreateTcpClient(withBearer: true);
        using var content = CreateSegmentRequest(fixture, "verbose_json", "de");

        using var response = await client.PostAsync("/v1/transcribe/local-file", content);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Single(json.RootElement.EnumerateObject());
        Assert.Equal(
            "Translation did not preserve the subtitle segments. Retry with another LLM model.",
            json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task JsonTranslationDoesNotTranslateSegments()
    {
        var translation = CreateTranslationMock();
        using var fixture = CreateSegmentFixture(translation.Object);
        fixture.Start();
        using var client = fixture.CreateTcpClient(withBearer: true);
        using var content = CreateSegmentRequest(fixture, "json", "de");

        using var response = await client.PostAsync("/v1/transcribe/local-file", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Hallo Welt", json.RootElement.GetProperty("text").GetString());
        Assert.Equal("de", json.RootElement.GetProperty("language").GetString());
        translation.Verify(service => service.TranslateSegmentsAsync(
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<LlmCallCapture?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WithoutTargetLanguageSegmentsAndDetectedLanguageAreUntouched()
    {
        var translation = new Mock<ITranslationService>(MockBehavior.Strict);
        using var fixture = CreateSegmentFixture(translation.Object);
        fixture.Start();
        using var client = fixture.CreateTcpClient(withBearer: true);
        using var content = CreateSegmentRequest(fixture, "verbose_json", null);

        using var response = await client.PostAsync("/v1/transcribe/local-file", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Hello World", json.RootElement.GetProperty("text").GetString());
        Assert.Equal("en", json.RootElement.GetProperty("language").GetString());
        AssertSegments(json.RootElement, "Hello", "World");
        translation.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EveryCatalogRouteReachesAHandler()
    {
        var dictionary = new Mock<IDictionaryService>();
        dictionary.Setup(service => service.GetEnabledTerms()).Returns([]);
        dictionary.Setup(service => service.GetCorrections()).Returns([]);
        using var fixture = new ApiFixture(dictionary: dictionary.Object);
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);

        Assert.Equal(HttpApiRoutes.All.Count, HttpApiRoutes.All.Distinct().Count());
        foreach (var (method, path) in HttpApiRoutes.All)
        {
            Assert.True(HttpApiRoutes.Contains(method, path));
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            using var response = await client.SendAsync(request);
            Assert.True(response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed),
                $"{method} {path}: {response.StatusCode}");
        }
    }

    [Fact]
    public async Task KnownPathRejectsWrongMethodWithAllowAndUnknownPathRemainsNotFound()
    {
        using var fixture = new ApiFixture();
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        using var wrongMethod = await client.PutAsync("/v1/status", null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Equal("GET", Assert.Single(wrongMethod.Content.Headers.Allow));
        using var error = JsonDocument.Parse(await wrongMethod.Content.ReadAsStringAsync());
        Assert.Equal("Method not allowed", error.RootElement.GetProperty("error").GetString());
        using var unknown = await client.GetAsync("/v1/nope");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task CapabilitiesDescribeCatalogAndLimits()
    {
        using var fixture = new ApiFixture();
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        using var response = await client.GetAsync("/v1/capabilities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        using var statusResponse = await client.GetAsync("/v1/status");
        using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
        Assert.Equal(status.RootElement.GetProperty("api_version").GetString(), root.GetProperty("api_version").GetString());
        Assert.Equal(HttpApiRoutes.All.Select(route => route.Path).Distinct(),
            root.GetProperty("endpoints").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(HttpApiRoutes.All, root.GetProperty("routes").EnumerateArray()
            .Select(route => (route.GetProperty("method").GetString()!, route.GetProperty("path").GetString()!)).ToArray());
        Assert.Equal(["json", "verbose_json", "text", "srt", "vtt"],
            root.GetProperty("response_formats").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(HttpApiService.MaxTranscribeRequestBytes, root.GetProperty("max_upload_bytes").GetInt64());
        Assert.Equal("request-scoped engine/model overrides; await_download supported", root.GetProperty("model_selection").GetString());
        foreach (var route in new[] { ("POST", "/v1/models/load"), ("POST", "/v1/models/unload"), ("DELETE", "/v1/models") })
        {
            Assert.Contains(root.GetProperty("routes").EnumerateArray(), value =>
                value.GetProperty("method").GetString() == route.Item1 && value.GetProperty("path").GetString() == route.Item2);
        }
        Assert.True(root.GetProperty("supports_dictation_control").GetBoolean());
        Assert.True(root.GetProperty("requires_authentication").GetBoolean());
    }

    [Theory]
    [InlineData("/docs")]
    [InlineData("/docs/")]
    public async Task DocsArePublicButStillCheckOriginAndHost(string path)
    {
        using var fixture = new ApiFixture();
        fixture.Start();
        using var client = fixture.CreateTcpClient(withBearer: false);
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains($"http://127.0.0.1:{fixture.Port}", html);
        foreach (var route in HttpApiRoutes.Descriptions)
        {
            Assert.Contains(route.Path, html);
            Assert.Contains(route.Description, html);
        }
        using var forbiddenOrigin = new HttpRequestMessage(HttpMethod.Get, path);
        forbiddenOrigin.Headers.Add("Origin", "https://example.com");
        using var forbidden = await client.SendAsync(forbiddenOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var forbiddenHost = new HttpRequestMessage(HttpMethod.Get, path);
        forbiddenHost.Headers.Host = "example.com";
        using var hostResponse = await client.SendAsync(forbiddenHost);
        Assert.Equal(HttpStatusCode.Forbidden, hostResponse.StatusCode);
        using var status = await client.GetAsync("/v1/status");
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
    }

    [Fact]
    public async Task RulesAliasProfilesForListingAndToggling()
    {
        using var fixture = new ApiFixture();
        fixture.Profiles.AddProfile(new Profile { Id = "alias", Name = "Alias", IsEnabled = true });
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        var rules = await client.GetStringAsync("/v1/rules");
        Assert.Equal(await client.GetStringAsync("/v1/profiles"), rules);
        using var json = JsonDocument.Parse(rules);
        Assert.Equal(json.RootElement.GetProperty("rules").GetRawText(), json.RootElement.GetProperty("profiles").GetRawText());
        Assert.Single(json.RootElement.GetProperty("rules").EnumerateArray());
        foreach (var path in new[] { "/v1/rules/toggle", "/v1/profiles/toggle" })
        {
            using var response = await client.PutAsync(path + "?id=alias", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var toggle = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("alias", toggle.RootElement.GetProperty("id").GetString());
            Assert.Equal("Alias", toggle.RootElement.GetProperty("name").GetString());
            Assert.Equal(path == "/v1/profiles/toggle", toggle.RootElement.GetProperty("is_enabled").GetBoolean());
        }
    }

    [Theory]
    [InlineData("text", "text/plain", "Hello World")]
    [InlineData("srt", "application/x-subrip", "00:00:00,200 --> 00:00:01,300")]
    [InlineData("vtt", "text/vtt", "WEBVTT")]
    public async Task TranscriptionSupportsTextAndSubtitles(string format, string mediaType, string expected)
    {
        using var fixture = CreateSegmentFixture(Mock.Of<ITranslationService>());
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        using var content = CreateSegmentRequest(fixture, format, null);
        using var response = await client.PostAsync("/v1/transcribe/local-file", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        var body = await response.Content.ReadAsStringAsync();
        switch (format)
        {
            case "text":
                Assert.Equal(expected, body);
                break;
            case "vtt":
                Assert.StartsWith(expected, body);
                break;
            default:
                Assert.Contains(expected, body);
                break;
        }
    }

    [Theory]
    [InlineData("text")]
    [InlineData("srt")]
    [InlineData("vtt")]
    public async Task TranslationRunsForSubtitleSegmentsButNotPlainText(string format)
    {
        var translation = CreateTranslationMock();
        using var fixture = CreateSegmentFixture(translation.Object);
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        using var content = CreateSegmentRequest(fixture, format, "de");
        using var response = await client.PostAsync("/v1/transcribe/local-file", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Hallo", body);
        Assert.Contains("Welt", body);
        switch (format)
        {
            case "srt":
                Assert.Contains("00:00:00,200 --> 00:00:01,300", body);
                break;
            case "vtt":
                Assert.Contains("00:00:00.200 --> 00:00:01.300", body);
                break;
        }
        translation.Verify(service => service.TranslateSegmentsAsync(
            It.IsAny<IReadOnlyList<string>>(), "en", "de", null, It.IsAny<CancellationToken>()),
            format == "text" ? Times.Never() : Times.Once());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(-1, 1)]
    [InlineData(double.NaN, 1)]
    [InlineData(0, double.PositiveInfinity)]
    [InlineData(1.0001, 1.0002)]
    public async Task SubtitlesRejectInvalidTimestamps(double start, double end)
    {
        using var fixture = CreateSegmentFixture(Mock.Of<ITranslationService>(), [new PluginTranscriptionSegment("Invalid", start, end)]);
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        foreach (var format in new[] { "srt", "vtt" })
        {
            using var content = CreateSegmentRequest(fixture, format, null);
            using var response = await client.PostAsync("/v1/transcribe/local-file", content);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("Subtitle output requires valid segment timestamps.", json.RootElement.GetProperty("error").GetString());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SubtitlesRequireSegmentsInStartOrder(bool empty)
    {
        using var fixture = CreateSegmentFixture(Mock.Of<ITranslationService>(),
            empty ? [] : [new PluginTranscriptionSegment("First", 1, 2), new PluginTranscriptionSegment("Second", 0, 1)]);
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        using var content = CreateSegmentRequest(fixture, "srt", null);
        using var response = await client.PostAsync("/v1/transcribe/local-file", content);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task SubtitlesNormalizeTextAndRoundTimestamps()
    {
        using var fixture = CreateSegmentFixture(Mock.Of<ITranslationService>(),
            [new PluginTranscriptionSegment("  Héllo\r\nWorld\rAgain  ", 0.2006, 1.3006), new PluginTranscriptionSegment("Tick", 1.3006, 1.3016)]);
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        using var content = CreateSegmentRequest(fixture, "srt", null);
        using var response = await client.PostAsync("/v1/transcribe/local-file", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("00:00:00,201 --> 00:00:01,301", body);
        // A one-millisecond cue must not collapse to zero duration on export.
        Assert.Contains("00:00:01,301 --> 00:00:01,302", body);
        Assert.Contains("Héllo\nWorld\nAgain", body);
        Assert.DoesNotContain("\r", body);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, null)]
    [InlineData(true, false)]
    [InlineData(true, null)]
    public async Task ApplyCorrectionsControlsPipelineForUploadAndLocalFile(bool upload, bool? applyCorrections)
    {
        var pipeline = new Mock<IPostProcessingPipeline>();
        using var fixture = CreateSegmentFixture(Mock.Of<ITranslationService>(), pipeline: pipeline);
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        var payload = new Dictionary<string, object> { ["path"] = fixture.CreateSupportedAudioFile() };
        if (applyCorrections.HasValue)
        {
            payload["apply_corrections"] = applyCorrections.Value;
        }
        using HttpContent content = upload
            ? new ByteArrayContent([1, 2, 3])
            : new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        if (upload && applyCorrections.HasValue)
        {
            content.Headers.Add("x-apply-corrections", "false");
        }
        using var response = await client.PostAsync(upload ? "/v1/transcribe" : "/v1/transcribe/local-file", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        pipeline.Verify(service => service.ProcessAsync("Hello World",
            It.Is<PipelineOptions>(options => options.DictionaryCorrector != null == (applyCorrections ?? true)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LocalFileRejectsStringCorrectionBooleanWith400()
    {
        using var fixture = new ApiFixture();
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);
        using var content = new StringContent("""{"path":"/tmp/clip.wav","apply_corrections":"false"}""", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/transcribe/local-file", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Mock<ITranslationService> CreateTranslationMock()
    {
        var translation = new Mock<ITranslationService>(MockBehavior.Strict);
        translation.Setup(service => service.TranslateAsync(
                "Hello World", "en", "de", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Hallo Welt");
        translation.Setup(service => service.TranslateSegmentsAsync(
                It.IsAny<IReadOnlyList<string>>(), "en", "de", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(["Hallo", "Welt"]);
        return translation;
    }

    private static ApiFixture CreateSegmentFixture(
        ITranslationService translation,
        IReadOnlyList<PluginTranscriptionSegment>? segments = null,
        Mock<IPostProcessingPipeline>? pipeline = null)
    {
        var dictionary = new Mock<IDictionaryService>();
        dictionary.Setup(service => service.GetEnabledTerms()).Returns([]);
        pipeline ??= new Mock<IPostProcessingPipeline>();
        pipeline.Setup(service => service.ProcessAsync(
                "Hello World", It.IsAny<PipelineOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PostProcessingResult { Text = "Hello World" });
        return new ApiFixture(
            transcriptionEngine: new SegmentedTranscriptionEngine(segments),
            dictionary: dictionary.Object,
            pipeline: pipeline.Object,
            translation: translation,
            audioProbeResult: new ProcessRunOutcome(
                ProcessRunStatus.Exited, 0, [0, 1, 2, 3], [], ProcessOutputStatus.Complete, null));
    }

    private static StringContent CreateSegmentRequest(ApiFixture fixture, string format, string? target)
    {
        var path = fixture.CreateSupportedAudioFile();
        return new StringContent(
            JsonSerializer.Serialize(new { path, response_format = format, target_language = target }),
            Encoding.UTF8,
            "application/json");
    }

    private static void AssertSegments(JsonElement response, string firstText, string secondText)
    {
        var segments = response.GetProperty("segments").EnumerateArray().ToList();
        Assert.Equal(2, segments.Count);
        Assert.Equal(firstText, segments[0].GetProperty("text").GetString());
        Assert.Equal(0.2, segments[0].GetProperty("start").GetDouble());
        Assert.Equal(1.3, segments[0].GetProperty("end").GetDouble());
        Assert.Equal(0.1f, segments[0].GetProperty("no_speech_probability").GetSingle());
        Assert.Equal(secondText, segments[1].GetProperty("text").GetString());
        Assert.Equal(2.1, segments[1].GetProperty("start").GetDouble());
        Assert.Equal(3.8, segments[1].GetProperty("end").GetDouble());
        Assert.Equal(0.2f, segments[1].GetProperty("no_speech_probability").GetSingle());
    }

    private sealed class SegmentedTranscriptionEngine(IReadOnlyList<PluginTranscriptionSegment>? segments = null) : ITranscriptionEngineRole
    {
        public string PluginId => "test-segments";
        public string ProviderId => "test-segments";
        public string ProviderDisplayName => "Test segments";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels => [new("test", "Test")];
        public string SelectedModelId => "test";
        public bool SupportsTranslation => false;
        public void SelectModel(string modelId) { }
        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
        {
            return Task.FromResult(new PluginTranscriptionResult("Hello World", "en", 4)
            {
                Segments = segments ??
                [
                    new PluginTranscriptionSegment("Hello", 0.2, 1.3) { NoSpeechProbability = 0.1f },
                    new PluginTranscriptionSegment("World", 2.1, 3.8) { NoSpeechProbability = 0.2f },
                ],
            });
        }
    }

    [Fact]
    public async Task LocalFileEndpointClipsDictionaryPromptToEngineBudget()
    {
        var engine = new BudgetTranscriptionEngine();
        var dictionary = new Mock<IDictionaryService>();
        dictionary.Setup(service => service.GetEnabledTerms())
            .Returns(["too many words", "Alpha", "Beta", "Gamma"]);
        var pipeline = new Mock<IPostProcessingPipeline>();
        pipeline.Setup(service => service.ProcessAsync(It.IsAny<string>(), It.IsAny<PipelineOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PostProcessingResult { Text = "transcribed" });
        using var fixture = new ApiFixture(
            transcriptionEngine: engine,
            dictionary: dictionary.Object,
            pipeline: pipeline.Object,
            audioProbeResult: new ProcessRunOutcome(ProcessRunStatus.Exited, 0, [0, 1, 2, 3], [], ProcessOutputStatus.Complete, null));
        fixture.Start();
        using var client = fixture.CreateTcpClient(withBearer: true);
        var path = fixture.CreateSupportedAudioFile();
        using var content = new StringContent(
            $$"""{"path":{{JsonSerializer.Serialize(path)}},"language":"en"}""",
            Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/v1/transcribe/local-file", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Alpha, Beta", engine.LastPrompt);
        dictionary.Verify(service => service.GetEnabledTerms(), Times.Once);
    }

    private sealed class BudgetTranscriptionEngine : ITranscriptionEngineRole
    {
        public string PluginId => "test-budget";
        public string ProviderId => "test-budget";
        public string ProviderDisplayName => "Test budget";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels => [new("test", "Test")];
        public string SelectedModelId => "test";
        public bool SupportsTranslation => false;
        // ReSharper disable once ReturnTypeCanBeNotNullable -- matches the nullable interface contract.
        public DictionaryTermsBudget? DictionaryTermsBudget => new(MaxTerms: 2, MaxCharsPerTerm: 5, MaxWordsPerTerm: 1, MaxTotalChars: 11);
        public string? LastPrompt { get; private set; }
        public void SelectModel(string modelId) { }
        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct)
        {
            LastPrompt = prompt;
            return Task.FromResult(new PluginTranscriptionResult("transcribed", "en", 1));
        }
    }

    private const string Token =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public async Task TcpAndUnixEndpointsServeStatusWithBearer()
    {
        using var fixture = new ApiFixture();
        fixture.Start();
        using var tcp = fixture.CreateTcpClient(withBearer: true);
        using var uds = fixture.CreateUnixClient(withBearer: true);

        using var tcpResponse = await tcp.GetAsync("/v1/status");
        using var udsResponse = await uds.GetAsync("/v1/status");

        Assert.Equal(HttpStatusCode.OK, tcpResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, udsResponse.StatusCode);
        using var tcpBody = JsonDocument.Parse(await tcpResponse.Content.ReadAsStringAsync());
        using var udsBody = JsonDocument.Parse(await udsResponse.Content.ReadAsStringAsync());
        Assert.Equal("1.0", tcpBody.RootElement.GetProperty("api_version").GetString());
        Assert.Equal("1.0", udsBody.RootElement.GetProperty("api_version").GetString());

        using var discovery = JsonDocument.Parse(
            await File.ReadAllTextAsync(fixture.DiscoveryPath)
        );
        Assert.Equal(2, discovery.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(
            fixture.SocketPath,
            discovery.RootElement.GetProperty("socket_path").GetString()
        );
        Assert.Equal(Token, discovery.RootElement.GetProperty("token").GetString());
    }

    [Fact]
    public void UnixSocketIsOwnerReadWriteOnly()
    {
        using var fixture = new ApiFixture();
        fixture.Start();

#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(fixture.SocketPath)
        );
#pragma warning restore CA1416
    }

    [Fact]
    public async Task UnixPeerMiddlewareRejectsMismatchedUid()
    {
        using var fixture = new ApiFixture(_ => false);
        fixture.Start();
        using var tcp = fixture.CreateTcpClient(withBearer: true);
        using var uds = fixture.CreateUnixClient(withBearer: true);

        using var tcpResponse = await tcp.GetAsync("/v1/status");
        Assert.Equal(HttpStatusCode.OK, tcpResponse.StatusCode);

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await uds.GetAsync("/v1/status")
        );
    }

    [Fact]
    public async Task BearerIsRequiredOnBothTransports()
    {
        using var fixture = new ApiFixture();
        fixture.Start();
        using var tcp = fixture.CreateTcpClient(withBearer: false);
        using var uds = fixture.CreateUnixClient(withBearer: false);

        using var tcpResponse = await tcp.GetAsync("/v1/status");
        using var udsResponse = await uds.GetAsync("/v1/status");

        Assert.Equal(HttpStatusCode.Unauthorized, tcpResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, udsResponse.StatusCode);
        Assert.Equal("Bearer", tcpResponse.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal("Bearer", udsResponse.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public void EmbeddedHostDoesNotTakeOverProcessSignals()
    {
        using var fixture = new ApiFixture();
        fixture.Start();

        // ConsoleLifetime cancels SIGINT/SIGTERM and only stops this inner host,
        // which would leave the desktop app running after a logout or `kill`.
        Assert.IsNotType<ConsoleLifetime>(fixture.Service.HostLifetime);
    }

    [Fact]
    public async Task AmbientKestrelEndpointConfigurationIsIgnored()
    {
        var roguePort = ApiFixture.GetFreeTcpPort();
        Environment.SetEnvironmentVariable(
            "Kestrel__Endpoints__Rogue__Url",
            $"http://127.0.0.1:{roguePort}"
        );
        try
        {
            using var fixture = new ApiFixture();
            fixture.Start();

            using var tcp = fixture.CreateTcpClient(withBearer: true);
            using var tcpResponse = await tcp.GetAsync("/v1/status");
            Assert.Equal(HttpStatusCode.OK, tcpResponse.StatusCode);

            using var rogue = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Stream,
                ProtocolType.Tcp
            );
            var refused = await Assert.ThrowsAsync<SocketException>(async () =>
                await rogue.ConnectAsync(IPAddress.Loopback, roguePort)
            );
            Assert.Equal(SocketError.ConnectionRefused, refused.SocketErrorCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Kestrel__Endpoints__Rogue__Url", null);
        }
    }

    [Fact]
    public void UnpublishableDiscoveryFileIsReportedInStatus()
    {
        using var fixture = new ApiFixture();
        fixture.BlockDiscoveryDirectory();

        fixture.Service.ApplySettings();

        Assert.False(File.Exists(fixture.DiscoveryPath));
        Assert.Contains(
            "the CLI cannot connect",
            fixture.Service.StatusText,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void StopDeletesDiscoveryAndUnixSocket()
    {
        using var fixture = new ApiFixture();
        fixture.Start();
        Assert.True(File.Exists(fixture.DiscoveryPath));
        Assert.True(File.Exists(fixture.SocketPath));

        fixture.Disable();

        Assert.False(File.Exists(fixture.DiscoveryPath));
        Assert.False(File.Exists(fixture.SocketPath));
        Assert.Equal("Local API is disabled.", fixture.Service.StatusText);
    }

    [Fact]
    public async Task ThirdConcurrentRequestGetsPinned429Response()
    {
        using var entered = new CountdownEvent(HttpApiService.MaxConcurrentRequests);
        using var release = new ManualResetEventSlim();
        var history = new Mock<IHistoryService>();
        history
            .SetupGet(service => service.Records)
            // ReSharper disable AccessToDisposedClosure -- the finally block awaits both in-flight requests, so the callback is done before these leave scope.
            .Returns(() =>
            {
                entered.Signal();
                release.Wait(TimeSpan.FromSeconds(5));
                return [];
            });
        // ReSharper restore AccessToDisposedClosure

        using var fixture = new ApiFixture(history: history);
        fixture.Start();
        using var client = fixture.CreateTcpClient(withBearer: true);
        var first = client.GetAsync("/v1/history");
        var second = client.GetAsync("/v1/history");

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            using var third = await client.GetAsync("/v1/status");
            var body = await third.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
            Assert.Equal("1", third.Headers.RetryAfter?.ToString());
            using var json = JsonDocument.Parse(body);
            Assert.Equal(
                "Too many concurrent requests",
                json.RootElement.GetProperty("error").GetString()
            );
        }
        finally
        {
            release.Set();
            using var firstResponse = await first;
            using var secondResponse = await second;
        }
    }

    [Fact]
    public async Task Quiesce_WaitsForParkedRequest_RejectsNewConnections_ThenCompletes()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var history = new Mock<IHistoryService>();
        history
            .SetupGet(service => service.Records)
            // ReSharper disable AccessToDisposedClosure -- the finally block releases and observes the parked request before the gates leave scope.
            .Returns(() =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                return [];
            });
        // ReSharper restore AccessToDisposedClosure

        using var fixture = new ApiFixture(history: history);
        fixture.Start();
        using var client = fixture.CreateTcpClient(withBearer: true);
        var parked = client.GetAsync("/v1/history");

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            var quiesce = fixture.Service.QuiesceAsync(TimeSpan.FromSeconds(5));
            Assert.False(quiesce.IsCompleted);

            using var newClient = fixture.CreateTcpClient(withBearer: true);
            HttpResponseMessage? rejected = null;
            try
            {
                rejected = await newClient.GetAsync("/v1/status");
                Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
                Assert.Null(rejected.Headers.RetryAfter);
            }
            catch (HttpRequestException)
            {
                // Kestrel may close the listener before the post-close request connects.
            }
            finally
            {
                rejected?.Dispose();
            }

            Assert.False(quiesce.IsCompleted);
            release.Set();
            Assert.True(await quiesce);
            await ObserveRequestCompletionAsync(parked);
            Assert.False(File.Exists(fixture.SocketPath));
            Assert.False(File.Exists(fixture.DiscoveryPath));
        }
        finally
        {
            release.Set();
            await ObserveRequestCompletionAsync(parked);
        }
    }

    [Fact]
    public async Task ApplySettings_AfterQuiesce_DoesNotRestartListener()
    {
        using var fixture = new ApiFixture();
        fixture.Start();

        Assert.True(await fixture.Service.QuiesceAsync(TimeSpan.FromSeconds(5)));
        fixture.Service.ApplySettings();

        Assert.Null(fixture.Service.HostLifetime);
        Assert.False(File.Exists(fixture.SocketPath));
        Assert.False(File.Exists(fixture.DiscoveryPath));
        using var client = fixture.CreateTcpClient(withBearer: true);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await client.GetAsync("/v1/status")
        );
    }

    [Fact]
    public async Task Quiesce_AfterTimedOutDrain_RedrainsCompletedHandler()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var history = new Mock<IHistoryService>();
        history
            .SetupGet(service => service.Records)
            // ReSharper disable AccessToDisposedClosure -- the finally releases and observes the request before the gates leave scope.
            .Returns(() =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                return [];
            });
        // ReSharper restore AccessToDisposedClosure

        using var fixture = new ApiFixture(history: history);
        fixture.Start();
        using var client = fixture.CreateTcpClient(withBearer: true);
        var parked = client.GetAsync("/v1/history");

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(await fixture.Service.QuiesceAsync(TimeSpan.Zero));

            release.Set();
            await ObserveRequestCompletionAsync(parked);

            Assert.True(await fixture.Service.QuiesceAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.Set();
            await ObserveRequestCompletionAsync(parked);
        }
    }

    [Fact]
    public async Task LocalFileEndpointRejectsUnknownTaskAndFormat()
    {
        using var fixture = new ApiFixture();
        fixture.Start();
        var audioPath = fixture.CreateSupportedAudioFile();
        using var client = fixture.CreateTcpClient(withBearer: true);

        var taskError = await PostLocalFileForErrorAsync(
            client,
            $$"""{"path":{{JsonSerializer.Serialize(audioPath)}},"task":"transalte"}"""
        );
        var formatError = await PostLocalFileForErrorAsync(
            client,
            $$"""{"path":{{JsonSerializer.Serialize(audioPath)}},"response_format":"xml"}"""
        );

        Assert.Contains("Invalid task", taskError, StringComparison.Ordinal);
        Assert.Contains("Invalid response_format", formatError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalFileEndpointRejectsInvalidLanguageWithStableReason()
    {
        using var fixture = new ApiFixture();
        fixture.Start();
        var audioPath = fixture.CreateSupportedAudioFile();
        using var client = fixture.CreateTcpClient(withBearer: true);
        using var content = new StringContent(
            $$"""{"path":{{JsonSerializer.Serialize(audioPath)}},"language":"notalang"}""",
            Encoding.UTF8,
            "application/json"
        );

        using var response = await client.PostAsync("/v1/transcribe/local-file", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "invalid_language_selection",
            json.RootElement.GetProperty("reason").GetString()
        );
        Assert.Contains(
            "valid BCP-47 tag",
            json.RootElement.GetProperty("error").GetString(),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task LocalFileEndpointRejectedFallbackProbeReturnsStableReason()
    {
        using var fixture = new ApiFixture(
            audioProbeResult: new ProcessRunOutcome(
                ProcessRunStatus.Exited,
                1,
                [],
                [],
                ProcessOutputStatus.Complete,
                null
            )
        );
        fixture.Start();
        var audioPath = fixture.CreateAudioFile("extensionless-audio");
        using var client = fixture.CreateTcpClient(withBearer: true);
        using var content = new StringContent(
            $$"""{"path":{{JsonSerializer.Serialize(audioPath)}}}""",
            Encoding.UTF8,
            "application/json"
        );

        using var response = await client.PostAsync("/v1/transcribe/local-file", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "Unsupported format",
            json.RootElement.GetProperty("error").GetString()
        );
        Assert.Equal(
            "unsupported_audio_format",
            json.RootElement.GetProperty("reason").GetString()
        );
        var invocation = Assert.Single(fixture.ProcessRunner.SupervisorInvocations);
        Assert.Equal("ffmpeg", invocation.Command.FileName);
        Assert.Equal(audioPath, invocation.Command.Arguments[4]);
    }

    [Fact]
    public async Task LocalFileEndpointKnownExtensionWithoutFfmpegReturnsServiceUnavailable()
    {
        using var fixture = new ApiFixture(ffmpegAvailable: false);
        fixture.Start();
        var audioPath = fixture.CreateSupportedAudioFile();
        using var client = fixture.CreateTcpClient(withBearer: true);
        using var content = new StringContent(
            $$"""{"path":{{JsonSerializer.Serialize(audioPath)}}}""",
            Encoding.UTF8,
            "application/json"
        );

        using var response = await client.PostAsync("/v1/transcribe/local-file", content);

        // A recognized extension passes the format gate without ffmpeg; the
        // conversion requires it, so the endpoint answers 503 with a stable
        // reason instead of the loader's failure surfacing as a generic 500.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "audio_importer_unavailable",
            json.RootElement.GetProperty("reason").GetString()
        );
    }

    [Fact]
    public async Task LocalFileEndpointProbeTimeoutReturnsServiceUnavailable()
    {
        using var fixture = new ApiFixture(
            audioProbeResult: new ProcessRunOutcome(
                ProcessRunStatus.TimedOut,
                null,
                [],
                [],
                ProcessOutputStatus.Complete,
                null
            )
        );
        fixture.Start();
        var audioPath = fixture.CreateAudioFile("extensionless-audio");
        using var client = fixture.CreateTcpClient(withBearer: true);
        using var content = new StringContent(
            $$"""{"path":{{JsonSerializer.Serialize(audioPath)}}}""",
            Encoding.UTF8,
            "application/json"
        );

        using var response = await client.PostAsync("/v1/transcribe/local-file", content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "Audio probe timed out",
            json.RootElement.GetProperty("error").GetString()
        );
        Assert.Equal(
            "audio_probe_timeout",
            json.RootElement.GetProperty("reason").GetString()
        );
    }

    [Fact]
    public async Task LocalFileEndpointRejectsFifoWithoutInvokingFfmpeg()
    {
        using var fixture = new ApiFixture();
        fixture.Start();
        // A FIFO passes File.Exists, and ffmpeg opening it would block until a
        // writer appears — pinning a dispatcher slot for the client's lifetime.
        var fifoPath = fixture.CreateFifo("pipe.wav");
        using var client = fixture.CreateTcpClient(withBearer: true);
        using var content = new StringContent(
            $$"""{"path":{{JsonSerializer.Serialize(fifoPath)}}}""",
            Encoding.UTF8,
            "application/json"
        );

        using var response = await client.PostAsync("/v1/transcribe/local-file", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "Not a regular file",
            json.RootElement.GetProperty("error").GetString()
        );
        Assert.Equal(
            "not_a_regular_file",
            json.RootElement.GetProperty("reason").GetString()
        );
        Assert.Empty(fixture.ProcessRunner.SupervisorInvocations);
    }

    [Fact]
    public async Task LocalFileEndpointConversionTimeoutReturnsServiceUnavailable()
    {
        using var fixture = new ApiFixture(
            audioProbeResult: new ProcessRunOutcome(
                ProcessRunStatus.TimedOut,
                null,
                [],
                [],
                ProcessOutputStatus.Complete,
                null
            )
        );
        fixture.Start();
        // The recognized .wav extension skips the probe, so the staged TimedOut
        // outcome is consumed by the conversion run.
        var audioPath = fixture.CreateSupportedAudioFile();
        using var client = fixture.CreateTcpClient(withBearer: true);
        using var content = new StringContent(
            $$"""{"path":{{JsonSerializer.Serialize(audioPath)}}}""",
            Encoding.UTF8,
            "application/json"
        );

        using var response = await client.PostAsync("/v1/transcribe/local-file", content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "Audio conversion timed out",
            json.RootElement.GetProperty("error").GetString()
        );
        Assert.Equal(
            "audio_conversion_timeout",
            json.RootElement.GetProperty("reason").GetString()
        );
    }

    [Fact]
    public async Task LocalFileEndpointSuccessfulFallbackProbePassesFormatGate()
    {
        using var fixture = new ApiFixture(
            audioProbeResult: new ProcessRunOutcome(
                ProcessRunStatus.Exited,
                0,
                [],
                [],
                ProcessOutputStatus.Complete,
                null
            )
        );
        fixture.Start();
        var audioPath = fixture.CreateAudioFile("extensionless-audio");
        using var client = fixture.CreateTcpClient(withBearer: true);

        var error = await PostLocalFileForErrorAsync(
            client,
            $$"""{"path":{{JsonSerializer.Serialize(audioPath)}},"task":"invalid"}"""
        );

        Assert.Contains("Invalid task", error, StringComparison.Ordinal);
        var invocation = Assert.Single(fixture.ProcessRunner.SupervisorInvocations);
        Assert.Equal("ffmpeg", invocation.Command.FileName);
    }

    [Fact]
    public async Task LocalFileEndpointRecognizedExtensionDoesNotProbe()
    {
        using var fixture = new ApiFixture();
        fixture.Start();
        var audioPath = fixture.CreateSupportedAudioFile();
        using var client = fixture.CreateTcpClient(withBearer: true);

        var error = await PostLocalFileForErrorAsync(
            client,
            $$"""{"path":{{JsonSerializer.Serialize(audioPath)}},"task":"invalid"}"""
        );

        Assert.Contains("Invalid task", error, StringComparison.Ordinal);
        Assert.Empty(fixture.ProcessRunner.SupervisorInvocations);
    }

    [Fact]
    public async Task ProfileToggleApi_EnableWithCollidingHotkey_Returns409AndLeavesDisabled()
    {
        using var fixture = new ApiFixture();
        fixture.Profiles.AddProfile(
            new Profile
            {
                Id = "disabled-profile",
                Name = "Disabled profile",
                IsEnabled = false,
                HotkeyData = "Alt+F8",
            }
        );
        fixture.PromptActions.AddAction(
            new PromptAction
            {
                Id = "enabled-action",
                Name = "Enabled action",
                SystemPrompt = "x",
                HotkeyKey = "Alt+F8",
            }
        );
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);

        using var response = await client.PutAsync(
            "/v1/profiles/toggle?id=disabled-profile",
            null
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("hotkey-collision", json.RootElement.GetProperty("reason").GetString());
        Assert.Equal(
            "Profile hotkey cannot be enabled because it conflicts with an enabled shortcut.",
            json.RootElement.GetProperty("error").GetString()
        );
        Assert.False(Assert.Single(fixture.Profiles.Profiles).IsEnabled);
    }

    [Fact]
    public async Task ProfileToggleApi_DisableDoesNotRequireHotkeyValidation()
    {
        using var fixture = new ApiFixture();
        fixture.Profiles.AddProfile(
            new Profile
            {
                Id = "enabled-profile",
                Name = "Enabled profile",
                HotkeyData = "Ctrl+NoSuchKey",
                HotkeyBehavior = ProfileHotkeyBehavior.ProcessSelectedText,
                PromptActionId = "missing-action",
            }
        );
        fixture.Start();
        using var client = fixture.CreateUnixClient(withBearer: true);

        using var response = await client.PutAsync(
            "/v1/profiles/toggle?id=enabled-profile",
            null
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("is_enabled").GetBoolean());
        Assert.False(Assert.Single(fixture.Profiles.Profiles).IsEnabled);
    }

    private static async Task<string> PostLocalFileForErrorAsync(HttpClient client, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/transcribe/local-file", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetString()!;
    }

    private static async Task ObserveRequestCompletionAsync(
        Task<HttpResponseMessage> request
    )
    {
        try
        {
            using var response = await request;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Quiesce cancels the admitted request's linked RequestAborted token.
        }
    }

    [Fact]
    public async Task HistoryListing_SkipsRecordsThatDidNotSucceed()
    {
        var history = new Mock<IHistoryService>();
        history
            .SetupGet(service => service.Records)
            .Returns(
            [
                new TranscriptionRecord
                {
                    Id = "ok", Timestamp = DateTime.UtcNow, RawText = "raw", FinalText = "kept final",
                },
                new TranscriptionRecord
                {
                    Id = "failed", Timestamp = DateTime.UtcNow, RawText = "", FinalText = "",
                    Status = TranscriptionRecordStatus.TranscriptionFailed, FailureMessage = "dropped failure",
                },
            ]);

        using var fixture = new ApiFixture(history: history);
        fixture.Start();
        using var client = fixture.CreateTcpClient(withBearer: true);

        using var response = await client.GetAsync("/v1/history");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("total").GetInt32());
        var record = Assert.Single(json.RootElement.GetProperty("records").EnumerateArray().ToList());
        Assert.Equal("ok", record.GetProperty("id").GetString());
    }

    private sealed class ApiFixture : IDisposable
    {
        private readonly string? _originalConfigHome =
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        private readonly string _tempDirectory =
            TestPaths.CreateTempDirectory("TypeWhisper.HttpApiUnixSocketTests");
        private readonly HotkeyService _hotkeys = TestShortcutBackend.CreateHotkeyService();
        
        private readonly DictationSessionResultStore _sessionResults = new();
        private AppSettings _current;

        internal ApiFixture(
            Func<Socket, bool>? validateUnixPeer = null,
            Mock<IHistoryService>? history = null,
            ProcessRunOutcome? audioProbeResult = null,
            bool ffmpegAvailable = true,
            ITranscriptionEngineRole? transcriptionEngine = null,
            IDictionaryService? dictionary = null,
            IPostProcessingPipeline? pipeline = null,
            ITranslationService? translation = null,
            IReadOnlyList<ITranscriptionEngineRole>? transcriptionEngines = null
        )
        {
            Port = GetFreeTcpPort();
            SocketPath = Path.Join(_tempDirectory, "api.sock");
            DiscoveryPath = Path.Join(
                _tempDirectory,
                "config",
                "typewhisper",
                "api-discovery.json"
            );
            Environment.SetEnvironmentVariable(
                "XDG_CONFIG_HOME",
                Path.Join(_tempDirectory, "config")
            );

            _current = new AppSettings
            {
                SelectedModelId = transcriptionEngine is null ? null : ModelManagerService.GetPluginModelId(transcriptionEngine.PluginId, "test"),
                ApiServerEnabled = true,
                ApiServerPort = Port,
                ApiServerBearerToken = Token,
            };
            Settings = new Mock<ISettingsService>();
            Settings.SetupGet(service => service.Current).Returns(() => _current);
            Settings
                .Setup(service => service.Update(It.IsAny<Func<AppSettings, AppSettings>>()))
                .Returns((Func<AppSettings, AppSettings> mutate) =>
                {
                    _current = mutate(_current);
                    return _current;
                });

            Models = new ModelManagerService(
                TestPluginManagerFactory.Create(transcriptionEngines: transcriptionEngines ?? (transcriptionEngine is null ? null : [transcriptionEngine])),
                Settings.Object
            );
            var historyService = history ?? new Mock<IHistoryService>();
            Profiles = new ProfileService(Path.Join(_tempDirectory, "profiles.json"));
            PromptActions = new PromptActionService(
                Path.Join(_tempDirectory, "prompt-actions.json")
            );
            ProcessRunner = new FakeProcessRunner
            {
                SupervisorDefault = audioProbeResult,
            };
            var commands = new SystemCommandAvailabilityService(ProcessRunner);
            // The content probe short-circuits to "unsupported" when ffmpeg is
            // absent, so probe-path tests need it declared available; the
            // importer-unavailable tests opt out.
            commands.RaiseSnapshotChangedForTests(
                commands.GetSnapshot() with { HasFfmpeg = ffmpegAvailable }
            );
            ProcessRunner.Invocations.Clear();
            ProcessRunner.SupervisorInvocations.Clear();
            var audioFiles = new AudioFileService(commands, ProcessRunner);
            Service = new HttpApiService(
                Models,
                Settings.Object,
                audioFiles,
                historyService.Object,
                Profiles,
                PromptActions,
                _hotkeys,
                dictionary!,
                null!,
                pipeline!,
                translation!,
                null!,
                _sessionResults,
                new ApiDiscoveryFile(),
                Path.Join(_tempDirectory, "secret-protection.key"),
                SocketPath,
                validateUnixPeer
            );
        }

        internal ModelManagerService Models { get; }

        internal int Port { get; }

        internal string SocketPath { get; }

        internal string DiscoveryPath { get; }

        internal ProfileService Profiles { get; }

        internal PromptActionService PromptActions { get; }

        internal FakeProcessRunner ProcessRunner { get; }

        private Mock<ISettingsService> Settings { get; }

        internal HttpApiService Service { get; }

        internal void Start()
        {
            Service.ApplySettings();
            Assert.StartsWith(
                "Local API is running",
                Service.StatusText,
                StringComparison.Ordinal
            );
        }

        /// <summary>Creates an empty file with a supported audio extension so the handler reaches option validation.</summary>
        internal string CreateSupportedAudioFile()
        {
            return CreateAudioFile("clip.wav");
        }

        internal string CreateAudioFile(string fileName)
        {
            var path = Path.Join(_tempDirectory, fileName);
            File.WriteAllBytes(path, []);
            return path;
        }

        internal string CreateFifo(string fileName)
        {
            var path = Path.Join(_tempDirectory, fileName);
            using var mkfifo = System.Diagnostics.Process.Start("mkfifo", [path]);
            mkfifo.WaitForExit();
            Assert.Equal(0, mkfifo.ExitCode);
            return path;
        }

        /// <summary>Occupies the discovery directory's path with a file so the write fails.</summary>
        internal void BlockDiscoveryDirectory()
        {
            var directory = Path.GetDirectoryName(DiscoveryPath)!;
            Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
            File.WriteAllText(directory, "not a directory");
        }

        internal void Disable()
        {
            _current = _current with { ApiServerEnabled = false };
            Service.ApplySettings();
        }

        internal HttpClient CreateTcpClient(bool withBearer)
        {
            var client = new HttpClient
            {
                BaseAddress = new Uri($"http://localhost:{Port}"),
                Timeout = TimeSpan.FromSeconds(3),
            };
            AddBearer(client, withBearer);
            return client;
        }

        internal HttpClient CreateUnixClient(bool withBearer)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(
                        AddressFamily.Unix,
                        SocketType.Stream,
                        ProtocolType.Unspecified
                    );
                    try
                    {
                        await socket.ConnectAsync(
                            new UnixDomainSocketEndPoint(SocketPath),
                            ct
                        );
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost"),
                Timeout = TimeSpan.FromSeconds(3),
            };
            AddBearer(client, withBearer);
            return client;
        }

        public void Dispose()
        {
            Service.Dispose();
            _sessionResults.Dispose();
            Models.Dispose();
            _hotkeys.Dispose();
            Environment.SetEnvironmentVariable(
                "XDG_CONFIG_HOME",
                _originalConfigHome
            );
            TestPaths.DeleteDirectory(_tempDirectory);
        }

        private static void AddBearer(HttpClient client, bool withBearer)
        {
            if (withBearer)
            {
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", Token);
            }
        }

        internal static int GetFreeTcpPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
