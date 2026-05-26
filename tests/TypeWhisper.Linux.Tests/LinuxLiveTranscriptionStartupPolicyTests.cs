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
        // Without the streaming opt-in, a SupportsStreaming plugin falls
        // through to the cloud branch and stays gated on the batch opt-in.
        var settings = AppSettings.Default with
        {
            LiveTranscriptionEnabled = true,
            LiveTranscriptionStreamingEnabled = false,
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

    [Fact]
    public void Select_WhenStreamingCapableAndOptedIn_ReturnsStreaming()
    {
        var settings = AppSettings.Default with
        {
            LiveTranscriptionEnabled = true,
            LiveTranscriptionStreamingEnabled = true
        };

        var mode = LinuxLiveTranscriptionStartupPolicy.Select(
            settings,
            new FakeTranscriptionEnginePlugin
            {
                SupportsModelDownload = false,
                SupportsStreaming = true
            });

        Assert.Equal(LiveTranscriptionMode.Streaming, mode);
    }

    [Fact]
    public void Select_WhenStreamingCapableButOptedOut_FallsThroughToPolling()
    {
        var settings = AppSettings.Default with
        {
            LiveTranscriptionEnabled = true,
            LiveTranscriptionStreamingEnabled = false
        };

        var mode = LinuxLiveTranscriptionStartupPolicy.Select(
            settings,
            new FakeTranscriptionEnginePlugin
            {
                SupportsModelDownload = true,
                SupportsStreaming = true
            });

        Assert.Equal(LiveTranscriptionMode.Polling, mode);
    }

    [Fact]
    public void Select_WhenStreamingNotCapableButOptedIn_FallsThroughToPolling()
    {
        var settings = AppSettings.Default with
        {
            LiveTranscriptionEnabled = true,
            LiveTranscriptionStreamingEnabled = true,
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
    public void Select_WhenStreamingWinsOverLocalModel_ReturnsStreaming()
    {
        // Pins precedence: the streaming branch sits before the local-model
        // branch, so a plugin that advertises both still gets Streaming when
        // the user opts in.
        var settings = AppSettings.Default with
        {
            LiveTranscriptionEnabled = true,
            LiveTranscriptionStreamingEnabled = true
        };

        var mode = LinuxLiveTranscriptionStartupPolicy.Select(
            settings,
            new FakeTranscriptionEnginePlugin
            {
                SupportsModelDownload = true,
                SupportsStreaming = true
            });

        Assert.Equal(LiveTranscriptionMode.Streaming, mode);
    }

    [Fact]
    public void Select_WhenLiveTranscriptionDisabled_BeatsEverything()
    {
        var settings = AppSettings.Default with
        {
            LiveTranscriptionEnabled = false,
            LiveTranscriptionStreamingEnabled = true,
            OnlineAsrBatchLiveTranscriptionEnabled = true
        };

        var mode = LinuxLiveTranscriptionStartupPolicy.Select(
            settings,
            new FakeTranscriptionEnginePlugin
            {
                SupportsModelDownload = true,
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
