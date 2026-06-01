using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class LinuxLiveTranscriptionStartupPolicyTests
{
    [Fact]
    public void Select_LiveTranscriptionDisabled_ReturnsNone()
    {
        var settings = AppSettings.Default with { LiveTranscriptionEnabled = false };

        var mode = LinuxLiveTranscriptionStartupPolicy.Select(
            settings,
            new FakeTranscriptionEnginePlugin { SupportsModelDownload = true });

        Assert.Equal(LiveTranscriptionMode.None, mode);
    }

    [Fact]
    public void Select_NoPlugin_ReturnsNone()
    {
        var settings = AppSettings.Default with { LiveTranscriptionEnabled = true };

        var mode = LinuxLiveTranscriptionStartupPolicy.Select(settings, plugin: null);

        Assert.Equal(LiveTranscriptionMode.None, mode);
    }

    [Fact]
    public void Select_LocalDownloadablePlugin_ReturnsPolling()
    {
        var settings = AppSettings.Default with { LiveTranscriptionEnabled = true };

        var mode = LinuxLiveTranscriptionStartupPolicy.Select(
            settings,
            new FakeTranscriptionEnginePlugin { SupportsModelDownload = true });

        Assert.Equal(LiveTranscriptionMode.Polling, mode);
    }

    [Fact]
    public void Select_CloudPluginWithoutOptIn_ReturnsNone()
    {
        var settings = AppSettings.Default with
        {
            LiveTranscriptionEnabled = true,
            OnlineAsrBatchLiveTranscriptionEnabled = false
        };

        var mode = LinuxLiveTranscriptionStartupPolicy.Select(
            settings,
            new FakeTranscriptionEnginePlugin
            {
                SupportsModelDownload = false,
                SupportsStreaming = false
            });

        Assert.Equal(LiveTranscriptionMode.None, mode);
    }

    [Fact]
    public void Select_CloudPluginWithOptIn_ReturnsPolling()
    {
        var settings = AppSettings.Default with
        {
            LiveTranscriptionEnabled = true,
            OnlineAsrBatchLiveTranscriptionEnabled = true
        };

        var mode = LinuxLiveTranscriptionStartupPolicy.Select(
            settings,
            new FakeTranscriptionEnginePlugin
            {
                SupportsModelDownload = false,
                SupportsStreaming = false
            });

        Assert.Equal(LiveTranscriptionMode.Polling, mode);
    }

    [Fact]
    public void Select_StreamingCapableCloudPluginWithoutOptIn_ReturnsNone()
    {
        // The Linux fork has no real websocket streaming path: it polls
        // SupportsStreaming plugins with full-buffer batch uploads, exactly as
        // costly as any other cloud provider. So SupportsStreaming is not a
        // free pass — the opt-in is still required.
        var settings = AppSettings.Default with
        {
            LiveTranscriptionEnabled = true,
            OnlineAsrBatchLiveTranscriptionEnabled = false
        };

        var mode = LinuxLiveTranscriptionStartupPolicy.Select(
            settings,
            new FakeTranscriptionEnginePlugin
            {
                SupportsModelDownload = false,
                SupportsStreaming = true
            });

        Assert.Equal(LiveTranscriptionMode.None, mode);
    }

    private sealed class FakeTranscriptionEnginePlugin : ITranscriptionEnginePlugin
    {
        public bool SupportsModelDownload { get; init; }
        public bool SupportsStreaming { get; init; }

        public string PluginId => "com.test.fake";
        public string PluginName => "Fake";
        public string PluginVersion => "1.0.0";
        public string ProviderId => "fake";
        public string ProviderDisplayName => "Fake";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels => [];
        public string? SelectedModelId => null;
        public bool SupportsTranslation => false;

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public void SelectModel(string modelId) { }
        public void Dispose() { }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
