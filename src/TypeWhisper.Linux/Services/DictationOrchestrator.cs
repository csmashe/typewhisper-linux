using System.Diagnostics;
using System.Text;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Models;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Hotkey.Evdev;
using TypeWhisper.Linux.Services.SpokenCommand;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Immutable snapshot of the per-recording context captured at stop time.
///     Passed to the post-stop pipeline so it reads a stable view even if a new
///     dictation has already started and overwritten the instance-level fields.
/// </summary>
internal sealed record RecordingContext(
    int SessionId,
    DateTime RecordingStart,
    string? AppProcess,
    string? AppTitle,
    string? AppUrl,
    string? WindowId,
    Profile? Profile,
    string RecoveredPartialPreview,
    string? StreamingFinalText,
    bool StreamingFaulted,
    string? StreamingProviderId,
    string? StreamingModelId,
    string? StreamingLanguageHint,
    CancellationToken CancelToken
)
{
    /// <summary>
    ///     Per-run sink for LLM prompt provenance. Null when capture is disabled
    ///     for this run (history saving or the provenance setting is off), so the
    ///     pipeline records nothing and pays no cost.
    /// </summary>
    public LlmCallCapture? Capture { get; init; }
}

public sealed class DictationOrchestrator : IDisposable
{
    // Treat a second toggle within this gap as spurious, not intentional: covers
    // (1) key autorepeat and (2) in-app hook + desktop gsettings shortcut both
    // firing for the same press (~0.1s apart). 350ms is above both but below a
    // deliberate tap-tap.
    private static readonly TimeSpan s_toggleDebounce = TimeSpan.FromMilliseconds(350);

    // Bound the wait for the toggle gate during session-loss teardown so a stuck start/stop can't
    // hang the lock handler; long enough to outlast a normal StartAsync's synchronous setup.
    private static readonly TimeSpan s_sessionLossGateTimeout = TimeSpan.FromSeconds(5);

    private readonly ActiveWindowService _activeWindow;
    private readonly AudioRecordingService _audio;
    private readonly IAudioDuckingService _audioDucking;
    private readonly LlmCleanupService _cleanup;
    private readonly SystemCommandAvailabilityService _commands;
    private readonly IDictionaryService _dictionary;
    private readonly IErrorLogService _errorLog;
    private readonly IDetectionFailureTracker _failureTracker;
    private readonly IHistoryService _history;
    private readonly HotkeyService _hotkey;
    private readonly IdeFileReferenceService _ideFileReferences;
    private readonly DictationInFlightSessionTracker _inFlightTracker = new();
    private readonly DictationInsertionOrderGate _insertionOrder = new();
    private readonly IMediaPauseService _mediaPause;
    private readonly MemoryService _memory;
    private readonly ModelManagerService _models;
    private readonly Lock _overlayStateLock = new();
    private readonly StreamingTranscriptState _partialTranscriptState = new();
    private readonly IPostProcessingPipeline _pipeline;
    private readonly IProfileService _profiles;
    private readonly IPromptActionService _promptActions;
    private readonly PromptProcessingService _promptProcessing;
    private readonly RecentTranscriptionsService _recentTranscriptions;
    private readonly Lock _recordingSessionLock = new();
    private readonly ISessionActivityMonitor _sessionActivityMonitor;
    private readonly SessionAudioFileService _sessionAudioFiles;
    private readonly ISettingsService _settings;
    private readonly ISnippetService _snippets;
    private readonly SoundFeedbackService _soundFeedback;
    private readonly SpeechFeedbackService _speechFeedback;
    private readonly TargetAppCorrectionLearningService _targetAppLearning;
    private readonly TextInsertionService _textInsertion;

    // The debounce check-and-write must be atomic: two threads (hook + IPC) can
    // both read the stale timestamp and both pass the gap check. DateTime can't
    // be volatile, so a lock is required.
    private readonly Lock _toggleDebounceLock = new();
    private readonly DictationToggleGate _toggleGate = new();
    private readonly ITranslationService _translation;
    private readonly IVocabularyBoostingService _vocabularyBoosting;
    private CancellationTokenSource? _activeDictationCts;

    // Cancels an in-flight spoken command (its LLM stream + typing). Distinct from
    // _activeDictationCts, which only covers the recording/transcription phase and is nulled once
    // recording stops — a command runs after that, so it needs its own Escape-reachable source.
    // Holds the NEWEST command (what Escape targets); _activeCommandCtsSet holds them all.
    private CancellationTokenSource? _activeCommandCts;

    // Guarded by itself. Two dictations stopped in quick succession can reach spoken-command
    // processing concurrently; a session-loss discard must cancel all of them so an older command
    // can never keep synthesizing selection-copy or streamed typing behind the lock screen.
    private readonly HashSet<CancellationTokenSource> _activeCommandCtsSet = [];
    private EventHandler? _cancelHandler;
    private EventHandler? _sessionActivityHandler;
    private volatile bool _cancelRequested;

    private bool _disposed;
    private EventHandler<string>? _hookFailedHandler;
    private bool _initialized;
    private string? _lastPublishedPartialText;
    private DateTime _lastSpeechDetectedAtUtc;
    private DateTime _lastToggleUtc = DateTime.MinValue;
    private DictationOverlayState _overlayState = DictationOverlayState.Hidden;
    private CancellationTokenSource? _partialTranscriptionCts;
    private Task? _partialTranscriptionTask;
    private string? _recordingAppProcess;
    private string? _recordingAppTitle;
    private string? _recordingAppUrl;
    private Profile? _recordingProfile;

    // Monotonically incremented per StartAsync. The active-window snapshot task
    // captures this at start and guards every write behind it; a late snapshot
    // (after AwaitRecordingSnapshotAsync timed out) drops its writes rather
    // than corrupting the next dictation's context.
    private int _recordingSession;
    private Task? _recordingSnapshotTask;
    private DateTime _recordingStart;
    private string? _recordingWindowId;
    private bool _silenceStopRequested;
    private EventHandler? _startHandler;
    private EventHandler? _stopHandler;
    private EventHandler? _discardHandler;
    private StreamingTranscriptionCoordinator? _streamingCoordinator;
    private string? _streamingLanguageHint;
    private string? _streamingModelId;
    private string? _streamingProviderId;
    private CancellationTokenSource? _streamingStartupCts;

    private EventHandler? _toggleHandler;

    public DictationOrchestrator(
        HotkeyService hotkey,
        AudioRecordingService audio,
        SessionAudioFileService sessionAudioFiles,
        SoundFeedbackService soundFeedback,
        SpeechFeedbackService speechFeedback,
        TextInsertionService textInsertion,
        IAudioDuckingService audioDucking,
        IMediaPauseService mediaPause,
        ModelManagerService models,
        IHistoryService history,
        ISettingsService settings,
        ActiveWindowService activeWindow,
        IProfileService profiles,
        IPromptActionService promptActions,
        IDictionaryService dictionary,
        ISnippetService snippets,
        IVocabularyBoostingService vocabularyBoosting,
        LlmCleanupService cleanup,
        IPostProcessingPipeline pipeline,
        ITranslationService translation,
        PromptProcessingService promptProcessing,
        MemoryService memory,
        RecentTranscriptionsService recentTranscriptions,
        IdeFileReferenceService ideFileReferences,
        SystemCommandAvailabilityService commands,
        TargetAppCorrectionLearningService targetAppLearning,
        IDetectionFailureTracker failureTracker,
        IErrorLogService errorLog,
        ISessionActivityMonitor sessionActivityMonitor
    )
    {
        _hotkey = hotkey;
        _audio = audio;
        _sessionAudioFiles = sessionAudioFiles;
        _soundFeedback = soundFeedback;
        _speechFeedback = speechFeedback;
        _textInsertion = textInsertion;
        _audioDucking = audioDucking;
        _mediaPause = mediaPause;
        _models = models;
        _history = history;
        _settings = settings;
        _activeWindow = activeWindow;
        _profiles = profiles;
        _promptActions = promptActions;
        _dictionary = dictionary;
        _snippets = snippets;
        _vocabularyBoosting = vocabularyBoosting;
        _cleanup = cleanup;
        _pipeline = pipeline;
        _translation = translation;
        _promptProcessing = promptProcessing;
        _memory = memory;
        _recentTranscriptions = recentTranscriptions;
        _ideFileReferences = ideFileReferences;
        _commands = commands;
        _targetAppLearning = targetAppLearning;
        _failureTracker = failureTracker;
        _errorLog = errorLog;
        _sessionActivityMonitor = sessionActivityMonitor;
    }

    public bool IsRecording => _audio.IsRecording;

    /// <summary>
    ///     Current pipeline phase for <c>typewhisper status</c>. The audio
    ///     recorder is source of truth for <c>recording</c>; once stopped, the
    ///     overlay StatusText drives transcribing / injecting / idle.
    /// </summary>
    public string CurrentStateLabel
    {
        get
        {
            if (_audio.IsRecording)
            {
                return "recording";
            }

            DictationOverlayState snapshot;
            lock (_overlayStateLock)
            {
                snapshot = _overlayState;
            }

            return MapOverlayStatusToStateLabel(snapshot.StatusText);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_toggleHandler is not null)
        {
            _hotkey.DictationToggleRequested -= _toggleHandler;
        }

        if (_startHandler is not null)
        {
            _hotkey.DictationStartRequested -= _startHandler;
        }

        if (_stopHandler is not null)
        {
            _hotkey.DictationStopRequested -= _stopHandler;
        }

        if (_discardHandler is not null)
        {
            _hotkey.DictationDiscardRequested -= _discardHandler;
        }

        if (_cancelHandler is not null)
        {
            _hotkey.CancelRequested -= _cancelHandler;
        }

        if (_hookFailedHandler is not null)
        {
            _hotkey.HookFailed -= _hookFailedHandler;
        }

        if (_sessionActivityHandler is not null)
        {
            _sessionActivityMonitor.InputAllowedChanged -= _sessionActivityHandler;
        }

        // Stop any active recording and undo ducking/media-pause before teardown
        // so the user isn't left with a muted system after exit.
        if (_audio.IsRecording)
        {
            try
            {
                _audio.StopRecording();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Dictation] StopRecording during dispose failed: {ex.Message}");
            }

            try
            {
                _audioDucking.RestoreAudio();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Dictation] RestoreAudio during dispose failed: {ex.Message}");
            }

