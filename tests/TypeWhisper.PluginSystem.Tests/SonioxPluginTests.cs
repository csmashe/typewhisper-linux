using System.Text.Json;
using TypeWhisper.Plugin.Soniox;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using SonioxSession = TypeWhisper.Plugin.Soniox.SonioxStreamingSession;

namespace TypeWhisper.PluginSystem.Tests;

public class SonioxPluginTests
{
    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifestPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "plugins",
                "TypeWhisper.Plugin.Soniox",
                "manifest.json"
            )
        );
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        var sut = new SonioxPlugin();

        Assert.NotNull(manifest);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    [Fact]
    public async Task ActivateAsync_SetsIdentityAndSupportsStreaming()
    {
        var host = new TestHost();
        host.Secrets["api-key"] = "soniox-key";

        var sut = new SonioxPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("com.typewhisper.soniox", sut.PluginId);
        Assert.Equal("soniox", sut.ProviderId);
        Assert.True(sut.IsConfigured);
        Assert.True(sut.SupportsStreaming);
        Assert.False(sut.SupportsTranslation);
    }

    [Fact]
    public async Task StartStreamingAsync_Throws_WhenNotConfigured()
    {
        var sut = new SonioxPlugin();
        await sut.ActivateAsync(new TestHost());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartStreamingAsync(null, CancellationToken.None)
        );
    }

    [Fact]
    public void BuildConfigMessage_IncludesRawPcmFormatAndModel()
    {
        var json = SonioxSession.BuildConfigMessage("k-123", SonioxSession.RealtimeModel, null);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("k-123", root.GetProperty("api_key").GetString());
        Assert.Equal("stt-rt-v4", root.GetProperty("model").GetString());
        Assert.Equal("pcm_s16le", root.GetProperty("audio_format").GetString());
        Assert.Equal(16000, root.GetProperty("sample_rate").GetInt32());
        Assert.Equal(1, root.GetProperty("num_channels").GetInt32());
        Assert.True(root.GetProperty("enable_endpoint_detection").GetBoolean());
        // No language → no hints.
        Assert.False(root.TryGetProperty("language_hints", out _));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData(null)]
    public void BuildConfigMessage_OmitsLanguageHints_ForAutoOrEmpty(string? language)
    {
        var json = SonioxSession.BuildConfigMessage("k", SonioxSession.RealtimeModel, language);

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("language_hints", out _));
    }

    [Fact]
    public void BuildConfigMessage_AddsLanguageHints_WhenLanguageGiven()
    {
        var json = SonioxSession.BuildConfigMessage("k", SonioxSession.RealtimeModel, "de");

        using var doc = JsonDocument.Parse(json);
        var hints = doc.RootElement.GetProperty("language_hints");
        Assert.Equal(JsonValueKind.Array, hints.ValueKind);
        Assert.Equal("de", hints[0].GetString());
    }

    [Fact]
    public void ParseMessage_DiscriminatesFinalAndNonFinalTokens()
    {
        var message = SonioxSession.ParseMessage(
            """
            { "tokens": [
                { "text": "Hello", "is_final": true },
                { "text": " world", "is_final": false }
            ] }
            """
        );

        Assert.Null(message.ErrorMessage);
        Assert.False(message.Finished);
        Assert.Equal(2, message.Tokens.Count);
        Assert.Equal(new SonioxSession.SonioxToken("Hello", true), message.Tokens[0]);
        Assert.Equal(new SonioxSession.SonioxToken(" world", false), message.Tokens[1]);
    }

    [Fact]
    public void ParseMessage_DetectsFinished()
    {
        var message = SonioxSession.ParseMessage("""{ "tokens": [], "finished": true }""");

        Assert.True(message.Finished);
        Assert.Empty(message.Tokens);
        Assert.Null(message.ErrorMessage);
    }

    [Fact]
    public void ParseMessage_SurfacesErrorMessage()
    {
        var message = SonioxSession.ParseMessage(
            """{ "tokens": [], "error_code": 503, "error_message": "service unavailable" }"""
        );

        Assert.Equal("service unavailable", message.ErrorMessage);
    }

    [Fact]
    public void ParseMessage_SurfacesError_WhenOnlyCodePresent()
    {
        var message = SonioxSession.ParseMessage("""{ "error_code": 401 }""");

        Assert.NotNull(message.ErrorMessage);
    }

    [Fact]
    public void ParseMessage_ReturnsEmpty_OnMalformedJson()
    {
        var message = SonioxSession.ParseMessage("not json {");

        Assert.Empty(message.Tokens);
        Assert.False(message.Finished);
        Assert.Null(message.ErrorMessage);
    }

    [Fact]
    public void Aggregator_AccumulatesFinals_AndReplacesProvisionalTail()
    {
        var aggregator = new SonioxTranscriptAggregator();

        var first = aggregator.Apply(
            new SonioxSession.SonioxMessage(
                [new SonioxSession.SonioxToken("Hello", true),
                 new SonioxSession.SonioxToken(" wor", false)],
                Finished: false,
                ErrorMessage: null
            )
        );
        Assert.Equal("Hello wor", first.PreviewText);
        Assert.False(first.Finished);

        // Next message: the provisional tail is replaced (not appended), and a new
        // final token is committed.
        var second = aggregator.Apply(
            new SonioxSession.SonioxMessage(
                [new SonioxSession.SonioxToken(" world", true),
                 new SonioxSession.SonioxToken(" how", false)],
                Finished: false,
                ErrorMessage: null
            )
        );
        Assert.Equal("Hello world how", second.PreviewText);
        Assert.Equal("Hello world", second.FinalText);
    }

    [Fact]
    public void Aggregator_ProducesFullTranscript_OnFinished()
    {
        var aggregator = new SonioxTranscriptAggregator();
        aggregator.Apply(Final("Hello"));
        aggregator.Apply(Final(" there"));
        var finished = aggregator.Apply(
            new SonioxSession.SonioxMessage([], Finished: true, ErrorMessage: null)
        );

        Assert.True(finished.Finished);
        Assert.Equal("Hello there", finished.FinalText);
        Assert.Equal("Hello there", aggregator.FinalText);
    }

    [Fact]
    public void Aggregator_SkipsControlTokens()
    {
        var aggregator = new SonioxTranscriptAggregator();
        var update = aggregator.Apply(
            new SonioxSession.SonioxMessage(
                [new SonioxSession.SonioxToken("Hi", true),
                 new SonioxSession.SonioxToken("<end>", true)],
                Finished: false,
                ErrorMessage: null
            )
        );

        Assert.Equal("Hi", update.FinalText);
        Assert.DoesNotContain("<end>", update.PreviewText);
    }

    private static SonioxSession.SonioxMessage Final(string text) =>
        new([new SonioxSession.SonioxToken(text, true)], Finished: false, ErrorMessage: null);

    private sealed class TestHost : IPluginHostServices
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
        public IPluginEventBus EventBus { get; } = new TestBus();
        public IReadOnlyList<string> AvailableProfileNames => [];

        public void Log(PluginLogLevel level, string message) { }

        public void NotifyCapabilitiesChanged() { }

        public IPluginLocalization Localization { get; } = new TestLocalization();
    }

    private sealed class TestLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];

        public string GetString(string key) => key;

        public string GetString(string key, params object[] args) => string.Format(key, args);
    }

    private sealed class TestBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent)
            where T : PluginEvent { }

        public IDisposable Subscribe<T>(Func<T, Task> handler)
            where T : PluginEvent => new NoOp();
    }

    private sealed class NoOp : IDisposable
    {
        public void Dispose() { }
    }
}
