using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SpeechFeedbackServiceTests
{
    [Fact]
    public void AvailableProviders_includes_system_and_plugin_tts()
    {
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());
        var plugin = new FakeTtsProvider("cloud", "Cloud Voice", true);
        var manager = TestPluginManagerFactory.Create(ttsProviders: [plugin]);
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            new FakeTtsProvider("linux-system", "Linux system", true)
        );

        var providers = sut.AvailableProviders.Select(provider => provider.Id).ToArray();

        Assert.Equal(["linux-system", "cloud"], providers);
    }

    [Fact]
    public void SelectVoice_passes_default_voice_as_null()
    {
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());
        var manager = TestPluginManagerFactory.Create();
        var systemProvider = new FakeTtsProvider("linux-system", "Linux system", true);
        using var sut = new SpeechFeedbackService(settings.Object, manager, systemProvider);

        sut.SelectVoice("linux-system", SpeechFeedbackService.DefaultVoiceOptionId);

        Assert.Null(systemProvider.SelectedVoiceId);
    }

    [Fact]
    public void EffectiveProvider_falls_back_to_system_when_selected_plugin_is_not_configured()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackProviderId = "cloud" }
        );
        var plugin = new FakeTtsProvider("cloud", "Cloud Voice", false);
        var manager = TestPluginManagerFactory.Create(ttsProviders: [plugin]);
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            new FakeTtsProvider("linux-system", "Linux system", true)
        );

        Assert.Equal("linux-system", sut.EffectiveProviderId);
    }

    [Fact]
    public async Task SpeakAutomaticTranscription_substitutes_configured_language_when_request_has_none()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings
            {
                Language = "de",
                SpokenFeedbackEnabled = true,
                SpokenFeedbackProviderId = "cloud"
            }
        );
        var plugin = new FakeTtsProvider("cloud", "Cloud Voice", true);
        var manager = TestPluginManagerFactory.Create(ttsProviders: [plugin]);
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            new FakeTtsProvider("linux-system", "Linux system", true)
        );

        sut.SpeakAutomaticTranscription("Hallo Welt");

        await WaitUntilAsync(() => plugin.Requests.Count > 0);

        var request = Assert.Single(plugin.Requests);
        Assert.Equal("de", request.Language);
        Assert.Equal(TtsPurpose.Transcription, request.Purpose);
    }

    [Fact]
    public async Task SpeakAutomaticTranscription_keeps_explicit_request_language()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings
            {
                Language = "de",
                SpokenFeedbackEnabled = true,
                SpokenFeedbackProviderId = "cloud"
            }
        );
        var plugin = new FakeTtsProvider("cloud", "Cloud Voice", true);
        var manager = TestPluginManagerFactory.Create(ttsProviders: [plugin]);
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            new FakeTtsProvider("linux-system", "Linux system", true)
        );

        sut.SpeakAutomaticTranscription("Hallo Welt", "fr");

        await WaitUntilAsync(() => plugin.Requests.Count > 0);

        var request = Assert.Single(plugin.Requests);
        Assert.Equal("fr", request.Language);
    }

    [Fact]
    public async Task SpeakAutomaticTranscription_skips_configured_language_fallback_when_disabled()
    {
        // Callers that have already resolved the readback language opt out of
        // the configured-language fallback; a null language must stay null.
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings
            {
                Language = "de",
                SpokenFeedbackEnabled = true,
                SpokenFeedbackProviderId = "cloud"
            }
        );
        var plugin = new FakeTtsProvider("cloud", "Cloud Voice", true);
        var manager = TestPluginManagerFactory.Create(ttsProviders: [plugin]);
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            new FakeTtsProvider("linux-system", "Linux system", true)
        );

        sut.SpeakAutomaticTranscription(
            "Hallo Welt",
            language: null,
            useConfiguredLanguageFallback: false
        );

        await WaitUntilAsync(() => plugin.Requests.Count > 0);

        var request = Assert.Single(plugin.Requests);
        Assert.Null(request.Language);
        Assert.Equal(TtsPurpose.Transcription, request.Purpose);
    }

    // The service speaks on a fire-and-forget background task, so poll until
    // the captured request is observable rather than asserting synchronously.
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(condition(), "Condition was not met within the timeout.");
    }

    private sealed class FakeTtsProvider(string providerId, string displayName, bool configured)
        : ITtsProviderPlugin
    {
        public string PluginId => $"plugin.{providerId}";
        public string PluginName => displayName;
        public string PluginVersion => "1.0.0";
        public string ProviderId => providerId;
        public string ProviderDisplayName => displayName;
        public bool IsConfigured => configured;
        public IReadOnlyList<PluginVoiceInfo> AvailableVoices { get; } = [new("voice", "Voice")];
        public string? SelectedVoiceId { get; private set; }
        public List<TtsSpeakRequest> Requests { get; } = [];

        public Task ActivateAsync(IPluginHostServices host)
        {
            return Task.CompletedTask;
        }

        public Task DeactivateAsync()
        {
            return Task.CompletedTask;
        }

        public void SelectVoice(string? voiceId)
        {
            SelectedVoiceId = voiceId;
        }

        public Task<ITtsPlaybackSession> SpeakAsync(
            TtsSpeakRequest request,
            CancellationToken ct
        )
        {
            Requests.Add(request);
            return Task.FromResult<ITtsPlaybackSession>(InactiveSession.Instance);
        }

        public void Dispose() { }
    }

    private sealed class InactiveSession : ITtsPlaybackSession
    {
        public static InactiveSession Instance { get; } = new();
        public bool IsActive => false;

        public event EventHandler? Completed
        {
            add { value?.Invoke(this, EventArgs.Empty); }
            remove { }
        }

        public void Stop() { }
    }
}