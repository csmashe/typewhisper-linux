using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.Plugin.Cohere;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class CoherePluginTests
{
    private const string TranscriptionUrl = "https://api.cohere.com/v2/audio/transcriptions";

    [Fact]
    public Task RequestFailures_AreClassified() =>
        ProviderFailureAssertions.VerifyAsync<CoherePlugin>();

    [Fact]
    public async Task TranscriptionRole_AdvertisesExplicitOnlyLanguagesAndOneModel()
    {
        using var sut = await CreateConfiguredPluginAsync(new StubHttpMessageHandler(Unexpected));

        Assert.Equal("cohere", sut.ProviderId);
        Assert.True(sut.IsConfigured);
        Assert.False(sut.SupportsTranslation);
        Assert.False(((ITranscriptionEngineRole)sut).SupportsStreaming);
        Assert.False(((ITranscriptionEngineRole)sut).SupportsStructuredDictionaryTerms);
        Assert.Equal(LanguageSelectionSupport.Unsupported, sut.AutomaticDetectionSupport);
        Assert.Equal(LanguageSelectionSupport.Supported, sut.ExplicitSelectionSupport);
        Assert.Equal(
            ["ar", "de", "el", "en", "es", "fr", "it", "ja", "ko", "nl", "pl", "pt", "vi", "zh"],
            sut.SupportedLanguages
        );
        var model = Assert.Single(sut.TranscriptionModels);
        Assert.Equal("cohere-transcribe-03-2026", model.Id);
        Assert.Equal(model.Id, sut.SelectedModelId);
    }

    [Fact]
    public void SelectModel_AcceptsOnlyTheTranscriptionModel()
    {
        using var sut = new CoherePlugin();

        sut.SelectModel("cohere-transcribe-03-2026");
        var exception = Assert.Throws<ArgumentException>(() => sut.SelectModel("command-a-03-2025"));

        Assert.Equal("Unknown model: command-a-03-2025", exception.Message);
    }

    [Fact]
    public async Task TranscribeAsync_SendsFieldsBeforeTheFileAndParsesText()
    {
        var wav = CreateWav(TimeSpan.FromSeconds(1.5));
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(TranscriptionUrl, request.RequestUri?.ToString());
            Assert.Equal("Bearer test-key", request.Headers.Authorization?.ToString());

            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var parts = form.ToList();
            Assert.Equal(["model", "language", "file"], parts.Select(PartName));
            Assert.Equal("cohere-transcribe-03-2026", await parts[0].ReadAsStringAsync(ct));
            Assert.Equal("de", await parts[1].ReadAsStringAsync(ct));
            Assert.Equal("audio/wav", parts[2].Headers.ContentType?.MediaType);
            Assert.Equal("audio.wav", parts[2].Headers.ContentDisposition?.FileName?.Trim('"'));
            Assert.Equal(wav, await parts[2].ReadAsByteArrayAsync(ct));

            return JsonResponse("""{ "text": " Hallo Welt " }""");
        });
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.TranscribeAsync(wav, "de", translate: false, prompt: "ignored", CancellationToken.None);

        Assert.Equal("Hallo Welt", result.Text);
        Assert.Null(result.DetectedLanguage);
        Assert.Equal(1.5, result.DurationSeconds, precision: 6);
        Assert.Null(result.NoSpeechProbability);
        Assert.Empty(result.Segments);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("de-DE", "de")]
    [InlineData(" PT-br ", "pt")]
    [InlineData("zh-Hans", "zh")]
    [InlineData("en", "en")]
    public async Task TranscribeAsync_SendsTheBaseLanguageCode(string language, string expected)
    {
        string? sent = null;
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            sent = await form.Single(part => PartName(part) == "language").ReadAsStringAsync(ct);
            return JsonResponse("""{ "text": "ok" }""");
        });
        using var sut = await CreateConfiguredPluginAsync(handler);

        await sut.TranscribeAsync(CreateWav(TimeSpan.FromSeconds(0.1)), language, false, null, CancellationToken.None);

        Assert.Equal(expected, sent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("auto")]
    [InlineData(" AUTO ")]
    public async Task TranscribeAsync_RejectsAutomaticSelectionBeforeSendingHttpRequest(string? language)
    {
        var handler = new StubHttpMessageHandler(Unexpected);
        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<LanguageSelectionNotSupportedException>(() =>
            sut.TranscribeAsync([1], language, false, null, CancellationToken.None));

        Assert.True(exception.Selection.IsAutomatic);
        Assert.Equal("cohere", exception.ProviderId);
        Assert.Equal("cohere-transcribe-03-2026", exception.ModelId);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("tr-TR")]
    public async Task TranscribeAsync_RejectsUnsupportedLanguageBeforeSendingHttpRequest(string language)
    {
        var handler = new StubHttpMessageHandler(Unexpected);
        using var sut = await CreateConfiguredPluginAsync(handler);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sut.TranscribeAsync([1], language, false, null, CancellationToken.None));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task TranscribeAsync_RejectsTranslationBeforeSendingHttpRequest()
    {
        var handler = new StubHttpMessageHandler(Unexpected);
        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            sut.TranscribeAsync([1], "en", translate: true, null, CancellationToken.None));

        Assert.Equal("Translation is not supported by the Cohere plugin.", exception.Message);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task TranscribeAsync_WhenUnconfigured_ReportsConfigurationWithoutRequest()
    {
        var handler = new StubHttpMessageHandler(Unexpected);
        using var sut = new CoherePlugin(new HttpClient(handler));
        await sut.ActivateAsync(new TestPluginHostServices());

        Assert.False(sut.IsConfigured);
        var exception = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.TranscribeAsync([1], "en", false, null, CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.Configuration, exception.FailureKind);
        Assert.Equal("Settings.ApiKeyNotConfigured", exception.Message);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task TranscribeAsync_RejectsRecordingAboveTheUploadLimitWithoutRequest()
    {
        var handler = new StubHttpMessageHandler(Unexpected);
        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.TranscribeAsync(new byte[25_000_001], "en", false, null, CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.RequestTooLarge, exception.FailureKind);
        Assert.Equal("Errors.RecordingTooLarge", exception.Message);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task TranscribeAsync_SendsRecordingAtTheUploadLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse("""{ "text": "ok" }""")));
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.TranscribeAsync(new byte[25_000_000], "en", false, null, CancellationToken.None);

        Assert.Equal("ok", result.Text);
        Assert.Equal(0, result.DurationSeconds);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task TranscribeAsync_AcceptsEmptyText()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse("""{ "text": "" }""")));
        using var sut = await CreateConfiguredPluginAsync(handler);

        var result = await sut.TranscribeAsync([1], "en", false, null, CancellationToken.None);

        Assert.Equal(string.Empty, result.Text);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "text": null }""")]
    [InlineData("""{ "text": 42 }""")]
    public async Task TranscribeAsync_RejectsResponseWithoutStringText(string body)
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(body)));
        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.TranscribeAsync([1], "en", false, null, CancellationToken.None));

        Assert.Equal(PluginRequestFailureKind.OutputIncomplete, exception.FailureKind);
    }

    [Theory]
    [InlineData(401, PluginRequestFailureKind.Authentication)]
    [InlineData(413, PluginRequestFailureKind.RequestTooLarge)]
    [InlineData(422, PluginRequestFailureKind.InvalidRequest)]
    [InlineData(429, PluginRequestFailureKind.RateLimit)]
    [InlineData(503, PluginRequestFailureKind.ServerError)]
    public async Task TranscribeAsync_ClassifiesHttpFailuresWithoutLeakingTheKey(
        int status,
        PluginRequestFailureKind expected
    )
    {
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = JsonResponse("""{ "message": "rejected test-key" }""", (HttpStatusCode)status);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
            return Task.FromResult(response);
        });
        using var sut = await CreateConfiguredPluginAsync(handler);

        var exception = await Assert.ThrowsAsync<PluginRequestException>(() =>
            sut.TranscribeAsync([1], "en", false, null, CancellationToken.None));

        Assert.Equal(expected, exception.FailureKind);
        Assert.Equal(status, exception.HttpStatusCode);
        Assert.Equal(TimeSpan.FromSeconds(3), exception.RetryAfter);
        Assert.DoesNotContain("test-key", exception.Message);
    }

    [Fact]
    public async Task TranscribeAsync_PropagatesCancellation()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return JsonResponse("""{ "text": "unreachable" }""");
        });
        using var sut = await CreateConfiguredPluginAsync(handler);
        using var cancellation = new CancellationTokenSource();

        var transcription = sut.TranscribeAsync([1], "en", false, null, cancellation.Token);
        // ReSharper disable once MethodSupportsCancellation -- fixed hang-guard; the only in-scope token is cancellation.Token (the token under test), which the next line cancels, so forwarding it here would abort this wait instead of guarding it.
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transcription);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200_000)]
    public async Task ProcessAsync_KeepsOutputTokensWithinCommandLimit(int inputLength)
    {
        int? maxTokens = null;
        var handler = new StubHttpMessageHandler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            maxTokens = body.RootElement.GetProperty("max_tokens").GetInt32();
            return JsonResponse(
                """{ "choices": [{ "finish_reason": "stop", "message": { "role": "assistant", "content": "done" } }] }""");
        });
        using var sut = await CreateConfiguredPluginAsync(handler);

        await sut.ProcessAsync("system", new string('x', inputLength), "command-a-03-2025", CancellationToken.None);

        Assert.InRange(maxTokens!.Value, 1, 8192);
    }

    [Fact]
    public void CalculateWavDurationSeconds_ReadsTheDataChunkAndRejectsMalformedHeaders()
    {
        Assert.Equal(2.0, CoherePlugin.CalculateWavDurationSeconds(CreateWav(TimeSpan.FromSeconds(2))), precision: 6);
        Assert.Equal(0, CoherePlugin.CalculateWavDurationSeconds([]));
        Assert.Equal(0, CoherePlugin.CalculateWavDurationSeconds("RIFF\0\0\0\0WAVX"u8.ToArray()));

        var truncated = CreateWav(TimeSpan.FromSeconds(1));
        Assert.Equal(0.5, CoherePlugin.CalculateWavDurationSeconds(truncated[..(44 + 16_000)]), precision: 6);
    }

    [Fact]
    public void CalculateWavDurationSeconds_MeasuresFfmpegStdoutWavWithUnknownDataSize()
    {
        var wav = CreateWav(TimeSpan.FromSeconds(2));
        // FFmpeg writing to pipe:1 cannot seek back, so it leaves both sizes as 0xFFFFFFFF.
        BitConverter.GetBytes(uint.MaxValue).CopyTo(wav, 4);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(wav, 40);

        Assert.Equal(2.0, CoherePlugin.CalculateWavDurationSeconds(wav), precision: 6);
    }

    private static async Task<CoherePlugin> CreateConfiguredPluginAsync(StubHttpMessageHandler handler)
    {
        var host = new TestPluginHostServices { Secrets = { ["apiKey"] = "test-key" } };
        var sut = new CoherePlugin(new HttpClient(handler));
        await sut.ActivateAsync(host);
        return sut;
    }

    private static string? PartName(HttpContent part) =>
        part.Headers.ContentDisposition?.Name?.Trim('"');

    private static Task<HttpResponseMessage> Unexpected(HttpRequestMessage request, CancellationToken ct) =>
        throw new InvalidOperationException($"Unexpected request to {request.RequestUri}.");

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // 16 kHz mono 16-bit PCM silence, the format the host records.
    private static byte[] CreateWav(TimeSpan duration)
    {
        var dataSize = (int)(duration.TotalSeconds * 32_000);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16_000);
        writer.Write(32_000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        writer.Write(new byte[dataSize]);
        return stream.ToArray();
    }

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
        private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly Dictionary<string, JsonElement> _settings = [];

        public Dictionary<string, string?> Secrets { get; } = [];

        public Task StoreSecretAsync(string key, string value)
        {
            Secrets[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> LoadSecretAsync(string key) => Task.FromResult(Secrets.GetValueOrDefault(key));

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
        public IPluginLocalization Localization { get; } = new KeyEchoLocalization();

        public void Log(PluginLogLevel level, string message)
        {
        }

        public void NotifyCapabilitiesChanged()
        {
        }
    }

    private sealed class KeyEchoLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => key;
    }

    private sealed class TestPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent
        {
        }

        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent => new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
