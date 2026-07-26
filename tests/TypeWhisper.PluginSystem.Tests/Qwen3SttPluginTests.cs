using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Qwen3Stt;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class Qwen3SttPluginTests
{
    [Fact]
    public async Task ActivateAsync_RestoresEndpointCredentialsAndSelectedModel()
    {
        var host = new TestPluginHostServices
        {
            Secrets =
            {
                ["api-key"] = "qwen-key",
            },
        };
        host.SetSetting("baseUrl", "https://qwen.example");
        host.SetSetting("selectedModel", "Qwen/Qwen3-ASR");

        using var sut = new Qwen3SttPlugin();
        await sut.ActivateAsync(host);

        Assert.True(sut.IsConfigured);
        Assert.Equal("https://qwen.example", await sut.GetSettingValueAsync("baseUrl"));
        Assert.Equal("qwen-key", await sut.GetSettingValueAsync("api-key"));
        Assert.Equal("Qwen/Qwen3-ASR", sut.SelectedModelId);
        Assert.False(sut.SupportsTranslation);
    }

    [Fact]
    public async Task SetSettingValueAsync_NormalizesAndPersistsEndpointCredentialsAndModel()
    {
        var host = new TestPluginHostServices();
        using var sut = new Qwen3SttPlugin();
        await sut.ActivateAsync(host);

        await sut.SetSettingValueAsync("baseUrl", " https://qwen.example/v1/ ");
        await sut.SetSettingValueAsync("api-key", " qwen-key ");
        await sut.SetSettingValueAsync("selectedModel", "Qwen/Qwen3-ASR");

        Assert.Equal("https://qwen.example", host.GetSetting<string>("baseUrl"));
        Assert.Equal("qwen-key", host.Secrets["api-key"]);
        Assert.Equal("Qwen/Qwen3-ASR", host.GetSetting<string>("selectedModel"));
        Assert.Equal(2, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task TranscribeAsync_PostsOpenAiMultipartRequestAndParsesVerboseJson()
    {
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "https://qwen.example/v1/audio/transcriptions",
                request.RequestUri?.ToString()
            );
            Assert.Equal("Bearer qwen-key", request.Headers.Authorization?.ToString());

            var content = Assert.IsType<MultipartFormDataContent>(request.Content);
            var parts = content.ToArray();
            Assert.Equal(
                ["file", "model", "response_format", "language", "prompt"],
                parts.Select(GetPartName).ToArray()
            );

            var file = Assert.Single(parts, part => GetPartName(part) == "file");
            Assert.Equal("audio.wav", file.Headers.ContentDisposition?.FileName?.Trim('"'));
            Assert.Equal("audio/wav", file.Headers.ContentType?.MediaType);
            Assert.Equal([1, 2, 3], await file.ReadAsByteArrayAsync(ct));

            var model = Assert.Single(parts, part => GetPartName(part) == "model");
            Assert.Equal("Qwen/Qwen3-ASR", await model.ReadAsStringAsync(ct));
            var responseFormat = Assert.Single(
                parts,
                part => GetPartName(part) == "response_format"
            );
            Assert.Equal("verbose_json", await responseFormat.ReadAsStringAsync(ct));
            var language = Assert.Single(parts, part => GetPartName(part) == "language");
            Assert.Equal("de", await language.ReadAsStringAsync(ct));
            var prompt = Assert.Single(parts, part => GetPartName(part) == "prompt");
            Assert.Equal("TypeWhisper vocabulary", await prompt.ReadAsStringAsync(ct));

            return JsonResponse(
                """
                {
                  "text": " Hallo Welt ",
                  "language": "de",
                  "duration": 1.5,
                  "segments": [
                    {
                      "text": "Hallo",
                      "start": 0.0,
                      "end": 0.6,
                      "no_speech_prob": 0.2
                    },
                    {
                      "text": " Welt",
                      "start": 0.6,
                      "end": 1.5,
                      "no_speech_prob": 0.1
                    }
                  ]
                }
                """
            );
        });
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.TranscribeAsync(
            [1, 2, 3],
            "de",
            translate: false,
            prompt: "TypeWhisper vocabulary",
            CancellationToken.None
        );

        Assert.Equal("Hallo Welt", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(1.5, result.DurationSeconds);
        Assert.Equal(0.1f, result.NoSpeechProbability);
        Assert.Collection(
            result.Segments,
            segment => Assert.Equal(("Hallo", 0.0, 0.6), (segment.Text, segment.Start, segment.End)),
            segment =>
                Assert.Equal((" Welt", 0.6, 1.5), (segment.Text, segment.Start, segment.End))
        );
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{ "text": null }""")]
    [InlineData("""{ "text": 42 }""")]
    [InlineData("""{ "error": { "message": "model failed" } }""")]
    public async Task TranscribeAsync_RejectsResponseWithoutStringText(string responseBody)
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse(responseBody))
        );
        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                sut.TranscribeAsync(
                    [1],
                    null,
                    translate: false,
                    prompt: null,
                    CancellationToken.None
                )
        );

        Assert.Contains(
            "Invalid transcription response: required field 'text' must be a string.",
            exception.Message
        );
    }

    [Fact]
    public async Task TranscribeAsync_SurfacesProviderHttpError()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(
                JsonResponse(
                    """{ "error": { "message": "model unavailable" } }""",
                    HttpStatusCode.ServiceUnavailable
                )
            )
        );
        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                sut.TranscribeAsync(
                    [1],
                    null,
                    translate: false,
                    prompt: null,
                    CancellationToken.None
                )
        );

        Assert.Equal("API error 503: model unavailable", exception.Message);
    }

    [Fact]
    public async Task TranscribeAsync_PropagatesCancellation()
    {
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return JsonResponse("""{ "text": "unreachable" }""");
        });
        using var sut = await CreateConfiguredPluginAsync(handler);
        using var cancellation = new CancellationTokenSource();

        var transcription = sut.TranscribeAsync(
            [1],
            null,
            translate: false,
            prompt: null,
            cancellation.Token
        );
        // ReSharper disable once MethodSupportsCancellation -- fixed hang-guard; the only in-scope token is cancellation.Token (the token under test), which the next line cancels, so forwarding it here would abort this wait instead of guarding it.
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transcription);
    }

    [Fact]
    public async Task TranscribeAsync_RejectsTranslationBeforeSendingHttpRequest()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse("""{ "text": "unexpected" }"""))
        );
        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () =>
                sut.TranscribeAsync(
                    [1],
                    "en",
                    translate: true,
                    prompt: null,
                    CancellationToken.None
                )
        );

        Assert.Equal(
            "Translation is not supported by the Qwen3 STT plugin.",
            exception.Message
        );
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void SelectModel_RejectsUnknownModel()
    {
        using var sut = new Qwen3SttPlugin();

        var exception = Assert.Throws<ArgumentException>(() => sut.SelectModel("unknown"));

        Assert.Equal("Unknown model: unknown", exception.Message);
    }

    [Fact]
    public async Task TranscribeAsync_WhenUnconfigured_UsesLocalizedMessage()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse("""{ "text": "unexpected" }"""))
        );
        using var sut = new Qwen3SttPlugin(new HttpClient(handler));
        sut.SetLocalization(new TestPluginLocalization());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () =>
                sut.TranscribeAsync(
                    [1],
                    null,
                    translate: false,
                    prompt: null,
                    CancellationToken.None
                )
        );

        Assert.Equal("Localized Qwen base URL is required.", exception.Message);
        Assert.Equal(0, handler.CallCount);
    }

    private static async Task<Qwen3SttPlugin> CreateConfiguredPluginAsync(
        StubHttpMessageHandler handler
    )
    {
        var host = new TestPluginHostServices
        {
            Secrets =
            {
                ["api-key"] = "qwen-key",
            },
        };
        host.SetSetting("baseUrl", "https://qwen.example");
        host.SetSetting("selectedModel", "Qwen/Qwen3-ASR");

        var sut = new Qwen3SttPlugin(new HttpClient(handler));
        await sut.ActivateAsync(host);
        return sut;
    }

    private static string GetPartName(HttpContent content)
    {
        var name = content.Headers.ContentDisposition?.Name;
        Assert.NotNull(name);
        return name.Trim('"');
    }

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK
    ) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder
    ) : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref _callCount);
            return responder(request, cancellationToken);
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
        public IPluginLocalization Localization { get; } = new TestPluginLocalization();

        public void Log(PluginLogLevel level, string message)
        {
        }

        public void NotifyCapabilitiesChanged()
        {
            NotifyCapabilitiesChangedCount++;
        }
    }

    private sealed class TestPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];

        public string GetString(string key) =>
            key == "Settings.NotConfiguredBaseUrlRequired"
                ? "Localized Qwen base URL is required."
                : key;

        public string GetString(string key, params object[] args) =>
            string.Format(GetString(key), args);
    }

    private sealed class TestPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent)
            where T : PluginEvent
        {
        }

        public IDisposable Subscribe<T>(Func<T, Task> handler)
            where T : PluginEvent =>
            new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
