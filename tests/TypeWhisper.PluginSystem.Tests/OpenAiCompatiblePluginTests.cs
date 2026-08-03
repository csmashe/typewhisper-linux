using System.Net;
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
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
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

        Assert.Equal(["Hel", "lo"], chunks);
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
                Encoding.UTF8, "application/json"),
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

    private static HttpClient ModelsClient() =>
        new(new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"data":[{"id":"m1"},{"id":"m2"}]}""",
                Encoding.UTF8, "application/json"),
        }));

    private static PluginCollectionItem ProfileItem(
        string name, string baseUrl, string? apiKey = null,
        string? llmModel = null, string? id = "") =>
        new(new Dictionary<string, string?>
        {
            ["name"] = name,
            ["baseUrl"] = baseUrl,
            ["api-key"] = apiKey,
            ["selectedLlmModel"] = llmModel,
            ["__id"] = id,
        });

    [Fact]
    public async Task SetItemsAsync_AddsProfile_ExposesRoleWithProfileSelectionId()
    {
        var host = new TestPluginHostServices();
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("Local Ollama", "http://localhost:11434", apiKey: "secret123", llmModel: "m1")]);

        Assert.True(result.IsSuccess);

        var llm = Assert.Single(sut.AdditionalLlmProviders);
        Assert.Equal("Local Ollama", llm.ProviderName);
        Assert.True(llm.IsAvailable);

        var selectionId = llm.GetLlmSelectionId();
        Assert.StartsWith("openai-compatible-", selectionId);
        Assert.DoesNotContain(":", selectionId); // must round-trip in plugin:{id}:{model}

        var engine = Assert.Single(sut.AdditionalTranscriptionEngines);
        Assert.Equal(selectionId, engine.GetTranscriptionSelectionId());
        Assert.Equal(sut.PluginId, engine.PluginId); // role keeps the owner's plugin id
    }

    [Fact]
    public async Task GetItemsAsync_DoesNotEchoApiKey()
    {
        var host = new TestPluginHostServices();
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("Local Ollama", "http://localhost:11434", apiKey: "secret123")]);

        var item = Assert.Single(await sut.GetItemsAsync("profiles"));

        Assert.Null(item.Values["api-key"]);
        Assert.Equal("Local Ollama", item.Values["name"]);
        Assert.Equal("http://localhost:11434", item.Values["baseUrl"]);
    }

    [Fact]
    public async Task AdditionalProfiles_PersistAndReloadWithSecret()
    {
        var host = new TestPluginHostServices();
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("P1", "http://localhost:11434", apiKey: "k", llmModel: "m1")]);

        // A fresh instance over the same host (same settings + secrets) reloads them.
        var reloaded = new OpenAiCompatiblePlugin(httpClient);
        await reloaded.ActivateAsync(host);

        var llm = Assert.Single(reloaded.AdditionalLlmProviders);
        Assert.Equal("P1", llm.ProviderName);
        Assert.True(llm.IsAvailable);
    }

    [Fact]
    public async Task SetItemsAsync_RejectsInvalidBaseUrl()
    {
        var host = new TestPluginHostServices();
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        var result = await sut.SetItemsAsync("profiles", [ProfileItem("Bad", "not-a-url")]);

        Assert.False(result.IsSuccess);
        Assert.Empty(sut.AdditionalLlmProviders);
    }

    [Fact]
    public async Task SetItemsAsync_EndpointChange_RefetchesCatalog()
    {
        // /v1/models returns different models depending on the server port, so we can
        // tell whether the catalog was refetched after the base URL changed.
        var handler = new CapturingHandler((request, _) =>
        {
            var models = request.RequestUri!.Port == 11434
                ? """{"data":[{"id":"m1"},{"id":"m2"}]}"""
                : """{"data":[{"id":"x1"}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(models, Encoding.UTF8, "application/json"),
            };
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(new TestPluginHostServices());

        await sut.SetItemsAsync("profiles", [ProfileItem("P", "http://localhost:11434")]);
        var id = Assert.Single(await sut.GetItemsAsync("profiles")).Values["__id"];
        Assert.Contains(sut.AdditionalLlmProviders[0].SupportedModels, m => m.Id == "m1");

        // Re-save the SAME profile (same __id) pointing at a different server.
        await sut.SetItemsAsync("profiles", [ProfileItem("P", "http://localhost:9999", id: id)]);

        var models = sut.AdditionalLlmProviders[0].SupportedModels.Select(m => m.Id).ToList();
        Assert.Contains("x1", models);
        Assert.DoesNotContain("m1", models); // stale catalog must not survive the endpoint change
    }

    [Fact]
    public async Task RefreshModelCatalogAsync_UpdatesProfileCatalog()
    {
        var modelsJson = """{"data":[{"id":"m1"}]}""";
        // Responder reads the current modelsJson each call, so we can simulate the
        // server's model list changing after the profile was first saved.
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // Reading the reassigned-below modelsJson is the point (see comment above):
            // each call returns the server's current model list.
            // ReSharper disable once AccessToModifiedClosure
            Content = new StringContent(modelsJson, Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(new TestPluginHostServices());
        await sut.SetItemsAsync("profiles", [ProfileItem("P", "http://localhost:11434")]);

        Assert.Contains(sut.AdditionalLlmProviders[0].SupportedModels, m => m.Id == "m1");
        Assert.DoesNotContain(sut.AdditionalLlmProviders[0].SupportedModels, m => m.Id == "m2");

        // Server gains a model; the dropdown-open refresh path should pick it up.
        modelsJson = """{"data":[{"id":"m1"},{"id":"m2"}]}""";
        await sut.RefreshModelCatalogAsync();

        Assert.Contains(sut.AdditionalLlmProviders[0].SupportedModels, m => m.Id == "m2");
    }

    [Fact]
    public async Task ProcessStreamingAsync_ThroughProfile_StreamsDeltas()
    {
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}",
            "",
            "data: [DONE]",
            "");
        var handler = new CapturingHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path.EndsWith("/chat/completions", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":[{"id":"m1"}]}""", Encoding.UTF8, "application/json"),
                };
        });
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(new TestPluginHostServices()); // streamResponses defaults true
        await sut.SetItemsAsync(
            "profiles",
            [ProfileItem("P", "http://localhost:11434", llmModel: "m1")]);

        var role = Assert.Single(sut.AdditionalLlmProviders);
        var chunks = new List<string>();
        await foreach (var chunk in role.ProcessStreamingAsync("sys", "user", "m1", CancellationToken.None))
            chunks.Add(chunk);

        Assert.Equal(["Hel", "lo"], chunks);
    }

    [Fact]
    public async Task ActivateAsync_PersistedProfilesContainNulls_SkipsThemAndKeepsValidOnes()
    {
        // Hand-edited or partially-written settings can carry nulls the declared types forbid.
        var host = new TestPluginHostServices();
        host.SetSetting(
            "additionalProfiles",
            JsonSerializer.Deserialize<JsonElement>(
                """
                [
                  null,
                  {"id":"profile-a","name":"A","baseUrl":null,"fetchedModels":null},
                  {"id":"profile-b","name":"B","baseUrl":"http://localhost:11434",
                   "fetchedModels":[null,{"id":"m1","ownedBy":null},{"id":"  "}]}
                ]
                """));
        using var httpClient = ModelsClient();
        var sut = new OpenAiCompatiblePlugin(httpClient);

        await sut.ActivateAsync(host);

        var roles = sut.AdditionalLlmProviders;
        Assert.Equal(2, roles.Count);
        Assert.Equal(["A", "B"], roles.Select(r => r.ProviderName));
        // Only the null base URL was unusable, so only that profile is unconfigured.
        Assert.Equal([false, true], roles.Select(r => r.IsAvailable || r.SupportedModels.Count > 0));
        Assert.Equal(["m1"], roles[1].SupportedModels.Select(m => m.Id));
    }

    [Fact]
    public async Task ProcessStreamingAsync_TokenCancelledMidStream_StopsConsumingResponse()
    {
        // The token reaches the enumerator as a plain parameter rather than through
        // WithCancellation, so pin that it still interrupts an unfinished stream.
        using var cts = new CancellationTokenSource();
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(
                new StalledSseStream("data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n")),
        });

        var host = new TestPluginHostServices();
        host.SetSetting("baseUrl", "http://localhost:11434");
        host.SetSetting("selectedLlmModel", "llama3");
        using var httpClient = new HttpClient(handler);
        var sut = new OpenAiCompatiblePlugin(httpClient);
        await sut.ActivateAsync(host);

        var chunks = new List<string>();
        var consume = Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var chunk in sut.ProcessStreamingAsync("sys", "user", "llama3", cts.Token))
            {
                chunks.Add(chunk);
                await cts.CancelAsync();
            }
        });

        // Bounded independently of the token under test, so a propagation regression fails here
        // instead of hanging the run.
        // ReSharper disable once MethodSupportsCancellation -- the cancellation-aware overload takes the token under test, the one dependency this bound must not have.
        await consume.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(["Hel"], chunks);
    }

    /// <summary>Serves one SSE frame, then stalls like a server still generating tokens.</summary>
    private sealed class StalledSseStream(string firstFrame) : Stream
    {
        private readonly byte[] _frame = Encoding.UTF8.GetBytes(firstFrame);
        private int _offset;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < _frame.Length)
            {
                var count = Math.Min(buffer.Length, _frame.Length - _offset);
                _frame.AsSpan(_offset, count).CopyTo(buffer.Span);
                _offset += count;
                return count;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
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
        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly Dictionary<string, JsonElement> _settings = [];
        private Dictionary<string, string?> Secrets { get; } = [];

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
