using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services;

public sealed record TtsProviderOption(string Id, string DisplayName);

// ReSharper disable once NotAccessedPositionalProperty.Global  LocaleIdentifier carried in the voice-option record's data shape
public sealed record TtsVoiceOption(string Id, string DisplayName, string? LocaleIdentifier = null);

internal interface IStartupFeedbackReservation : IDisposable
{
    Task StopPriorPlaybackAsync();
}

public sealed class SpeechFeedbackService : IDisposable
{
    public const string DefaultVoiceOptionId = "__typewhisper_default_voice__";
    internal static readonly TimeSpan s_recordingAnnouncementTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan s_stopPlaybackTimeout = TimeSpan.FromMilliseconds(500);

    private sealed class PlaybackRequest(long version)
    {
        private readonly Lock _lifetimeLock = new();
        private bool _cancellationDisposed;
        private bool _completed;
        private int _stopWorkerCount;

        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        public ITtsPlaybackSession? Session;
        public long Version { get; } = version;

        public void CancelAndStop()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completion won the race and already released the source.
            }
            catch (Exception ex)
            {
                // A plugin cancellation callback threw. The session stop below must
                // still be attempted, otherwise the prior speech keeps playing into
                // the newly opened microphone.
                Debug.WriteLine($"SpeechFeedback cancellation error: {ex.Message}");
            }

