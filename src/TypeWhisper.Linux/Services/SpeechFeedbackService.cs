using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services;

public sealed record TtsProviderOption(string Id, string DisplayName);

public sealed record TtsVoiceOption(string Id, string DisplayName, string? LocaleIdentifier = null);

public sealed class SpeechFeedbackService : IDisposable
{
    public const string DefaultVoiceOptionId = "__typewhisper_default_voice__";
    private readonly object _lock = new();
    private readonly PluginManager _pluginManager;

    private readonly ISettingsService _settings;
    private readonly ITtsProviderPlugin _systemProvider;
    private bool _disposed;
    private bool _isPlaybackPending;
    private ITtsPlaybackSession? _playbackSession;
    private long _playbackVersion;

    private CancellationTokenSource? _speakCts;

    public SpeechFeedbackService(
        ISettingsService settings,
        PluginManager pluginManager,
        SystemCommandAvailabilityService commands
    )
        : this(settings, pluginManager, new LinuxSystemTtsProvider(settings, commands))
    {
    }

    internal SpeechFeedbackService(
        ISettingsService settings,
        PluginManager pluginManager,
        ITtsProviderPlugin systemProvider
    )
    {
        _settings = settings;
        _pluginManager = pluginManager;
        _systemProvider = systemProvider;
        _pluginManager.PluginStateChanged += OnPluginStateChanged;
    }

    public bool IsAvailable => ResolveSpeakProvider().IsConfigured;
    public string? BackendName => ResolveSpeakProvider().ProviderDisplayName;

    public bool IsSpeaking
    {
        get
        {
            lock (_lock)
            {
                return _isPlaybackPending || _playbackSession?.IsActive == true;
            }
        }
    }

    public IReadOnlyList<TtsProviderOption> AvailableProviders =>
        AllProviders()
            .Select(provider => new TtsProviderOption(
                provider.ProviderId,
                provider.ProviderDisplayName
            ))
            .ToList();

