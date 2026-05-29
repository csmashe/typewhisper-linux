using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Cerebras;
using TypeWhisper.Plugin.Cohere;
using TypeWhisper.Plugin.Fireworks;
using TypeWhisper.Plugin.Gemini;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

// C7 Phase 4: the four shared-helper LLM plugins that gained a self-gated
// ProcessStreamingAsync routing through OpenAiChatHelper.SendChatCompletionStreamingAsync.
// They are structurally identical (toggle on → SSE stream, off → one bulk yield),
// so the streaming behavior is exercised through a shared driver here.
public sealed class SharedHelperStreamingCohortTests
{
    [Fact]
    public async Task Cerebras_ProcessStreamingAsync_StreamsDeltas()
    {
        var (chunks, body, url) = await StreamAsync(h => new CerebrasPlugin(h), "api-key", "llama-test");

        Assert.Equal(new[] { "Hel", "lo" }, chunks);
        Assert.Equal("https://api.cerebras.ai/v1/chat/completions", url);
        AssertStreamBody(body, "llama-test");
    }

    [Fact]
    public async Task Cohere_ProcessStreamingAsync_StreamsDeltas()
    {
        var (chunks, body, url) = await StreamAsync(h => new CoherePlugin(h), "apiKey", "command-test");

        Assert.Equal(new[] { "Hel", "lo" }, chunks);
        Assert.Equal("https://api.cohere.com/compatibility/v1/chat/completions", url);
        AssertStreamBody(body, "command-test");
    }

    [Fact]
    public async Task Fireworks_ProcessStreamingAsync_StreamsDeltas()
    {
        var (chunks, body, url) = await StreamAsync(h => new FireworksPlugin(h), "apiKey", "fw-test");

        Assert.Equal(new[] { "Hel", "lo" }, chunks);
        Assert.Equal("https://api.fireworks.ai/v1/chat/completions", url);
        AssertStreamBody(body, "fw-test");
    }

    [Fact]
    public async Task Gemini_ProcessStreamingAsync_StreamsDeltas()
    {
        var (chunks, body, url) = await StreamAsync(h => new GeminiPlugin(h), "api-key", "gemini-test");

        Assert.Equal(new[] { "Hel", "lo" }, chunks);
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/openai/v1/chat/completions",
            url);
        AssertStreamBody(body, "gemini-test");
    }

    [Fact]
    public async Task Cerebras_ProcessStreamingAsync_ToggleOff_YieldsSingleBulkChunk() =>
        await AssertToggleOffYieldsBulk(h => new CerebrasPlugin(h), "api-key");

    [Fact]
    public async Task Cohere_ProcessStreamingAsync_ToggleOff_YieldsSingleBulkChunk() =>
        await AssertToggleOffYieldsBulk(h => new CoherePlugin(h), "apiKey");

    [Fact]
    public async Task Fireworks_ProcessStreamingAsync_ToggleOff_YieldsSingleBulkChunk() =>
        await AssertToggleOffYieldsBulk(h => new FireworksPlugin(h), "apiKey");

    [Fact]
    public async Task Gemini_ProcessStreamingAsync_ToggleOff_YieldsSingleBulkChunk() =>
        await AssertToggleOffYieldsBulk(h => new GeminiPlugin(h), "api-key");

    [Fact]
    public async Task ProcessStreamingAsync_ThrowsOnErrorFrameAfterPartialDeltas()
    {
        // A chat-completions stream returns 200 then can fail mid-flight via a
        // top-level `error` frame. The reader must throw so LlmStreamPump faults
        // and the caller falls back to batch, rather than committing the partial
        // deltas seen so far as a successful result. Exercised through Cerebras;
        // the path is the shared OpenAiChatHelper, so it covers the whole cohort.
        var sse = string.Join(
            "\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}",
            "",
            "data: {\"error\":{\"message\":\"server had an error\",\"type\":\"server_error\"}}",
            "");
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });

        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "test-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var sut = new CerebrasPlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var chunk in sut.ProcessStreamingAsync(
                "system", "user", "model", CancellationToken.None))
            {
                chunks.Add(chunk);
            }
        });

        Assert.Equal(new[] { "Hel" }, chunks);
        Assert.Equal("server had an error", ex.Message);
    }

    private static void AssertStreamBody(string? body, string expectedModel)
    {
        using var doc = JsonDocument.Parse(body!);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(expectedModel, doc.RootElement.GetProperty("model").GetString());
    }

    private static async Task<(List<string> Chunks, string? Body, string? Url)> StreamAsync(
        Func<HttpClient, ITypeWhisperPlugin> factory,
        string secretKey,
        string model)
    {
        string? capturedBody = null;
        string? capturedUrl = null;
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
            capturedUrl = request.RequestUri?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
        });

        var host = new TestPluginHostServices();
        host.Secrets[secretKey] = "test-key";
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var plugin = factory(httpClient);
        await plugin.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (
            var chunk in ((ILlmProviderPlugin)plugin).ProcessStreamingAsync(
                "system", "user", model, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        return (chunks, capturedBody, capturedUrl);
    }

    private static async Task AssertToggleOffYieldsBulk(
        Func<HttpClient, ITypeWhisperPlugin> factory,
        string secretKey)
    {
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"choices":[{"message":{"content":"bulk"}}]}""",
                Encoding.UTF8, "application/json"),
        });

        var host = new TestPluginHostServices();
        host.Secrets[secretKey] = "test-key";
        host.SetSetting("streamResponses", false);
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var plugin = factory(httpClient);
        await plugin.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (
            var chunk in ((ILlmProviderPlugin)plugin).ProcessStreamingAsync(
                "system", "user", "model", CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Single(chunks);
        Assert.Equal("bulk", chunks[0]);
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
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly Dictionary<string, JsonElement> _settings = [];
        public Dictionary<string, string?> Secrets { get; } = [];

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
            _settings.TryGetValue(key, out var value) ? value.Deserialize<T>(JsonOptions) : default;

        public void SetSetting<T>(string key, T value) =>
            _settings[key] = JsonSerializer.SerializeToElement(value, JsonOptions);

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() { }
        public IPluginLocalization Localization { get; } = new TestPluginLocalization();
    }

    private sealed class TestPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(key, args);
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
