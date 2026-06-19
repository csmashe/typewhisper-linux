using System.Text.Json;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Plugin.Gladia;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using GladiaSession = TypeWhisper.Plugin.Gladia.GladiaStreamingSession;

namespace TypeWhisper.PluginSystem.Tests;

public class GladiaPluginTests
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
                "TypeWhisper.Plugin.Gladia",
                "manifest.json"
            )
        );
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        var sut = new GladiaPlugin();

        Assert.NotNull(manifest);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    [Fact]
    public void GetSettingDefinitions_LocalizesLabels_WithoutActivation()
    {
        // Regression: a disabled plugin is never activated, so _host (and thus the
        // host-provided localization) is null. The loader injects the catalog via
        // IPluginLocalizationAware at load, so the settings panel must still render
        // real labels — not raw keys like "Settings.ApiKey" — before the user
        // enables the plugin.
        var pluginDir = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "plugins", "TypeWhisper.Plugin.Gladia"
            )
        );

        var sut = new GladiaPlugin();
        // No ActivateAsync — mimics a discovered-but-disabled plugin.
        ((IPluginLocalizationAware)sut).SetLocalization(new PluginLocalization(pluginDir, "en"));

        var definitions = sut.GetSettingDefinitions();

        Assert.NotEmpty(definitions);
        foreach (var definition in definitions)
        {
            Assert.False(
                definition.Label.StartsWith("Settings.", StringComparison.Ordinal),
                $"Unlocalized label leaked as raw key: {definition.Label}"
            );
            Assert.False(
                (definition.Description ?? string.Empty).StartsWith("Settings.", StringComparison.Ordinal),
                $"Unlocalized description leaked as raw key: {definition.Description}"
            );
        }

        Assert.Contains(definitions, d => d.Label == "API key");
    }

    [Fact]
    public void ManifestNameAndDescription_AreLocalized()
    {
        // The Plugins-list card text (name + description) is resolved from the
        // plugin's catalog via Manifest.Name / Manifest.Description, falling back
        // to the manifest literal. Guard that the keys exist and German is
        // actually translated (not just echoing English / the raw key).
        var pluginDir = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "plugins", "TypeWhisper.Plugin.Gladia"
            )
        );

        var en = new PluginLocalization(pluginDir, "en");
        var de = new PluginLocalization(pluginDir, "de");

        // Keys are present (GetString echoes the key back on a miss).
        Assert.NotEqual("Manifest.Description", en.GetString("Manifest.Description"));
        Assert.NotEqual("Manifest.Description", de.GetString("Manifest.Description"));

        // German description is a real translation, not the English string.
        Assert.NotEqual(
            en.GetString("Manifest.Description"),
            de.GetString("Manifest.Description")
        );
    }

    [Fact]
    public async Task ActivateAsync_SetsIdentityAndSupportsStreaming()
    {
        var host = new TestHost();
        host.Secrets["api-key"] = "gladia-key";

        var sut = new GladiaPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("com.typewhisper.gladia", sut.PluginId);
        Assert.Equal("gladia", sut.ProviderId);
        Assert.True(sut.IsConfigured);
        Assert.True(sut.SupportsStreaming);
    }

    [Fact]
    public async Task StartStreamingAsync_Throws_WhenNotConfigured()
    {
        var sut = new GladiaPlugin();
        await sut.ActivateAsync(new TestHost());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.StartStreamingAsync(null, CancellationToken.None)
        );
    }

    [Fact]
    public void BuildInitRequest_UsesWavPcmAndEnablesPartials()
    {
        var json = GladiaSession.BuildInitRequest("de", 16000);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("wav/pcm", root.GetProperty("encoding").GetString());
        Assert.Equal(16, root.GetProperty("bit_depth").GetInt32());
        Assert.Equal(16000, root.GetProperty("sample_rate").GetInt32());
        Assert.True(
            root.GetProperty("messages_config").GetProperty("receive_partial_transcripts").GetBoolean()
        );
        Assert.Equal(
            "de",
            root.GetProperty("language_config").GetProperty("languages")[0].GetString()
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    public void BuildInitRequest_OmitsLanguageConfig_ForAutoOrEmpty(string? language)
    {
        var json = GladiaSession.BuildInitRequest(language, 16000);

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("language_config", out _));
    }

    [Fact]
    public void ParseSessionUrl_ExtractsUrl()
    {
        var url = GladiaSession.ParseSessionUrl(
            """{ "id": "abc", "url": "wss://api.gladia.io/v2/live?token=xyz" }"""
        );

        Assert.Equal("wss://api.gladia.io/v2/live?token=xyz", url);
    }

    [Fact]
    public void ParseSessionUrl_ReturnsNull_WhenMissingOrMalformed()
    {
        Assert.Null(GladiaSession.ParseSessionUrl("""{ "id": "abc" }"""));
        Assert.Null(GladiaSession.ParseSessionUrl("not json"));
    }

    [Fact]
    public void ParseMessage_ReadsFinalTranscript()
    {
        var msg = GladiaSession.ParseMessage(
            """
            { "type": "transcript",
              "data": { "is_final": true, "utterance": { "text": " Hello world" } } }
            """
        );

        Assert.Equal("transcript", msg.MessageType);
        Assert.Equal(" Hello world", msg.Text);
        Assert.True(msg.IsFinal);
    }

    [Fact]
    public void ParseMessage_ReadsPartialTranscript()
    {
        var msg = GladiaSession.ParseMessage(
            """
            { "type": "transcript",
              "data": { "is_final": false, "utterance": { "text": "Hello" } } }
            """
        );

        Assert.Equal("Hello", msg.Text);
        Assert.False(msg.IsFinal);
    }

    [Fact]
    public void ParseMessage_IgnoresNonTranscriptTypes()
    {
        var msg = GladiaSession.ParseMessage("""{ "type": "audio_chunk_acknowledgment" }""");

        Assert.Equal("audio_chunk_acknowledgment", msg.MessageType);
        Assert.Null(msg.Text);
    }

    [Fact]
    public void ParseMessage_ReturnsEmpty_OnMalformedJson()
    {
        var msg = GladiaSession.ParseMessage("garbage {");

        Assert.Equal("", msg.MessageType);
        Assert.Null(msg.Text);
    }

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
