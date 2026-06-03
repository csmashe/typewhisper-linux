using System.Text.Json;
using TypeWhisper.Plugin.Speechmatics;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using SmSession = TypeWhisper.Plugin.Speechmatics.SpeechmaticsStreamingSession;

namespace TypeWhisper.PluginSystem.Tests;

public class SpeechmaticsPluginTests
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
                "TypeWhisper.Plugin.Speechmatics",
                "manifest.json"
            )
        );
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        var sut = new SpeechmaticsPlugin();

        Assert.NotNull(manifest);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    [Fact]
    public async Task ActivateAsync_SetsIdentityAndSupportsStreaming()
    {
        var host = new TestHost();
        host.Secrets["api-key"] = "sm-key";

        var sut = new SpeechmaticsPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("com.typewhisper.speechmatics", sut.PluginId);
        Assert.Equal("speechmatics", sut.ProviderId);
        Assert.True(sut.IsConfigured);
        Assert.True(sut.SupportsStreaming);
    }

    [Fact]
    public async Task StartStreamingAsync_Throws_WhenNotConfigured()
    {
        var sut = new SpeechmaticsPlugin();
        await sut.ActivateAsync(new TestHost());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartStreamingAsync(null, CancellationToken.None)
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData(" Auto ")]
    public async Task StartStreamingAsync_RejectsAutoLanguage(string? language)
    {
        // Speechmatics has no auto-detect; the host collapses "auto" to null. Rather
        // than silently streaming as English, reject so the host falls back to batch.
        var host = new TestHost();
        host.Secrets["api-key"] = "sm-key";
        var sut = new SpeechmaticsPlugin();
        await sut.ActivateAsync(host);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => sut.StartStreamingAsync(language, CancellationToken.None)
        );
    }

    [Fact]
    public void BuildStartRecognition_UsesRawPcmAndEnablesPartials()
    {
        var json = SmSession.BuildStartRecognition("de", 16000);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("StartRecognition", root.GetProperty("message").GetString());

        var format = root.GetProperty("audio_format");
        Assert.Equal("raw", format.GetProperty("type").GetString());
        Assert.Equal("pcm_s16le", format.GetProperty("encoding").GetString());
        Assert.Equal(16000, format.GetProperty("sample_rate").GetInt32());

        var config = root.GetProperty("transcription_config");
        Assert.Equal("de", config.GetProperty("language").GetString());
        Assert.Equal("enhanced", config.GetProperty("operating_point").GetString());
        Assert.True(config.GetProperty("enable_partials").GetBoolean());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    public void BuildStartRecognition_DefaultsLanguageToEnglish(string? language)
    {
        var json = SmSession.BuildStartRecognition(language, 16000);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(
            "en",
            doc.RootElement.GetProperty("transcription_config").GetProperty("language").GetString()
        );
    }

    [Fact]
    public void BuildEndOfStream_CarriesSeqNo()
    {
        var json = SmSession.BuildEndOfStream(42);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("EndOfStream", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal(42, doc.RootElement.GetProperty("last_seq_no").GetInt32());
    }

    [Fact]
    public void ParseMessage_ReadsTranscriptFromMetadata()
    {
        // Canonical real-time shape: the plain-text segment is under metadata.transcript.
        var msg = SmSession.ParseMessage(
            """
            { "message": "AddTranscript", "format": "2.1",
              "metadata": { "transcript": "hello world", "start_time": 0.0, "end_time": 1.5 },
              "results": [] }
            """
        );

        Assert.Equal("AddTranscript", msg.MessageType);
        Assert.Equal("hello world", msg.Transcript);
        Assert.Null(msg.ErrorReason);
    }

    [Fact]
    public void ParseMessage_FallsBackToRootLevelTranscript()
    {
        // Defensive fallback for a root-level "transcript" shape.
        var msg = SmSession.ParseMessage(
            """{ "message": "AddPartialTranscript", "transcript": "hi there", "metadata": {} }"""
        );

        Assert.Equal("AddPartialTranscript", msg.MessageType);
        Assert.Equal("hi there", msg.Transcript);
    }

    [Fact]
    public void ParseMessage_SurfacesErrorReason()
    {
        var msg = SmSession.ParseMessage(
            """{ "message": "Error", "type": "quota_exceeded", "reason": "limit reached", "code": 4005 }"""
        );

        Assert.Equal("Error", msg.MessageType);
        Assert.Equal("limit reached", msg.ErrorReason);
    }

    [Fact]
    public void ParseMessage_ReturnsEmpty_OnMalformedJson()
    {
        var msg = SmSession.ParseMessage("garbage {");

        Assert.Equal("", msg.MessageType);
        Assert.Null(msg.Transcript);
    }

    [Fact]
    public void Aggregator_AccumulatesFinals_AndReplacesPartialTail()
    {
        var aggregator = new SpeechmaticsTranscriptAggregator();

        var partial = aggregator.Apply(Msg("AddPartialTranscript", "hello wor"));
        Assert.Equal("hello wor", partial.PreviewText);
        Assert.False(partial.Completed);

        var final1 = aggregator.Apply(Msg("AddTranscript", "hello world"));
        Assert.Equal("hello world", final1.PreviewText);
        Assert.Equal("hello world", final1.FinalText);

        // Partial tail is replaced, not appended onto the accumulated final.
        var partial2 = aggregator.Apply(Msg("AddPartialTranscript", " how are"));
        Assert.Equal("hello world how are", partial2.PreviewText);

        var final2 = aggregator.Apply(Msg("AddTranscript", " how are you"));
        Assert.Equal("hello world how are you", final2.FinalText);
    }

    [Fact]
    public void Aggregator_SignalsCompleted_OnEndOfTranscript()
    {
        var aggregator = new SpeechmaticsTranscriptAggregator();
        aggregator.Apply(Msg("AddTranscript", "all done"));
        var end = aggregator.Apply(Msg("EndOfTranscript", null));

        Assert.True(end.Completed);
        Assert.Equal("all done", end.FinalText);
    }

    private static SmSession.SpeechmaticsMessage Msg(string type, string? transcript) =>
        new(type, transcript, null);

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
