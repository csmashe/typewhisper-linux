using System.Runtime.CompilerServices;
using TypeWhisper.Linux.Services.Plugins;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

using TypeWhisper.Plugin.FillerWords;

namespace TypeWhisper.PluginSystem.Tests;

/// <summary>Tests filler word filtering and plugin behavior.</summary>
public sealed class FillerWordsPluginTests
{
    private static readonly PostProcessingContext s_context = new() { SourceLanguage = "en" };
    private static readonly PostProcessingContext s_germanContext = new() { SourceLanguage = "de-DE" };
    private static readonly PostProcessingContext s_unknownLanguageContext = new();
    private static readonly JsonSerializerOptions s_manifestJsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifestPath = Path.Join(PluginDirectory(), "manifest.json");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            s_manifestJsonOptions);

        var sut = new FillerWordsPlugin();

        Assert.NotNull(manifest);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    [Fact]
    public void PluginId_IsExpectedValue() =>
        Assert.Equal("com.typewhisper.filler-words", new FillerWordsPlugin().PluginId);

    [Fact]
    public void Priority_IsExpectedValue() =>
        Assert.Equal(70, new FillerWordsPlugin().Priority);

    [Fact]
    public async Task ProcessAsync_RemovesDefaultFillerWords()
    {
        var sut = new FillerWordsPlugin();
        await sut.ActivateAsync(new TestPluginHostServices());

        var result = await sut.ProcessAsync("So um I think uh this works", s_context, CancellationToken.None);

        Assert.Equal("So I think this works", result);
    }

    [Theory]
    [InlineData("Es geht um Geld", "Es geht um Geld")]
    [InlineData("Das ist ähm nicht gut", "Das ist nicht gut")]
    [InlineData("Wir treffen uns um acht Uhr", "Wir treffen uns um acht Uhr")]
    public async Task ProcessAsync_GermanDefaults_KeepThePrepositionUm(string input, string expected)
    {
        var sut = new FillerWordsPlugin();
        await sut.ActivateAsync(new TestPluginHostServices());

        Assert.Equal(expected, await sut.ProcessAsync(input, s_germanContext, CancellationToken.None));
    }

    [Fact]
    public async Task ProcessAsync_UnknownLanguage_AppliesOnlyUnscopedWords()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("words", "meh\nen: um");
        var sut = new FillerWordsPlugin();
        await sut.ActivateAsync(host);

        Assert.Equal("um yes", await sut.ProcessAsync("um meh yes", s_unknownLanguageContext, CancellationToken.None));
        Assert.Equal("yes", await sut.ProcessAsync("um meh yes", s_context, CancellationToken.None));
    }

    [Fact]
    public async Task ProcessAsync_UsesConfiguredWords()
    {
        var host = new TestPluginHostServices();
        var sut = new FillerWordsPlugin();
        await sut.ActivateAsync(host);
        sut.Settings!.WordsText = "basically";

        var result = await sut.ProcessAsync("It is basically fine, um yes", s_context, CancellationToken.None);

        Assert.Equal("It is fine, um yes", result);
    }

    [Fact]
    public async Task ProcessAsync_ReturnsTextUnchanged_WhenListIsEmpty()
    {
        var host = new TestPluginHostServices();
        var sut = new FillerWordsPlugin();
        await sut.ActivateAsync(host);
        sut.Settings!.WordsText = "   ";

        var result = await sut.ProcessAsync("So um I think", s_context, CancellationToken.None);

        Assert.Equal("So um I think", result);
    }

    [Fact]
    public async Task ActivateAsync_SeedsDefaultWords()
    {
        var host = new TestPluginHostServices();
        var sut = new FillerWordsPlugin();

        await sut.ActivateAsync(host);

        Assert.Equal(FillerWordsSettingsStore.DefaultWordsText, host.GetSetting<string>("words"));
    }

    [Fact]
    public async Task WordsText_PersistsToHostSettings()
    {
        var host = new TestPluginHostServices();
        var sut = new FillerWordsPlugin();
        await sut.ActivateAsync(host);

        sut.Settings!.WordsText = "meh\nwelp";

        Assert.Equal("meh\nwelp", host.GetSetting<string>("words"));
        Assert.Equal(2, sut.Settings.WordCount);
    }

    [Fact]
    public async Task ActivateAsync_KeepsStoredWords()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("words", "welp");
        var sut = new FillerWordsPlugin();

        await sut.ActivateAsync(host);

        Assert.Equal("welp", sut.Settings!.WordsText);
    }

    [Fact]
    public async Task ResetToDefaults_RestoresBuiltInList()
    {
        var host = new TestPluginHostServices();
        var sut = new FillerWordsPlugin();
        await sut.ActivateAsync(host);
        sut.Settings!.WordsText = "welp";

        sut.Settings.ResetToDefaults();

        Assert.Equal(FillerWordsSettingsStore.DefaultWordsText, sut.Settings.WordsText);
        Assert.Equal(FillerWordFilter.DefaultFillerWords.Count, sut.Settings.WordCount);
    }

    [Fact]
    public async Task DeactivateAsync_ClearsSettings()
    {
        var sut = new FillerWordsPlugin();
        await sut.ActivateAsync(new TestPluginHostServices());

        await sut.DeactivateAsync();

        Assert.Null(sut.Settings);
        Assert.Equal(FillerWordsSettingsStore.DefaultWordsText, await sut.GetSettingValueAsync("words"));
    }

    [Fact]
    public void GetSettingDefinitions_ExposesOneMultilineWordsField()
    {
        var sut = new FillerWordsPlugin();
        sut.SetLocalization(new PluginLocalization(PluginDirectory(), "en"));

        var definition = Assert.Single(sut.GetSettingDefinitions());

        Assert.Equal("words", definition.Key);
        Assert.Equal(PluginSettingKind.Multiline, definition.Kind);
        Assert.Equal("Filler words", definition.Label);
        Assert.Equal("One word per line; commas and semicolons also separate words. Start a line with a language code and a colon (de: äh, ähm) to apply it to that spoken language only; other lines apply to every language.", definition.Description);
    }

    [Fact]
    public async Task SetSettingValue_Null_ResetsToDefaults()
    {
        var host = new TestPluginHostServices();
        var sut = new FillerWordsPlugin();
        await sut.ActivateAsync(host);
        await sut.SetSettingValueAsync("words", "welp");

        await sut.SetSettingValueAsync("words", null);

        Assert.Equal(FillerWordsSettingsStore.DefaultWordsText, await sut.GetSettingValueAsync("words"));
        Assert.Equal(FillerWordsSettingsStore.DefaultWordsText, host.GetSetting<string>("words"));
    }

    [Fact]
    public async Task SetSettingValue_PersistsAndAffectsProcessing()
    {
        var host = new TestPluginHostServices();
        var sut = new FillerWordsPlugin();
        await sut.ActivateAsync(host);

        await sut.SetSettingValueAsync("words", "basically");

        Assert.Equal("basically", host.GetSetting<string>("words"));
        Assert.Equal("basically", await sut.GetSettingValueAsync("words"));
        Assert.Equal("It is fine, um yes", await sut.ProcessAsync(
            "It is basically fine, um yes", s_context, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_ReportsWordCount()
    {
        var sut = new FillerWordsPlugin();
        await sut.ActivateAsync(new TestPluginHostServices());
        await sut.SetSettingValueAsync("words", "welp, WELP; meh");

        var result = await sut.ValidateAsync();

        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.Equal("2 words", result.Message);
    }

    [Fact]
    public void Localization_ShipsManifestKeysInEveryLocaleWithRealGerman()
    {
        string[] keys = ["Settings.Title", "Settings.Hint", "Settings.WordCount", "Manifest.Name", "Manifest.Description"];
        foreach (var locale in new[] { "en", "de", "es", "ru" })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(
                Path.Join(PluginDirectory(), "Localization", locale + ".json")));
            var localization = new PluginLocalization(PluginDirectory(), locale);
            foreach (var key in keys)
            {
                Assert.True(document.RootElement.TryGetProperty(key, out var value));
                Assert.False(string.IsNullOrWhiteSpace(value.GetString()));
                Assert.NotEqual(key, localization.GetString(key));
            }
        }

        var en = new PluginLocalization(PluginDirectory(), "en");
        var de = new PluginLocalization(PluginDirectory(), "de");
        Assert.NotEqual(en.GetString("Settings.Title"), de.GetString("Settings.Title"));
        Assert.NotEqual(en.GetString("Manifest.Name"), de.GetString("Manifest.Name"));
        Assert.NotEqual(en.GetString("Manifest.Description"), de.GetString("Manifest.Description"));
    }

    private static string PluginDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Join(Path.GetDirectoryName(thisFile)!, "..", "..",
            "plugins", "TypeWhisper.Plugin.FillerWords"));

    private sealed class TestPluginHostServices : IPluginHostServices
    {
        private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly Dictionary<string, JsonElement> _settings = [];

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value) ? value.Deserialize<T>(s_jsonOptions) : default;

        public void SetSetting<T>(string key, T value) =>
            _settings[key] = JsonSerializer.SerializeToElement(value, s_jsonOptions);

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new NoOpEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() { }
        public IPluginLocalization Localization { get; } = new PluginLocalization(PluginDirectory(), "en");

        public Task StoreSecretAsync(string key, string value) => Task.CompletedTask;
        public Task<string?> LoadSecretAsync(string key) => Task.FromResult<string?>(null);
        public Task DeleteSecretAsync(string key) => Task.CompletedTask;
    }

    private sealed class NoOpEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }
        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent => new NoOpSubscription();

        private sealed class NoOpSubscription : IDisposable
        {
            public void Dispose() { }
        }
    }

}