            try
            {
                Volatile.Read(ref Session)?.Stop();
            }
            catch
            {
                // Best-effort stop of the speech session.
            }
        }

        public Task LaunchCancelAndStop()
        {
            lock (_lifetimeLock)
            {
                _stopWorkerCount++;
            }

            try
            {
                // Cancellation callbacks and synchronous plugin Stop() implementations
                // cross the host trust boundary on this worker. Startup may advance once
                // the existing budget expires; PA25's reservation/version checks keep
                // reentrant publication and late completion safe, and Complete is idempotent.
                return Task.Run(() =>
                {
                    try
                    {
                        CancelAndStop();
                    }
                    finally
                    {
                        StopWorkerCompleted();
                    }
                });
            }
            catch
            {
                StopWorkerCompleted();
                throw;
            }
        }

        public void Complete()
        {
            lock (_lifetimeLock)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
            }

            Completion.TrySetResult();
            DisposeCancellationIfReady();
        }

        private void StopWorkerCompleted()
        {
            lock (_lifetimeLock)
            {
                _stopWorkerCount--;
            }

            DisposeCancellationIfReady();
        }

        private void DisposeCancellationIfReady()
        {
            var dispose = false;
            lock (_lifetimeLock)
            {
                if (
                    _completed
                    && _stopWorkerCount == 0
                    && !_cancellationDisposed
                )
                {
                    _cancellationDisposed = true;
                    dispose = true;
                }
            }

            // A permanently blocked plugin never lets its worker count reach zero and so
            // deliberately retains this request and its CTS.
            if (dispose)
            {
                Cancellation.Dispose();
            }
        }
    }

    private sealed class StartupFeedbackReservation(
        SpeechFeedbackService owner,
        PlaybackRequest? priorRequest
    ) : IStartupFeedbackReservation
    {
        private readonly Lock _stopLock = new();
        private int _disposed;
        private Task? _stopTask;

        public Task StopPriorPlaybackAsync()
        {
            lock (_stopLock)
            {
                return _stopTask ??= owner.WaitForPriorPlaybackBeforeCaptureAsync(priorRequest);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            owner.ReleaseStartupFeedback(this);
        }
    }

    private readonly Func<TimeSpan, Task> _delay;
    private readonly Lock _lock = new();
    private readonly Action<long, bool>? _playbackVersionAllocated;
    private readonly PluginManager _pluginManager;

    private readonly ISettingsService _settings;
    private readonly ITtsProviderPlugin _systemProvider;
    private bool _disposed;
    private bool _isPlaybackPending;
    private PlaybackRequest? _playbackRequest;
    private ITtsPlaybackSession? _playbackSession;
    private long _playbackVersion;
    private StartupFeedbackReservation? _startupFeedbackReservation;

    // ReSharper disable once UnusedMember.Global -- resolved by DI (AddSingleton<SpeechFeedbackService>); the analyzer cannot see the reflection-driven construction.
    public SpeechFeedbackService(
        ISettingsService settings,
        PluginManager pluginManager,
        SystemCommandAvailabilityService commands,
        IProcessRunner processRunner
    )
        : this(
            settings,
            pluginManager,
            new LinuxSystemTtsProvider(settings, commands, processRunner)
        )
    {
    }

    internal SpeechFeedbackService(
        ISettingsService settings,
        PluginManager pluginManager,
        ITtsProviderPlugin systemProvider,
        Func<TimeSpan, Task>? delay = null,
        Action<long, bool>? playbackVersionAllocated = null
    )
    {
        _settings = settings;
        _pluginManager = pluginManager;
        _systemProvider = systemProvider;
        _delay = delay ?? Task.Delay;
        _playbackVersionAllocated = playbackVersionAllocated;
        _pluginManager.PluginStateChanged += OnPluginStateChanged;
    }

    public bool IsAvailable => ResolveSpeakProvider().IsConfigured;
    public string BackendName => ResolveSpeakProvider().ProviderDisplayName;

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

    private void Speak(string text, string? language = null)
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

    // ReSharper disable once UnusedMember.Global  public API surface (manual TTS read-back entry point); not currently called in-tree
    public void ReadBack(string text, string? language = null)
    {
        PlaybackRequest? toggledOffRequest;
        lock (_lock)
        {
            // Manual readback is also a stop-toggle. Keep that stop decision in
            // the reservation lock so it cannot cancel the matching start cue.
            if (_startupFeedbackReservation is not null)
            {
                return;
            }

            if (_isPlaybackPending || _playbackSession?.IsActive == true)
            {
                toggledOffRequest = _playbackRequest;
                _playbackRequest = null;
                _playbackSession = null;
                _isPlaybackPending = false;
            }
            else
            {
                toggledOffRequest = null;
            }
        }

        if (toggledOffRequest is not null)
        {
            toggledOffRequest.CancelAndStop();
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

    internal IStartupFeedbackReservation ReserveStartupFeedback()
    {
        StartupFeedbackReservation reservation;
        lock (_lock)
        {
            // A late readback from the prior dictation must not supersede or
            // overlap the new start cue. Install the reservation before detaching
            // the current request so there is no stop-to-reserve publication gap.
            var priorRequest = _playbackRequest;
            reservation = new StartupFeedbackReservation(this, priorRequest);
            _startupFeedbackReservation = reservation;
            _playbackRequest = null;
            _playbackSession = null;
            _isPlaybackPending = false;
        }

        return reservation;
    }

    private async Task WaitForPriorPlaybackBeforeCaptureAsync(PlaybackRequest? request)
    {
        if (request is null)
        {
            return;
        }

        var stopWorker = request.LaunchCancelAndStop();
        try
        {
            _ = await WaitForCompletionAsync(request, s_stopPlaybackTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SpeechFeedback stop wait error: {ex.Message}");
        }
        finally
        {
            ObserveStopWorker(stopWorker, "prior playback stop");
        }
    }

    internal async Task AnnounceRecordingStartedAsync(
        bool spokenFeedbackEnabled,
        IStartupFeedbackReservation reservation
    )
    {
        if (!spokenFeedbackEnabled)
        {
            return;
        }

        var request = StartPlayback(
            new TtsSpeakRequest(Loc.Instance["Speech.Recording"]),
            requireEnabled: false,
            startupFeedbackReservation: reservation
        );
        if (request is null)
        {
            return;
        }

        Task? stopWorker = null;
        try
        {
            if (
                await WaitForCompletionAsync(request, s_recordingAnnouncementTimeout)
                    .ConfigureAwait(false)
            )
            {
                return;
            }

            stopWorker = request.LaunchCancelAndStop();
            ReleasePlaybackOwnership(request);
            _ = await WaitForCompletionAsync(request, s_stopPlaybackTimeout)
                .ConfigureAwait(false);
            request.Complete();
        }
        catch (Exception ex)
        {
            // Spoken feedback is optional; a failed timeout wait or provider
            // completion must not leave the request's session unstopped.
            stopWorker ??= request.LaunchCancelAndStop();
            ReleasePlaybackOwnership(request);
            request.Complete();
            Debug.WriteLine($"SpeechFeedback recording announcement error: {ex.Message}");
        }
        finally
        {
            if (stopWorker is not null)
            {
                ObserveStopWorker(stopWorker, "recording announcement stop");
            }
        }
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

    // The Begin* variants split announcement into a host-only registration phase
    // (safe to run under the orchestrator's session lock — it linearizes with
    // ReserveStartupFeedback exactly like the inline path) and a returned launch
    // continuation that crosses the provider/plugin trust boundary, so callers
    // can invoke it after releasing their locks. A request registered here but
    // superseded before launch is captured and stopped by the reservation, and
    // SpeakAsync's acceptance check discards its session.
    internal Action? BeginAnnounceError(string reason)
    {
        return BeginSpeech(
            new TtsSpeakRequest(Loc.Instance.GetString("Speech.Error", reason)),
            useConfiguredLanguageFallback: true
        );
    }

    internal Action? BeginAnnounceTranscriptionComplete(
        string text,
        string? language = null,
        bool useConfiguredLanguageFallback = true
    )
    {
        return BeginSpeech(
            new TtsSpeakRequest(text, language, TtsPurpose.Transcription),
            useConfiguredLanguageFallback
        );
    }

    private Action? BeginSpeech(TtsSpeakRequest request, bool useConfiguredLanguageFallback)
    {
        var pending = BeginPlayback(request, requireEnabled: true, useConfiguredLanguageFallback);
        if (pending is null)
        {
            return null;
        }

        var captured = pending.Value;
        return () => LaunchPlayback(captured);
    }

    internal bool TryRunOrdinaryFeedback(Action feedback)
    {
        lock (_lock)
        {
            if (_startupFeedbackReservation is not null)
            {
                // Suppressed terminal cues are intentionally dropped. Replaying
                // one after release could send it into the microphone we just opened.
                return false;
            }

            feedback();
            return true;
        }
    }

    private void Stop()
    {
        _ = StopPlayback();
    }

    public event EventHandler? ProvidersChanged;

    private void SpeakCore(
        TtsSpeakRequest request,
        bool requireEnabled,
        bool useConfiguredLanguageFallback = true
    )
    {
        _ = StartPlayback(request, requireEnabled, useConfiguredLanguageFallback);
    }

    private PlaybackRequest? StartPlayback(
        TtsSpeakRequest request,
        bool requireEnabled,
        bool useConfiguredLanguageFallback = true,
        IStartupFeedbackReservation? startupFeedbackReservation = null
    )
    {
        var pending = BeginPlayback(
            request,
            requireEnabled,
            useConfiguredLanguageFallback,
            startupFeedbackReservation
        );
        if (pending is null)
        {
            return null;
        }

        LaunchPlayback(pending.Value);
        return pending.Value.Playback;
    }

    private readonly record struct PendingPlayback(
        TtsSpeakRequest Speak,
        PlaybackRequest Playback,
        PlaybackRequest? Superseded
    );

    private PendingPlayback? BeginPlayback(
        TtsSpeakRequest request,
        bool requireEnabled,
        bool useConfiguredLanguageFallback = true,
        IStartupFeedbackReservation? startupFeedbackReservation = null
    )
    {
        if (_disposed || string.IsNullOrWhiteSpace(request.Text))
        {
            return null;
        }

        if (requireEnabled && !_settings.Current.SpokenFeedbackEnabled)
        {
            return null;
        }

        // Callers that have already resolved the readback language (e.g. the
        // dictation orchestrator) opt out so the configured-language fallback
        // does not override their decision — see ApplyConfiguredLanguageFallback.
        if (useConfiguredLanguageFallback)
        {
            request = ApplyConfiguredLanguageFallback(request);
        }

        PlaybackRequest? supersededRequest;
        PlaybackRequest playbackRequest;

        lock (_lock)
        {
            // Ordinary speech is dropped while startup owns this lane. Only the
            // exact lease may publish the spoken "Recording" cue; stale and
            // foreign leases cannot allocate a version or reach a provider.
            if (
                startupFeedbackReservation is null
                    ? _startupFeedbackReservation is not null
                    : !ReferenceEquals(
                        _startupFeedbackReservation,
                        startupFeedbackReservation
                    )
            )
            {
                return null;
            }

            var version = AllocatePlaybackVersion();
            supersededRequest = _playbackRequest;
            playbackRequest = new PlaybackRequest(version);
            _playbackRequest = playbackRequest;
            _playbackSession = null;
            _isPlaybackPending = true;
        }

        return new PendingPlayback(request, playbackRequest, supersededRequest);
    }

    private void LaunchPlayback(PendingPlayback pending)
    {
        // Both calls cross the host trust boundary (plugin cancellation
        // callbacks / Stop(), and the provider's synchronous SpeakAsync prefix),
        // so this must never run under the orchestrator's session lock.
        pending.Superseded?.CancelAndStop();
        _ = SpeakAsync(pending.Speak, pending.Playback);
    }

    private void ReleaseStartupFeedback(StartupFeedbackReservation reservation)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_startupFeedbackReservation, reservation))
            {
                _startupFeedbackReservation = null;
            }
        }
    }

    private long AllocatePlaybackVersion()
    {
        var version = Interlocked.Increment(ref _playbackVersion);
        _playbackVersionAllocated?.Invoke(version, _lock.IsHeldByCurrentThread);
        return version;
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
        PlaybackRequest playbackRequest
    )
    {
        ITtsPlaybackSession? session;
        try
        {
            var provider = ResolveSpeakProvider();
            session = await provider
                .SpeakAsync(request, playbackRequest.Cancellation.Token)
                .ConfigureAwait(false);
            Volatile.Write(ref playbackRequest.Session, session);

            // Check that no newer Speak / Stop call has superseded us while
            // SpeakAsync was awaited. If the version has advanced, discard
            // this session so the newer request's session owns the slot.
            var accepted = false;
            lock (_lock)
            {
                if (
                    ReferenceEquals(_playbackRequest, playbackRequest)
                    && playbackRequest.Version == Volatile.Read(ref _playbackVersion)
                    && !playbackRequest.Cancellation.IsCancellationRequested
                )
                {
                    _playbackSession = session;
                    _isPlaybackPending = false;
                    accepted = true;
                }
            }

            if (!accepted)
            {
                playbackRequest.CancelAndStop();
                ClearPending(playbackRequest);
                return;
            }

            EventHandler? completedHandler = null;
            completedHandler = (_, _) =>
            {
                session.Completed -= completedHandler;
                OnPlaybackCompleted(session, playbackRequest);
            };
            session.Completed += completedHandler;

            if (!session.IsActive)
            {
                session.Completed -= completedHandler;
                OnPlaybackCompleted(session, playbackRequest);
            }
        }
        catch (OperationCanceledException)
        {
            ClearPending(playbackRequest);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SpeechFeedback error: {ex.Message}");
            ClearPending(playbackRequest);
        }
    }

    private void OnPlaybackCompleted(
        ITtsPlaybackSession session,
        PlaybackRequest playbackRequest
    )
    {
        lock (_lock)
        {
            if (
                ReferenceEquals(_playbackRequest, playbackRequest)
                && ReferenceEquals(_playbackSession, session)
                && playbackRequest.Version == Volatile.Read(ref _playbackVersion)
            )
            {
                _playbackSession = null;
                _isPlaybackPending = false;
                _playbackRequest = null;
            }
        }

        playbackRequest.Complete();
    }

    private void ClearPending(PlaybackRequest playbackRequest)
    {
        ReleasePlaybackOwnership(playbackRequest);
        playbackRequest.Complete();
    }

    private void ReleasePlaybackOwnership(PlaybackRequest playbackRequest)
    {
        lock (_lock)
        {
            // ReSharper disable once InvertIf -- last statement in the lock; inverting would add a return inside the lock.
            if (
                ReferenceEquals(_playbackRequest, playbackRequest)
                && playbackRequest.Version == Volatile.Read(ref _playbackVersion)
            )
            {
                _playbackRequest = null;
                _playbackSession = null;
                _isPlaybackPending = false;
            }
        }
    }

    private PlaybackRequest? StopPlayback()
    {
        PlaybackRequest? playbackRequest;
        lock (_lock)
        {
            playbackRequest = _playbackRequest;
            _playbackRequest = null;
            _playbackSession = null;
            _isPlaybackPending = false;
        }

        playbackRequest?.CancelAndStop();
        return playbackRequest;
    }

    private async Task<bool> WaitForCompletionAsync(
        PlaybackRequest playbackRequest,
        TimeSpan timeout
    )
    {
        var completion = playbackRequest.Completion.Task;
        if (completion.IsCompleted)
        {
            await completion.ConfigureAwait(false);
            return true;
        }

        var timeoutTask = _delay(timeout);
        if (await Task.WhenAny(completion, timeoutTask).ConfigureAwait(false) == completion)
        {
            await completion.ConfigureAwait(false);
            return true;
        }

        await timeoutTask.ConfigureAwait(false);
        return false;
    }

    private static void ObserveStopWorker(Task stopWorker, string operation)
    {
        if (stopWorker.IsCompleted)
        {
            if (stopWorker.Exception is { } exception)
            {
                Debug.WriteLine(
                    $"SpeechFeedback {operation} error: {exception.GetBaseException().Message}"
                );
            }

            return;
        }

        _ = stopWorker.ContinueWith(
            static (task, state) =>
                Debug.WriteLine(
                    $"SpeechFeedback {state} error: {task.Exception!.GetBaseException().Message}"
                ),
            operation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously
                | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default
        );
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