    public string EffectiveProviderId => ResolveSpeakProvider().ProviderId;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pluginManager.PluginStateChanged -= OnPluginStateChanged;
        Stop();
        _systemProvider.Dispose();
    }

    public IReadOnlyList<TtsVoiceOption> GetVoiceOptions(string? providerId)
    {
        var provider = FindProvider(providerId) ?? _systemProvider;
        var voices = new List<TtsVoiceOption> { new(DefaultVoiceOptionId, Loc.Instance["Speech.SystemDefaultVoice"]) };

        voices.AddRange(
            provider.AvailableVoices.Select(voice =>
            {
                var displayName = string.IsNullOrWhiteSpace(voice.LocaleIdentifier)
                    ? voice.DisplayName
                    : $"{voice.DisplayName} ({voice.LocaleIdentifier})";
                return new TtsVoiceOption(voice.Id, displayName, voice.LocaleIdentifier);
            })
        );

        return voices;
    }

    public string? GetSelectedVoiceId(string? providerId)
    {
        var provider = FindProvider(providerId) ?? _systemProvider;
        return string.IsNullOrWhiteSpace(provider.SelectedVoiceId)
            ? DefaultVoiceOptionId
            : provider.SelectedVoiceId;
    }

    public void SelectVoice(string? providerId, string? voiceId)
    {
        var provider = FindProvider(providerId) ?? _systemProvider;
        try
        {
            provider.SelectVoice(IsDefaultVoiceOptionId(voiceId) ? null : voiceId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SpeechFeedback voice selection error: {ex.Message}");
        }
    }

    public static bool IsDefaultVoiceOptionId(string? voiceId)
    {
        return string.IsNullOrWhiteSpace(voiceId)
               || string.Equals(voiceId, DefaultVoiceOptionId, StringComparison.Ordinal);
    }

    public void Speak(string text, string? language = null)
    {
        SpeakCore(new TtsSpeakRequest(text, language), true);
    }

    public void SpeakAutomaticTranscription(
        string text,
        string? language = null,
        bool useConfiguredLanguageFallback = true
    )
    {
        SpeakCore(
            new TtsSpeakRequest(text, language, TtsPurpose.Transcription),
            true,
            useConfiguredLanguageFallback
        );
    }

    public void ReadBack(string text, string? language = null)
    {
        if (IsSpeaking)
        {
            Stop();
            return;
        }

        SpeakCore(
            new TtsSpeakRequest(text, language, TtsPurpose.ManualReadback),
            false
        );
    }

    public void AnnounceRecordingStarted()
    {
        Speak(Loc.Instance["Speech.Recording"]);
    }

    public void AnnounceTranscriptionComplete(
        string text,
        string? language = null,
        bool useConfiguredLanguageFallback = true
    )
    {
        SpeakAutomaticTranscription(text, language, useConfiguredLanguageFallback);
    }

    public void AnnounceError(string reason)
    {
        Speak(Loc.Instance.GetString("Speech.Error", reason));
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        ITtsPlaybackSession? session;

        lock (_lock)
        {
            cts = _speakCts;
            session = _playbackSession;
            _speakCts = null;
            _playbackSession = null;
            _isPlaybackPending = false;
        }

        try
        {
            cts?.Cancel();
        }
        catch { }
        finally
        {
            cts?.Dispose();
        }

        try
        {
            session?.Stop();
        }
        catch { }
    }

    public event EventHandler? ProvidersChanged;

    private void SpeakCore(
        TtsSpeakRequest request,
        bool requireEnabled,
        bool useConfiguredLanguageFallback = true
    )
    {
        if (_disposed || string.IsNullOrWhiteSpace(request.Text))
        {
            return;
        }

        if (requireEnabled && !_settings.Current.SpokenFeedbackEnabled)
        {
            return;
        }

        // Callers that have already resolved the readback language (e.g. the
        // dictation orchestrator) opt out so the configured-language fallback
        // does not override their decision — see ApplyConfiguredLanguageFallback.
        if (useConfiguredLanguageFallback)
        {
            request = ApplyConfiguredLanguageFallback(request);
        }

        Stop();

        var cts = new CancellationTokenSource();
        var version = Interlocked.Increment(ref _playbackVersion);

        lock (_lock)
        {
            _speakCts = cts;
            _isPlaybackPending = true;
        }

        _ = SpeakAsync(request, cts, version);
    }

    // When a transcription / manual-readback request carries no language, fall
    // back to the configured app language so the TTS provider speaks it in the
    // expected language rather than guessing. Ported from upstream 552ad88.
    private TtsSpeakRequest ApplyConfiguredLanguageFallback(TtsSpeakRequest request)
    {
        if (
            !ShouldUseConfiguredLanguageFallback(request.Purpose)
            || !string.IsNullOrWhiteSpace(request.Language)
        )
        {
            return request;
        }

        var configuredLanguage = _settings.Current.Language;
        if (
            string.IsNullOrWhiteSpace(configuredLanguage)
            || string.Equals(configuredLanguage, "auto", StringComparison.OrdinalIgnoreCase)
        )
        {
            return request;
        }

        return request with { Language = configuredLanguage };
    }

    private static bool ShouldUseConfiguredLanguageFallback(TtsPurpose purpose)
    {
        return purpose is TtsPurpose.Transcription or TtsPurpose.ManualReadback;
    }

    private async Task SpeakAsync(
        TtsSpeakRequest request,
        CancellationTokenSource cts,
        long version
    )
    {
        ITtsPlaybackSession? session = null;
        try
        {
            var provider = ResolveSpeakProvider();
            session = await provider.SpeakAsync(request, cts.Token).ConfigureAwait(false);

            if (cts.IsCancellationRequested)
            {
                session.Stop();
                return;
            }

            // Check that no newer Speak / Stop call has superseded us while
            // SpeakAsync was awaited. If the version has advanced, discard
            // this session so the newer request's session owns the slot.
            var accepted = false;
            lock (_lock)
            {
                if (_speakCts == cts && version == Volatile.Read(ref _playbackVersion))
                {
                    _playbackSession = session;
                    _isPlaybackPending = false;
                    accepted = true;
                }
            }

            if (!accepted)
            {
                session.Stop();
                return;
            }

            EventHandler? completedHandler = null;
            completedHandler = (_, _) =>
            {
                session.Completed -= completedHandler;
                OnPlaybackCompleted(session, cts, version);
            };
            session.Completed += completedHandler;

            if (!session.IsActive)
            {
                OnPlaybackCompleted(session, cts, version);
            }
        }
        catch (OperationCanceledException)
        {
            ClearPending(cts, version);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SpeechFeedback error: {ex.Message}");
            ClearPending(cts, version);
        }
    }

    private void OnPlaybackCompleted(
        ITtsPlaybackSession session,
        CancellationTokenSource cts,
        long version
    )
    {
        var disposeCts = false;
        lock (_lock)
        {
            if (
                ReferenceEquals(_playbackSession, session)
                && version == Volatile.Read(ref _playbackVersion)
            )
            {
                _playbackSession = null;
                _isPlaybackPending = false;
                if (_speakCts == cts)
                {
                    _speakCts = null;
                    disposeCts = true;
                }
            }
        }

        if (disposeCts)
        {
            cts.Dispose();
        }
    }

    private void ClearPending(CancellationTokenSource cts, long version)
    {
        var disposeCts = false;
        lock (_lock)
        {
            if (_speakCts == cts && version == Volatile.Read(ref _playbackVersion))
            {
                _speakCts = null;
                _isPlaybackPending = false;
                disposeCts = true;
            }
        }

        if (disposeCts)
        {
            cts.Dispose();
        }
    }

    private IReadOnlyList<ITtsProviderPlugin> AllProviders()
    {
        return [_systemProvider, .. _pluginManager.TtsProviders];
    }

    private ITtsProviderPlugin? FindProvider(string? providerId)
    {
        if (
            string.IsNullOrWhiteSpace(providerId)
            || string.Equals(
                providerId,
                _systemProvider.ProviderId,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return _systemProvider;
        }

        return _pluginManager.GetTtsProvider(providerId);
    }

    private ITtsProviderPlugin ResolveSpeakProvider()
    {
        var selectedProviderId = _settings.Current.SpokenFeedbackProviderId;
        var selectedProvider = FindProvider(selectedProviderId);

        if (
            selectedProvider is not null
            && !ReferenceEquals(selectedProvider, _systemProvider)
            && selectedProvider.IsConfigured
        )
        {
            return selectedProvider;
        }

        return _systemProvider;
    }

    private void OnPluginStateChanged(object? sender, EventArgs e)
    {
        ProvidersChanged?.Invoke(this, EventArgs.Empty);
    }
}