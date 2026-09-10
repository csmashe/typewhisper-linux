using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Claude;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

// The CapturingHandler lambdas assert on the outgoing request (method, URI,
// headers, body) and return a canned response. ReSharper reads xUnit asserts
// as precondition checks and concludes those parameters are only validated,
// never used — but asserting on the request is exactly what these tests
// verify, so the inspection is a false positive here.
// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local

namespace TypeWhisper.PluginSystem.Tests;

// C7 Phase 5: Claude is bespoke Anthropic Messages SSE (content_block_delta →
// delta.text, no [DONE] sentinel, error frames after a 200). These exercise the
// self-gated ProcessStreamingAsync + the reflection-free frame parsers.
public sealed class ClaudePluginTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestFailure_LogsStatusAndReasonWithoutBody(bool streaming)
    {
        const string body = "private transcript and remembered context";
        using var client = new HttpClient(new CapturingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                ReasonPhrase = "Bad Request", Content = new StringContent(body),
            }));
        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "key" } };
        using var sut = new ClaudePlugin(client);
        await sut.ActivateAsync(host);
        var error = await Assert.ThrowsAsync<PluginRequestException>(async () =>
        {
            if (streaming)
            {
                await foreach (var _ in sut.ProcessStreamingAsync("system", "user", "claude", CancellationToken.None)) { }
            }
            else
                await sut.ProcessAsync("system", "user", "claude", CancellationToken.None);
        });
        Assert.Equal("Anthropic API returned 400: Bad Request", error.Message);
        Assert.DoesNotContain(body, error.Message);
        Assert.Contains("Anthropic API error 400: Bad Request", host.Messages);
        Assert.All(host.Messages, message => Assert.DoesNotContain(body, message));
    }

    [Fact]
    public Task RequestFailures_AreClassified() =>
        ProviderFailureAssertions.VerifyAsync<ClaudePlugin>();


    [Fact]
    public async Task ProcessAsync_ScalesMaxTokensAndRejectsMaxTokensStopReason()
    {
        var input = string.Concat(Enumerable.Repeat("dictated input ", 1000));
        using var client = new HttpClient(new CapturingHandler((_, body) =>
        {
            using var doc = JsonDocument.Parse(body!);
            Assert.Equal(LlmOutputTokenBudget.Calculate("system", input), doc.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.True(doc.RootElement.GetProperty("max_tokens").GetInt32() > 2048);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                """{"stop_reason":"max_tokens","content":[{"type":"text","text":"partial"}]}""") };
        }));
        var sut = new ClaudePlugin(client);
        await sut.ActivateAsync(new TestPluginHostServices { Secrets = { ["api-key"] = "key" } });
        var ex = await Assert.ThrowsAsync<PluginRequestException>(() => sut.ProcessAsync("system", input, "claude", CancellationToken.None));
        Assert.Equal(PluginRequestFailureKind.OutputTruncated, ex.FailureKind);
    }

    [Fact]
    public async Task ProcessStreamingAsync_MaxTokensStopReason_ThrowsTruncation()
    {
        const string sse = "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}\n\n"
            + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"max_tokens\"}}\n\n"
            + "data: {\"type\":\"message_stop\"}\n\n";
        using var client = new HttpClient(new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") }));
        var sut = new ClaudePlugin(client);
        await sut.ActivateAsync(new TestPluginHostServices { Secrets = { ["api-key"] = "key" } });
        var chunks = new List<string>();
        var ex = await Assert.ThrowsAsync<PluginRequestException>(async () =>
        {
            await foreach (var chunk in sut.ProcessStreamingAsync("system", "user", "claude", CancellationToken.None))
                chunks.Add(chunk);
        });
        Assert.Equal(PluginRequestFailureKind.OutputTruncated, ex.FailureKind);
        Assert.Equal(["partial"], chunks);
    }

    [Fact]
    public async Task ProcessStreamingAsync_StreamsContentBlockDeltasInOrder()
    {
        string? capturedBody = null;
        var sse = string.Join(
            "\n",
            "event: message_start",
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg\"}}",
            "",
            "event: content_block_start",
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
            "",
            "event: content_block_delta",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hel\"}}",
            "",
            "event: content_block_delta",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"lo\"}}",
            "",
            "event: content_block_stop",
            "data: {\"type\":\"content_block_stop\",\"index\":0}",
            "",
            "event: message_stop",
            "data: {\"type\":\"message_stop\"}",
            "",
            "");
        var handler = new CapturingHandler((request, body) =>
        {
            capturedBody = body;
            Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-ant-test" } };
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new ClaudePlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (var chunk in sut.ProcessStreamingAsync(
            "system", "user", "claude-haiku-4-5-20251001", CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(["Hel", "lo"], chunks);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("claude-haiku-4-5-20251001", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ProcessStreamingAsync_ToggleOff_YieldsSingleBulkChunk()
    {
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"content":[{"type":"text","text":"bulk"}]}""",
                Encoding.UTF8, "application/json"),
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-ant-test" } };
        host.SetSetting("streamResponses", false);
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new ClaudePlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        await foreach (var chunk in sut.ProcessStreamingAsync(
            "system", "user", "model", CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Single(chunks);
        Assert.Equal("bulk", chunks[0]);
    }

    [Fact]
    public async Task ProcessStreamingAsync_ThrowsOnErrorFrameAfterPartialDeltas()
    {
        // A Messages stream returns 200 then can fail mid-flight via an
        // `event: error` frame. The reader must throw so LlmStreamPump faults and
        // the caller falls back to batch, rather than committing the partial.
        var sse = string.Join(
            "\n",
            "event: content_block_delta",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hel\"}}",
            "",
            "event: error",
            "data: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}",
            "",
            "");
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-ant-test" } };
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new ClaudePlugin(httpClient);
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

        Assert.Equal(["Hel"], chunks);
        Assert.Equal("Overloaded", ex.Message);
    }

    [Fact]
    public async Task ProcessStreamingAsync_ThrowsWhenEofPrecedesMessageStop()
    {
        var sse = string.Join(
            "\n",
            "event: content_block_delta",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}",
            "",
            "");
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });

        var host = new TestPluginHostServices { Secrets = { ["api-key"] = "sk-ant-test" } };
        using var httpClient = new HttpClient(handler);
        httpClient.Timeout = TimeSpan.FromSeconds(5);
        var sut = new ClaudePlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        var ex = await Assert.ThrowsAsync<IncompleteSseStreamException>(async () =>
        {
            await foreach (var chunk in sut.ProcessStreamingAsync(
                "system", "user", "model", CancellationToken.None))
            {
                chunks.Add(chunk);
            }
        });

        Assert.Equal(["partial"], chunks);
        Assert.Equal("Anthropic stream", ex.StreamName);
        Assert.Equal("a message_stop event", ex.ExpectedTerminal);
    }

    [Theory]
    [InlineData("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hi"}}""", "hi")]
    [InlineData("""{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{"}}""", null)]
    [InlineData("""{"type":"message_start","message":{"id":"msg"}}""", null)]
    [InlineData("""{"type":"message_stop"}""", null)]
    [InlineData("not json", null)]
    public void ParseStreamDelta_ExtractsOnlyTextDeltaFrames(string payload, string? expected)
    {
        Assert.Equal(expected, ClaudePlugin.ParseStreamDelta(payload));
    }

    [Theory]
    [InlineData("""{"type":"error","error":{"type":"overloaded_error","message":"boom"}}""", "boom")]
    [InlineData("""{"type":"error","error":{"type":"overloaded_error"}}""", "Anthropic streaming error.")]
    [InlineData("""{"type":"content_block_delta","delta":{"type":"text_delta","text":"hi"}}""", null)]
    [InlineData("""{"type":"message_stop"}""", null)]
    [InlineData("not json", null)]
    public void ParseStreamError_DetectsErrorFrames(string payload, string? expected)
    {
        Assert.Equal(expected, ClaudePlugin.ParseStreamError(payload));
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
        private static readonly JsonSerializerOptions s_jsonOptions = new()
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
            Task.FromResult(Secrets.GetValueOrDefault(key));

        public Task DeleteSecretAsync(string key)
        {
            Secrets.Remove(key);
            return Task.CompletedTask;
        }

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value) ? value.Deserialize<T>(s_jsonOptions) : default;

        public void SetSetting<T>(string key, T value) =>
            _settings[key] = JsonSerializer.SerializeToElement(value, s_jsonOptions);

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public List<string> Messages { get; } = [];
        public void Log(PluginLogLevel level, string message) => Messages.Add(message);
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
