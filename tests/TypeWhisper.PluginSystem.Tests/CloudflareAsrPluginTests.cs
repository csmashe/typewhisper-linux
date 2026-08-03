using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.CloudflareAsr;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class CloudflareAsrPluginTests
{
    [Fact]
    public async Task ActivateAsync_RestoresNormalizedCredentialsAndSelectedModel()
    {
        var host = new TestPluginHostServices
        {
            Secrets =
            {
                ["account-id"] = " account-123 ",
                ["api-token"] = " token-123 ",
            },
        };
        host.SetSetting("selectedModel", "whisper");

        using var sut = new CloudflareAsrPlugin();
        await sut.ActivateAsync(host);

        Assert.True(sut.IsConfigured);
        Assert.Equal("whisper", sut.SelectedModelId);
        Assert.Equal("account-123", await sut.GetSettingValueAsync("account-id"));
        Assert.Equal("token-123", await sut.GetSettingValueAsync("api-token"));
        Assert.False(sut.SupportsTranslation);
    }

    [Fact]
    public async Task SetSettingValueAsync_PersistsCredentialsAndSelectedModel()
    {
        var host = new TestPluginHostServices();
        using var sut = new CloudflareAsrPlugin();
        await sut.ActivateAsync(host);

        await sut.SetSettingValueAsync("account-id", " account-456 ");
        await sut.SetSettingValueAsync("api-token", " token-456 ");
        await sut.SetSettingValueAsync("selectedModel", "whisper");

        Assert.Equal("account-456", host.Secrets["account-id"]);
        Assert.Equal("token-456", host.Secrets["api-token"]);
        Assert.Equal("whisper", host.GetSetting<string>("selectedModel"));
        Assert.Equal(2, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task TranscribeAsync_PostsRawAudioWithBearerAuthAndParsesResult()
    {
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "https://api.cloudflare.com/client/v4/accounts/account-123/ai/run/@cf/openai/whisper",
                request.RequestUri?.ToString()
            );
            Assert.Equal("Bearer token-123", request.Headers.Authorization?.ToString());
            Assert.Equal("application/octet-stream", request.Content?.Headers.ContentType?.MediaType);
            Assert.Equal([1, 2, 3, 4], await request.Content!.ReadAsByteArrayAsync(ct));

            return JsonResponse(
                """
                {
                  "result": {
                    "text": " Hallo Welt ",
                    "language": "de",
                    "duration": 1.25
                  }
                }
                """
            );
        });
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.TranscribeAsync(
            [1, 2, 3, 4],
            "de",
            translate: false,
            prompt: "ignored",
            CancellationToken.None
        );

        Assert.Equal("Hallo Welt", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(1.25, result.DurationSeconds);
        Assert.Null(result.NoSpeechProbability);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task TranscribeAsync_AcceptsExplicitEmptyResultText()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse("""{ "result": { "text": "" } }"""))
        );
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.TranscribeAsync(
            [1],
            null,
            translate: false,
            prompt: null,
            CancellationToken.None
        );

        Assert.Equal(string.Empty, result.Text);
    }

    [Theory]
    // ReSharper disable once RawStringCanBeSimplified -- kept raw so every InlineData in this theory has the same form.
    [InlineData("""{}""")]
    [InlineData("""{ "result": null }""")]
    [InlineData("""{ "result": {} }""")]
    [InlineData("""{ "result": { "text": null } }""")]
    [InlineData("""{ "result": { "text": 42 } }""")]
    public async Task TranscribeAsync_RejectsResponseWithoutStringResultText(string responseBody)
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

        Assert.Equal(
            "Invalid Cloudflare transcription response: required field 'result.text' must be a string.",
            exception.Message
        );
    }

    [Fact]
    public async Task TranscribeAsync_SurfacesProviderHttpError()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(
                JsonResponse(
                    """{ "errors": [{ "message": "invalid audio" }] }""",
                    HttpStatusCode.UnprocessableEntity
                )
            )
        );
        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () =>
                sut.TranscribeAsync(
                    [1],
                    null,
                    translate: false,
                    prompt: null,
                    CancellationToken.None
                )
        );

        Assert.Equal(
            "Cloudflare API error 422: Unprocessable Entity",
            exception.Message
        );
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
            return JsonResponse("""{ "result": { "text": "unreachable" } }""");
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
            Task.FromResult(JsonResponse("""{ "result": { "text": "unexpected" } }"""))
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
            "Translation is not supported by the Cloudflare ASR plugin.",
            exception.Message
        );
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void SelectModel_RejectsUnknownModel()
    {
        using var sut = new CloudflareAsrPlugin();

        var exception = Assert.Throws<ArgumentException>(() => sut.SelectModel("unknown"));

        Assert.Equal("Unknown model: unknown", exception.Message);
    }

    [Fact]
    public async Task TranscribeAsync_WhenUnconfigured_UsesLocalizedMessage()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse("""{ "result": { "text": "unexpected" } }"""))
        );
        using var sut = new CloudflareAsrPlugin(new HttpClient(handler));
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

        Assert.Equal("Localized Cloudflare credentials are required.", exception.Message);
        Assert.Equal(0, handler.CallCount);
    }

    private static async Task<CloudflareAsrPlugin> CreateConfiguredPluginAsync(
        StubHttpMessageHandler handler
    )
    {
        var host = new TestPluginHostServices
        {
            Secrets =
            {
                ["account-id"] = "account-123",
                ["api-token"] = "token-123",
            },
        };
        var sut = new CloudflareAsrPlugin(new HttpClient(handler));
        await sut.ActivateAsync(host);
        return sut;
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
            key == "Settings.EnterAccountIdAndApiToken"
                ? "Localized Cloudflare credentials are required."
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
