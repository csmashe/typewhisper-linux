using TypeWhisper.Core.Interfaces;
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
        var plugin = new FakeTtsProvider("cloud", "Cloud Voice", configured: true);
        var manager = TestPluginManagerFactory.Create(ttsProviders: [plugin]);
        using var sut = new SpeechFeedbackService(settings.Object, manager, new FakeTtsProvider("linux-system", "Linux system", configured: true));

        var providers = sut.AvailableProviders.Select(provider => provider.Id).ToArray();

        Assert.Equal(["linux-system", "cloud"], providers);
    }

    [Fact]
    public void SelectVoice_passes_default_voice_as_null()
    {
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());
        var manager = TestPluginManagerFactory.Create();
        var systemProvider = new FakeTtsProvider("linux-system", "Linux system", configured: true);
        using var sut = new SpeechFeedbackService(settings.Object, manager, systemProvider);

        sut.SelectVoice("linux-system", SpeechFeedbackService.DefaultVoiceOptionId);

        Assert.Null(systemProvider.SelectedVoiceId);
    }

    [Fact]
    public void EffectiveProvider_falls_back_to_system_when_selected_plugin_is_not_configured()
    {
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings
        {
            SpokenFeedbackProviderId = "cloud"
        });
        var plugin = new FakeTtsProvider("cloud", "Cloud Voice", configured: false);
        var manager = TestPluginManagerFactory.Create(ttsProviders: [plugin]);
        using var sut = new SpeechFeedbackService(settings.Object, manager, new FakeTtsProvider("linux-system", "Linux system", configured: true));

        Assert.Equal("linux-system", sut.EffectiveProviderId);
    }

    private sealed class FakeTtsProvider(string providerId, string displayName, bool configured) : ITtsProviderPlugin
    {
        public string PluginId => $"plugin.{providerId}";
        public string PluginName => displayName;
        public string PluginVersion => "1.0.0";
        public string ProviderId => providerId;
        public string ProviderDisplayName => displayName;
        public bool IsConfigured => configured;
        public IReadOnlyList<PluginVoiceInfo> AvailableVoices { get; } = [new("voice", "Voice")];
        public string? SelectedVoiceId { get; private set; }

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public void SelectVoice(string? voiceId) => SelectedVoiceId = voiceId;
        public Task<ITtsPlaybackSession> SpeakAsync(TtsSpeakRequest request, CancellationToken ct) =>
            Task.FromResult<ITtsPlaybackSession>(InactiveSession.Instance);
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