            try
            {
                _mediaPause.ResumeMedia();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Dictation] ResumeMedia during dispose failed: {ex.Message}");
            }
        }

        ShutdownPartialTranscriptionSession();

        // Null the audio tap and snapshot the coordinator before awaiting close
        // so no stray frames or shared-field reads can race teardown.
        var disposingCoordinator = _streamingCoordinator;
        var disposingStartupCts = _streamingStartupCts;
        _streamingCoordinator = null;
        _streamingStartupCts = null;
        _streamingProviderId = null;
        _streamingModelId = null;
        _streamingLanguageHint = null;
        if (disposingCoordinator is not null)
        {
            _audio.LiveFrameSink = null;
        }

        var streamingTeardown = TeardownStreamingSessionAsync(
            disposingCoordinator,
            disposingStartupCts,
            false,
            CancellationToken.None
        );
        try
        {
            streamingTeardown.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Dictation] Streaming teardown on dispose failed: {ex.Message}");
        }

        try
        {
            _recordingSnapshotTask?.Wait(TimeSpan.FromMilliseconds(300));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Dictation] Snapshot shutdown failed: {ex.Message}");
        }

        _toggleGate.Dispose();
    }

    public void Initialize()
    {
        if (_initialized || _disposed)
        {
            return;
        }

        // Lambdas (not method groups): StartAsync/ToggleAsync have optional parameters
        // that prevent zero-arg method-group conversion.
        // Start the AT-SPI focus listener now (when enabled) so it has captured the
        // target field's focus before the user dictates into it.
        _targetAppLearning.Initialize();

        // Arm the session-activity monitor so the StartAsync/insertion lock guards observe real
        // lock state on every backend. The evdev backend also initializes it (idempotent), but on
        // X11/SharpHook or Wayland-without-evdev nothing else would, leaving IsInputAllowed stuck
        // at its default-true and the guards inert. Fire-and-forget: a slow/absent system bus must
        // not block startup, and an absent logind just leaves the legacy input-allowed fallback.
        FireAndLog(
            () => _sessionActivityMonitor.InitializeAsync(CancellationToken.None),
            nameof(ISessionActivityMonitor.InitializeAsync)
        );

        // Subscribe directly so a lock aborts an active recording on EVERY backend. The evdev
        // backend raises DictationDiscardRequested on lock, but SharpHook (all X11 sessions and the
        // Wayland fallback) does not; without this, a toggle recording started before the lock would
        // keep capturing behind the lock screen because the start/insertion guards only block new work.
        _sessionActivityHandler = (_, _) =>
        {
            if (!_sessionActivityMonitor.IsInputAllowed)
            {
                FireAndLog(AbortForSessionLossAsync, nameof(AbortForSessionLossAsync));
            }
        };
        _sessionActivityMonitor.InputAllowedChanged += _sessionActivityHandler;

        _toggleHandler = (_, _) => FireAndLog(() => ToggleAsync(), nameof(ToggleAsync));
        _startHandler = (_, _) => FireAndLog(() => StartAsync(), nameof(StartAsync));
        _stopHandler = (_, _) => FireAndLog(StopAsync, nameof(StopAsync));
        _discardHandler = (_, _) =>
            FireAndLog(AbortForSessionLossAsync, nameof(AbortForSessionLossAsync));
        _cancelHandler = (_, _) => FireAndLog(CancelAsync, nameof(CancelAsync));
        _hookFailedHandler = (_, message) =>
        {
            Trace.WriteLine($"[Dictation] Hotkey hook unavailable: {message}");
            ReportStatus("Global hotkey disabled.");
            ShowFeedback(
                "Global hotkey disabled. Check libuiohook/X11 permissions.",
                true
            );
        };
        _hotkey.DictationToggleRequested += _toggleHandler;
        _hotkey.DictationStartRequested += _startHandler;
        _hotkey.DictationStopRequested += _stopHandler;
        _hotkey.DictationDiscardRequested += _discardHandler;
        _hotkey.CancelRequested += _cancelHandler;
        _hotkey.HookFailed += _hookFailedHandler;
        try
        {
            _hotkey.Initialize();
        }
        catch
        {
            _hotkey.DictationToggleRequested -= _toggleHandler;
            _hotkey.DictationStartRequested -= _startHandler;
            _hotkey.DictationStopRequested -= _stopHandler;
            _hotkey.DictationDiscardRequested -= _discardHandler;
            _hotkey.CancelRequested -= _cancelHandler;
            _hotkey.HookFailed -= _hookFailedHandler;
            if (_sessionActivityHandler is not null)
            {
                _sessionActivityMonitor.InputAllowedChanged -= _sessionActivityHandler;
            }

            _toggleHandler = null;
            _startHandler = null;
            _stopHandler = null;
            _discardHandler = null;
            _cancelHandler = null;
            _hookFailedHandler = null;
            _sessionActivityHandler = null;
            throw;
        }

        _initialized = true;
    }

    public async Task ToggleAsync(string? forcedProfileId = null)
    {
        lock (_toggleDebounceLock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastToggleUtc < s_toggleDebounce)
            {
                return;
            }

            _lastToggleUtc = now;
        }

        if (_audio.IsRecording)
        {
            await StopAsync();
        }
        else
        {
            // Only the start branch honors the forced profile — a profile
            // hotkey pressed while recording just stops, like the main key.
            await StartAsync(forcedProfileId);
        }
    }

    /// <summary>
    ///     Aborts the active dictation. While recording, triggers a stop that
    ///     discards the audio (no transcription). While transcribing or running
    ///     the post-processing pipeline, cancels the active token so the in-flight
    ///     async work bails out and "Canceled" is surfaced instead of "Failed".
    /// </summary>
    public async Task CancelAsync()
    {
        await CancelInFlightWorkAsync().ConfigureAwait(false);

        // If we're still recording, route through StopAsync with the cancel
        // flag set. StopAsync owns the toggle gate and the recording-cleanup
        // ordering; piggy-backing on it keeps the lifecycle consistent. Pass the
        // cancel intent explicitly so a racing StartAsync that clears the shared
        // _cancelRequested flag between here and the gate probe can't downgrade
        // this discard to a normal save.
        if (_audio.IsRecording)
        {
            _cancelRequested = true;
            await StopAsync(cancelRequested: true);
        }
    }

    public async Task<int> StartAsync(string? forcedProfileId = null)
    {
        if (!_toggleGate.TryBeginStartup(() => _cancelRequested = false))
        {
            return 0;
        }

        var startedSessionId = 0;
        DictationDeferredStop pendingStop;
        try
        {
            if (_audio.IsRecording)
            {
                goto StartupComplete;
            }

            // Reject starts while the session is locked/inactive: the HTTP API and control socket
            // call StartAsync directly, bypassing the hotkey path, and the discard event only
            // aborts work already in flight. Checked under the gate so a concurrent session-loss
            // discard (which holds the gate across its stop) can't slip its transition between
            // this check and opening the microphone.
            if (!_sessionActivityMonitor.IsInputAllowed)
            {
                Trace.WriteLine("[Dictation] Start rejected: session locked or inactive.");
                goto StartupComplete;
            }

            _audio.WhisperModeEnabled = _settings.Current.WhisperModeEnabled;

            // Start capturing immediately — user may already be speaking (especially PTT).
            _recordingStart = DateTime.UtcNow;
            _lastSpeechDetectedAtUtc = _recordingStart;
            _silenceStopRequested = false;
            try
            {
                _audio.StartRecording();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Dictation] Failed to start recording: {ex}");
                var message = BuildRecordingStartFailureMessage(ex);
                ReportStatus(message);
                ShowFeedback(message, true);
                goto StartupComplete;
            }

            if (!_audio.IsRecording)
            {
                var message = BuildRecordingStartFailureMessage(null);
                ReportStatus(message);
                ShowFeedback(message, true);
                goto StartupComplete;
            }

            // Set overlay to "Recording…" after the stream is confirmed open but
            // before slow startup work (playerctl, sound). On Wayland the earlier
            // ordering made the stale feedback bubble linger until after PauseMedia.
            SetOverlayState(state =>
                // ReSharper disable once WithExpressionModifiesAllMembers -- `with` preserves any future-added state members; intentional even though all current members are set.
                state with
                {
                    IsOverlayVisible = true,
                    ShowFeedback = false,
                    FeedbackIsError = false,
                    FeedbackText = null,
                    PartialText = null,
                    LlmResponseText = null,
                    IsRecording = true,
                    StatusText = Localization.Loc.Instance["Dictation.StatusRecording"],
                    ActiveProfileName = null,
                    ActiveAppName = null,
                    SessionStartedAtUtc = DateTime.UtcNow
                }
            );

            try
            {
                if (_settings.Current.AudioDuckingEnabled)
                {
                    _audioDucking.DuckAudio(_settings.Current.AudioDuckingLevel);
                }

                if (_settings.Current.PauseMediaDuringRecording)
                {
                    _mediaPause.PauseMedia();
                }

                if (_settings.Current.SoundFeedbackEnabled)
                {
                    _soundFeedback.PlayRecordingStarted();
                }

                _speechFeedback.AnnounceRecordingStarted();
                RecordingStateChanged?.Invoke(this, true);
                // Bump the session version once per recording; both the polling loop
                // and the streaming coordinator share this version. Bumping twice
                // would immediately invalidate the streaming session.
                var sessionVersion = _partialTranscriptState.StartSession();

                var startupSettings = _settings.Current;
                // A profile hotkey forces a specific profile; resolve it
                // synchronously here so streaming/language decisions don't use the
                // stale _recordingProfile from the previous session. The background
                // snapshot task still runs for the context-match path.
                var startupProfile = _recordingProfile;
                if (forcedProfileId is not null)
                {
                    var forcedMatch = _profiles.MatchProfile(null, null, forcedProfileId);
                    if (forcedMatch.Kind == MatchKind.ManualOverride)
                    {
                        startupProfile = forcedMatch.Profile;
                    }
                }

                var startupLanguage =
                    startupProfile?.InputLanguage ?? startupSettings.Language;
                // ReSharper disable once InlineTemporaryVariable -- named local kept for readability over inlining into the pattern match.
                var startupLanguageHint =
                    startupLanguage is { Length: > 0 } lang && lang != "auto"
                        ? lang
                        : null;
                var startupPlugin = _models.ActiveTranscriptionPlugin;
                var startupMode = LinuxLiveTranscriptionStartupPolicy.Select(
                    startupSettings, startupPlugin);
                // Streaming doesn't support translation; skip the WebSocket when
                // `translate` is selected to avoid burning provider bandwidth on
                // a session we'd discard.
                var startupTaskName =
                    startupProfile?.SelectedTask ?? startupSettings.TranscriptionTask;
                var startupIsTranslate = string.Equals(
                    startupTaskName, "translate", StringComparison.OrdinalIgnoreCase
                );

                if (
                    startupMode == LiveTranscriptionMode.Streaming
                    && startupPlugin is not null
                    && !startupIsTranslate
                )
                {
                    StartStreamingTranscriptionSession(
                        startupPlugin, startupLanguageHint, sessionVersion);
                }

                // Always start the partial loop — it drives silence-auto-stop.
                // When streaming is active, the in-loop policy short-circuits
                // PollPartialTranscriptOnceAsync so polling stays a no-op.
                StartPartialTranscriptionSession(sessionVersion);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Dictation] Post-start setup failed: {ex}");
                RollBackStartedRecording();
                _ = await StopPartialTranscriptionSessionAsync();
                var faultedCoordinator = _streamingCoordinator;
                var faultedStartupCts = _streamingStartupCts;
                _streamingCoordinator = null;
                _streamingStartupCts = null;
                _streamingProviderId = null;
                _streamingModelId = null;
                _streamingLanguageHint = null;
                _audio.LiveFrameSink = null;
                _ = await TeardownStreamingSessionAsync(
                    faultedCoordinator,
                    faultedStartupCts,
                    false,
                    CancellationToken.None
                );
                throw;
            }

            // Arm the per-dictation CTS after setup succeeds. CancelAsync uses
            // this to abort an in-flight pipeline; Escape is armed here so it
            // only fires while a dictation is live.
            _activeDictationCts = new CancellationTokenSource();
            _hotkey.IsCancelShortcutEnabled = true;

            // Publish the snapshot task before releasing the gate so a
            // near-immediate StopAsync can observe and await it. The Task.Run
            // only commits results if the session is still active — protecting
            // the next dictation from late writes after the 500ms timeout.
            int sessionId;
            lock (_recordingSessionLock)
            {
                sessionId = ++_recordingSession;
                _inFlightTracker.Begin(sessionId);
                _recordingAppProcess = null;
                _recordingAppTitle = null;
                _recordingAppUrl = null;
                _recordingWindowId = _activeWindow.GetActiveWindowId();
                _recordingProfile = null;
            }

            startedSessionId = sessionId;

            var recordingSnapshotTask = Task.Run(async () =>
            {
                ActiveWindowSnapshot? initialSnap = null;
                string? appProcess = null;
                string? appTitle = null;
                string? appUrl = null;
                var initialMatch = MatchResult.NoMatch;
                Profile? matchedProfile = null;
                try
                {
                    // 50ms was too tight: xdotool's chain (window-id + title +
                    // pid → ProcessName) is three sequential subprocesses that
                    // can exceed 500ms. Runs in the background so it doesn't
                    // add user-visible latency.
                    using var initialCts = new CancellationTokenSource(
                        TimeSpan.FromMilliseconds(500)
                    );
                    initialSnap = await _activeWindow
                        .GetActiveWindowSnapshotAsync(initialCts.Token)
                        .ConfigureAwait(false);
                    appProcess = initialSnap?.ProcessName;
                    appTitle = initialSnap?.Title;
                    // A forced profile id yields MatchKind.ManualOverride, bypassing window/URL context.
                    initialMatch = _profiles.MatchProfile(appProcess, null, forcedProfileId);
                    matchedProfile = initialMatch.Profile;

                    if (initialSnap is null)
                    {
                        _failureTracker.RecordFailure(
                            DesktopDetector.DetectId() switch
                            {
                                "gnome" => "gnome-shell",
                                "kde" => "kwin",
                                "hyprland" => "hyprland",
                                "sway" => "sway",
                                _ => "xdotool"
                            },
                            "No active-window provider returned a snapshot"
                        );
                    }
                    else
                    {
                        _failureTracker.RecordSuccess();
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(
                        $"[Dictation] Initial active-window snapshot failed: {ex.Message}"
                    );
                }

                bool committed;
                lock (_recordingSessionLock)
                {
                    committed = _recordingSession == sessionId;
                    if (committed)
                    {
                        _recordingAppProcess = appProcess;
                        _recordingAppTitle = appTitle;
                        _recordingAppUrl = appUrl;
                        _recordingProfile = matchedProfile;
                    }
                }

                if (!committed)
                {
                    Trace.WriteLine(
                        $"[Dictation] Snapshot for session {sessionId} discarded — session no longer active."
                    );
                    return;
                }

                _audio.WhisperModeEnabled =
                    matchedProfile?.WhisperModeOverride ?? _settings.Current.WhisperModeEnabled;
                SetOverlayState(state =>
                    state with { ActiveProfileName = matchedProfile?.Name, ActiveAppName = appTitle }
                );
                _models.PluginManager.EventBus.Publish(
                    new RecordingStartedEvent { AppName = appTitle, AppProcessName = appProcess }
                );

                try
                {
                    // AT-SPI URL walks can take 2+ seconds on a busy Gmail tree.
                    // The timeout must exceed the walker's budget so a valid URL
                    // isn't discarded on a close race. Dictation is done; user
                    // isn't waiting.
                    using var deferredCts = new CancellationTokenSource(
                        TimeSpan.FromMilliseconds(4000)
                    );
                    var deferredUrl = await Task.Run(
                            () => _activeWindow.GetBrowserUrl(false),
                            deferredCts.Token
                        )
                        .ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(deferredUrl))
                    {
                        // The URL comes from the currently focused window, which may
                        // have changed. Re-snapshot and only commit if we're on the
                        // same window, to avoid binding to an unrelated tab/window.
                        ActiveWindowSnapshot? verifySnap = null;
                        try
                        {
                            // 500ms matches the initial-snapshot budget; 50ms was
                            // too tight for xdotool's multi-subprocess chain.
                            using var verifyCts = new CancellationTokenSource(
                                TimeSpan.FromMilliseconds(500)
                            );
                            verifySnap = await _activeWindow
                                .GetActiveWindowSnapshotAsync(verifyCts.Token)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            // Verification snapshot is best-effort; fall through to the initial snapshot.
                        }

                        if (
                            initialSnap is null
                            || verifySnap is null
                            || !IsSameWindow(initialSnap, verifySnap)
                        )
                        {
                            Trace.WriteLine(
                                "[Dictation] Deferred URL discarded — focused window changed mid-capture."
                            );
                        }
                        else
                        {
                            // Commit the URL for history/diagnostics regardless of whether
                            // it changes the profile match. Gate the rematch on a tier upgrade
                            // to avoid churn/downgrade.
                            lock (_recordingSessionLock)
                            {
                                if (_recordingSession != sessionId)
                                {
                                    return;
                                }

                                _recordingAppUrl = deferredUrl;
                            }

                            // A forced profile must not be overridden by a URL
                            // rematch (ManualOverride is the highest MatchKind,
                            // but an ungated Website rematch could displace it).
                            if (forcedProfileId is null)
                            {
                                var rematch = _profiles.MatchProfile(appProcess, deferredUrl);
                                if (
                                    rematch.Profile is not null
                                    && (int)rematch.Kind < (int)initialMatch.Kind
                                )
                                {
                                    lock (_recordingSessionLock)
                                    {
                                        if (_recordingSession != sessionId)
                                        {
                                            return;
                                        }

                                        _recordingProfile = rematch.Profile;
                                    }

                                    SetOverlayState(state =>
                                        state with { ActiveProfileName = rematch.Profile.Name }
                                    );

                                    _audio.WhisperModeEnabled =
                                        rematch.Profile.WhisperModeOverride
                                        ?? _settings.Current.WhisperModeEnabled;
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Dictation] Deferred URL re-match failed: {ex.Message}");
                }
            });
            _recordingSnapshotTask = recordingSnapshotTask;

            // Final in-gate re-check: a lock can land during the slow synchronous setup above, and
            // while StartAsync holds the gate a concurrent session-loss discard times out and bails
            // without stopping audio — so tear the just-started recording down here. Mirrors the
            // full post-start-failure teardown so no streaming coordinator, live-frame sink,
            // partial loop or late snapshot survives to send buffered audio or repopulate state.
            if (!_sessionActivityMonitor.IsInputAllowed)
            {
                Trace.WriteLine("[Dictation] Session locked during start; rolling back recording.");
                RollBackStartedRecording();
                _ = await StopPartialTranscriptionSessionAsync();

                StreamingTranscriptionCoordinator? rolledBackCoordinator;
                CancellationTokenSource? rolledBackStartupCts;
                lock (_recordingSessionLock)
                {
                    // Advance the generation so the background snapshot task drops its writes and
                    // never publishes RecordingStartedEvent or restores stale profile fields.
                    _recordingSession++;
                    rolledBackCoordinator = _streamingCoordinator;
                    rolledBackStartupCts = _streamingStartupCts;
                    _streamingCoordinator = null;
                    _streamingStartupCts = null;
                    _streamingProviderId = null;
                    _streamingModelId = null;
                    _streamingLanguageHint = null;
                }

                _audio.LiveFrameSink = null;
                _ = await TeardownStreamingSessionAsync(
                    rolledBackCoordinator,
                    rolledBackStartupCts,
                    false,
                    CancellationToken.None
                );

                _hotkey.IsCancelShortcutEnabled = false;
                _activeDictationCts?.Dispose();
                _activeDictationCts = null;
                ClearSessionInFlight(startedSessionId);
                startedSessionId = 0;
            }

            // ReSharper disable once BadControlBracesIndent -- the `goto StartupComplete` label is deliberately deindented; the brace-indent nit is a byproduct of that layout.
        StartupComplete:;
        }
        finally
        {
            pendingStop = _toggleGate.CompleteStartupAndRelease();
        }

        if (pendingStop.HasPendingStop)
        {
            await ForwardDeferredStopAsync(pendingStop);
        }

        return startedSessionId;
    }

    // Honors a stop queued while this startup held the gate. Restore _cancelRequested *before*
    // re-acquiring so an ordinary stop that wins the release-window race still folds in the discard
    // intent instead of saving audio the user canceled. If another startup holds the gate, the stop
    // is re-queued (cancel intent intact) and that startup honors it.
    private async Task ForwardDeferredStopAsync(DictationDeferredStop deferredStop)
    {
        if (deferredStop.WasCancel)
        {
            _cancelRequested = true;
        }

        if (_toggleGate.TryAcquireForStop(deferredStop.WasCancel) != DictationStopGateResult.Acquired)
        {
            return;
        }

        await StopWhileHoldingGateAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Discards any active recording because the login session became inactive/locked. Unlike
    ///     <see cref="StopAsync" /> it forces the cancel/discard path so audio is dropped without
    ///     transcription or text insertion — synthesized paste/type must not reach the lock screen.
    /// </summary>
    public async Task AbortForSessionLossAsync()
    {
        _cancelRequested = true;

        // Cancel in-flight transcription / spoken command(s) / insertion first: the session can lock
        // after StopAsync already stopped capture and released its gate, in which case the stop
        // below is a no-op but a completed transcription could still type into the lock screen.
        await CancelInFlightWorkAsync(cancelAllCommands: true).ConfigureAwait(false);

        // Unlike StopAsync's non-blocking probe, block (bounded) for the gate so a discard isn't
        // silently dropped while a start/stop is mid-flight. Keep the gate across the stop (rather
        // than release/reacquire) so a concurrent start can't slip in and record behind the lock.
        if (!await _toggleGate.WaitAsync(s_sessionLossGateTimeout).ConfigureAwait(false))
        {
            Trace.WriteLine("[Dictation] Session-loss discard timed out waiting for the toggle gate.");
            return;
        }

        // _cancelRequested makes the stop discard the audio without transcription or insertion.
        _cancelRequested = true;
        await StopWhileHoldingGateAsync().ConfigureAwait(false);
    }

    // Cancels the spoken-command source(s) first (they run after recording stops) then the
    // recording source, so a long-running command or transcription unwinds immediately.
    // cancelAllCommands widens the scope from the newest command (Escape/IPC cancel) to all of them.
    private async Task CancelInFlightWorkAsync(bool cancelAllCommands = false)
    {
        CancellationTokenSource[] commandSources;
        if (cancelAllCommands)
        {
            lock (_activeCommandCtsSet)
            {
                commandSources = [.. _activeCommandCtsSet];
            }
        }
        else
        {
            var newest = _activeCommandCts;
            commandSources = newest is null ? [] : [newest];
        }

        foreach (var commandCts in commandSources)
        {
            try
            {
                await commandCts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                /* command already finished and disposed it */
            }
        }

        var cts = _activeDictationCts;
        if (cts is not null)
        {
            try
            {
                await cts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                /* StopAsync just disposed it — nothing to cancel. */
            }
        }
    }

    public Task StopAsync()
    {
        // External stops carry no cancel intent of their own; the private overload folds any
        // in-flight discard in via _cancelRequested.
        return StopAsync(cancelRequested: false);
    }

    private async Task StopAsync(bool cancelRequested)
    {
        // Fold both intent sources into the gate so a stop deferred behind an in-progress startup
        // keeps its discard semantics even if a racing start clears the shared _cancelRequested flag.
        var wasCancel = cancelRequested || _cancelRequested;
        if (_toggleGate.TryAcquireForStop(wasCancel) != DictationStopGateResult.Acquired)
        {
            return;
        }

        // Owning the gate directly: make the shared flag agree with the intent so teardown discards.
        if (wasCancel)
        {
            _cancelRequested = true;
        }

        await StopWhileHoldingGateAsync().ConfigureAwait(false);
    }

    // Runs the stop teardown assuming the caller already owns _toggleGate; the finally releases it.
    private async Task StopWhileHoldingGateAsync()
    {
        var earlyCleanupDone = false;
        var wasRecording = false;
        var gateReleased = false;
        CancellationTokenSource? snapshotCts = null;
        int? insertionOrderSessionId = null;
        try
        {
            if (!_audio.IsRecording)
            {
                return;
            }

            wasRecording = true;

            // Snapshot and clear the cancel flag now that we own the gate.
            var canceledThisStop = _cancelRequested;
            _cancelRequested = false;

            // ReSharper disable once MethodSupportsCancellation -- stop path must run teardown to completion; recording stop is intentionally non-cancellable.
            var wav = await _audio.StopRecordingAsync();
            var recoveredPartialPreview = await StopPartialTranscriptionSessionAsync();
            await AwaitRecordingSnapshotAsync();
            _audioDucking.RestoreAudio();
            _mediaPause.ResumeMedia();
            earlyCleanupDone = true;
            if (_settings.Current.SoundFeedbackEnabled)
            {
                _soundFeedback.PlayRecordingStopped();
            }

            RecordingStateChanged?.Invoke(this, false);

            // Snapshot context into locals so transcription reads a stable view
            // even if a new StartAsync overwrites instance fields. Null the
            // shared CTS so Escape targets only live recordings — once nulled,
            // Cancel can no longer reach this dictation's background transcription.
            snapshotCts = _activeDictationCts;
            _activeDictationCts = null;
            _hotkey.IsCancelShortcutEnabled = false;
            _cancelRequested = false;

            // Advance the session counter so any late active-window snapshot
            // drops its writes rather than clobbering the next dictation's
            // fields. Snapshot the streaming coordinator before releasing the
            // gate — a new StartAsync could install a fresh coordinator on the
            // shared field before we tear down ours.
            StreamingTranscriptionCoordinator? stoppedStreamingCoordinator;
            CancellationTokenSource? stoppedStreamingStartupCts;
            RecordingContext recordingContext;
            lock (_recordingSessionLock)
            {
                var stoppedSessionId = _recordingSession;
                _recordingSession++;
                stoppedStreamingCoordinator = _streamingCoordinator;
                stoppedStreamingStartupCts = _streamingStartupCts;
                var stoppedStreamingProviderId = _streamingProviderId;
                var stoppedStreamingModelId = _streamingModelId;
                var stoppedStreamingLanguageHint = _streamingLanguageHint;
                _streamingCoordinator = null;
                _streamingStartupCts = null;
                _streamingProviderId = null;
                _streamingModelId = null;
                _streamingLanguageHint = null;

                recordingContext = new RecordingContext(
                    stoppedSessionId,
                    _recordingStart,
                    _recordingAppProcess,
                    _recordingAppTitle,
                    _recordingAppUrl,
                    _recordingWindowId,
                    _recordingProfile,
                    recoveredPartialPreview,
                    null,
                    false,
                    stoppedStreamingProviderId,
                    stoppedStreamingModelId,
                    stoppedStreamingLanguageHint,
                    snapshotCts?.Token ?? CancellationToken.None
                );

                _recordingAppProcess = null;
                _recordingAppTitle = null;
                _recordingAppUrl = null;
                _recordingWindowId = null;
                _recordingProfile = null;
                _recordingStart = default;
            }

            if (stoppedStreamingCoordinator is not null)
            {
                _audio.LiveFrameSink = null;
            }

            // Release the gate now that capture is torn down and context is
            // snapshotted. A new StartAsync can record while transcription runs.
            // Reserve this session's insertion-order slot before releasing the
            // gate so reservations happen in strict session-start order — a new
            // StartAsync's own future stop cannot reach this point until this one
            // has passed it (audit §2 H3).
            _insertionOrder.Reserve(recordingContext.SessionId);
            insertionOrderSessionId = recordingContext.SessionId;

            _toggleGate.Release();
            gateReleased = true;

            // Single terminal guard (audit §2 H1): every post-stop step below
            // can throw, and the session id must leave `_inFlightTracker` no
            // matter which step fails — `RunAsync`'s finally is the chokepoint
            // that guarantees that. The catch below additionally turns an
            // otherwise-silent failure (e.g. disk-full saving the WAV, a
            // throwing RecordingCaptured subscriber) into a published "failed"
            // terminal and visible overlay feedback instead of leaving
            // IsSessionInFlight stuck true and the overlay on "Processing…"
            // forever.
            try
            {
                await _inFlightTracker.RunAsync(recordingContext.SessionId, async () =>
                {
                    if (canceledThisStop)
                    {
                        // User hit Escape while still recording: clean up audio/media
                        // (already done above) and surface "Canceled" without saving
                        // the WAV or running transcription.
                        SetOverlayState(state =>
                            state with
                            {
                                IsOverlayVisible = true,
                                ShowFeedback = true,
                                FeedbackText = Localization.Loc.Instance["Overlay.Canceled"],
                                FeedbackIsError = false,
                                IsRecording = false,
                                StatusText = Localization.Loc.Instance["Overlay.Canceled"],
                                PartialText = null,
                                SessionStartedAtUtc = null
                            }
                        );
                        StatusMessage?.Invoke(this, "Canceled");
                        _models.PluginManager.EventBus.Publish(
                            new RecordingStoppedEvent
                            {
                                DurationSeconds = LinuxDictationShortSpeechPolicy.ComputeDurationSeconds(
                                    wav
                                )
                            }
                        );
                        _ = await TeardownStreamingSessionAsync(
                            stoppedStreamingCoordinator,
                            stoppedStreamingStartupCts,
                            false,
                            CancellationToken.None
                        );
                        FinalizeSession(recordingContext.SessionId, "canceled", "Canceled");
                        return;
                    }

                    SetOverlayState(state =>
                        state with
                        {
                            IsOverlayVisible = true,
                            ShowFeedback = false,
                            FeedbackText = null,
                            FeedbackIsError = false,
                            IsRecording = false,
                            StatusText = Localization.Loc.Instance["Overlay.Processing"],
                            SessionStartedAtUtc = null
                        }
                    );
                    var duration = LinuxDictationShortSpeechPolicy.ComputeDurationSeconds(wav);
                    _models.PluginManager.EventBus.Publish(
                        new RecordingStoppedEvent { DurationSeconds = duration }
                    );

                    var shortSpeechDecision = LinuxDictationShortSpeechPolicy.Classify(
                        duration,
                        LinuxDictationShortSpeechPolicy.ComputePeakLevel(wav),
                        _settings.Current.TranscribeShortQuietClipsAggressively
                    );

                    // Transcribe intentionally falls through to the normal transcription path below.
                    // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
                    switch (shortSpeechDecision)
                    {
                        case LinuxShortSpeechDecision.DiscardTooShort:
                            SetOverlayState(state =>
                                state with
                                {
                                    IsOverlayVisible = true,
                                    ShowFeedback = true,
                                    FeedbackText = Localization.Loc.Instance["Overlay.TooShort"],
                                    FeedbackIsError = true,
                                    IsRecording = false,
                                    StatusText = Localization.Loc.Instance["Overlay.TooShort"],
                                    PartialText = null
                                }
                            );
                            StatusMessage?.Invoke(this, "Too short");
                            _ = await TeardownStreamingSessionAsync(
                                stoppedStreamingCoordinator,
                                stoppedStreamingStartupCts,
                                false,
                                CancellationToken.None
                            );
                            FinalizeSession(recordingContext.SessionId, "discarded", "Too short");
                            return;
                        case LinuxShortSpeechDecision.DiscardNoSpeech:
                            SetOverlayState(state =>
                                state with
                                {
                                    IsOverlayVisible = true,
                                    ShowFeedback = true,
                                    FeedbackText = Localization.Loc.Instance["Overlay.NoSpeech"],
                                    FeedbackIsError = true,
                                    IsRecording = false,
                                    StatusText = Localization.Loc.Instance["Overlay.NoSpeech"],
                                    PartialText = null
                                }
                            );
                            StatusMessage?.Invoke(this, "No speech detected");
                            _ = await TeardownStreamingSessionAsync(
                                stoppedStreamingCoordinator,
                                stoppedStreamingStartupCts,
                                false,
                                CancellationToken.None
                            );
                            FinalizeSession(
                                recordingContext.SessionId,
                                "discarded",
                                "No speech detected"
                            );
                            return;
                    }

                    // Streaming finalize must run BEFORE pad/save so the EOF grace-window
                    // flush captures any trailing partials. Read fault state from the
                    // just-torn-down coordinator, never from a shared field a racing
                    // StartAsync could have reset.
                    if (stoppedStreamingCoordinator is not null)
                    {
                        var streamingCancelToken = snapshotCts?.Token ?? CancellationToken.None;
                        var (streamingFinalText, streamingFaulted) =
                            await TeardownStreamingSessionAsync(
                                stoppedStreamingCoordinator,
                                stoppedStreamingStartupCts,
                                true,
                                streamingCancelToken
                            );
                        recordingContext = recordingContext with
                        {
                            StreamingFinalText = streamingFinalText,
                            StreamingFaulted = streamingFaulted
                        };
                    }

                    // Keep the recorded (pre-padding) length: PadWavForFinalTranscription adds ~0.3s of
                    // silence, which would push a borderline-short silent clip past the hallucination filter's
                    // duration cutoff and let a stock "Thank you." artifact through.
                    var recordedDuration = duration;
                    wav = LinuxDictationShortSpeechPolicy.PadWavForFinalTranscription(
                        wav,
                        duration
                    );
                    duration = LinuxDictationShortSpeechPolicy.ComputeDurationSeconds(wav);

                    var path = _sessionAudioFiles.SaveDictationCapture(wav);
                    RecordingCaptured?.Invoke(this, path);
                    Trace.WriteLine($"[Dictation] Captured → {path} ({wav.Length} bytes)");

                    await TranscribeAndInsertAsync(
                        wav,
                        path,
                        duration,
                        recordedDuration,
                        recordingContext
                    );
                });
            }
            catch (OperationCanceledException)
            {
                Trace.WriteLine("[Dictation] Post-stop pipeline canceled before completion.");
                PublishSessionTerminal(recordingContext.SessionId, "canceled", "Canceled");
            }
            catch (Exception ex)
            {
                // Reached only for a step BEFORE TranscribeAndInsertAsync's own
                // try/catch chain (streaming teardown, WAV padding, capture
                // persistence, the RecordingCaptured event), or a rethrow from
                // its `await using` lease disposal. Either way the session must
                // not stay "in_progress" forever (audit §2 H1).
                Trace.WriteLine($"[Dictation] Post-stop pipeline failed before completion: {ex}");
                // Publish the terminal result FIRST: RunAsync's finally has
                // already dropped the id from the in-flight set, so a throw from
                // any log/UI callback below must not stop the "failed" result
                // from being recorded — otherwise the session would poll as
                // not_found forever, defeating audit §2 H1.
                PublishSessionTerminal(recordingContext.SessionId, "failed", ex.Message);
                _errorLog.AddEntry(
                    $"Dictation capture could not be saved or transcribed ({ex.Message}).",
                    ErrorCategory.Recording
                );
                ReportStatus(
                    recordingContext,
                    Localization.Loc.Instance["Overlay.CaptureSaveFailed"]
                );
                ShowFeedback(
                    recordingContext,
                    Localization.Loc.Instance["Overlay.CaptureSaveFailed"],
                    true
                );
            }
        }
        finally
        {
            // Safety net: every early-discard branch above and every early return
            // in TranscribeAndInsertAsync that never reaches the insertion call
            // must still release this session's insertion-order slot, or a
            // successor blocked in WaitForTurnAsync would wait forever. Release is
            // idempotent, so this is a no-op on the common path, which already
            // released around the insertion call.
            if (insertionOrderSessionId is { } reservedSessionId)
            {
                _insertionOrder.Release(reservedSessionId);
            }

            // Restore ducking/media only when there was an active recording and
            // the normal cleanup path didn't already run (earlyCleanupDone).
            if (wasRecording && !earlyCleanupDone)
            {
                _audioDucking.RestoreAudio();
                _mediaPause.ResumeMedia();
            }

            // Dispose after TranscribeAndInsertAsync so token registrations
            // remain valid for the full pipeline lifetime.
            if (snapshotCts is not null)
            {
                try
                {
                    snapshotCts.Dispose();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(
                        $"[Dictation] Active dictation CTS dispose failed: {ex.Message}"
                    );
                }
            }

            if (!gateReleased)
            {
                _toggleGate.Release();
            }
        }
    }

    /// <summary>
    ///     True while <paramref name="sessionId" /> is recording or in its
    ///     post-stop transcription pipeline. Backed by an explicit in-flight set
    ///     removed at every terminal point (success, cancel, failure, discard).
    ///     Unknown/completed ids return false — callers should fall back to
    ///     <see cref="DictationSessionResultStore" /> to distinguish completed
    ///     states from "not_found".
    /// </summary>
    public bool IsSessionInFlight(int sessionId)
    {
        return _inFlightTracker.Contains(sessionId);
    }

    /// <summary>
    ///     Maps an overlay StatusText to one of the <c>typewhisper status</c>
    ///     labels (transcribing / injecting / idle). The <c>recording</c> label
    ///     comes from the audio recorder, not StatusText, so it is not produced here.
    /// </summary>
    internal static string MapOverlayStatusToStateLabel(string? statusText)
    {
        if (statusText is null)
        {
            return "idle";
        }

        if (
            statusText.StartsWith("Processing", StringComparison.OrdinalIgnoreCase)
            || statusText.StartsWith("Transcribing", StringComparison.OrdinalIgnoreCase)
        )
        {
            return "transcribing";
        }

        // Overlay shows "Inserting…"; the documented CLI state is "injecting".
        return statusText.StartsWith("Inserting", StringComparison.OrdinalIgnoreCase)
            ? "injecting"
            : "idle";
    }

    /// <summary>
    ///     Returns the enabled, non-manual-only action matching
    ///     <paramref name="promptActionId" />. Manual-only actions are excluded
    ///     so they only fire from the palette or per-action hotkey, not a Profile
    ///     binding. Exposed internally for unit testing.
    /// </summary>
    internal static PromptAction? ResolveAutoPromptAction(
        string? promptActionId,
        IReadOnlyList<PromptAction> enabledActions
    )
    {
        if (string.IsNullOrWhiteSpace(promptActionId))
        {
            return null;
        }

        return enabledActions.FirstOrDefault(action =>
            action.Id == promptActionId && !action.IsManualOnly
        );
    }

    /// <summary>
    ///     Selects the raw text for post-processing, falling back to the
    ///     streaming live preview when batch transcription returned nothing.
    ///     Exposed internally for unit testing.
    /// </summary>
    internal static string SelectRawTextWithPreviewFallback(
        string? batchText,
        string recoveredPreview,
        out bool usedPreviewFallback
    )
    {
        var rawText = LinuxDictationFinalTextPolicy.SelectRawText(batchText);
        if (!string.IsNullOrEmpty(rawText))
        {
            usedPreviewFallback = false;
            return rawText;
        }

        var preview = LinuxDictationFinalTextPolicy.SelectRawText(recoveredPreview);
        if (!string.IsNullOrEmpty(preview))
        {
            usedPreviewFallback = true;
            return preview;
        }

        usedPreviewFallback = false;
        return "";
    }

    /// <summary>
    ///     Resolves the language post-processing should treat the transcript as.
    ///     "en" only when a translate task was requested AND the engine supports
    ///     translation — engines with <c>SupportsTranslation=false</c> ignore the
    ///     translate task and return source-language text; otherwise the detected
    ///     (else configured) source language (audit §2 M1).
    ///     Exposed internally for unit testing.
    /// </summary>
    internal static string? ResolvePostProcessingSourceLanguage(
        string? detectedLanguage,
        string? configuredLanguage,
        bool translateRequested,
        bool engineSupportsTranslation
    )
    {
        var engineTranslatedToEnglish = translateRequested && engineSupportsTranslation;
        return engineTranslatedToEnglish ? "en" : detectedLanguage ?? configuredLanguage;
    }

    public event EventHandler<string>? RecordingCaptured; // arg = WAV file path
    public event EventHandler<bool>? RecordingStateChanged;
    public event EventHandler<string>? TranscriptionCompleted;
    public event EventHandler<string>? StatusMessage;
    public event EventHandler<DictationOverlayState>? OverlayStateChanged;

    /// <summary>
    ///     Fires once per dictation immediately after the publish of
    ///     <see cref="TranscriptionCompletedEvent" /> with the just-completed
    ///     session's metadata. Used by <see cref="DictationSessionResultStore" />
    ///     to back the <c>GET /v1/dictation/transcription</c> poll endpoint.
    /// </summary>
    public event Action<DictationSessionResult>? SessionCompleted;

    private async Task TranscribeAndInsertAsync(
        byte[] wav,
        string wavPath,
        double duration,
        double recordedDuration,
        RecordingContext context
    )
    {
        var cancelToken = context.CancelToken;

        // Attach an LLM-provenance sink only when it will actually be persisted:
        // capture piggybacks history storage, so both toggles must be on. Left
        // null otherwise, so the prompt chokepoint records (and costs) nothing.
        if (_settings.Current is { SaveToHistoryEnabled: true, CaptureLlmProvenance: true })
        {
            context = context with { Capture = new LlmCallCapture() };
        }

        var effectiveModelId =
            context.Profile?.TranscriptionModelOverride ?? _settings.Current.SelectedModelId;

        // Exclusive lease serializes model-load + transcribe so a concurrent
        // dictation cannot swap the plugin's native model mid-flight. Held for
        // the whole method; `await using` releases on every early return.
        ModelManagerService.TranscriptionLease lease;
        try
        {
            lease = await _models.AcquireTranscriptionAsync(
                effectiveModelId,
                cancellationToken: cancelToken
            );
        }
        catch (OperationCanceledException) when (cancelToken.IsCancellationRequested)
        {
            Trace.WriteLine($"[Dictation] Model load canceled by user ('{effectiveModelId}').");
            ReportStatus(context, "Canceled");
            ShowFeedback(context, "Canceled", false, true);
            PublishSessionTerminal(context.SessionId, "canceled", "Canceled");
            return;
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[Dictation] Failed to load effective model '{effectiveModelId}': {ex}"
            );
            _errorLog.AddEntry(
                $"Transcription model '{effectiveModelId}' failed to load: {ex.Message}",
                ErrorCategory.Transcription
            );
            ReportStatus(context, $"Failed to load configured model: {ex.Message}");
            ShowFeedback(context, "Model load failed.", true);
            PublishSessionTerminal(context.SessionId, "failed", ex.Message);
            return;
        }

        await using var leaseScope = lease;
        var plugin = lease.Plugin;

        // Capture engine metadata while the lease is held; the lease releases
        // after TranscribeAsync, so reading these during post-processing would
        // race a concurrent dictation's model swap.
        var engineProviderId = plugin.ProviderId;
        var engineModelId = plugin.SelectedModelId;
        var engineSupportsTranslation = plugin.SupportsTranslation;

        ReportStatus(context, $"Transcribing via {plugin.ProviderDisplayName}…");

        var transcriptionCompletedPublished = false;
        try
        {
            var effectiveLanguage = context.Profile?.InputLanguage ?? _settings.Current.Language;
            // ReSharper disable once InlineTemporaryVariable -- named local kept for readability over inlining into the pattern match.
            var languageHint =
                effectiveLanguage is { Length: > 0 } lang && lang != "auto" ? lang : null;
            var translate = string.Equals(
                context.Profile?.SelectedTask ?? _settings.Current.TranscriptionTask,
                "translate",
                StringComparison.OrdinalIgnoreCase
            );

            PluginTranscriptionResult? result;
            try
            {
                // Reject streaming if the engine or language changed mid-session
                // (profile override resolved post-start, or global vs. profile
                // InputLanguage mismatch). Also reject when translate is requested —
                // streaming doesn't support it.
                var streamingEngineMatches =
                    context.StreamingProviderId is not null
                    && string.Equals(
                        context.StreamingProviderId,
                        plugin.ProviderId,
                        StringComparison.Ordinal
                    )
                    && string.Equals(
                        context.StreamingModelId,
                        plugin.SelectedModelId,
                        StringComparison.Ordinal
                    );
                var streamingLanguageMatches = string.Equals(
                    context.StreamingLanguageHint,
                    languageHint,
                    StringComparison.Ordinal
                );

                if (
                    !string.IsNullOrWhiteSpace(context.StreamingFinalText)
                    && !context.StreamingFaulted
                    && streamingEngineMatches
                    && streamingLanguageMatches
                    && !translate
                )
                {
                    // Streaming finalized cleanly within its deadlines — skip
                    // the redundant batch call.
                    result = new PluginTranscriptionResult(
                        context.StreamingFinalText!,
                        languageHint,
                        DurationSeconds: duration
                    );
                }
                else
                {
                    // Streaming faulted, timed out, or produced nothing — fall
                    // back to batch on the captured WAV. The audio tap is
                    // non-destructive so the WAV is complete regardless of
                    // streaming state.
                    result = await plugin.TranscribeAsync(
                        wav,
                        languageHint,
                        translate,
                        null,
                        cancelToken
                    );
                }
            }
            catch (OperationCanceledException) when (cancelToken.IsCancellationRequested)
            {
                Trace.WriteLine("[Dictation] Transcription canceled by user.");
                ReportStatus(context, "Canceled");
                ShowFeedback(context, "Canceled", false, true);
                PublishSessionTerminal(context.SessionId, "canceled", "Canceled");
                return;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Dictation] Transcription failed: {ex}");
                _errorLog.AddEntry(
                    $"Transcription failed via {plugin.ProviderDisplayName} ({engineModelId}): {ex.Message}",
                    ErrorCategory.Transcription
                );
                _models.PluginManager.EventBus.Publish(
                    new TranscriptionFailedEvent
                    {
                        ErrorMessage = ex.Message, ModelId = engineModelId, AppName = context.AppTitle
                    }
                );
                ReportStatus(context, $"Transcription failed: {ex.Message}");
                _speechFeedback.AnnounceError(ex.Message);
                ShowFeedback(context, "Transcription failed.", true);
                PublishSessionTerminal(context.SessionId, "failed", ex.Message);
                return;
            }
            finally
            {
                // Release the model lock now so a concurrent dictation isn't
                // blocked by post-processing, insertion, and history below.
                // ReSharper disable once DisposeOnUsingVariable -- intentional early dispose to release the lock; the using re-dispose at scope end is idempotent.
                await leaseScope.DisposeAsync();
            }

            // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract -- result comes from a plugin transcription call whose non-null annotation may not hold.
            var rawText = SelectRawTextWithPreviewFallback(
                result?.Text,
                context.RecoveredPartialPreview,
                out var usedPreviewFallback
            );
            if (usedPreviewFallback)
            {
                Trace.WriteLine(
                    "[Dictation] Batch transcription returned empty; "
                    + $"substituting live-preview fallback ({rawText.Length} chars)."
                );
            }

            if (string.IsNullOrEmpty(rawText))
            {
                ReportStatus(context, "Transcription returned no text.");
                ShowFeedback(context, "Transcription returned no text.", true);
                PublishSessionTerminal(
                    context.SessionId,
                    "discarded",
                    "Transcription returned no text."
                );
                return;
            }

            // Skip the no-speech guard when the preview fallback fired: the
            // streaming session captured real words, so the engine's no-speech
            // verdict on the empty batch pass is exactly what the fallback recovers.
            // A compound boolean guard reads clearer as an if than as a switch.
            // ReSharper disable once ConvertIfStatementToSwitchStatement
            if (
                !usedPreviewFallback
                && result?.NoSpeechProbability is > 0.8f
                && !_settings.Current.TranscribeShortQuietClipsAggressively
            )
            {
                ReportStatus(context, "No speech detected.");
                ShowFeedback(context, "No speech detected.", true);
                PublishSessionTerminal(context.SessionId, "discarded", "No speech detected.");
                return;
            }

            // Whisper's stock silence artifacts ("Thank you.") slip past the no-speech gate above.
            // Scope this to engines that report a no-speech probability — it's a Whisper-family
            // signal, and applying it when the engine returns null (as many non-Whisper plugins do)
            // would discard short real dictations like "you" or "bye" as hallucinations. Honor the
            // aggressive short-clip setting too, exactly as the no-speech gate above does.
            if (!usedPreviewFallback
                && !_settings.Current.TranscribeShortQuietClipsAggressively
                && result?.NoSpeechProbability is not null
                && WhisperHallucinationFilter.IsLikelyHallucination(
                    rawText,
                    recordedDuration,
                    result.NoSpeechProbability))
            {
                Trace.WriteLine(
                    $"[Dictation] Discarded likely Whisper hallucination ('{rawText}', {recordedDuration:0.00}s)."
                );
                ReportStatus(context, "No speech detected.");
                ShowFeedback(context, "No speech detected.", true);
                PublishSessionTerminal(context.SessionId, "discarded", "No speech detected.");
                return;
            }

            // Spoken command mode: a dictation that opens with the keyphrase is an
            // instruction, not text to type. Route it to the LLM before cleanup so the
            // command is neither mangled by post-processing nor typed literally.
            if (
                _settings.Current.CommandModeEnabled
                && SpokenCommandKeyphrase.TryStrip(
                    rawText,
                    _settings.Current.CommandKeyphrase,
                    out var spokenCommand
                )
            )
            {
                // Spoken commands insert through their own path (one-shot or
                // streamed-while-typing), not the InsertTextAsync/
                // ExecuteActionPluginAsync boundary this gate orders — their
                // delivery is out of scope for audit §2 H3. Release now rather
                // than hold a waiting successor for the whole LLM+typing round
                // trip.
                _insertionOrder.Release(context.SessionId);
                var outcome = await RunSpokenCommandAsync(spokenCommand, context, cancelToken);
                // A spoken command is still a dictation the user issued: record it in
                // history (with the LLM request/response captured on context.Capture)
                // so it appears in the History list and Inspect panel like any other.
                // RawText is the source the command acted on (selected text for an
                // edit, the command itself for a create), so the raw→final diff reads
                // "source → result".
                if (outcome is not null && _settings.Current.SaveToHistoryEnabled)
                {
                    AddSpokenCommandHistoryRecord(
                        context,
                        outcome.SourceText,
                        outcome.Result,
                        duration,
                        result,
                        wavPath,
                        engineProviderId,
                        engineModelId,
                        outcome.InsertionStatus
                    );
                }

                return;
            }

            var postProcessingLanguage = ResolvePostProcessingSourceLanguage(
                result?.DetectedLanguage,
                languageHint,
                translate,
                engineSupportsTranslation
            );

            var pipelineContext = new PostProcessingContext
            {
                SourceLanguage = postProcessingLanguage,
                ActiveAppName = context.AppTitle,
                ActiveAppProcessName = context.AppProcess,
                ProfileName = context.Profile?.Name,
                AudioDurationSeconds = duration
            };

            var promptAction = ResolvePromptAction(context);
            if (context.Profile is not null)
            {
                Trace.WriteLine(
                    $"[Dictation] Matched profile '{context.Profile.Name}' "
                    + $"(process='{context.AppProcess ?? "<unknown>"}', "
                    + $"url='{context.AppUrl ?? "<unknown>"}', "
                    + $"promptAction='{promptAction?.Name ?? "<none>"}')."
                );

                if (
                    !string.IsNullOrWhiteSpace(context.Profile.PromptActionId)
                    && promptAction is null
                )
                {
                    // Fail early before building the pipeline — no point running
                    // lower-priority steps on a transcript we'll reject anyway.
                    var message =
                        $"Prompt action for profile '{context.Profile.Name}' is disabled or missing.";
                    Trace.WriteLine(
                        $"[Dictation] {message} actionId='{context.Profile.PromptActionId}'."
                    );
                    ReportStatus(context, message);
                    throw new InvalidOperationException(message);
                }
            }

            var translationTarget =
                context.Profile?.TranslationTarget ?? _settings.Current.TranslationTargetLanguage;
            var cleanupLevel = ResolveCleanupLevel(context, promptAction);

            var pluginProcessors = _models
                .PluginManager.PostProcessors.Select(processor => new PluginPostProcessor(
                    processor.Priority,
                    (text, token) => processor.ProcessAsync(text, pipelineContext, token)
                ))
                .ToList();

            var pipelineResult = await _pipeline.ProcessAsync(
                rawText,
                new PipelineOptions
                {
                    NormalizeSpokenLineBreaks = true,
                    NormalizeSpokenPunctuation = true,
                    AppFormatter = AppFormatterService.Format,
                    TargetProcessName = context.AppProcess,
                    DictionaryCorrector = _dictionary.ApplyCorrections,
                    VocabularyBooster = _settings.Current.VocabularyBoostingEnabled
                        ? _vocabularyBoosting.Apply
                        : null,
                    CleanupHandler =
                        cleanupLevel == CleanupLevel.None
                            ? null
                            : (text, token) =>
                                _cleanup.CleanAsync(
                                    text,
                                    cleanupLevel,
                                    message =>
                                    {
                                        ReportStatus(context, message);
                                        return Task.CompletedTask;
                                    },
                                    context.Capture,
                                    token
                                ),
                    SnippetExpander = text =>
                        _snippets.ApplySnippets(text, profileId: context.Profile?.Id),
                    LlmHandler = promptAction is not null
                        ? (text, token) => RunPromptActionAsync(context, promptAction, text, token)
                        : null,
                    RequireLlmSuccess = promptAction is not null,
                    TranslationHandler = !string.IsNullOrWhiteSpace(translationTarget)
                        ? (text, source, target, token) =>
                            _translation.TranslateAsync(text, source, target, context.Capture, token)
                        : null,
                    TranslationTarget = string.IsNullOrWhiteSpace(translationTarget)
                        ? null
                        : translationTarget,
                    EffectiveSourceLanguage = postProcessingLanguage,
                    DetectedLanguage = postProcessingLanguage,
                    PluginPostProcessors = pluginProcessors,
                    StatusCallback = status =>
                    {
                        ReportStatus(
                            context,
                            status == "AI" ? "Processing prompt action…" : $"Processing {status}…"
                        );
                        return Task.CompletedTask;
                    }
                },
                cancelToken
            );

            var commandResult = VoiceCommandParser.Parse(pipelineResult.Text);
            var finalText = ApplyProfileStyleFormatting(context, commandResult.Text);

            TranscriptionCompleted?.Invoke(this, finalText);
            // The orchestrator has full language context (detection, profile /
            // global input language, translate task, and the actual
            // post-processing step outcomes), so it resolves the readback
            // language itself and opts out of SpeechFeedbackService's
            // configured-language fallback.
            _speechFeedback.AnnounceTranscriptionComplete(
                finalText,
                LinuxDictationReadbackLanguagePolicy.Resolve(
                    result?.DetectedLanguage,
                    effectiveLanguage,
                    translate,
                    translationTarget,
                    pipelineResult.Steps
                ),
                false
            );
            _models.PluginManager.EventBus.Publish(
                new TranscriptionCompletedEvent
                {
                    RawText = rawText,
                    Text = finalText,
                    DetectedLanguage = result?.DetectedLanguage,
                    DurationSeconds = duration,
                    EngineUsed = engineProviderId,
                    ModelId = engineModelId,
                    ProfileName = context.Profile?.Name,
                    AppName = context.AppTitle,
                    AppProcessName = context.AppProcess,
                    Url = context.AppUrl
                }
            );
            PublishSessionResult(
                new DictationSessionResult(
                    context.SessionId,
                    "ready",
                    finalText,
                    rawText,
                    result?.DetectedLanguage,
                    duration,
                    engineProviderId,
                    engineModelId
                )
            );
            transcriptionCompletedPublished = true;

            var actionPlugin = ResolveActionPlugin(promptAction);

            // Yield focus before any synthesized keystroke: on Wayland a
            // visible overlay can still hold keyboard focus, and ydotool's
            // virtual keyboard fires Ctrl+V to whatever has focus. wtype on
            // GNOME/KDE was always compositor-rejected, so this was latent
            // until the ydotool backend was added.
            if (actionPlugin is null && !commandResult.CancelInsertion)
            {
                // Surface the inject phase so `typewhisper status` reports
                // `injecting` while a long transcript is still being typed.
                ReportStatus(context, "Inserting…");
                await YieldFocusForInsertionAsync().ConfigureAwait(false);
            }

            // Pad with a trailing space so back-to-back dictations don't run
            // together. Only the insertion and TextInsertedEvent use this;
            // history, recent transcriptions, and completion events keep the
            // unpadded finalText.
            var insertionText = DictationInsertionTextFormatter.TextForInsertion(finalText);

            InsertionResult insertion;
            try
            {
                // Wait for every earlier-started session to finish inserting first
                // (audit §2 H3) — but only around the delivery call itself, not the
                // transcription/post-processing above, which stays concurrent.
                await _insertionOrder.WaitForTurnAsync(context.SessionId, cancelToken)
                    .ConfigureAwait(false);

                // Final lock check before synthesizing any keystroke, re-evaluated
                // AFTER the insertion-order wait above. A normal stop nulls
                // _activeDictationCts and releases the gate before transcription
                // finishes, and the wait can span an arbitrary predecessor (up to
                // the fail-open backstop), so a lock landing during transcription
                // OR while queued cannot reach that token — this authoritative
                // check keeps synthesized paste/type off the lock screen.
                if (!_sessionActivityMonitor.IsInputAllowed)
                {
                    Trace.WriteLine("[Dictation] Insertion suppressed: session locked or inactive.");
                    ReportStatus(context, "Canceled");
                    ShowFeedback(context, "Canceled", false, true);
                    return;
                }

                insertion =
                    commandResult.CancelInsertion
                        ? InsertionResult.NoText
                        : actionPlugin is null
                            ? await _textInsertion.InsertTextAsync(
                                new TextInsertionRequest(
                                    insertionText,
                                    _settings.Current.AutoPaste,
                                    context.WindowId,
                                    context.AppProcess,
                                    context.AppTitle,
                                    commandResult.AutoEnter,
                                    ResolveInsertionStrategy(context.AppProcess)
                                )
                            )
                            : await ExecuteActionPluginAsync(
                                actionPlugin,
                                context,
                                finalText,
                                rawText,
                                result?.DetectedLanguage,
                                cancelToken
                            );
            }
            catch (OperationCanceledException) when (cancelToken.IsCancellationRequested)
            {
                Trace.WriteLine(
                    $"[Dictation] Action canceled by user "
                    + $"(action='{actionPlugin?.ActionId ?? "<none>"}')."
                );
                ReportStatus(context, "Canceled");
                ShowFeedback(context, "Canceled", false, true);
                return;
            }
            catch (Exception ex)
            {
                // Insertion/action failures must NOT republish the dictation
                // as a transcription failure — TranscriptionCompletedEvent has
                // already fired. Surface a separate insertion-failure status.
                Trace.WriteLine(
                    $"[Dictation] Text insertion/action failed (target='{context.AppProcess}', "
                    + $"action='{actionPlugin?.ActionId ?? "<none>"}'): {ex}"
                );
                ReportStatus(context, $"Insertion failed: {ex.Message}");
                ShowFeedback(context, "Insertion failed.", true);
                return;
            }
            finally
            {
                _insertionOrder.Release(context.SessionId);
            }

            var completionMessage = insertion switch
            {
                InsertionResult.Pasted when commandResult.AutoEnter && finalText.Length == 0 =>
                    "Pressed Enter.",
                InsertionResult.Pasted or InsertionResult.Typed =>
                    $"Typed {finalText.Length} char(s).",
                InsertionResult.CopiedToClipboard => ClipboardFallbackMessage(),
                InsertionResult.ActionHandled => "Action completed.",
                InsertionResult.ActionFailed => "Action failed.",
                InsertionResult.MissingClipboardTool => ClipboardToolMissingMessage(),
                InsertionResult.MissingPasteTool =>
                    $"Text insertion failed. {_commands.GetSnapshot().PasteToolInstallHint}",
                InsertionResult.Failed =>
                    "Text insertion failed. Dictated text could not be copied or pasted.",
                InsertionResult.NoText when commandResult.CancelInsertion => "Dictation canceled.",
                _ => "Done."
            };
            var isError =
                insertion
                    is InsertionResult.Failed
                    or InsertionResult.ActionFailed
                    or InsertionResult.MissingClipboardTool
                    or InsertionResult.MissingPasteTool;
            var isCanceled =
                insertion is InsertionResult.NoText && commandResult.CancelInsertion;
            ReportStatus(context, completionMessage);
            ShowFeedback(context, completionMessage, isError, isCanceled);

            if (
                insertion
                is InsertionResult.Pasted
                or InsertionResult.Typed
                or InsertionResult.CopiedToClipboard
            )
            {
                _models.PluginManager.EventBus.Publish(
                    new TextInsertedEvent { Text = insertionText, AppName = context.AppTitle }
                );
            }

            if (ShouldArmTargetAppLearning(insertion, actionPlugin, insertionText))
            {
                // Fire-and-forget: arm a bounded tracking window on the field that just
                // received the text, so a follow-up type-over is learned silently. Mirrors
                // the memory-extraction hook below — never blocks the dictation path.
                // ReSharper disable once MethodSupportsCancellation -- background arm; not tied to the dictation token.
                FireAndLog(
                    () => _targetAppLearning.ArmAsync(insertionText),
                    "target-app correction learning"
                );
            }

            var transcriptionId = Guid.NewGuid().ToString();
            var timestamp =
                context.RecordingStart == default ? DateTime.UtcNow : context.RecordingStart;
            _recentTranscriptions.RecordTranscription(
                transcriptionId,
                finalText,
                timestamp,
                context.AppTitle,
                context.AppProcess
            );

            // Memory extraction is itself an LLM call. When provenance capture is
            // active, run it (awaited) before writing history so its request is
            // recorded on the entry; otherwise keep it fire-and-forget so the
            // common path isn't delayed by the extraction round-trip.
            if (_settings.Current.MemoryEnabled)
            {
                if (context.Capture is not null)
                {
                    try
                    {
                        // ReSharper disable once MethodSupportsCancellation -- best-effort memory extraction; awaited only to record its provenance before history, deliberately not tied to the recording's cancellation token (mirrors the fire-and-forget sibling below).
                        await _memory.ExtractAndStoreAsync(finalText, context.Capture);
                    }
                    catch (Exception ex)
                    {
                        // Extraction is best-effort; never let it fail the dictation.
                        Trace.WriteLine($"[Dictation] memory extraction failed: {ex.Message}");
                    }
                }
                else
                {
                    // ReSharper disable once MethodSupportsCancellation -- fire-and-forget background memory extraction; intentionally not tied to a cancellation token.
                    FireAndLog(() => _memory.ExtractAndStoreAsync(finalText), "memory extraction");
                }
            }

            // Write to history last so stats reflect the just-completed capture
            // (and any memory-extraction provenance recorded above).
            if (_settings.Current.SaveToHistoryEnabled)
            {
                AddHistoryRecord(
                    context,
                    transcriptionId,
                    timestamp,
                    rawText,
                    finalText,
                    duration,
                    result,
                    wavPath,
                    insertion,
                    pipelineResult,
                    cleanupLevel,
                    engineProviderId,
                    engineModelId
                );
            }
        }
        catch (OperationCanceledException) when (cancelToken.IsCancellationRequested)
        {
            // User pressed Escape while the post-processing pipeline (LLM
            // cleanup, translation, plugin processors) was running. Surface
            // "Canceled" rather than a transcription failure regardless of
            // whether TranscriptionCompletedEvent had fired.
            Trace.WriteLine("[Dictation] Pipeline canceled by user.");
            ReportStatus(context, "Canceled");
            ShowFeedback(context, "Canceled", false, true);
            if (!transcriptionCompletedPublished)
            {
                PublishSessionTerminal(context.SessionId, "canceled", "Canceled");
            }
        }
        catch (Exception ex) when (!transcriptionCompletedPublished)
        {
            // Failures before TranscriptionCompletedEvent fires (post-processing,
            // voice-command parsing) surface as transcription failures.
            Trace.WriteLine($"[Dictation] Post-transcription processing failed: {ex}");
            _models.PluginManager.EventBus.Publish(
                new TranscriptionFailedEvent
                {
                    ErrorMessage = ex.Message, ModelId = engineModelId, AppName = context.AppTitle
                }
            );
            ReportStatus(context, $"Transcription failed: {ex.Message}");
            _speechFeedback.AnnounceError(ex.Message);
            var feedbackText = ex is InvalidOperationException ? ex.Message : "Transcription failed.";
            ShowFeedback(context, feedbackText, true);
            PublishSessionTerminal(context.SessionId, "failed", ex.Message);
        }
        catch (Exception ex)
        {
            // Something after TranscriptionCompletedEvent threw (e.g. history
            // persistence). Don't republish a Failed event for an already-announced
            // dictation.
            Trace.WriteLine($"[Dictation] Post-completion bookkeeping failed: {ex}");
        }
    }

    private PromptAction? ResolvePromptAction(RecordingContext context)
    {
        return ResolveAutoPromptAction(context.Profile?.PromptActionId, _promptActions.EnabledActions);
    }

    private async Task<string> RunPromptActionAsync(
        RecordingContext context,
        PromptAction promptAction,
        string text,
        CancellationToken token
    )
    {
        try
        {
            var message = $"Running prompt action '{promptAction.Name}'...";
            Trace.WriteLine($"[Dictation] {message}");
            ReportStatus(context, message);

            var pump = new LlmStreamPump(accumulated =>
            {
                // Match ReportStatus(context,...)/ShowFeedback(context,...): a
                // newer session that has taken over the overlay must not have its
                // LlmResponseText clobbered by an older session's still-running
                // prompt action (audit §2 H3). The event still publishes
                // unconditionally for non-overlay observers.
                if (IsContextStillOwningOverlay(context))
                {
                    SetOverlayState(state => state with { LlmResponseText = accumulated });
                }

                _models.PluginManager.EventBus.Publish(
                    new LlmResponseTokenEvent
                    {
                        AccumulatedText = accumulated, StepName = PostProcessingStepNames.Llm
                    });
            });

            var streamed = await pump.RunAsync(
                _promptProcessing.ProcessStreamingAsync(promptAction, text, context.Capture, token),
                token);

            // Streaming→batch fallback: retry with the batch path when the pump
            // faulted OR yielded nothing (proxy EOF, empty 200). ReceivedAnyChunk
            // distinguishes a legitimately empty single-chunk result from a silent
            // empty stream — the single chunk is already a completed ProcessAsync call.
            // Pass the capture on the fallback: the batch retry is a distinct call whose
            // response is the text actually used, so it must be recorded too — otherwise
            // the saved provenance shows the faulted/empty streaming attempt while the
            // history FinalText is the batch response.
            var result = pump.Faulted || !pump.ReceivedAnyChunk
                ? await _promptProcessing.ProcessAsync(promptAction, text, context.Capture, token)
                : streamed;

            _models.PluginManager.EventBus.Publish(
                new LlmResponseTokenEvent
                {
                    AccumulatedText = result,
                    IsFinal = true,
                    Faulted = pump.Faulted,
                    StepName = PostProcessingStepNames.Llm
                });

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = $"Prompt action '{promptAction.Name}' failed: {ex.Message}";
            Trace.WriteLine($"[Dictation] {message}");
            ReportStatus(context, message);
            throw;
        }
    }

    // A weak model handed a non-generative command sometimes echoes an empty container instead of
    // refusing; never type that into the app.
    private static bool IsTrivialCommandResult(string result)
    {
        var trimmed = result.Trim();
        return trimmed is "{}" or "[]" or "\"\"" or "''" or "``" or "null";
    }

    private static string BuildCreateSystemPrompt()
    {
        return """
               You carry out a spoken instruction and produce text to insert at the user's cursor.
               The user's message is that instruction. Follow it directly and concisely, and produce
               ONLY the resulting text — no preamble, no surrounding quotes, no explanation, and no
               markdown code fences.
               """;
    }

    /// <summary>
    ///     Handles a keyphrase-prefixed spoken command: routes edit-vs-create by whether text is
    ///     selected, then transforms the selection or generates new text, streaming the result
    ///     straight onto the page. Escape aborts mid-flight via <see cref="_activeCommandCts" />.
    /// </summary>
    private async Task<SpokenCommandOutcome?> RunSpokenCommandAsync(
        string command,
        RecordingContext context,
        CancellationToken cancelToken
    )
    {
        // Recording's CTS is nulled and Escape disarmed by now, so give the command its own linked
        // source and re-arm Escape so a long stream/typing pass can be cancelled. Register in the
        // set so a session-loss discard cancels this command even if a newer one overwrites the
        // Escape-target field.
        using var commandCts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
        lock (_activeCommandCtsSet)
        {
            _activeCommandCtsSet.Add(commandCts);
        }

        _activeCommandCts = commandCts;
        _hotkey.IsCancelShortcutEnabled = true;
        var commandToken = commandCts.Token;

        try
        {
            // Reject a spoken command born after the session locked, WITHOUT synthesizing any
            // keystroke into the lock screen. Registering commandCts before this check makes it
            // atomic against a concurrent session-loss discard: a discard landing after
            // registration cancels commandCts (caught here), one landing before is caught by this
            // IsInputAllowed read. Inside the try so the ownership-aware finally (set removal +
            // conditional Escape disarm) still runs.
            if (!_sessionActivityMonitor.IsInputAllowed || commandToken.IsCancellationRequested)
            {
                Trace.WriteLine("[Dictation] Spoken command suppressed: session locked or inactive.");
                PublishSessionTerminal(context.SessionId, "canceled", "Canceled");
                return null;
            }

            if (!_promptProcessing.IsAnyProviderAvailable)
            {
                var noProvider = Localization.Loc.Instance["Command.NoProvider"];
                ReportStatus(context, noProvider);
                ShowFeedback(context, noProvider, true);
                PublishSessionTerminal(context.SessionId, "failed", noProvider);
                return null;
            }

            ReportStatus(context, Localization.Loc.Instance["Command.Thinking"]);

            // Match a saved transform action by name first — pure, no side effects, safe before any
            // clipboard probe. A matched action IS an in-place transform, so it needs a selection to
            // operate on just as an explicit "fix this" would — UNLESS the command opens with a
            // creation verb ("write an email to Bob" incidentally shares words with a "Write Email"
            // prompt). That's a from-scratch request, so let it create rather than forcing a selection
            // probe and failing with "Nothing highlighted". An explicit selection cue still wins.
            var matchedAction = SpokenCommandActionMatcher.Match(command, CommandTransformActions());
            var wantsSelection =
                (matchedAction is not null && !SpokenCommandIntent.OpensWithCreationVerb(command))
                || SpokenCommandIntent.RefersToSelection(command);

            PromptAction action;
            string input;
            bool wrapInput;
            if (wantsSelection)
            {
                // Only an edit/transform needs the selection. Capturing it synthesizes a copy, so
                // probe here and nowhere else — a pure create command must never fire a copy keystroke
                // at the focused app. Terminals map plain Ctrl+C to SIGINT, so the probe uses
                // Ctrl+Shift+C there (and still can't read a TUI editor's internal selection).
                var targetIsTerminal = TextInsertionService.IsTerminalApp(context.AppProcess);
                var selectedText = await _textInsertion.CaptureSelectedTextAsync(targetIsTerminal);
                if (string.IsNullOrWhiteSpace(selectedText))
                {
                    // Transform intent with nothing selected has nowhere to land — hint and stop. A
                    // matched saved action lands here too: with no selection it has nothing to work on.
                    // Terminal TUI editors (Neovim, less, …) keep the selection internal, so the copy
                    // probe legitimately comes back empty — say that instead of "Nothing highlighted".
                    var nothing = targetIsTerminal
                        ? Localization.Loc.Instance["Command.NoTerminalSelection"]
                        : Localization.Loc.Instance["Command.NothingHighlighted"];
                    ReportStatus(context, nothing);
                    ShowFeedback(context, nothing, false);
                    PublishSessionTerminal(context.SessionId, "discarded", nothing);
                    return null;
                }

                // Matched saved action, else an ad-hoc transform of the selection. Edit wraps the
                // selection as untrusted data.
                action = matchedAction
                         ?? BuildTransientCommandAction(
                             "spoken-command-edit",
                             TransformSelectionService.BuildTransformPrompt(selectedText, command)
                         );
                input = selectedText;
                wrapInput = true;
            }
            else
            {
                // Create sends the command unwrapped as an instruction and never touches the
                // clipboard, so it fires no Ctrl+C (SIGINT) at the focused app.
                action = BuildTransientCommandAction(
                    "spoken-command-create",
                    BuildCreateSystemPrompt()
                );
                input = command;
                wrapInput = false;
            }

            Trace.WriteLine(
                $"[Command] routing wantsSelection={wantsSelection} "
                + $"matchedSaved={matchedAction is not null} action={action.Id}"
            );

            var applyingStatus = matchedAction is not null
                ? Localization.Loc.Instance.GetString("Command.ApplyingPrompt", matchedAction.Name)
                : Localization.Loc.Instance["Command.ApplyingOneOff"];
            ReportStatus(context, applyingStatus);

            // Streaming types each chunk directly, so it may only run where that matches what the
            // one-shot insert would do: auto-paste on, a non-terminal target, and either the user
            // forced DirectTyping or an Auto target the app policy already types into (browsers,
            // Codex). A terminal result must be generated in one pass because a later stream chunk
            // can introduce a newline; the one-shot insertion can then safely paste multiline text
            // with Ctrl+Shift+V while preserving direct typing for a single-line result. Everything
            // else (copy-only, or an Auto GUI/unknown target the one-shot would paste) also routes
            // through that one-shot insert.
            var strategy = ResolveInsertionStrategy(context.AppProcess);
            var canStreamDirectly = _settings.Current.AutoPaste
                && !TextInsertionService.IsTerminalApp(context.AppProcess)
                && (strategy is TextInsertionStrategy.DirectTyping
                    || (strategy is TextInsertionStrategy.Auto
                        && TextInsertionService.AppPrefersDirectTyping(context.AppProcess, context.AppTitle)));

            if (!canStreamDirectly)
            {
                var oneShot = await _promptProcessing
                    .ProcessAsync(action, input, context.Capture, commandToken, wrapInput)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(oneShot) || IsTrivialCommandResult(oneShot))
                {
                    var empty = Localization.Loc.Instance["Command.NoResult"];
                    ReportStatus(context, empty);
                    ShowFeedback(context, empty, true);
                    PublishSessionTerminal(context.SessionId, "failed", empty);
                    return null;
                }

                var oneShotInsertion = await CompleteViaOneShotInsertionAsync(context, oneShot);
                return SpokenCommandOutcomeFor(input, oneShot, oneShotInsertion);
            }

            // Keep the named StreamCommandResult: `stream.Text/TypingFailed/TypedAnything` read
            // clearer than three loose locals and let the state test below use a property pattern.
            // ReSharper disable once UseDeconstruction
            var stream = await StreamCommandOntoPageAsync(
                action,
                input,
                wrapInput,
                context.Capture,
                context.AppProcess,
                async () =>
                {
                    SetOverlayState(state =>
                        state with
                        {
                            IsOverlayVisible = false,
                            ShowFeedback = false,
                            FeedbackText = null,
                            LlmResponseText = null,
                            PartialText = null
                        }
                    );
                    // Re-activate the window the command was issued from before typing the first
                    // chunk: a focus change during the LLM round-trip would otherwise send output
                    // into whatever app is now active. Mirrors the one-shot insert's focus step and
                    // covers the pre-type yield too (it delays as well). A false result means focus
                    // couldn't be confirmed, so streaming aborts to the safe one-shot fallback.
                    return await _textInsertion.FocusWindowAsync(context.WindowId).ConfigureAwait(false);
                },
                commandToken
            );

            var result = stream.Text;
            if (string.IsNullOrWhiteSpace(result) || IsTrivialCommandResult(result))
            {
                var empty = Localization.Loc.Instance["Command.NoResult"];
                ReportStatus(context, empty);
                ShowFeedback(context, empty, true);
                PublishSessionTerminal(context.SessionId, "failed", empty);
                return null;
            }

            if (stream.Faulted)
            {
                // The provider stream broke mid-way and any output already on the page is truncated.
                // Report failure instead of a false success and don't publish a ready result.
                var failed = Localization.Loc.Instance["Command.Failed"];
                ReportStatus(context, failed);
                ShowFeedback(context, failed, true);
                PublishSessionTerminal(context.SessionId, "failed", failed);
                return null;
            }

            // Sequential early-return guards read clearer here than a switch on stream.
            // ReSharper disable once ConvertIfStatementToSwitchStatement
            if (stream is { TypingFailed: true, TypedAnything: false })
            {
                // No chunk ever landed (e.g. no working injection backend): fall back to a single
                // paste/insert of the whole result and report that outcome instead of a false success.
                var fallbackInsertion = await CompleteViaOneShotInsertionAsync(context, result);
                return SpokenCommandOutcomeFor(input, result, fallbackInsertion);
            }

            if (stream.TypingFailed)
            {
                // Some text landed, then typing broke mid-stream. A one-shot re-insert would
                // duplicate what's already on the page, so surface the failure rather than retry.
                var failed = Localization.Loc.Instance["Command.Failed"];
                ReportStatus(context, failed);
                ShowFeedback(context, failed, true);
                PublishSessionTerminal(context.SessionId, "failed", failed);
                return null;
            }

            var done = Localization.Loc.Instance["Command.Done"];
            ReportStatus(context, done);
            ShowFeedback(context, done, false);

            // Terminal session record so a polling API client resolves instead of looping.
            PublishSessionResult(
                new DictationSessionResult(
                    context.SessionId,
                    "ready",
                    result,
                    null,
                    null,
                    0,
                    null,
                    null,
                    done
                )
            );
            _models.PluginManager.EventBus.Publish(
                new TextInsertedEvent { Text = result, AppName = context.AppTitle }
            );

            // Streamed directly onto the page, chunk by chunk — a genuine typed insertion.
            return new SpokenCommandOutcome(input, result, TextInsertionStatus.Typed);
        }
        catch (OperationCanceledException) when (commandToken.IsCancellationRequested)
        {
            Trace.WriteLine("[Command] Spoken command canceled by user.");
            ReportStatus(context, "Canceled");
            ShowFeedback(context, "Canceled", false, true);
            PublishSessionTerminal(context.SessionId, "canceled", "Canceled");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Command] Spoken command failed: {ex}");
            ReportStatus(context, $"Command failed: {ex.Message}");
            ShowFeedback(context, Localization.Loc.Instance["Command.Failed"], true);
            PublishSessionTerminal(context.SessionId, "failed", ex.Message);
        }
        finally
        {
            lock (_activeCommandCtsSet)
            {
                _activeCommandCtsSet.Remove(commandCts);
            }

            if (ReferenceEquals(_activeCommandCts, commandCts))
            {
                _activeCommandCts = null;
            }

            // Don't disarm Escape if a new recording — or a newer overlapping spoken command (its
            // CTS is still set above) — has taken over the shortcut meanwhile.
            if (_activeCommandCts is null && _activeDictationCts is null && !_audio.IsRecording)
            {
                _hotkey.IsCancelShortcutEnabled = false;
            }
        }

        // Reached only via a catch (cancel/fail) — no savable result was produced.
        return null;
    }

    // Result of a completed spoken command, for the history entry. SourceText is what the LLM
    // operated on — the selected text for an edit/transform, or the command itself for a create —
    // so it maps to the entry's RawText and the raw→final diff reads "source → result". Result is
    // the generated/transformed text that was produced. InsertionStatus is how that result actually
    // reached the page (typed/pasted/copied), so history records the real label instead of assuming
    // "Typed".
    private sealed record SpokenCommandOutcome(
        string SourceText,
        string Result,
        TextInsertionStatus InsertionStatus
    );

    // Maps a one-shot / fallback insertion to a savable outcome. Only the states where the text
    // actually reached the user (typed, pasted, or copied to the clipboard) produce a record; a
    // failed insert (missing tool, Failed, …) returns null so a command that never landed isn't
    // persisted as a success.
    private static SpokenCommandOutcome? SpokenCommandOutcomeFor(
        string source,
        string result,
        InsertionResult insertion
    )
    {
        return insertion is InsertionResult.Pasted
            or InsertionResult.Typed
            or InsertionResult.CopiedToClipboard
            ? new SpokenCommandOutcome(source, result, ToTextInsertionStatus(insertion))
            : null;
    }

    // Outcome of streaming a spoken-command result onto the page. Text is the full accumulated LLM
    // output (useful even when typing failed); TypingFailed marks a chunk-injection failure and
    // TypedAnything whether any chunk landed before that failure; Faulted marks the provider stream
    // breaking mid-way after partial output already landed, so the accumulated text is incomplete.
    private sealed record StreamCommandResult(
        string Text,
        bool TypingFailed,
        bool TypedAnything,
        bool Faulted
    );

    // Types each delta of a spoken-command result onto the page as it streams. Buffers into modest
    // chunks to limit ydotool invocations; onFirstType runs once, just before the first characters,
    // and returns whether the target window is focused — a false result aborts typing so the caller
    // re-inserts via the focus-and-fallback one-shot path instead of typing into the wrong window.
    private async Task<StreamCommandResult> StreamCommandOntoPageAsync(
        PromptAction action,
        string input,
        bool wrapInput,
        LlmCallCapture? capture,
        string? targetProcessName,
        Func<Task<bool>> onFirstType,
        CancellationToken token
    )
    {
        var accumulated = new StringBuilder();
        var buffer = new StringBuilder();
        var firstTypeDone = false;
        var typingFailed = false;
        var typedAnything = false;
        var streamFaulted = false;

        try
        {
            await foreach (var delta in _promptProcessing
                               .ProcessStreamingAsync(action, input, capture, token, wrapInput))
            {
                if (string.IsNullOrEmpty(delta))
                {
                    continue;
                }

                accumulated.Append(delta);
                buffer.Append(delta);
                // Keep accumulating the full result after a typing failure (the caller reuses it for
                // a one-shot fallback), but stop flushing chunks that can no longer land.
                if (buffer.Length >= 20 && !typingFailed)
                {
                    await FlushAsync();
                }
            }

            // Skip the final flush when nothing has been typed yet and the whole result is empty or a
            // trivial container ("{}"/"null") — the caller reports NoResult, so typing it is worse
            // than nothing.
            if (!typingFailed && !(!firstTypeDone && IsWhitespaceOrTrivialResult(accumulated.ToString())))
            {
                await FlushAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // User hit Escape: drop the buffered tail rather than typing it, and never trigger
            // onFirstType post-cancel (it hides the overlay and refocuses the target window).
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Command] Streaming insertion faulted: {ex.Message}");

            // Key recovery off whether anything was actually TYPED, not merely accumulated: a short
            // first delta can still be sitting unflushed in the buffer, so the page is clean and a
            // full batch retry recovers the whole result. Flushing that buffer first would type a
            // truncated prefix the retry can't cleanly replace. Once a chunk has landed, the visible
            // text is a truncated prefix a retry would duplicate, so just mark the stream faulted.
            if (typedAnything || !await TryBatchFallbackAsync())
            {
                streamFaulted = true;
            }
        }

        // Streaming completed but yielded nothing (proxy EOF / empty 200) — the batch endpoint may
        // still return the result, mirroring the prompt-action path's !ReceivedAnyChunk fallback. An
        // empty batch here is a genuine NoResult, not a fault, so don't mark the stream faulted.
        if (!typedAnything && !streamFaulted && accumulated.Length == 0)
        {
            await TryBatchFallbackAsync();
        }

        return new StreamCommandResult(accumulated.ToString(), typingFailed, typedAnything, streamFaulted);

        async Task TypeAsync(string text)
        {
            // Once injection has failed, stop typing but let the stream keep accumulating.
            if (typingFailed)
            {
                return;
            }

            if (!firstTypeDone)
            {
                firstTypeDone = true;
                if (!await onFirstType().ConfigureAwait(false))
                {
                    // Target window couldn't be confirmed focused; typing now would land in the wrong
                    // app. Bail so the caller re-inserts via the focus-and-fallback one-shot path.
                    typingFailed = true;
                    return;
                }
            }

            if (
                await _textInsertion
                    .TypeStreamChunkAsync(text, targetProcessName)
                    .ConfigureAwait(false)
            )
            {
                typedAnything = true;
            }
            else
            {
                typingFailed = true;
            }
        }

        async Task FlushAsync()
        {
            if (buffer.Length == 0)
            {
                return;
            }

            await TypeAsync(buffer.ToString());
            buffer.Clear();
        }

        // Runs the non-streaming endpoint and types its result. Returns whether it produced anything;
        // callers replace the (empty or truncated) accumulated text with the batch result.
        async Task<bool> TryBatchFallbackAsync()
        {
            // Pass the capture: this batch retry is a distinct LLM call whose response is
            // what actually reaches the page and is saved as FinalText, so it must be
            // recorded too. On this degraded path the Inspect panel then shows both the
            // faulted/empty streaming attempt and the batch retry that produced the result,
            // instead of a history entry whose recorded response doesn't match the inserted
            // text (mirrors the prompt-action streaming→batch fallback).
            var batch = await _promptProcessing.ProcessAsync(action, input, capture, token, wrapInput)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(batch))
            {
                return false;
            }

            accumulated.Clear();
            accumulated.Append(batch);
            // Same guard as the streaming path: never type an empty/trivial container.
            if (!IsWhitespaceOrTrivialResult(batch))
            {
                await TypeAsync(batch);
            }

            return true;
        }
    }

    private static bool IsWhitespaceOrTrivialResult(string text)
    {
        return string.IsNullOrWhiteSpace(text) || IsTrivialCommandResult(text);
    }

    // Fallback when streaming typing never landed a single chunk (e.g. no injection backend):
    // insert the whole result in one shot and map the outcome to the same completion messaging the
    // normal insertion path uses, so a genuine failure is never reported as success.
    private async Task<InsertionResult> CompleteViaOneShotInsertionAsync(
        RecordingContext context,
        string result
    )
    {
        var insertion = await _textInsertion.InsertTextAsync(
            new TextInsertionRequest(
                result,
                _settings.Current.AutoPaste,
                context.WindowId,
                context.AppProcess,
                context.AppTitle,
                false,
                ResolveInsertionStrategy(context.AppProcess)
            )
        );

        var completionMessage = insertion switch
        {
            InsertionResult.Pasted or InsertionResult.Typed =>
                Localization.Loc.Instance["Command.Done"],
            InsertionResult.CopiedToClipboard => ClipboardFallbackMessage(),
            InsertionResult.MissingClipboardTool => ClipboardToolMissingMessage(),
            InsertionResult.MissingPasteTool =>
                $"Text insertion failed. {_commands.GetSnapshot().PasteToolInstallHint}",
            _ => "Text insertion failed. Command result could not be inserted."
        };
        var isError =
            insertion
                is not InsertionResult.Pasted
                and not InsertionResult.Typed
                and not InsertionResult.CopiedToClipboard;
        ReportStatus(context, completionMessage);
        ShowFeedback(context, completionMessage, isError);

        // Terminal session record so a polling API client resolves instead of looping.
        PublishSessionResult(
            new DictationSessionResult(
                context.SessionId,
                isError ? "failed" : "ready",
                isError ? string.Empty : result,
                null,
                null,
                0,
                null,
                null,
                completionMessage
            )
        );

        if (
            insertion
            is InsertionResult.Pasted
            or InsertionResult.Typed
            or InsertionResult.CopiedToClipboard
        )
        {
            _models.PluginManager.EventBus.Publish(
                new TextInsertedEvent { Text = result, AppName = context.AppTitle }
            );
        }

        return insertion;
    }

    // Saved prompt actions eligible for name matching. Action-plugin-backed actions are excluded —
    // they route output to a plugin, not the in-place transform this flow performs.
    private List<PromptAction> CommandTransformActions()
    {
        return _promptActions
            .EnabledActions.Where(candidate =>
                string.IsNullOrWhiteSpace(candidate.TargetActionPluginId)
            )
            .ToList();
    }

    // Transient prompt action for an ad-hoc spoken command, carrying the spoken-command model
    // override (null falls through to the global default).
    private PromptAction BuildTransientCommandAction(string id, string systemPrompt)
    {
        return new PromptAction
        {
            Id = id,
            Name = "Spoken command",
            SystemPrompt = systemPrompt,
            ProviderOverride = _settings.Current.SpokenCommandLlmProvider
        };
    }

    private CleanupLevel ResolveCleanupLevel(RecordingContext context, PromptAction? promptAction)
    {
        if (context.Profile is null)
        {
            return _settings.Current.CleanupLevel;
        }

        var style = ProfileStylePresetService.Resolve(context.Profile.StylePreset);
        var cleanupLevel = context.Profile.CleanupLevelOverride ?? style.CleanupLevel;

        // Profile prompt actions are LLM transforms; don't run a separate cleanup
        // pass first — the action should receive the raw dictated text.
        return promptAction is not null && cleanupLevel > CleanupLevel.Light
            ? CleanupLevel.Light
            : cleanupLevel;
    }

    private string ApplyProfileStyleFormatting(RecordingContext context, string text)
    {
        if (context.Profile is null)
        {
            return text;
        }

        var style = ProfileStylePresetService.Resolve(context.Profile.StylePreset);
        var developerFormattingEnabled =
            context.Profile.DeveloperFormattingOverride ?? style.DeveloperFormattingEnabled;
        if (!developerFormattingEnabled)
        {
            return text;
        }

        var fileReference = _ideFileReferences.TryFormatReferenceCommand(text);
        return fileReference ?? DeveloperFormattingService.Format(text);
    }

    private TextInsertionStrategy ResolveInsertionStrategy(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return TextInsertionStrategy.Auto;
        }

        var strategies = _settings.Current.AppInsertionStrategies;
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- AppInsertionStrategies is JSON-deserialized and can be null when omitted; the guard is defensive.
        if (strategies is null || strategies.Count == 0)
        {
            return TextInsertionStrategy.Auto;
        }

        var process = ProcessNameNormalizer.Normalize(processName);
        // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator -- explicit loop with dual-key (raw + normalized) matching is clearer than a LINQ rewrite.
        foreach (var entry in strategies)
        {
            if (
                string.Equals(entry.Key, processName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Key, process, StringComparison.OrdinalIgnoreCase)
            )
            {
                return entry.Value;
            }
        }

        return TextInsertionStrategy.Auto;
    }

    private static string ClipboardToolMissingMessage()
    {
        return Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 }
            ? "Text insertion failed. Install wl-clipboard to enable clipboard insertion."
            : "Text insertion failed. Install xclip to enable clipboard insertion.";
    }

    /// <summary>
    ///     Reason-aware fallback notification for the
    ///     <see cref="InsertionResult.CopiedToClipboard" /> branch. The detail
    ///     comes from <see cref="TextInsertionService.LastFailureReason" />,
    ///     which the service sets on the same call that produced this result —
    ///     so we can guide the user to the actual setup gap (e.g. ydotool not
    ///     running) instead of the generic "paste with Ctrl+V" line.
    /// </summary>
    private string ClipboardFallbackMessage()
    {
        return _textInsertion.LastFailureReason switch
        {
            InsertionFailureReason.WtypeCompositorUnsupported =>
                "Copied to clipboard. Compositor doesn't support direct typing — set up ydotool from Settings → Text insertion to enable auto-paste.",
            InsertionFailureReason.YdotoolSocketUnreachable =>
                "Copied to clipboard. ydotool socket not reachable — open Settings → Text insertion to check daemon status.",
            InsertionFailureReason.NoWaylandTypingTool =>
                $"Copied to clipboard. {_commands.GetSnapshot().PasteToolInstallHint}",
            InsertionFailureReason.FocusFailed =>
                "Copied to clipboard. Target window could not be focused for auto-paste — paste with Ctrl+V.",
            _ => "Copied to clipboard (paste with Ctrl+V)."
        };
    }

    private IActionPlugin? ResolveActionPlugin(PromptAction? promptAction)
    {
        if (string.IsNullOrWhiteSpace(promptAction?.TargetActionPluginId))
        {
            return null;
        }

        return _models.PluginManager.ActionPlugins.FirstOrDefault(plugin =>
            string.Equals(
                plugin.PluginId,
                promptAction.TargetActionPluginId,
                StringComparison.OrdinalIgnoreCase
            )
            || string.Equals(
                plugin.ActionId,
                promptAction.TargetActionPluginId,
                StringComparison.OrdinalIgnoreCase
            )
        );
    }

    private async Task<InsertionResult> ExecuteActionPluginAsync(
        IActionPlugin actionPlugin,
        RecordingContext context,
        string inputText,
        string rawText,
        string? detectedLanguage,
        CancellationToken cancelToken
    )
    {
        var result = await actionPlugin.ExecuteAsync(
            inputText,
            new ActionContext(
                context.AppTitle,
                context.AppProcess,
                context.AppUrl,
                detectedLanguage,
                rawText
            ),
            cancelToken
        );

        _models.PluginManager.EventBus.Publish(
            new ActionCompletedEvent
            {
                ActionId = actionPlugin.ActionId,
                Success = result.Success,
                Message = result.Message,
                AppName = context.AppTitle
            }
        );

        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            ReportStatus(context, result.Message);
        }

        return result.Success ? InsertionResult.ActionHandled : InsertionResult.ActionFailed;
    }

    /// <summary>
    ///     Gate for arming silent target-app correction learning: the feature is enabled,
    ///     the text went into the field directly (typed or pasted — not a clipboard
    ///     fallback), it was plain dictation output (no action plugin), and it is short
    ///     enough to be a normal edit rather than a document dump. AT-SPI availability is
    ///     checked inside <see cref="TargetAppCorrectionLearningService.ArmAsync" />.
    /// </summary>
    private bool ShouldArmTargetAppLearning(
        InsertionResult insertion,
        IActionPlugin? actionPlugin,
        string insertionText
    )
    {
        return TargetAppCorrectionLearningService.ShouldArm(
            _settings.Current.TargetAppCorrectionLearningEnabled,
            insertion,
            actionPlugin is not null,
            insertionText.Length
        );
    }

    private static void FireAndLog(Func<Task> start, string label)
    {
        Task task;
        try
        {
            task = start();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Dictation] {label} threw synchronously: {ex.Message}");
            return;
        }

        task.ContinueWith(
            t =>
                Trace.WriteLine(
                    $"[Dictation] {label} faulted: {t.Exception?.GetBaseException().Message}"
                ),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private string BuildRecordingStartFailureMessage(Exception? ex)
    {
        var selectedDevice = ResolveSelectedInputDeviceForMessage();
        if (selectedDevice is null)
        {
            return Localization.Loc.Instance["Overlay.RecordStartFailedNoDevice"];
        }

        var baseMessage = Localization.Loc.Instance.GetString(
            "Overlay.RecordStartFailedDevice",
            selectedDevice.Name
        );
        var detail = ex?.Message;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            baseMessage += $" {detail}.";
        }

        var suffix = IsBluetoothDeviceName(selectedDevice.Name)
            ? Localization.Loc.Instance["Overlay.RecordStartFailedBluetooth"]
            : Localization.Loc.Instance["Overlay.RecordStartFailedGeneric"];
        return $"{baseMessage} {suffix}";
    }

    private AudioInputDevice? ResolveSelectedInputDeviceForMessage()
    {
        try
        {
            return _audio.ResolveConfiguredDevice(
                _settings.Current.SelectedMicrophoneDevice,
                _settings.Current.SelectedMicrophoneDeviceId
            );
        }
        catch
        {
            return null;
        }
    }

    private static bool IsBluetoothDeviceName(string name)
    {
        return name.Contains("airpod", StringComparison.OrdinalIgnoreCase)
               || name.Contains("bluetooth", StringComparison.OrdinalIgnoreCase)
               || name.Contains("bluez", StringComparison.OrdinalIgnoreCase)
               || name.Contains("headset", StringComparison.OrdinalIgnoreCase);
    }

    // Builds a TranscriptionRecord pre-filled with the fields shared by both history paths
    // (dictation and spoken command). Callers supply the path-specific values — InsertionStatus,
    // pipeline flags, IsSpokenCommand, LlmCalls, … — via a `with` expression on the result.
    private TranscriptionRecord BuildHistoryRecord(
        RecordingContext context,
        string id,
        DateTime timestamp,
        string rawText,
        string finalText,
        double duration,
        PluginTranscriptionResult? result,
        string wavPath,
        string engineUsed,
        string? modelUsed
    )
    {
        var engine = string.IsNullOrEmpty(engineUsed) ? "unknown" : engineUsed;
        var language =
            result?.DetectedLanguage
            ?? (_settings.Current.Language is { Length: > 0 } l && l != "auto" ? l : null);

        return new TranscriptionRecord
        {
            Id = id,
            Timestamp = timestamp,
            RawText = rawText,
            FinalText = finalText,
            AppName = context.AppTitle,
            AppProcessName = context.AppProcess,
            AppUrl = context.AppUrl,
            DurationSeconds = duration,
            Language = language,
            ProfileName = context.Profile?.Name,
            EngineUsed = engine,
            ModelUsed = modelUsed,
            AudioFileName = Path.GetFileName(wavPath)
        };
    }

    // Writes a history entry for a completed spoken command. RawText is the source
    // text the command acted on (selected text for an edit, the command itself for a
    // create); FinalText is the generated/transformed text that was produced, so the
    // raw→final diff reads "source → result". InsertionStatus is the real result of the
    // insert (typed/pasted/copied). LlmCalls carries the command's request/response
    // (context.Capture) so the Inspect panel shows it exactly like a dictation's prompt action.
    private void AddSpokenCommandHistoryRecord(
        RecordingContext context,
        string rawText,
        string finalText,
        double duration,
        PluginTranscriptionResult? result,
        string wavPath,
        string engineUsed,
        string? modelUsed,
        TextInsertionStatus insertionStatus
    )
    {
        try
        {
            var timestamp =
                context.RecordingStart == default ? DateTime.UtcNow : context.RecordingStart;

            _history.AddRecord(
                BuildHistoryRecord(
                    context,
                    Guid.NewGuid().ToString(),
                    timestamp,
                    rawText,
                    finalText,
                    duration,
                    result,
                    wavPath,
                    engineUsed,
                    modelUsed
                ) with
                {
                    InsertionStatus = insertionStatus,
                    CleanupLevelUsed = CleanupLevel.None,
                    PromptActionApplied = true,
                    IsSpokenCommand = true,
                    LlmCalls = context.Capture?.Calls ?? []
                }
            );
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Command] AddSpokenCommandHistoryRecord failed: {ex.Message}");
        }
    }

    private void AddHistoryRecord(
        RecordingContext context,
        string id,
        DateTime timestamp,
        string rawText,
        string finalText,
        double duration,
        PluginTranscriptionResult? result,
        string wavPath,
        InsertionResult insertion,
        PostProcessingResult pipelineResult,
        CleanupLevel cleanupLevel,
        string engineUsed,
        string? modelUsed
    )
    {
        try
        {
            _history.AddRecord(
                BuildHistoryRecord(
                    context,
                    id,
                    timestamp,
                    rawText,
                    finalText,
                    duration,
                    result,
                    wavPath,
                    engineUsed,
                    modelUsed
                ) with
                {
                    InsertionStatus = ToTextInsertionStatus(insertion),
                    InsertionFailureReason = InsertionFailureReasonFor(insertion),
                    CleanupLevelUsed = cleanupLevel,
                    CleanupApplied = WasPipelineStepChanged(
                        pipelineResult,
                        PostProcessingStepNames.Cleanup
                    ),
                    SnippetApplied = WasPipelineStepChanged(
                        pipelineResult,
                        PostProcessingStepNames.Snippets
                    ),
                    DictionaryCorrectionApplied = WasPipelineStepChanged(
                        pipelineResult,
                        PostProcessingStepNames.Dictionary
                    ),
                    PromptActionApplied = WasPipelineStepSucceeded(
                        pipelineResult,
                        PostProcessingStepNames.Llm
                    ),
                    TranslationApplied = WasPipelineStepChanged(
                        pipelineResult,
                        PostProcessingStepNames.Translation
                    ),
                    LlmCalls = context.Capture?.Calls ?? []
                }
            );
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Dictation] AddHistoryRecord failed: {ex.Message}");
        }
    }

    private static TextInsertionStatus ToTextInsertionStatus(InsertionResult insertion)
    {
        return insertion switch
        {
            InsertionResult.Pasted => TextInsertionStatus.Pasted,
            InsertionResult.Typed => TextInsertionStatus.Typed,
            InsertionResult.CopiedToClipboard => TextInsertionStatus.CopiedToClipboard,
            InsertionResult.NoText => TextInsertionStatus.NoText,
            InsertionResult.ActionHandled => TextInsertionStatus.ActionHandled,
            InsertionResult.ActionFailed => TextInsertionStatus.ActionFailed,
            InsertionResult.MissingClipboardTool => TextInsertionStatus.MissingClipboardTool,
            InsertionResult.MissingPasteTool => TextInsertionStatus.MissingPasteTool,
            InsertionResult.Failed => TextInsertionStatus.Failed,
            _ => TextInsertionStatus.Unknown
        };
    }

    private static bool WasPipelineStepChanged(PostProcessingResult result, string name)
    {
        return result.Steps.Any(step =>
            step.Changed && string.Equals(step.Name, name, StringComparison.OrdinalIgnoreCase)
        );
    }

    private static bool WasPipelineStepSucceeded(PostProcessingResult result, string name)
    {
        return result.Steps.Any(step =>
            step.Succeeded && string.Equals(step.Name, name, StringComparison.OrdinalIgnoreCase)
        );
    }

    private static string? InsertionFailureReasonFor(InsertionResult insertion)
    {
        return insertion switch
        {
            InsertionResult.ActionFailed => "Action plugin failed.",
            InsertionResult.MissingClipboardTool => ClipboardToolMissingMessage(),
            InsertionResult.MissingPasteTool => "Automatic paste tool is unavailable.",
            InsertionResult.Failed => "Text insertion failed.",
            _ => null
        };
    }

    private void ReportStatus(string message)
    {
        StatusMessage?.Invoke(this, message);
        SetOverlayState(state =>
            state with { IsOverlayVisible = true, StatusText = message, ShowFeedback = false, FeedbackText = null }
        );
    }

    // Wait a short beat before firing the synthesized paste/type so the
    // compositor has time to settle any in-flight focus state. We
    // deliberately do NOT mutate the overlay here — an earlier version
    // hid the overlay via SetOverlayState, which flipped
    // HasVisibleContent off and triggered Avalonia's Window.Hide().
    // On Wayland with ShowActivated=False / Topmost=True, the matching
    // Show() that fires on the next StartAsync can fail to re-display
    // the window — the overlay "disappears" for every dictation after
    // the first, even though dictation itself keeps working. The
    // overlay is already configured to not grab keyboard focus, so
    // there's no need to hide it for the paste to land correctly.
    private static Task YieldFocusForInsertionAsync()
    {
        return Task.Delay(90);
    }

    private static bool IsSameWindow(ActiveWindowSnapshot a, ActiveWindowSnapshot b)
    {
        if (a.WindowId is not null && b.WindowId is not null)
        {
            return string.Equals(a.WindowId, b.WindowId, StringComparison.Ordinal);
        }

        return string.Equals(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(a.AppId, b.AppId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     <see cref="ReportStatus(string)" /> variant that suppresses overlay/status updates
    ///     once a newer dictation has taken over the overlay. The
    ///     <see cref="StatusMessage" /> event still fires for observers that care
    ///     about completion (history/log surfaces), but the visible overlay is left
    ///     alone so the live recording's "Recording…" status is not clobbered.
    /// </summary>
    private void ReportStatus(RecordingContext context, string message)
    {
        StatusMessage?.Invoke(this, message);
        if (!IsContextStillOwningOverlay(context))
        {
            return;
        }

        SetOverlayState(state =>
            state with { IsOverlayVisible = true, StatusText = message, ShowFeedback = false, FeedbackText = null }
        );
    }

    private void ShowFeedback(string text, bool isError, bool isCanceled = false)
    {
        SetOverlayState(state =>
            state with
            {
                IsOverlayVisible = false,
                ShowFeedback = true,
                FeedbackIsError = isError,
                FeedbackText = text,
                PartialText = null,
                LlmResponseText = null,
                IsRecording = false,
                ActiveProfileName = null,
                ActiveAppName = null,
                SessionStartedAtUtc = null
            }
        );

        // Terminal-outcome cue, matching the Windows build's success/error
        // sounds. ShowFeedback is the single chokepoint every terminal toast
        // flows through, so classify off the caller's intent. Cancellation
        // surfaces with isError=false but is neither success nor failure —
        // callers flag it via isCanceled rather than us sniffing the text
        // (which varies: "Canceled", "Dictation canceled.", …).
        if (!_settings.Current.SoundFeedbackEnabled)
        {
            return;
        }

        if (isError)
        {
            _soundFeedback.PlayError();
        }
        else if (!isCanceled)
        {
            _soundFeedback.PlaySuccess();
        }
    }

    /// <summary>
    ///     <see cref="ShowFeedback(string, bool, bool)" /> variant that no-ops once a newer dictation has
    ///     taken over the overlay. Prevents the previous recording's terminal
    ///     feedback ("Typed N char(s)", "Transcription failed", "Canceled") from
    ///     hiding the new recording's overlay.
    /// </summary>
    private void ShowFeedback(
        RecordingContext context,
        string text,
        bool isError,
        bool isCanceled = false
    )
    {
        if (!IsContextStillOwningOverlay(context))
        {
            return;
        }

        ShowFeedback(text, isError, isCanceled);
    }

    /// <summary>
    ///     True if no newer dictation has started since the context was captured.
    ///     StopAsync increments <c>_recordingSession</c> exactly once at the
    ///     transition out of recording, so the just-stopped context is still
    ///     "current" while <c>_recordingSession == context.SessionId + 1</c>. Any
    ///     higher value means a subsequent StartAsync has claimed the overlay.
    /// </summary>
    private bool IsContextStillOwningOverlay(RecordingContext context)
    {
        int current;
        lock (_recordingSessionLock)
        {
            current = _recordingSession;
        }

        return current <= context.SessionId + 1;
    }

    private void RollBackStartedRecording()
    {
        try
        {
            if (_audio.IsRecording)
            {
                _audio.StopRecording();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[Dictation] Failed to stop recording during start rollback: {ex.Message}"
            );
        }

        try
        {
            _audioDucking.RestoreAudio();
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[Dictation] Failed to restore audio during start rollback: {ex.Message}"
            );
        }

        try
        {
            _mediaPause.ResumeMedia();
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[Dictation] Failed to resume media during start rollback: {ex.Message}"
            );
        }

        RecordingStateChanged?.Invoke(this, false);
        SetOverlayState(state =>
            // ReSharper disable once WithExpressionModifiesAllMembers -- `with` preserves any future-added state members; intentional even though all current members are set.
            state with
            {
                IsOverlayVisible = false,
                ShowFeedback = false,
                FeedbackIsError = false,
                FeedbackText = null,
                PartialText = null,
                LlmResponseText = null,
                IsRecording = false,
                StatusText = Localization.Loc.Instance["Overlay.Ready"],
                ActiveProfileName = null,
                ActiveAppName = null,
                SessionStartedAtUtc = null
            }
        );
    }

    private void SetOverlayState(Func<DictationOverlayState, DictationOverlayState> updater)
    {
        // Serialize updates: SetOverlayState is invoked from the toggle path,
        // the active-window snapshot Task.Run, and the partial-transcription
        // loop concurrently. Without a lock, the read-modify-write on
        // _overlayState and the OverlayStateChanged dispatch can interleave
        // and emit a stale state after a newer one.
        lock (_overlayStateLock)
        {
            _overlayState = updater(_overlayState);
            OverlayStateChanged?.Invoke(this, _overlayState);
        }
    }

    private void StartPartialTranscriptionSession(int sessionVersion)
    {
        _partialTranscriptionCts?.Cancel();
        _partialTranscriptionCts?.Dispose();

        _lastPublishedPartialText = null;
        var cts = new CancellationTokenSource();
        _partialTranscriptionCts = cts;
        // ReSharper disable once MethodSupportsCancellation -- the loop receives cts.Token directly; a Task.Run token would be redundant.
        _partialTranscriptionTask = Task.Run(() =>
            RunPartialTranscriptionLoopAsync(sessionVersion, cts.Token)
        );
    }

    private void StartStreamingTranscriptionSession(
        ITranscriptionEnginePlugin plugin,
        string? language,
        int sessionVersion
    )
    {
        var coordinator = new StreamingTranscriptionCoordinator(
            plugin,
            language,
            sessionVersion,
            TryPublishPartialTranscript,
            ex =>
            {
                // Coordinator already sets its own Faulted flag — just log.
                // Keeping fault state per-coordinator avoids cross-session
                // races (rapid stop/start) where a shared field could be
                // reset by a new dictation before the previous teardown reads it.
                Trace.WriteLine(
                    $"[Dictation] Streaming fault: {ex.GetType().Name}: {ex.Message}"
                );
            }
        );

        // Wire the audio tap BEFORE StartAsync resolves so frames captured
        // during the connect handshake queue in the coordinator's pending
        // buffer (1 MB cap, drop-oldest). Detached in
        // TeardownStreamingSessionAsync.
        _audio.LiveFrameSink = samples =>
            coordinator.AcceptAudioFrame(samples, _audio.CaptureSampleRate);

        // Owns cancellation of the queued connect handshake. The coordinator
        // creates its own internal _cts inside StartAsync, but if teardown
        // runs BEFORE the queued Task.Run executes, the coordinator's _cts
        // doesn't exist yet — DisposeAsync's _cts?.Cancel() is a no-op and
        // the plugin's StartStreamingAsync would still fire after the recording
        // had ended. Owning a startup CTS here lets teardown cancel the
        // connect at any point in its lifecycle.
        var startupCts = new CancellationTokenSource();
        _streamingStartupCts = startupCts;

        _streamingCoordinator = coordinator;
        // Capture the engine identity AND language hint bound to this
        // streaming session so the post-stop path can detect a profile-driven
        // model swap or language switch (the active-window snapshot resolves
        // a profile asynchronously, so streaming may have started before the
        // profile's InputLanguage / TranscriptionModelOverride was known) and
        // refuse to insert a streaming transcript that was transcribed under
        // the wrong settings. SelectedModelId is set during plugin activation
        // and is stable for the recording's duration.
        _streamingProviderId = plugin.ProviderId;
        _streamingModelId = plugin.SelectedModelId;
        _streamingLanguageHint = language;

        // Fire-and-forget the connect. The coordinator owns its internal CTS
        // and its own Faulted flag once StartAsync runs; before then, our
        // startupCts is the only thing teardown can cancel.
        // ReSharper disable once MethodSupportsCancellation -- fire-and-forget; the coordinator owns its internal CTS once StartAsync runs (see comment above).
        _ = Task.Run(async () =>
        {
            if (startupCts.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await coordinator.StartAsync(startupCts.Token);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"[Dictation] Unexpected streaming start error: {ex.Message}"
                );
            }
        });
    }

    private static async Task<(string? FinalText, bool Faulted)> TeardownStreamingSessionAsync(
        StreamingTranscriptionCoordinator? coordinator,
        CancellationTokenSource? startupCts,
        bool finalize,
        CancellationToken ct
    )
    {
        // Cancel the startup CTS only on non-finalize paths (cancel / discard
        // / dispose). On the finalize path the coordinator's internal _cts is
        // linked from this token; cancelling here would propagate into
        // RunSenderAsync's ReadAllAsync(ct) and abort the queued-audio drain
        // mid-FinalizeAsync, truncating the streamed transcript. DisposeAsync
        // (called below after FinalizeAsync) cancels the coordinator's _cts
        // anyway, so the startup CTS gets implicitly cancelled too.
        if (startupCts is not null && !finalize)
        {
            try { await startupCts.CancelAsync(); }
            catch
            {
                /* ignore */
            }
        }

        if (coordinator is null)
        {
            startupCts?.Dispose();
            return (null, false);
        }

        string? finalText = null;
        var finalizeThrew = false;
        if (finalize)
        {
            try
            {
                finalText = await coordinator.FinalizeAsync(ct);
            }
            catch (TimeoutException ex)
            {
                Trace.WriteLine(
                    $"[Dictation] Streaming finalize deadline exhausted; "
                    + $"using complete-WAV batch fallback: {ex.Message}"
                );
                finalizeThrew = true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Dictation] Streaming finalize error: {ex.Message}");
                finalizeThrew = true;
            }
        }

        await coordinator.DisposeAsync();
        startupCts?.Dispose();
        return (finalText, coordinator.Faulted || finalizeThrew);
    }

    private async Task<string> StopPartialTranscriptionSessionAsync()
    {
        var cts = _partialTranscriptionCts;
        var task = _partialTranscriptionTask;
        _partialTranscriptionCts = null;
        _partialTranscriptionTask = null;

        if (cts is not null)
        {
            await cts.CancelAsync();
            cts.Dispose();
        }

        if (task is null)
        {
            return _partialTranscriptState.StopSession();
        }

        try
        {
            // ReSharper disable once MethodSupportsCancellation -- bounded 500 ms wait during teardown; intentionally not externally cancellable.
            await task.WaitAsync(TimeSpan.FromMilliseconds(500));
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Dictation] Partial transcription shutdown failed: {ex.Message}");
        }

        return _partialTranscriptState.StopSession();
    }

    private async Task AwaitRecordingSnapshotAsync()
    {
        var snapshotTask = _recordingSnapshotTask;
        _recordingSnapshotTask = null;
        if (snapshotTask is null)
        {
            return;
        }

        try
        {
            // Cover the deferred URL re-match's full background pipeline:
            //   - initial snapshot         up to 500 ms
            //   - AT-SPI URL walker        up to 2 500 ms (WalkBudget)
            //   - verification snapshot    up to 500 ms (matches initial)
            //   - rematch + lock overhead  small
            // Worst case ~3.5 s, so 4 s gives margin without being absurd.
            // Without this, any dictation shorter than the walker's runtime
            // would advance _recordingSession before the late URL write
            // lands, and the session-id guard would drop the write —
            // silently dropping URL-based profile matches for short
            // browser dictations.
            //
            // Cost: stop-to-transcription latency grows by up to ~4 s on
            // browser tabs when the walker uses its full budget. Non-
            // browser processes early-return from GetBrowserUrl in
            // milliseconds and aren't affected. Long dictations (>3 s of
            // recording) also aren't affected because the walker has
            // already completed in the background by the time Stop fires.
            await snapshotTask.WaitAsync(TimeSpan.FromMilliseconds(4000));
        }
        catch (TimeoutException)
        {
            Trace.WriteLine("[Dictation] Active-window snapshot timed out during stop.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Dictation] Active-window snapshot wait failed: {ex.Message}");
        }
    }

    private void ShutdownPartialTranscriptionSession()
    {
        var cts = _partialTranscriptionCts;
        var task = _partialTranscriptionTask;
        _partialTranscriptionCts = null;
        _partialTranscriptionTask = null;

        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }

        if (task is not null)
        {
            try
            {
                task.Wait(TimeSpan.FromSeconds(1));
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"[Dictation] Partial transcription dispose wait failed: {ex.Message}"
                );
            }
        }

        _partialTranscriptState.StopSession();
    }

    private async Task RunPartialTranscriptionLoopAsync(int sessionVersion, CancellationToken ct)
    {
        var partialPollInterval = TimeSpan.FromSeconds(3);
        var loopDelay = TimeSpan.FromMilliseconds(250);
        var nextPartialPollAtUtc = DateTime.UtcNow + partialPollInterval;

        try
        {
            while (!ct.IsCancellationRequested && _audio.IsRecording)
            {
                if (_audio.HasSpeechEnergy)
                {
                    _lastSpeechDetectedAtUtc = DateTime.UtcNow;
                }
                else if (ShouldAutoStopForSilence())
                {
                    _silenceStopRequested = true;
                    ReportStatus("Silence detected. Stopping…");
                    // ReSharper disable once MethodSupportsCancellation -- fire-and-forget silence auto-stop; StopAsync runs teardown to completion.
                    FireAndLog(() => Task.Run(StopAsync), "silence auto-stop");
                    return;
                }

                if (DateTime.UtcNow >= nextPartialPollAtUtc)
                {
                    var wav = _audio.GetCurrentBuffer();
                    // Partials are best-effort/cosmetic. Only poll when a model is
                    // already loaded (never *initiate* a load for a partial), and
                    // use TryAcquire so a partial silently skips when a final
                    // transcription holds the lease.
                    if (
                        wav is not null
                        && wav.Length > 44
                        && _audio.HasSpeechEnergy
                        && _models.ActiveModelId is not null
                    )
                    {
                        var partialModelId =
                            _recordingProfile?.TranscriptionModelOverride
                            ?? _settings.Current.SelectedModelId;
                        await using var lease = await _models.TryAcquireTranscriptionAsync(
                            partialModelId,
                            cancellationToken: ct
                        );
                        // Local engines poll cheaply; online batch providers
                        // re-upload the whole growing buffer each poll, so they
                        // stay off unless the user opts in. The lease still
                        // disposes either way, and the loop keeps running for
                        // silence auto-stop when live preview is gated off.
                        if (
                            lease is not null
                            && LinuxLiveTranscriptionStartupPolicy.Select(
                                _settings.Current,
                                lease.Plugin
                            ) == LiveTranscriptionMode.Polling
                        )
                        {
                            await PollPartialTranscriptOnceAsync(
                                lease.Plugin,
                                wav,
                                sessionVersion,
                                ct
                            );
                        }
                    }

                    nextPartialPollAtUtc = DateTime.UtcNow + partialPollInterval;
                }

                await Task.Delay(loopDelay, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Dictation] Partial transcription loop failed: {ex.Message}");
        }
    }

    private bool ShouldAutoStopForSilence()
    {
        if (_silenceStopRequested || !_settings.Current.SilenceAutoStopEnabled)
        {
            return false;
        }

        var timeoutSeconds = _settings.Current.SilenceAutoStopSeconds;
        if (timeoutSeconds <= 0)
        {
            return false;
        }

        return DateTime.UtcNow - _lastSpeechDetectedAtUtc >= TimeSpan.FromSeconds(timeoutSeconds);
    }

    private async Task PollPartialTranscriptOnceAsync(
        ITranscriptionEnginePlugin plugin,
        byte[] wav,
        int sessionVersion,
        CancellationToken ct
    )
    {
        var effectiveLanguage = _recordingProfile?.InputLanguage ?? _settings.Current.Language;
        // ReSharper disable once InlineTemporaryVariable -- named local kept for readability over inlining into the pattern match.
        var languageHint =
            effectiveLanguage is { Length: > 0 } lang && lang != "auto" ? lang : null;
        var translate = string.Equals(
            _recordingProfile?.SelectedTask ?? _settings.Current.TranscriptionTask,
            "translate",
            StringComparison.OrdinalIgnoreCase
        );

        try
        {
            var result = await plugin.TranscribeStreamingAsync(
                wav,
                languageHint,
                translate,
                null,
                partial =>
                {
                    TryPublishPartialTranscript(sessionVersion, partial);
                    return !ct.IsCancellationRequested && _audio.IsRecording;
                },
                ct
            );

            TryPublishPartialTranscript(sessionVersion, result.Text);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Dictation] Partial transcription polling failed: {ex.Message}");
        }
    }

    private void ClearSessionInFlight(int sessionId)
    {
        _inFlightTracker.End(sessionId);
    }

    private void PublishSessionResult(DictationSessionResult result)
    {
        ClearSessionInFlight(result.SessionId);
        try
        {
            SessionCompleted?.Invoke(result);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Dictation] SessionCompleted handler threw: {ex}");
        }
    }

    private void PublishSessionTerminal(int sessionId, string status, string? message)
    {
        PublishSessionResult(
            new DictationSessionResult(
                sessionId,
                status,
                string.Empty,
                null,
                null,
                0,
                null,
                null,
                message
            )
        );
    }

    /// <summary>
    ///     StopAsync calls this on the pre-transcription terminal paths
    ///     (canceled, too-short, no-speech). The post-transcription pipeline
    ///     calls <see cref="PublishSessionResult" /> directly.
    /// </summary>
    private void FinalizeSession(int sessionId, string status, string? message)
    {
        PublishSessionTerminal(sessionId, status, message);
    }

    private void TryPublishPartialTranscript(int sessionVersion, string? text)
    {
        if (
            !_partialTranscriptState.TryApplyPolling(
                sessionVersion,
                text ?? "",
                _dictionary.ApplyCorrections,
                out var partialText
            )
        )
        {
            return;
        }

        if (string.Equals(_lastPublishedPartialText, partialText, StringComparison.Ordinal))
        {
            return;
        }

        _lastPublishedPartialText = partialText;
        _models.PluginManager.EventBus.Publish(
            new PartialTranscriptionUpdateEvent
            {
                PartialText = partialText,
                IsRecording = _audio.IsRecording,
                ElapsedSeconds =
                    _recordingStart == default
                        ? 0
                        : Math.Max(0, (DateTime.UtcNow - _recordingStart).TotalSeconds)
            }
        );

        SetOverlayState(state => state with { PartialText = partialText });
    }
}
