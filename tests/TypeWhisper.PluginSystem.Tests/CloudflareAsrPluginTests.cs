using System.Net;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.CloudflareAsr;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class CloudflareAsrPluginTests
{
    private const string AccountId = "0123456789abcdef0123456789ABCDEF";
    private const string OtherAccountId = "fedcba9876543210fedcba9876543210";
    [Fact]
    public Task RequestFailures_AreClassified() =>
        ProviderFailureAssertions.VerifyAsync<CloudflareAsrPlugin>();


    [Fact]
    public async Task ActivateAsync_RestoresNormalizedCredentialsAndSelectedModel()
    {
        var host = new TestPluginHostServices
        {
            Secrets =
            {
                ["account-id"] = $" {AccountId} ",
                ["api-token"] = " token-123 ",
            },
        };
        host.SetSetting("selectedModel", "whisper");

        using var sut = new CloudflareAsrPlugin();
        await sut.ActivateAsync(host);

        Assert.True(sut.IsConfigured);
        Assert.Equal("whisper", sut.SelectedModelId);
        Assert.Equal(AccountId, await sut.GetSettingValueAsync("account-id"));
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
                $"https://api.cloudflare.com/client/v4/accounts/{AccountId}/ai/run/@cf/openai/whisper",
                request.RequestUri?.ToString()
            );
            Assert.Equal("Bearer token-123", request.Headers.Authorization?.ToString());
            Assert.Equal("application/octet-stream", request.Content?.Headers.ContentType?.MediaType);
            Assert.Equal([1, 2, 3, 4], await request.Content!.ReadAsByteArrayAsync(ct));

            return JsonResponse(
                """
                {
                  "success": true,
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

        // Null, like the host sends: raw audio, no language parameter, explicit selection off.
        var result = await sut.TranscribeAsync(
            [1, 2, 3, 4],
            null,
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
            Task.FromResult(JsonResponse("""{ "success": true, "result": { "text": "" } }"""))
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
    [InlineData("""{ "success": true }""")]
    [InlineData("""{ "success": true, "result": null }""")]
    [InlineData("""{ "success": true, "result": {} }""")]
    [InlineData("""{ "success": true, "result": { "text": null } }""")]
    [InlineData("""{ "success": true, "result": { "text": 42 } }""")]
    public async Task TranscribeAsync_RejectsResponseWithoutStringResultText(string responseBody)
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse(responseBody))
        );
        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<PluginRequestException>(
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
        Assert.Equal(PluginRequestFailureKind.OutputIncomplete, exception.FailureKind);
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

        var exception = await Assert.ThrowsAsync<PluginRequestException>(
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
            return JsonResponse("""{ "success": true, "result": { "text": "unreachable" } }""");
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

    [Theory]
    [InlineData("en")]
    [InlineData(" de-DE ")]
    public async Task TranscribeAsync_RejectsExplicitLanguageBeforeSendingHttpRequest(
        string language
    )
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse("""{ "result": { "text": "unexpected" } }"""))
        );
        using var sut = await CreateConfiguredPluginAsync(handler);

        await Assert.ThrowsAsync<NotSupportedException>(
            () =>
                sut.TranscribeAsync(
                    [1],
                    language,
                    translate: false,
                    prompt: null,
                    CancellationToken.None
                )
        );

        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData(" AUTO ")]
    public async Task TranscribeAsync_TreatsBlankAndAutoAsAutomaticDetection(string? language)
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse("""{ "success": true, "result": { "text": "ok" } }"""))
        );
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.TranscribeAsync(
            [1],
            language,
            translate: false,
            prompt: null,
            CancellationToken.None
        );

        Assert.Equal("ok", result.Text);
        Assert.Equal(1, handler.CallCount);
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

        var exception = await Assert.ThrowsAsync<PluginRequestException>(
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

    [Fact]
    public void SelectModel_SwitchesLanguageAndDictionaryCapabilities()
    {
        using var sut = new CloudflareAsrPlugin();

        sut.SelectModel("whisper");
        Assert.Equal(LanguageSelectionSupport.Supported, sut.AutomaticDetectionSupport);
        Assert.Equal(LanguageSelectionSupport.Unsupported, sut.ExplicitSelectionSupport);
        Assert.Empty(sut.SupportedLanguages);
        Assert.False(sut.SupportsStructuredDictionaryTerms);

        sut.SelectModel("whisper-large-v3-turbo");
        Assert.Equal(LanguageSelectionSupport.Supported, sut.AutomaticDetectionSupport);
        Assert.Equal(LanguageSelectionSupport.Supported, sut.ExplicitSelectionSupport);
        Assert.Contains("de", sut.SupportedLanguages);
        Assert.Contains("haw", sut.SupportedLanguages);
        Assert.True(sut.SupportsStructuredDictionaryTerms);
    }

    [Fact]
    public async Task ActivateAsync_FallsBackToWhisperForUnknownSavedModel()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("selectedModel", "retired-model");
        using var sut = new CloudflareAsrPlugin();

        await sut.ActivateAsync(host);

        Assert.Equal("whisper", sut.SelectedModelId);
    }

    [Fact]
    public async Task TranscribeAsync_Turbo_PostsJsonWithLanguageAndDictionaryPrompt()
    {
        var audio = Enumerable.Range(0, 12_288 * 2 + 5).Select(i => (byte)(i * 31)).ToArray();
        JsonElement? body = null;
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                $"https://api.cloudflare.com/client/v4/accounts/{AccountId}/ai/run/@cf/openai/whisper-large-v3-turbo",
                request.RequestUri?.ToString()
            );
            Assert.Equal("Bearer token-123", request.Headers.Authorization?.ToString());
            Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
            var contentLength = request.Content!.Headers.ContentLength;
            var bytes = await request.Content.ReadAsByteArrayAsync(ct);
            Assert.Equal(contentLength, bytes.Length);
            body = JsonDocument.Parse(bytes).RootElement.Clone();

            return JsonResponse(
                """
                {
                  "success": true,
                  "result": {
                    "text": " Guten Tag ",
                    "transcription_info": { "language": "de", "duration": 2.5 }
                  }
                }
                """
            );
        });
        using var sut = await CreateConfiguredPluginAsync(handler, "whisper-large-v3-turbo");

        var result = await sut.TranscribeAsync(
            audio,
            " DE ",
            translate: false,
            PluginTranscriptionPrompt.Encode(null, ["Washington, D.C.", "TypeWhisper"]),
            CancellationToken.None
        );

        Assert.NotNull(body);
        Assert.Equal(audio, Convert.FromBase64String(body.Value.GetProperty("audio").GetString()!));
        Assert.Equal("transcribe", body.Value.GetProperty("task").GetString());
        Assert.Equal("de", body.Value.GetProperty("language").GetString());
        Assert.Equal("Washington, D.C., TypeWhisper", body.Value.GetProperty("initial_prompt").GetString());
        Assert.Equal("Guten Tag", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(2.5, result.DurationSeconds);
    }

    [Fact]
    public async Task TranscribeAsync_Turbo_OmitsLanguageAndPromptWhenAbsent()
    {
        JsonElement? body = null;
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(ct)).RootElement.Clone();
            return JsonResponse("""{ "success": true, "result": { "text": "ok" } }""");
        });
        using var sut = await CreateConfiguredPluginAsync(handler, "whisper-large-v3-turbo");

        var result = await sut.TranscribeAsync([1, 2, 3], "auto", translate: false, prompt: null, CancellationToken.None);

        Assert.NotNull(body);
        Assert.Equal(
            ["audio", "task"],
            body.Value.EnumerateObject().Select(property => property.Name).ToArray()
        );
        Assert.Equal("ok", result.Text);
        Assert.Null(result.DetectedLanguage);
        Assert.Equal(0, result.DurationSeconds);
    }

    [Theory]
    [InlineData("xx")]
    [InlineData("de-DE")]
    public async Task TranscribeAsync_Turbo_RejectsUnsupportedLanguageBeforeSendingHttpRequest(string language)
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse("""{ "success": true, "result": { "text": "unexpected" } }"""))
        );
        using var sut = await CreateConfiguredPluginAsync(handler, "whisper-large-v3-turbo");

        await Assert.ThrowsAsync<NotSupportedException>(
            () => sut.TranscribeAsync([1], language, translate: false, prompt: null, CancellationToken.None)
        );

        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData("whisper", 100_000_000)]
    [InlineData("whisper-large-v3-turbo", 74_000_000)]
    public async Task TranscribeAsync_RejectsRecordingAboveModelUploadLimit(string modelId, int limit)
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse("""{ "success": true, "result": { "text": "unexpected" } }"""))
        );
        using var sut = await CreateConfiguredPluginAsync(handler, modelId);

        var exception = await Assert.ThrowsAsync<PluginRequestException>(
            () => sut.TranscribeAsync(new byte[limit + 1], null, translate: false, prompt: null, CancellationToken.None)
        );

        Assert.Equal(PluginRequestFailureKind.RequestTooLarge, exception.FailureKind);
        Assert.Equal("Errors.RecordingTooLarge", exception.Message);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData("account-123")]
    [InlineData("0123456789abcdef0123456789abcde/")]
    [InlineData("../0123456789abcdef0123456789abcd")]
    public async Task TranscribeAsync_RejectsMalformedAccountIdBeforeSendingHttpRequest(string accountId)
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            Task.FromResult(JsonResponse("""{ "success": true, "result": { "text": "unexpected" } }"""))
        );
        using var sut = await CreateConfiguredPluginAsync(handler);
        await sut.SetSettingValueAsync("account-id", accountId);

        var exception = await Assert.ThrowsAsync<PluginRequestException>(
            () => sut.TranscribeAsync([1], null, translate: false, prompt: null, CancellationToken.None)
        );

        Assert.Equal(PluginRequestFailureKind.Configuration, exception.FailureKind);
        Assert.Equal("Settings.InvalidAccountId", exception.Message);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData("""{ "result": { "text": "unconfirmed" } }""", "'success' must be true")]
    [InlineData("""{ "success": false, "result": { "text": "failed" } }""", "'success' must be true")]
    [InlineData("[]", "'success' must be true")]
    [InlineData("not json", "the body is not valid JSON")]
    public async Task TranscribeAsync_RejectsUnconfirmedOrMalformedResponse(string responseBody, string reason)
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(responseBody)));
        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<PluginRequestException>(
            () => sut.TranscribeAsync([1], null, translate: false, prompt: null, CancellationToken.None)
        );

        Assert.Equal($"Invalid Cloudflare transcription response: {reason}.", exception.Message);
        Assert.Equal(PluginRequestFailureKind.OutputIncomplete, exception.FailureKind);
    }

    [Fact]
    public async Task ValidateAsync_ListsOneWorkersAiModelForTheAccount()
    {
        // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local -- the stub exists to assert the outgoing request.
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                $"https://api.cloudflare.com/client/v4/accounts/{AccountId}/ai/models/search?per_page=1",
                request.RequestUri?.ToString()
            );
            Assert.Equal("Bearer token-123", request.Headers.Authorization?.ToString());
            Assert.Null(request.Content);
            return Task.FromResult(JsonResponse("""{ "success": true, "result": [{ "name": "@cf/openai/whisper" }] }"""));
        });
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.ValidateAsync();

        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.Equal("Settings.CredentialsValid", result.Message);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}", "Settings.TokenRejected")]
    [InlineData(HttpStatusCode.Forbidden, "{}", "Settings.TokenRejected")]
    [InlineData(HttpStatusCode.NotFound, "{}", "Settings.ValidationFailedStatus")]
    [InlineData(HttpStatusCode.Found, "{}", "Settings.ValidationFailedStatus")]
    [InlineData(HttpStatusCode.InternalServerError, "{}", "Settings.ValidationFailedStatus")]
    [InlineData(HttpStatusCode.OK, """{ "success": false, "result": [] }""", "Settings.InvalidValidationResponse")]
    [InlineData(HttpStatusCode.OK, """{ "success": true, "result": {} }""", "Settings.InvalidValidationResponse")]
    [InlineData(HttpStatusCode.OK, "not json", "Settings.InvalidValidationResponse")]
    public async Task ValidateAsync_ReportsRejectedOrUnexpectedResponses(
        HttpStatusCode statusCode,
        string responseBody,
        string expectedMessage
    )
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(responseBody, statusCode)));
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.ValidateAsync();

        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal(expectedMessage, result.Message);
    }

    [Fact]
    public async Task ValidateAsync_ReportsNetworkFailureAsUnreachable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("connection refused"));
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.ValidateAsync();

        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal("Settings.CloudflareUnreachable", result.Message);
    }

    [Fact]
    public async Task ValidateAsync_RejectsMalformedAccountIdWithoutRequest()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse("{}")));
        using var sut = await CreateConfiguredPluginAsync(handler);
        await sut.SetSettingValueAsync("account-id", "account-123");

        var result = await sut.ValidateAsync();

        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal("Settings.InvalidAccountId", result.Message);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ValidateAsync_DoesNotReportStaleResultAfterCredentialsChange()
    {
        var plugin = new TaskCompletionSource<CloudflareAsrPlugin>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            await (await plugin.Task).SetSettingValueAsync("account-id", OtherAccountId, ct);
            return JsonResponse("""{ "success": true, "result": [] }""");
        });
        using var sut = await CreateConfiguredPluginAsync(handler);
        plugin.SetResult(sut);

        var result = await sut.ValidateAsync();

        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal("Settings.SettingsChangedDuringValidation", result.Message);
    }

    private static async Task<CloudflareAsrPlugin> CreateConfiguredPluginAsync(
        StubHttpMessageHandler handler,
        string? modelId = null
    )
    {
        var host = new TestPluginHostServices
        {
            Secrets =
            {
                ["account-id"] = AccountId,
                ["api-token"] = "token-123",
            },
        };
        if (modelId is not null)
            host.SetSetting("selectedModel", modelId);
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
