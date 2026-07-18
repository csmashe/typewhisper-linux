using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SpeechFeedbackServiceTests
{
    private static readonly TimeSpan s_testGuard = TimeSpan.FromSeconds(2);

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

        await plugin.RequestReceived.Task.WaitAsync(s_testGuard);

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

        await plugin.RequestReceived.Task.WaitAsync(s_testGuard);

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

        await plugin.RequestReceived.Task.WaitAsync(s_testGuard);

        var request = Assert.Single(plugin.Requests);
        Assert.Null(request.Language);
        Assert.Equal(TtsPurpose.Transcription, request.Purpose);
    }

    [Fact]
    public async Task Stop_before_capture_stops_prior_playback_before_awaiting_completion()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackEnabled = true }
        );
        var manager = TestPluginManagerFactory.Create();
        var priorSession = new ControlledPlaybackSession();
        var provider = new ControlledTtsProvider(priorSession);
        var delay = new ControlledDelay();
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            provider,
            delay.WaitAsync
        );
        sut.SpeakAutomaticTranscription("prior playback");
        await priorSession.HandlerAttached.Task.WaitAsync(s_testGuard);

        var stop = sut.StopCurrentPlaybackBeforeCaptureAsync();

        Assert.Equal(1, priorSession.StopCount);
        Assert.False(stop.IsCompleted);

        priorSession.Complete();
        await stop.WaitAsync(s_testGuard);
    }

    [Fact]
    public async Task Stop_before_capture_returns_at_its_finite_bound_when_completion_is_missing()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackEnabled = true }
        );
        var manager = TestPluginManagerFactory.Create();
        var priorSession = new ControlledPlaybackSession();
        var provider = new ControlledTtsProvider(priorSession);
        var delay = new ControlledDelay();
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            provider,
            delay.WaitAsync
        );
        sut.SpeakAutomaticTranscription("prior playback");
        await priorSession.HandlerAttached.Task.WaitAsync(s_testGuard);

        var stop = sut.StopCurrentPlaybackBeforeCaptureAsync();
        var timeout = await delay.NextRequestAsync();

        Assert.Equal(SpeechFeedbackService.s_stopPlaybackTimeout, timeout.Duration);
        Assert.Equal(1, priorSession.StopCount);
        timeout.Complete();
        await stop.WaitAsync(s_testGuard);
    }

    [Fact]
    public async Task Recording_start_announcement_awaits_session_completion()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackEnabled = true }
        );
        var manager = TestPluginManagerFactory.Create();
        var session = new ControlledPlaybackSession();
        var provider = new ControlledTtsProvider(session);
        var delay = new ControlledDelay();
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            provider,
            delay.WaitAsync
        );

        var announcement = sut.AnnounceRecordingStartedAsync(spokenFeedbackEnabled: true);
        await session.HandlerAttached.Task.WaitAsync(s_testGuard);

        Assert.False(announcement.IsCompleted);
        session.Complete();
        await announcement.WaitAsync(s_testGuard);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Recording_start_announcement_timeout_stops_session_before_returning()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackEnabled = true }
        );
        var manager = TestPluginManagerFactory.Create();
        var session = new ControlledPlaybackSession(completeOnStop: true);
        var provider = new ControlledTtsProvider(session);
        var delay = new ControlledDelay();
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            provider,
            delay.WaitAsync
        );

        var announcement = sut.AnnounceRecordingStartedAsync(spokenFeedbackEnabled: true);
        await session.HandlerAttached.Task.WaitAsync(s_testGuard);
        var timeout = await delay.NextRequestAsync();

        Assert.Equal(SpeechFeedbackService.s_recordingAnnouncementTimeout, timeout.Duration);
        Assert.Equal(0, session.StopCount);
        timeout.Complete();

        await announcement.WaitAsync(s_testGuard);
        Assert.Equal(1, session.StopCount);
    }

    [Fact]
    public async Task Older_completion_cannot_clear_or_complete_newer_request()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { SpokenFeedbackEnabled = true }
        );
        var manager = TestPluginManagerFactory.Create();
        var olderSession = new ControlledPlaybackSession();
        var newerSession = new ControlledPlaybackSession();
        var provider = new ControlledTtsProvider(olderSession, newerSession);
        var delay = new ControlledDelay();
        using var sut = new SpeechFeedbackService(
            settings.Object,
            manager,
            provider,
            delay.WaitAsync
        );
        sut.SpeakAutomaticTranscription("older playback");
        await olderSession.HandlerAttached.Task.WaitAsync(s_testGuard);

        var newerAnnouncement = sut.AnnounceRecordingStartedAsync(
            spokenFeedbackEnabled: true
        );
        await newerSession.HandlerAttached.Task.WaitAsync(s_testGuard);
        Assert.Equal(1, olderSession.StopCount);

        olderSession.Complete();
        var stopNewer = sut.StopCurrentPlaybackBeforeCaptureAsync();

        Assert.Equal(1, newerSession.StopCount);
        Assert.False(newerAnnouncement.IsCompleted);
        newerSession.Complete();
        await Task.WhenAll(newerAnnouncement, stopNewer).WaitAsync(s_testGuard);
    }

    private sealed class ControlledDelay
    {
        private readonly Queue<DelayRequest> _requests = new();
        private readonly SemaphoreSlim _requestAvailable = new(0);

        public Task WaitAsync(TimeSpan duration)
        {
            var request = new DelayRequest(duration);
            lock (_requests)
            {
                _requests.Enqueue(request);
            }

            _requestAvailable.Release();
            return request.Completion.Task;
        }

        public async Task<DelayRequest> NextRequestAsync()
        {
            await _requestAvailable.WaitAsync().WaitAsync(s_testGuard);
            lock (_requests)
            {
                return _requests.Dequeue();
            }
        }
    }

    private sealed class DelayRequest(TimeSpan duration)
    {
        public TimeSpan Duration { get; } = duration;
        public TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public void Complete()
        {
            Completion.TrySetResult();
        }
    }

    private sealed class ControlledTtsProvider(params ITtsPlaybackSession[] sessions)
        : ITtsProviderPlugin
    {
        private readonly Queue<ITtsPlaybackSession> _sessions = new(sessions);

        public string PluginId => "plugin.controlled";
        public string PluginName => "Controlled";
        public string PluginVersion => "1.0.0";
        public string ProviderId => "controlled";
        public string ProviderDisplayName => "Controlled";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginVoiceInfo> AvailableVoices => [];
        public string? SelectedVoiceId => null;
        public List<TtsSpeakRequest> Requests { get; } = [];

        public Task ActivateAsync(IPluginHostServices host)
        {
            return Task.CompletedTask;
        }

        public Task DeactivateAsync()
        {
            return Task.CompletedTask;
        }

        public void SelectVoice(string? voiceId) { }

        public Task<ITtsPlaybackSession> SpeakAsync(
            TtsSpeakRequest request,
            CancellationToken ct
        )
        {
            Requests.Add(request);
            return Task.FromResult(_sessions.Dequeue());
        }

        public void Dispose() { }
    }

    private sealed class ControlledPlaybackSession(bool completeOnStop = false)
        : ITtsPlaybackSession
    {
        private readonly Lock _sync = new();
        private EventHandler? _completed;
        private bool _isActive = true;
        private int _stopCount;

        public bool IsActive
        {
            get
            {
                lock (_sync)
                {
                    return _isActive;
                }
            }
        }

        public int StopCount => Volatile.Read(ref _stopCount);
        public TaskCompletionSource HandlerAttached { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public event EventHandler? Completed
        {
            add
            {
                if (value is null)
                {
                    return;
                }

                var alreadyCompleted = false;
                lock (_sync)
                {
                    if (_isActive)
                    {
                        _completed += value;
                    }
                    else
                    {
                        alreadyCompleted = true;
                    }
                }

                HandlerAttached.TrySetResult();
                if (alreadyCompleted)
                {
                    value(this, EventArgs.Empty);
                }
            }
            remove
            {
                lock (_sync)
                {
                    _completed -= value;
                }
            }
        }

        public void Stop()
        {
            Interlocked.Increment(ref _stopCount);
            if (completeOnStop)
            {
                Complete();
            }
        }

        public void Complete()
        {
            EventHandler? handlers;
            lock (_sync)
            {
                if (!_isActive)
                {
                    return;
                }

                _isActive = false;
                handlers = _completed;
                _completed = null;
            }

            handlers?.Invoke(this, EventArgs.Empty);
        }
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
        public TaskCompletionSource RequestReceived { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

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
            RequestReceived.TrySetResult();
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
