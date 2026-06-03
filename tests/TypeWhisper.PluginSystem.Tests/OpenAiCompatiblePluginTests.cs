using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.OpenAiCompatible;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class OpenAiCompatiblePluginTests
{
    [Fact]
    public async Task ProcessStreamingAsync_StreamsDeltas_AgainstOpenAiCompatibleServer()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}",
            "",
            "data: [DONE]",
            "");
        var handler = new CapturingHandler((request, body) =>
        {
            capturedRequest = request;
            capturedBody = body;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
        });

        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedLlmModel", "llama3");
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (var chunk in sut.ProcessStreamingAsync("sys", "user", "llama3", CancellationToken.None))
            chunks.Add(chunk);

        Assert.Equal(new[] { "Hel", "lo" }, chunks);
        Assert.Equal("http://localhost:11434/v1/chat/completions", capturedRequest?.RequestUri?.ToString());
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("llama3", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ProcessStreamingAsync_ToggleOff_YieldsSingleBulkChunk()
    {
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"choices":[{"message":{"content":"bulk"}}]}""",
                Encoding.UTF8, "application/json")
        });

        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedLlmModel", "llama3");
        host.SetSetting("streamResponses", false);
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (var chunk in sut.ProcessStreamingAsync("sys", "user", "llama3", CancellationToken.None))
            chunks.Add(chunk);

        Assert.Single(chunks);
        Assert.Equal("bulk", chunks[0]);
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
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
            PropertyNameCaseInsensitive = true
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
        public void Publish<T>(T pluginEvent) where T : TypeWhisper.PluginSDK.Models.PluginEvent { }
        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : TypeWhisper.PluginSDK.Models.PluginEvent =>
            new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
