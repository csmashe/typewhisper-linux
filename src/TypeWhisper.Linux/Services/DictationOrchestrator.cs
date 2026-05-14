using System.Diagnostics;
using System.IO;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// Glues hotkey, recorder, transcription engine, post-processing, and text
/// injection into a single dictation loop:
///   hotkey → start recording → hotkey → stop → save WAV → transcribe via
///   the active transcription plugin → apply dictionary + snippets →
///   the resolved input backend (wtype on Wayland, xdotool on X11/XWayland)
///   types the result into the focused window → history record.
///
/// If no transcription plugin/model is loaded the WAV is still written so
/// the user can inspect what was captured.
/// </summary>
/// <summary>
/// Immutable snapshot of the per-recording context captured at stop time.
/// Passed to the post-stop transcription / insertion pipeline so that pipeline
/// reads a stable view of the recording's profile, app, and timing even if a
/// brand-new dictation has already started recording and overwritten the
/// instance-level <c>_recording*</c> fields.
/// </summary>
internal sealed record RecordingContext(
    int SessionId,
    DateTime RecordingStart,
    string? AppProcess,
    string? AppTitle,
    string? AppUrl,
    string? WindowId,
    Profile? Profile,
    CancellationToken CancelToken);

public sealed class DictationOrchestrator : IDisposable
{
    private readonly HotkeyService _hotkey;
    private readonly AudioRecordingService _audio;
    private readonly SessionAudioFileService _sessionAudioFiles;
    private readonly SoundFeedbackService _soundFeedback;
    private readonly SpeechFeedbackService _speechFeedback;
    private readonly TextInsertionService _textInsertion;
    private readonly IAudioDuckingService _audioDucking;
    private readonly IMediaPauseService _mediaPause;
    private readonly ModelManagerService _models;
    private readonly IHistoryService _history;
    private readonly ISettingsService _settings;
    private readonly ActiveWindowService _activeWindow;
    private readonly IProfileService _profiles;
    private readonly IPromptActionService _promptActions;
    private readonly IDictionaryService _dictionary;
    private readonly ISnippetService _snippets;
    private readonly IVocabularyBoostingService _vocabularyBoosting;
    private readonly LlmCleanupService _cleanup;
    private readonly IPostProcessingPipeline _pipeline;
    private readonly ITranslationService _translation;
    private readonly PromptProcessingService _promptProcessing;
    private readonly MemoryService _memory;
    private readonly RecentTranscriptionsService _recentTranscriptions;
    private readonly IdeFileReferenceService _ideFileReferences;
    private readonly SystemCommandAvailabilityService _commands;
    private readonly IDetectionFailureTracker _failureTracker;
    private readonly StreamingTranscriptState _partialTranscriptState = new();
    private readonly VoiceCommandParser _voiceCommands = new();
    private readonly DeveloperFormattingService _developerFormatting = new();
    private readonly SemaphoreSlim _toggleGate = new(1, 1);
    private readonly object _overlayStateLock = new();
    private DateTime _recordingStart;
    private string? _recordingAppProcess;
    private string? _recordingAppTitle;
    private string? _recordingAppUrl;
    private string? _recordingWindowId;
    private Profile? _recordingProfile;
    private DictationOverlayState _overlayState = DictationOverlayState.Hidden;
    private CancellationTokenSource? _partialTranscriptionCts;
    private Task? _partialTranscriptionTask;
    private Task? _recordingSnapshotTask;
    // Monotonically incremented for every StartAsync. The active-window
    // snapshot Task.Run captures the value at start time, then guards every
    // write to the shared _recording* fields and overlay/event publishes
    // behind it. If StopAsync (or another StartAsync) has already advanced
    // the counter — including the case where AwaitRecordingSnapshotAsync
    // timed out and the snapshot wrote late — the stale snapshot's writes
    // are dropped so they cannot corrupt the next dictation's context.
    private int _recordingSession;
    private readonly object _recordingSessionLock = new();
    private CancellationTokenSource? _activeDictationCts;
    private volatile bool _cancelRequested;
    private string? _lastPublishedPartialText;
    private DateTime _lastSpeechDetectedAtUtc;
    private bool _silenceStopRequested;
    private bool _initialized;
    private bool _disposed;

    private EventHandler? _toggleHandler;
    private EventHandler? _startHandler;
    private EventHandler? _stopHandler;
    private EventHandler? _cancelHandler;
    private EventHandler<string>? _hookFailedHandler;

    public event EventHandler<string>? RecordingCaptured; // arg = WAV file path
    public event EventHandler<bool>? RecordingStateChanged;
    public event EventHandler<string>? TranscriptionCompleted;
    public event EventHandler<string>? StatusMessage;
    public event EventHandler<DictationOverlayState>? OverlayStateChanged;

    public bool IsRecording => _audio.IsRecording;

    /// <summary>
    /// Snapshot of the current dictation pipeline phase, suitable for the
    /// <c>typewhisper status</c> JSON response. Derived from the audio
    /// capture state plus the live overlay state — no new mutable surface,
    /// just a read-only projection of state we already track.
    /// </summary>
    /// <remarks>
    /// The audio recorder is the source of truth for <c>recording</c>. Once
    /// recording stops, the overlay's StatusText drives transcribing /
    /// injecting / idle: "Processing…" / "Transcribing…" indicate the
    /// transcription engine is active; "Typed", "Pasted", "Copied" mean
    /// the inject phase is in progress or just completed. Anything else
    /// (Ready, Canceled, Too short, an error) falls through to idle.
    /// </remarks>
    public string CurrentStateLabel
    {
        get
        {
            if (_audio.IsRecording) return "recording";

            DictationOverlayState snapshot;
            lock (_overlayStateLock)
            {
                snapshot = _overlayState;
            }

            // Compare against the same status strings used by SetOverlayState
            // call sites elsewhere in this file. Keep the matcher cheap —
            // status reads must not block the UI thread.
            var status = snapshot.StatusText;
            if (status is null) return "idle";
            if (status.StartsWith("Processing", StringComparison.OrdinalIgnoreCase)
                || status.StartsWith("Transcribing", StringComparison.OrdinalIgnoreCase))
            {
                return "transcribing";
            }
            // The injection phase doesn't currently update StatusText to a
            // dedicated "Injecting" label before TextInsertionService runs;
            // the completion messages ("Typed N char(s)", "Pasted…",
            // "Copied to clipboard…") fire after the inject call has
            // returned. Treat those terminal labels as idle — the inject
            // is no longer in flight by the time the user sees them.
            return "idle";
        }
    }

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
        IDetectionFailureTracker failureTracker)
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
        _failureTracker = failureTracker;
    }

    public void Initialize()
    {
        if (_initialized || _disposed) return;
        _toggleHandler = (_, _) => FireAndLog(ToggleAsync, nameof(ToggleAsync));
        _startHandler = (_, _) => FireAndLog(StartAsync, nameof(StartAsync));
        _stopHandler = (_, _) => FireAndLog(StopAsync, nameof(StopAsync));
        _cancelHandler = (_, _) => FireAndLog(CancelAsync, nameof(CancelAsync));
        _hookFailedHandler = (_, message) =>
        {
            Trace.WriteLine($"[Dictation] Hotkey hook unavailable: {message}");
            ReportStatus("Global hotkey disabled.");
            ShowFeedback("Global hotkey disabled. Check libuiohook/X11 permissions.", isError: true);
        };
        _hotkey.DictationToggleRequested += _toggleHandler;
        _hotkey.DictationStartRequested += _startHandler;
        _hotkey.DictationStopRequested += _stopHandler;
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
            _hotkey.CancelRequested -= _cancelHandler;
            _hotkey.HookFailed -= _hookFailedHandler;
            _toggleHandler = null;
            _startHandler = null;
            _stopHandler = null;
            _cancelHandler = null;
            _hookFailedHandler = null;
            throw;
        }
        _initialized = true;
    }

    public async Task ToggleAsync()
    {
        if (_audio.IsRecording) await StopAsync();
        else await StartAsync();
    }

    /// <summary>
    /// Aborts the active dictation. While recording, triggers a stop that
    /// discards the audio (no transcription). While transcribing or running
    /// the post-processing pipeline, cancels the active token so the in-flight
    /// async work bails out and "Canceled" is surfaced instead of "Failed".
    /// </summary>
    public async Task CancelAsync()
    {
        // Cancel the in-flight async work first so a long-running TranscribeAsync
        // or pipeline call begins unwinding immediately, even if we have to
        // wait for the toggle gate below.
        var cts = _activeDictationCts;
        if (cts is not null)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* StopAsync just disposed it — nothing to cancel. */ }
        }

        // If we're still recording, route through StopAsync with the cancel
        // flag set. StopAsync owns the toggle gate and the recording-cleanup
        // ordering; piggy-backing on it keeps the lifecycle consistent.
        if (_audio.IsRecording)
        {
            _cancelRequested = true;
            await StopAsync();
        }
    }

    public async Task StartAsync()
    {
        Task? recordingSnapshotTask = null;
        if (!await _toggleGate.WaitAsync(0)) return;
        try
        {
            if (_audio.IsRecording) return;

            _audio.WhisperModeEnabled = _settings.Current.WhisperModeEnabled;

            // Start capturing audio immediately — the user's finger is on the
            // key and they may already be speaking (especially in PTT).
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
                ShowFeedback(message, isError: true);
                return;
            }

            if (!_audio.IsRecording)
            {
                var message = BuildRecordingStartFailureMessage(null);
                ReportStatus(message);
                ShowFeedback(message, isError: true);
                return;
            }

            try
            {
                if (_settings.Current.AudioDuckingEnabled)
                    _audioDucking.DuckAudio(_settings.Current.AudioDuckingLevel);
                if (_settings.Current.PauseMediaDuringRecording)
                    _mediaPause.PauseMedia();
                if (_settings.Current.SoundFeedbackEnabled)
                    _soundFeedback.PlayRecordingStarted();
                _speechFeedback.AnnounceRecordingStarted();
                RecordingStateChanged?.Invoke(this, true);
                SetOverlayState(state => state with
                {
                    IsOverlayVisible = true,
                    ShowFeedback = false,
                    FeedbackIsError = false,
                    FeedbackText = null,
                    PartialText = null,
                    IsRecording = true,
                    StatusText = "Recording… press the hotkey again to stop.",
                    ActiveProfileName = null,
                    ActiveAppName = null,
                    SessionStartedAtUtc = DateTime.UtcNow
                });
                StartPartialTranscriptionSession();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Dictation] Post-start setup failed: {ex}");
                RollBackStartedRecording();
                await StopPartialTranscriptionSessionAsync();
                throw;
            }

            // Set up the per-dictation cancellation source AFTER post-start
            // setup succeeds. CancelAsync uses this to abort an in-flight
            // TranscribeAsync / pipeline; the matching cancel hotkey is also
            // armed here so Escape only fires while a dictation is live.
            _cancelRequested = false;
            _activeDictationCts = new CancellationTokenSource();
            _hotkey.IsCancelShortcutEnabled = true;

            // Publish the snapshot task before releasing the toggle gate so a
            // near-immediate StopAsync can reliably observe and await it.
            // Reserve a session id for this recording and pre-populate fields.
            // The Task.Run below captures the id and only commits its results
            // if the session is still active when it finishes — protecting the
            // next dictation from late writes if AwaitRecordingSnapshotAsync's
            // 500ms timeout elapses before the snapshot completes.
            int sessionId;
            lock (_recordingSessionLock)
            {
                sessionId = ++_recordingSession;
                _recordingAppProcess = null;
                _recordingAppTitle = null;
                _recordingAppUrl = null;
                _recordingWindowId = _activeWindow.GetActiveWindowId();
                _recordingProfile = null;
            }
            recordingSnapshotTask = Task.Run(async () =>
            {
                ActiveWindowSnapshot? initialSnap = null;
                string? appProcess = null;
                string? appTitle = null;
                string? appUrl = null;
                MatchResult initialMatch = MatchResult.NoMatch;
                Profile? matchedProfile = null;
                try
                {
                    // 50 ms was too tight: the per-provider budget alone is
                    // 150 ms, and xdotool's chain (window-id + title + pid →
                    // ProcessName) is three sequential subprocesses that
                    // together can exceed half a second under normal load.
                    // The whole task runs in the background of audio
                    // recording, so a half-second budget here doesn't add
                    // user-visible latency — it just guarantees we get a
                    // process+title hit before the deferred URL pass runs.
                    using var initialCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                    initialSnap = await _activeWindow.GetActiveWindowSnapshotAsync(initialCts.Token).ConfigureAwait(false);
                    appProcess = initialSnap?.ProcessName;
                    appTitle = initialSnap?.Title;
                    initialMatch = _profiles.MatchProfile(appProcess, url: null);
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
                            "No active-window provider returned a snapshot");
                    }
                    else
                    {
                        _failureTracker.RecordSuccess();
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Dictation] Initial active-window snapshot failed: {ex.Message}");
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
                    Trace.WriteLine($"[Dictation] Snapshot for session {sessionId} discarded — session no longer active.");
                    return;
                }

                _audio.WhisperModeEnabled =
                    matchedProfile?.WhisperModeOverride ?? _settings.Current.WhisperModeEnabled;
                SetOverlayState(state => state with
                {
                    ActiveProfileName = matchedProfile?.Name,
                    ActiveAppName = appTitle
                });
                _models.PluginManager.EventBus.Publish(new RecordingStartedEvent
                {
                    AppName = appTitle,
                    AppProcessName = appProcess
                });

                try
                {
                    // AT-SPI URL walks on Wayland can take 2+ seconds on a
                    // busy Gmail tree (busctl process spawn + D-Bus round
                    // trip per node). Our orchestrator timeout has to
                    // exceed the walker's own budget by a healthy margin —
                    // otherwise this await cancels in the same window the
                    // walker returns its result, and a perfectly good URL
                    // gets discarded. Dictation has already finished by
                    // this point, so the user isn't waiting on it.
                    using var deferredCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(4000));
                    var deferredUrl = await Task.Run(
                        () => _activeWindow.GetBrowserUrl(allowInteractiveCapture: false),
                        deferredCts.Token).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(deferredUrl))
                    {
                        // The URL came from whatever window is focused right now, which
                        // may have changed since recording started. Re-snapshot and only
                        // apply the rematch if we're still on the same window — otherwise
                        // we'd bind this dictation to a URL from an unrelated tab/window.
                        ActiveWindowSnapshot? verifySnap = null;
                        try
                        {
                            // Match the initial-snapshot budget — 50 ms was
                            // tighter than the per-provider 150 ms slice and
                            // could return null on xdotool's multi-subprocess
                            // chain, causing us to discard a valid URL just
                            // because the same provider chain didn't finish
                            // in time on the verification pass.
                            using var verifyCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                            verifySnap = await _activeWindow
                                .GetActiveWindowSnapshotAsync(verifyCts.Token)
                                .ConfigureAwait(false);
                        }
                        catch { }

                        if (initialSnap is null || verifySnap is null || !IsSameWindow(initialSnap, verifySnap))
                        {
                            Trace.WriteLine("[Dictation] Deferred URL discarded — focused window changed mid-capture.");
                        }
                        else
                        {
                            // The URL is valid metadata regardless of whether it changes the
                            // profile match — history records and downstream prompt processing
                            // both consume _recordingAppUrl. Commit it once window identity is
                            // verified, then separately decide whether the rematch should swap
                            // the active profile (which we gate on a tier upgrade to avoid
                            // churn/downgrade).
                            lock (_recordingSessionLock)
                            {
                                if (_recordingSession != sessionId)
                                    return;
                                _recordingAppUrl = deferredUrl;
                            }

                            var rematch = _profiles.MatchProfile(appProcess, deferredUrl);
                            if (rematch.Profile is not null && (int)rematch.Kind < (int)initialMatch.Kind)
                            {
                                lock (_recordingSessionLock)
                                {
                                    if (_recordingSession != sessionId)
                                        return;
                                    _recordingProfile = rematch.Profile;
                                }

                                SetOverlayState(state => state with
                                {
                                    ActiveProfileName = rematch.Profile.Name
                                });

                                _audio.WhisperModeEnabled =
                                    rematch.Profile.WhisperModeOverride ?? _settings.Current.WhisperModeEnabled;
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
        }
        finally
        {
            _toggleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        if (!await _toggleGate.WaitAsync(0)) return;
        var earlyCleanupDone = false;
        var wasRecording = false;
        var gateReleased = false;
        CancellationTokenSource? snapshotCts = null;
        try
        {
            if (!_audio.IsRecording) return;
            wasRecording = true;

            // Snapshot the cancel flag once we own the toggle gate. CancelAsync
            // sets it to true before invoking us; clear it so the next dictation
            // starts fresh.
            var canceledThisStop = _cancelRequested;
            _cancelRequested = false;

            var wav = await _audio.StopRecordingAsync();
            await StopPartialTranscriptionSessionAsync();
            await AwaitRecordingSnapshotAsync();
            _audioDucking.RestoreAudio();
            _mediaPause.ResumeMedia();
            earlyCleanupDone = true;
            if (_settings.Current.SoundFeedbackEnabled)
                _soundFeedback.PlayRecordingStopped();
            RecordingStateChanged?.Invoke(this, false);

            // Snapshot the per-recording context into locals so the rest of
            // this stop (transcription + insertion) reads a stable view even
            // if a new StartAsync re-populates the instance fields. We also
            // pull the active dictation CTS into a local so its token drives
            // this dictation's transcription, then null the shared field.
            //
            // Cancel semantics after the gate release: CancelAsync uses
            // `_activeDictationCts` to abort an in-flight dictation. Once we
            // null it here, Escape can no longer cancel the transcription of
            // this just-stopped dictation. If a new dictation starts before
            // this transcription completes, the new StartAsync allocates a
            // fresh CTS and CancelAsync targets the new (recording) dictation.
            // The trade-off keeps Cancel's surface unambiguous: it always
            // targets the live recording, never a background transcription.
            snapshotCts = _activeDictationCts;
            _activeDictationCts = null;
            _hotkey.IsCancelShortcutEnabled = false;
            _cancelRequested = false;

            // Advance the recording session under the lock so any still-running
            // active-window snapshot Task.Run (e.g. if AwaitRecordingSnapshotAsync
            // timed out above) observes the new counter and drops its writes
            // rather than clobbering a future dictation's _recording* fields.
            // Capture the just-stopped session id in the context so the
            // post-stop pipeline can suppress overlay/status writes once a
            // newer dictation has taken ownership of the overlay.
            RecordingContext recordingContext;
            lock (_recordingSessionLock)
            {
                var stoppedSessionId = _recordingSession;
                _recordingSession++;
                recordingContext = new RecordingContext(
                    SessionId: stoppedSessionId,
                    RecordingStart: _recordingStart,
                    AppProcess: _recordingAppProcess,
                    AppTitle: _recordingAppTitle,
                    AppUrl: _recordingAppUrl,
                    WindowId: _recordingWindowId,
                    Profile: _recordingProfile,
                    CancelToken: snapshotCts?.Token ?? CancellationToken.None);

                _recordingAppProcess = null;
                _recordingAppTitle = null;
                _recordingAppUrl = null;
                _recordingWindowId = null;
                _recordingProfile = null;
                _recordingStart = default;
            }

            // Release the toggle gate now: audio capture is fully torn down
            // and the per-recording context has been snapshotted. A new
            // StartAsync can begin recording while transcription of the
            // previous capture runs below.
            _toggleGate.Release();
            gateReleased = true;

            if (canceledThisStop)
            {
                // User hit Escape while still recording: clean up audio/media
                // (already done above) and surface "Canceled" without saving
                // the WAV or running transcription.
                SetOverlayState(state => state with
                {
                    IsOverlayVisible = true,
                    ShowFeedback = true,
                    FeedbackText = "Canceled",
                    FeedbackIsError = false,
                    IsRecording = false,
                    StatusText = "Canceled",
                    SessionStartedAtUtc = null
                });
                StatusMessage?.Invoke(this, "Canceled");
                _models.PluginManager.EventBus.Publish(new RecordingStoppedEvent
                {
                    DurationSeconds = LinuxDictationShortSpeechPolicy.ComputeDurationSeconds(wav)
                });
                return;
            }

            SetOverlayState(state => state with
            {
                IsOverlayVisible = true,
                ShowFeedback = false,
                FeedbackText = null,
                FeedbackIsError = false,
                IsRecording = false,
                StatusText = "Processing…",
                SessionStartedAtUtc = null
            });
            var duration = LinuxDictationShortSpeechPolicy.ComputeDurationSeconds(wav);
            _models.PluginManager.EventBus.Publish(new RecordingStoppedEvent
            {
                DurationSeconds = duration
            });

            var shortSpeechDecision = LinuxDictationShortSpeechPolicy.Classify(
                duration,
                LinuxDictationShortSpeechPolicy.ComputePeakLevel(wav),
                _settings.Current.TranscribeShortQuietClipsAggressively);

            if (shortSpeechDecision == LinuxShortSpeechDecision.DiscardTooShort)
            {
                SetOverlayState(state => state with
                {
                    IsOverlayVisible = true,
                    ShowFeedback = true,
                    FeedbackText = "Too short",
                    FeedbackIsError = true,
                    IsRecording = false,
                    StatusText = "Too short",
                });
                StatusMessage?.Invoke(this, "Too short");
                return;
            }

            if (shortSpeechDecision == LinuxShortSpeechDecision.DiscardNoSpeech)
            {
                SetOverlayState(state => state with
                {
                    IsOverlayVisible = true,
                    ShowFeedback = true,
                    FeedbackText = "No speech detected",
                    FeedbackIsError = true,
                    IsRecording = false,
                    StatusText = "No speech detected",
                });
                StatusMessage?.Invoke(this, "No speech detected");
                return;
            }

            wav = LinuxDictationShortSpeechPolicy.PadWavForFinalTranscription(wav, duration);
            duration = LinuxDictationShortSpeechPolicy.ComputeDurationSeconds(wav);

            var path = _sessionAudioFiles.SaveDictationCapture(wav);
            RecordingCaptured?.Invoke(this, path);
            Trace.WriteLine($"[Dictation] Captured → {path} ({wav.Length} bytes)");

            await TranscribeAndInsertAsync(wav, path, duration, recordingContext);
        }
        finally
        {
            // Only restore audio/resume media if StartAsync had a chance to
            // duck/pause them — i.e., we actually had an active recording when
            // StopAsync was called. If teardown failed mid-stop, wasRecording
            // is still true and we DO want to restore so the user isn't left
            // muted.
            if (wasRecording && !earlyCleanupDone)
            {
                _audioDucking.RestoreAudio();
                _mediaPause.ResumeMedia();
            }
            // Dispose the snapshot CTS owned by this stop now that transcription
            // has returned (or thrown). Disposal must happen AFTER
            // TranscribeAndInsertAsync completes so any token registrations
            // observed during the pipeline stay valid for its full lifetime.
            if (snapshotCts is not null)
            {
                try { snapshotCts.Dispose(); }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Dictation] Active dictation CTS dispose failed: {ex.Message}");
                }
            }
            if (!gateReleased)
                _toggleGate.Release();
        }
    }

    private async Task TranscribeAndInsertAsync(
        byte[] wav,
        string wavPath,
        double duration,
        RecordingContext context)
    {
        var cancelToken = context.CancelToken;
        var effectiveModelId = context.Profile?.TranscriptionModelOverride ?? _settings.Current.SelectedModelId;
        if (!string.IsNullOrWhiteSpace(effectiveModelId) && _models.ActiveModelId != effectiveModelId)
        {
            try
            {
                var loaded = await _models.EnsureModelLoadedAsync(effectiveModelId, cancelToken);
                if (!loaded)
                {
                    ReportStatus(context, $"Configured model '{effectiveModelId}' is not available.");
                    ShowFeedback(context, "Model unavailable.", isError: true);
                    return;
                }
            }
            catch (OperationCanceledException) when (cancelToken.IsCancellationRequested)
            {
                Trace.WriteLine($"[Dictation] Model load canceled by user ('{effectiveModelId}').");
                ReportStatus(context, "Canceled");
                ShowFeedback(context, "Canceled", isError: false);
                return;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Dictation] Failed to load effective model '{effectiveModelId}': {ex}");
                ReportStatus(context, $"Failed to load configured model: {ex.Message}");
                ShowFeedback(context, "Model load failed.", isError: true);
                return;
            }
        }

        var plugin = _models.ActiveTranscriptionPlugin;
        if (plugin is null)
        {
            ReportStatus(context, "No transcription model loaded. WAV saved for review.");
            ShowFeedback(context, "No transcription model loaded.", isError: true);
            return;
        }

        ReportStatus(context, $"Transcribing via {plugin.ProviderDisplayName}…");

        var transcriptionCompletedPublished = false;
        try
        {
            var effectiveLanguage = context.Profile?.InputLanguage ?? _settings.Current.Language;
            var languageHint = effectiveLanguage is { Length: > 0 } lang && lang != "auto" ? lang : null;
            var translate = string.Equals(
                context.Profile?.SelectedTask ?? _settings.Current.TranscriptionTask,
                "translate",
                StringComparison.OrdinalIgnoreCase);

            PluginTranscriptionResult? result;
            try
            {
                result = await plugin.TranscribeAsync(
                    wavAudio: wav, language: languageHint, translate: translate,
                    prompt: null, ct: cancelToken);
            }
            catch (OperationCanceledException) when (cancelToken.IsCancellationRequested)
            {
                Trace.WriteLine("[Dictation] Transcription canceled by user.");
                ReportStatus(context, "Canceled");
                ShowFeedback(context, "Canceled", isError: false);
                return;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Dictation] Transcription failed: {ex}");
                _models.PluginManager.EventBus.Publish(new TranscriptionFailedEvent
                {
                    ErrorMessage = ex.Message,
                    ModelId = plugin.SelectedModelId,
                    AppName = context.AppTitle
                });
                ReportStatus(context, $"Transcription failed: {ex.Message}");
                _speechFeedback.AnnounceError(ex.Message);
                ShowFeedback(context, "Transcription failed.", isError: true);
                return;
            }

            var rawText = result?.Text?.Trim();
            if (string.IsNullOrEmpty(rawText))
            {
                ReportStatus(context, "Transcription returned no text.");
                ShowFeedback(context, "Transcription returned no text.", isError: true);
                return;
            }

            if (result?.NoSpeechProbability is > 0.8f && !_settings.Current.TranscribeShortQuietClipsAggressively)
            {
                ReportStatus(context, "No speech detected.");
                ShowFeedback(context, "No speech detected.", isError: true);
                return;
            }

            var pipelineContext = new PostProcessingContext
            {
                SourceLanguage = result?.DetectedLanguage ?? languageHint,
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
                    + $"promptAction='{promptAction?.Name ?? "<none>"}').");

                if (!string.IsNullOrWhiteSpace(context.Profile.PromptActionId) && promptAction is null)
                {
                    var message = $"Prompt action for profile '{context.Profile.Name}' is disabled or missing.";
                    Trace.WriteLine($"[Dictation] {message} actionId='{context.Profile.PromptActionId}'.");
                    ReportStatus(context, message);
                }
            }

            var translationTarget = context.Profile?.TranslationTarget ?? _settings.Current.TranslationTargetLanguage;
            var cleanupLevel = ResolveCleanupLevel(context, promptAction);

            var pluginProcessors = _models.PluginManager.PostProcessors
                .Select(processor => new PluginPostProcessor(
                    processor.Priority,
                    (text, token) => processor.ProcessAsync(text, pipelineContext, token)))
                .ToList();

            var pipelineResult = await _pipeline.ProcessAsync(
                rawText,
                new PipelineOptions
                {
                    AppFormatter = AppFormatterService.Format,
                    TargetProcessName = context.AppProcess,
                    DictionaryCorrector = _dictionary.ApplyCorrections,
                    VocabularyBooster = _settings.Current.VocabularyBoostingEnabled
                        ? _vocabularyBoosting.Apply
                        : null,
                    CleanupHandler = cleanupLevel == CleanupLevel.None
                        ? null
                        : (text, token) => _cleanup.CleanAsync(
                            text,
                            cleanupLevel,
                            message =>
                            {
                                ReportStatus(context, message);
                                return Task.CompletedTask;
                            },
                            token),
                    SnippetExpander = text => _snippets.ApplySnippets(text, profileId: context.Profile?.Id),
                    LlmHandler = promptAction is not null
                        ? (text, token) => RunPromptActionAsync(context, promptAction, text, token)
                        : null,
                    TranslationHandler = !string.IsNullOrWhiteSpace(translationTarget)
                        ? (text, source, target, token) => _translation.TranslateAsync(text, source, target, token)
                        : null,
                    TranslationTarget = string.IsNullOrWhiteSpace(translationTarget) ? null : translationTarget,
                    EffectiveSourceLanguage = languageHint,
                    DetectedLanguage = result?.DetectedLanguage,
                    PluginPostProcessors = pluginProcessors,
                    StatusCallback = status =>
                    {
                        ReportStatus(context, status == "AI"
                            ? "Processing prompt action…"
                            : $"Processing {status}…");
                        return Task.CompletedTask;
                    }
                },
                cancelToken);

            var commandResult = _voiceCommands.Parse(pipelineResult.Text);
            var finalText = ApplyProfileStyleFormatting(context, commandResult.Text);

            TranscriptionCompleted?.Invoke(this, finalText);
            _speechFeedback.AnnounceTranscriptionComplete(finalText);
            _models.PluginManager.EventBus.Publish(new TranscriptionCompletedEvent
            {
                RawText = rawText,
                Text = finalText,
                DetectedLanguage = result?.DetectedLanguage,
                DurationSeconds = duration,
                EngineUsed = plugin.ProviderId,
                ModelId = plugin.SelectedModelId,
                ProfileName = context.Profile?.Name,
                AppName = context.AppTitle,
                AppProcessName = context.AppProcess,
                Url = context.AppUrl
            });
            transcriptionCompletedPublished = true;

            var actionPlugin = ResolveActionPlugin(promptAction);

            // Yield focus back to the user's target window before any
            // synthesized keystroke fires. The dictation overlay is a
            // Topmost / ShowActivated=False window, but on Wayland a
            // visible app-owned surface can still hold keyboard focus —
            // and when ydotool's virtual keyboard fires Ctrl+V it goes
            // to whatever has focus, so a still-visible overlay would
            // swallow the paste. wtype never sent a key on GNOME/KDE
            // (compositor-rejected), so this path was latent until the
            // ydotool backend went in.
            if (actionPlugin is null && !commandResult.CancelInsertion)
                await YieldFocusForInsertionAsync().ConfigureAwait(false);

            InsertionResult insertion;
            try
            {
                insertion = commandResult.CancelInsertion
                    ? InsertionResult.NoText
                    : actionPlugin is null
                    ? await _textInsertion.InsertTextAsync(new TextInsertionRequest(
                        Text: finalText,
                        AutoPaste: _settings.Current.AutoPaste,
                        TargetWindowId: context.WindowId,
                        TargetProcessName: context.AppProcess,
                        TargetWindowTitle: context.AppTitle,
                        AutoEnter: commandResult.AutoEnter,
                        Strategy: ResolveInsertionStrategy(context.AppProcess)))
                    : await ExecuteActionPluginAsync(actionPlugin, context, finalText, rawText, result?.DetectedLanguage, cancelToken);
            }
            catch (OperationCanceledException) when (cancelToken.IsCancellationRequested)
            {
                Trace.WriteLine(
                    $"[Dictation] Action canceled by user "
                    + $"(action='{actionPlugin?.ActionId ?? "<none>"}').");
                ReportStatus(context, "Canceled");
                ShowFeedback(context, "Canceled", isError: false);
                return;
            }
            catch (Exception ex)
            {
                // Insertion/action failures must NOT republish the dictation
                // as a transcription failure — TranscriptionCompletedEvent has
                // already fired. Surface a separate insertion-failure status.
                Trace.WriteLine(
                    $"[Dictation] Text insertion/action failed (target='{context.AppProcess}', "
                    + $"action='{actionPlugin?.ActionId ?? "<none>"}'): {ex}");
                ReportStatus(context, $"Insertion failed: {ex.Message}");
                ShowFeedback(context, "Insertion failed.", isError: true);
                return;
            }

            var completionMessage = insertion switch
            {
                InsertionResult.Pasted when commandResult.AutoEnter && finalText.Length == 0 => "Pressed Enter.",
                InsertionResult.Pasted => $"Typed {finalText.Length} char(s).",
                InsertionResult.Typed => $"Typed {finalText.Length} char(s).",
                InsertionResult.CopiedToClipboard => ClipboardFallbackMessage(),
                InsertionResult.ActionHandled => "Action completed.",
                InsertionResult.ActionFailed => "Action failed.",
                InsertionResult.MissingClipboardTool => ClipboardToolMissingMessage(),
                InsertionResult.MissingPasteTool => $"Text insertion failed. {_commands.GetSnapshot().PasteToolInstallHint}",
                InsertionResult.Failed => "Text insertion failed. Dictated text could not be copied or pasted.",
                InsertionResult.NoText when commandResult.CancelInsertion => "Dictation canceled.",
                _ => "Done.",
            };
            var isError = insertion is InsertionResult.Failed
                or InsertionResult.ActionFailed
                or InsertionResult.MissingClipboardTool
                or InsertionResult.MissingPasteTool;
            ReportStatus(context, completionMessage);
            ShowFeedback(
                context,
                completionMessage,
                isError: isError);

            if (insertion is InsertionResult.Pasted or InsertionResult.Typed or InsertionResult.CopiedToClipboard)
            {
                _models.PluginManager.EventBus.Publish(new TextInsertedEvent
                {
                    Text = finalText,
                    TargetApp = context.AppProcess
                });
            }

            var transcriptionId = Guid.NewGuid().ToString();
            var timestamp = context.RecordingStart == default ? DateTime.UtcNow : context.RecordingStart;
            _recentTranscriptions.RecordTranscription(
                transcriptionId,
                finalText,
                timestamp,
                context.AppTitle,
                context.AppProcess);

            // Write to history last so stats reflect the just-completed capture.
            if (_settings.Current.SaveToHistoryEnabled)
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
                    cleanupLevel);

            if (_settings.Current.MemoryEnabled)
                FireAndLog(() => _memory.ExtractAndStoreAsync(finalText), "memory extraction");
        }
        catch (OperationCanceledException) when (cancelToken.IsCancellationRequested)
        {
            // User pressed Escape while the post-processing pipeline (LLM
            // cleanup, translation, plugin processors) was running. Surface
            // "Canceled" rather than a transcription failure regardless of
            // whether TranscriptionCompletedEvent had fired.
            Trace.WriteLine("[Dictation] Pipeline canceled by user.");
            ReportStatus(context, "Canceled");
            ShowFeedback(context, "Canceled", isError: false);
        }
        catch (Exception ex) when (!transcriptionCompletedPublished)
        {
            // Failures BEFORE TranscriptionCompletedEvent fires (post-processing
            // pipeline, voice-command parsing, etc.) are still surfaced as
            // transcription failures. The dedicated TranscribeAsync and
            // insertion try/catches above handle their own phases without
            // reaching here.
            Trace.WriteLine($"[Dictation] Post-transcription processing failed: {ex}");
            _models.PluginManager.EventBus.Publish(new TranscriptionFailedEvent
            {
                ErrorMessage = ex.Message,
                ModelId = plugin.SelectedModelId,
                AppName = context.AppTitle
            });
            ReportStatus(context, $"Transcription failed: {ex.Message}");
            _speechFeedback.AnnounceError(ex.Message);
            ShowFeedback(context, "Transcription failed.", isError: true);
        }
        catch (Exception ex)
        {
            // Reached only if something after TranscriptionCompletedEvent (e.g.
            // history persistence) throws unexpectedly. Don't republish a Failed
            // event for a dictation we already announced as completed.
            Trace.WriteLine($"[Dictation] Post-completion bookkeeping failed: {ex}");
        }
        finally
        {
            _models.ScheduleAutoUnload();
        }
    }

    private PromptAction? ResolvePromptAction(RecordingContext context)
    {
        var promptActionId = context.Profile?.PromptActionId;
        if (string.IsNullOrWhiteSpace(promptActionId))
            return null;

        return _promptActions.EnabledActions.FirstOrDefault(action => action.Id == promptActionId);
    }

    private async Task<string> RunPromptActionAsync(
        RecordingContext context,
        PromptAction promptAction,
        string text,
        CancellationToken token)
    {
        try
        {
            var message = $"Running prompt action '{promptAction.Name}'...";
            Trace.WriteLine($"[Dictation] {message}");
            ReportStatus(context, message);
            return await _promptProcessing.ProcessAsync(promptAction, text, token);
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

    private CleanupLevel ResolveCleanupLevel(RecordingContext context, PromptAction? promptAction)
    {
        if (context.Profile is null)
            return _settings.Current.CleanupLevel;

        var style = ProfileStylePresetService.Resolve(context.Profile.StylePreset);
        var cleanupLevel = context.Profile.CleanupLevelOverride ?? style.CleanupLevel;

        // Profile prompt actions are already LLM transforms. Avoid running a
        // separate LLM cleanup pass first, because the action should receive
        // the dictated text, not another model's interpretation of it.
        return promptAction is not null && cleanupLevel > CleanupLevel.Light
            ? CleanupLevel.Light
            : cleanupLevel;
    }

    private string ApplyProfileStyleFormatting(RecordingContext context, string text)
    {
        if (context.Profile is null)
            return text;

        var style = ProfileStylePresetService.Resolve(context.Profile.StylePreset);
        var developerFormattingEnabled = context.Profile.DeveloperFormattingOverride
            ?? style.DeveloperFormattingEnabled;
        if (!developerFormattingEnabled)
            return text;

        var fileReference = _ideFileReferences.TryFormatReferenceCommand(text);
        return fileReference ?? _developerFormatting.Format(text);
    }

    private TextInsertionStrategy ResolveInsertionStrategy(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return TextInsertionStrategy.Auto;

        var strategies = _settings.Current.AppInsertionStrategies;
        if (strategies is null || strategies.Count == 0)
            return TextInsertionStrategy.Auto;

        var process = ProcessNameNormalizer.Normalize(processName);
        foreach (var entry in strategies)
        {
            if (string.Equals(entry.Key, processName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Key, process, StringComparison.OrdinalIgnoreCase))
                return entry.Value;
        }

        return TextInsertionStrategy.Auto;
    }

    private static string ClipboardToolMissingMessage() =>
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 }
            ? "Text insertion failed. Install wl-clipboard to enable clipboard insertion."
            : "Text insertion failed. Install xclip to enable clipboard insertion.";

    /// <summary>
    /// Reason-aware fallback notification for the
    /// <see cref="InsertionResult.CopiedToClipboard"/> branch. The detail
    /// comes from <see cref="TextInsertionService.LastFailureReason"/>,
    /// which the service sets on the same call that produced this result —
    /// so we can guide the user to the actual setup gap (e.g. ydotool not
    /// running) instead of the generic "paste with Ctrl+V" line.
    /// </summary>
    private string ClipboardFallbackMessage() => _textInsertion.LastFailureReason switch
    {
        InsertionFailureReason.WtypeCompositorUnsupported =>
            "Copied to clipboard. Compositor doesn't support direct typing — set up ydotool from Settings → Text insertion to enable auto-paste.",
        InsertionFailureReason.YdotoolSocketUnreachable =>
            "Copied to clipboard. ydotool socket not reachable — open Settings → Text insertion to check daemon status.",
        InsertionFailureReason.NoWaylandTypingTool =>
            $"Copied to clipboard. {_commands.GetSnapshot().PasteToolInstallHint}",
        InsertionFailureReason.FocusFailed =>
            "Copied to clipboard. Target window could not be focused for auto-paste — paste with Ctrl+V.",
        _ => "Copied to clipboard (paste with Ctrl+V).",
    };

    private IActionPlugin? ResolveActionPlugin(PromptAction? promptAction)
    {
        if (string.IsNullOrWhiteSpace(promptAction?.TargetActionPluginId))
            return null;

        return _models.PluginManager.ActionPlugins.FirstOrDefault(plugin =>
            string.Equals(plugin.PluginId, promptAction.TargetActionPluginId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(plugin.ActionId, promptAction.TargetActionPluginId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<InsertionResult> ExecuteActionPluginAsync(
        IActionPlugin actionPlugin,
        RecordingContext context,
        string inputText,
        string rawText,
        string? detectedLanguage,
        CancellationToken cancelToken)
    {
        var result = await actionPlugin.ExecuteAsync(
            inputText,
            new ActionContext(
                context.AppTitle,
                context.AppProcess,
                context.AppUrl,
                detectedLanguage,
                rawText),
            cancelToken);

        _models.PluginManager.EventBus.Publish(new ActionCompletedEvent
        {
            ActionId = actionPlugin.ActionId,
            Success = result.Success,
            Message = result.Message,
            AppName = context.AppTitle
        });

        if (!string.IsNullOrWhiteSpace(result.Message))
            ReportStatus(context, result.Message);

        return result.Success ? InsertionResult.ActionHandled : InsertionResult.ActionFailed;
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
            t => Trace.WriteLine($"[Dictation] {label} faulted: {t.Exception?.GetBaseException().Message}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private string BuildRecordingStartFailureMessage(Exception? ex)
    {
        var selectedDevice = ResolveSelectedInputDeviceForMessage();
        if (selectedDevice is null)
            return "Could not start recording. No microphone input device is available.";

        var baseMessage = $"Could not start recording from '{selectedDevice.Name}'.";
        var detail = ex?.Message;
        if (!string.IsNullOrWhiteSpace(detail))
            baseMessage += $" {detail}.";

        return IsBluetoothDeviceName(selectedDevice.Name)
            ? $"{baseMessage} Bluetooth headsets must be in a microphone-capable headset profile; switch the device input profile or choose another microphone."
            : $"{baseMessage} Choose another microphone or check the device input profile.";
    }

    private AudioInputDevice? ResolveSelectedInputDeviceForMessage()
    {
        try
        {
            return _audio.ResolveConfiguredDevice(
                _settings.Current.SelectedMicrophoneDevice,
                _settings.Current.SelectedMicrophoneDeviceId);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsBluetoothDeviceName(string name) =>
        name.Contains("airpod", StringComparison.OrdinalIgnoreCase)
        || name.Contains("bluetooth", StringComparison.OrdinalIgnoreCase)
        || name.Contains("bluez", StringComparison.OrdinalIgnoreCase)
        || name.Contains("headset", StringComparison.OrdinalIgnoreCase);

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
        CleanupLevel cleanupLevel)
    {
        try
        {
            var engine = _models.ActiveTranscriptionPlugin?.ProviderId ?? "unknown";
            var model = _models.ActiveTranscriptionPlugin?.SelectedModelId;
            var language = result?.DetectedLanguage
                ?? (_settings.Current.Language is { Length: > 0 } l && l != "auto" ? l : null);

            _history.AddRecord(new TranscriptionRecord
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
                ModelUsed = model,
                AudioFileName = Path.GetFileName(wavPath),
                InsertionStatus = ToTextInsertionStatus(insertion),
                InsertionFailureReason = InsertionFailureReasonFor(insertion),
                CleanupLevelUsed = cleanupLevel,
                CleanupApplied = WasPipelineStepChanged(pipelineResult, PostProcessingStepNames.Cleanup),
                SnippetApplied = WasPipelineStepChanged(pipelineResult, PostProcessingStepNames.Snippets),
                DictionaryCorrectionApplied = WasPipelineStepChanged(pipelineResult, PostProcessingStepNames.Dictionary),
                PromptActionApplied = WasPipelineStepSucceeded(pipelineResult, PostProcessingStepNames.Llm),
                TranslationApplied = WasPipelineStepChanged(pipelineResult, PostProcessingStepNames.Translation),
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Dictation] AddHistoryRecord failed: {ex.Message}");
        }
    }

    private static TextInsertionStatus ToTextInsertionStatus(InsertionResult insertion) =>
        insertion switch
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
            _ => TextInsertionStatus.Unknown,
        };

    private static bool WasPipelineStepChanged(PostProcessingResult result, string name) =>
        result.Steps.Any(step =>
            step.Changed && string.Equals(step.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool WasPipelineStepSucceeded(PostProcessingResult result, string name) =>
        result.Steps.Any(step =>
            step.Succeeded && string.Equals(step.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string? InsertionFailureReasonFor(InsertionResult insertion) =>
        insertion switch
        {
            InsertionResult.ActionFailed => "Action plugin failed.",
            InsertionResult.MissingClipboardTool => ClipboardToolMissingMessage(),
            InsertionResult.MissingPasteTool => "Automatic paste tool is unavailable.",
            InsertionResult.Failed => "Text insertion failed.",
            _ => null,
        };

    private void ReportStatus(string message)
    {
        StatusMessage?.Invoke(this, message);
        SetOverlayState(state => state with
        {
            IsOverlayVisible = true,
            StatusText = message,
            ShowFeedback = false,
            FeedbackText = null
        });
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
    private static Task YieldFocusForInsertionAsync() => Task.Delay(90);

    private static bool IsSameWindow(ActiveWindowSnapshot a, ActiveWindowSnapshot b)
    {
        if (a.WindowId is not null && b.WindowId is not null)
            return string.Equals(a.WindowId, b.WindowId, StringComparison.Ordinal);
        return string.Equals(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(a.AppId, b.AppId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <see cref="ReportStatus"/> variant that suppresses overlay/status updates
    /// once a newer dictation has taken over the overlay. The
    /// <see cref="StatusMessage"/> event still fires for observers that care
    /// about completion (history/log surfaces), but the visible overlay is left
    /// alone so the live recording's "Recording…" status is not clobbered.
    /// </summary>
    private void ReportStatus(RecordingContext context, string message)
    {
        StatusMessage?.Invoke(this, message);
        if (!IsContextStillOwningOverlay(context))
            return;
        SetOverlayState(state => state with
        {
            IsOverlayVisible = true,
            StatusText = message,
            ShowFeedback = false,
            FeedbackText = null
        });
    }

    private void ShowFeedback(string text, bool isError)
    {
        SetOverlayState(state => state with
        {
            IsOverlayVisible = false,
            ShowFeedback = true,
            FeedbackIsError = isError,
            FeedbackText = text,
            PartialText = null,
            IsRecording = false,
            ActiveProfileName = null,
            ActiveAppName = null,
            SessionStartedAtUtc = null
        });
    }

    /// <summary>
    /// <see cref="ShowFeedback"/> variant that no-ops once a newer dictation has
    /// taken over the overlay. Prevents the previous recording's terminal
    /// feedback ("Typed N char(s)", "Transcription failed", "Canceled") from
    /// hiding the new recording's overlay.
    /// </summary>
    private void ShowFeedback(RecordingContext context, string text, bool isError)
    {
        if (!IsContextStillOwningOverlay(context))
            return;
        ShowFeedback(text, isError);
    }

    /// <summary>
    /// True if no newer dictation has started since the context was captured.
    /// StopAsync increments <c>_recordingSession</c> exactly once at the
    /// transition out of recording, so the just-stopped context is still
    /// "current" while <c>_recordingSession == context.SessionId + 1</c>. Any
    /// higher value means a subsequent StartAsync has claimed the overlay.
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
                _audio.StopRecording();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Dictation] Failed to stop recording during start rollback: {ex.Message}");
        }

        try { _audioDucking.RestoreAudio(); }
        catch (Exception ex) { Trace.WriteLine($"[Dictation] Failed to restore audio during start rollback: {ex.Message}"); }

        try { _mediaPause.ResumeMedia(); }
        catch (Exception ex) { Trace.WriteLine($"[Dictation] Failed to resume media during start rollback: {ex.Message}"); }

        RecordingStateChanged?.Invoke(this, false);
        SetOverlayState(state => state with
        {
            IsOverlayVisible = false,
            ShowFeedback = false,
            FeedbackIsError = false,
            FeedbackText = null,
            PartialText = null,
            IsRecording = false,
            StatusText = "Ready",
            ActiveProfileName = null,
            ActiveAppName = null,
            SessionStartedAtUtc = null
        });
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_toggleHandler is not null) _hotkey.DictationToggleRequested -= _toggleHandler;
        if (_startHandler is not null) _hotkey.DictationStartRequested -= _startHandler;
        if (_stopHandler is not null) _hotkey.DictationStopRequested -= _stopHandler;
        if (_cancelHandler is not null) _hotkey.CancelRequested -= _cancelHandler;
        if (_hookFailedHandler is not null) _hotkey.HookFailed -= _hookFailedHandler;

        // If we're shutting down mid-recording, the audio capture is still
        // active and ducking/media-pause are still applied. Stop the capture
        // and undo the playback effects before tearing down anything else,
        // otherwise the user is left with a muted system after exit.
        if (_audio.IsRecording)
        {
            try { _audio.StopRecording(); }
            catch (Exception ex) { Trace.WriteLine($"[Dictation] StopRecording during dispose failed: {ex.Message}"); }

            try { _audioDucking.RestoreAudio(); }
            catch (Exception ex) { Trace.WriteLine($"[Dictation] RestoreAudio during dispose failed: {ex.Message}"); }

            try { _mediaPause.ResumeMedia(); }
            catch (Exception ex) { Trace.WriteLine($"[Dictation] ResumeMedia during dispose failed: {ex.Message}"); }
        }

        ShutdownPartialTranscriptionSession();
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

    private void StartPartialTranscriptionSession()
    {
        _partialTranscriptionCts?.Cancel();
        _partialTranscriptionCts?.Dispose();

        _lastPublishedPartialText = null;
        var sessionVersion = _partialTranscriptState.StartSession();
        var cts = new CancellationTokenSource();
        _partialTranscriptionCts = cts;
        _partialTranscriptionTask = Task.Run(() => RunPartialTranscriptionLoopAsync(sessionVersion, cts.Token));
    }

    private async Task StopPartialTranscriptionSessionAsync()
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
                await task.WaitAsync(TimeSpan.FromMilliseconds(500));
            }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Dictation] Partial transcription shutdown failed: {ex.Message}");
            }
        }

        _partialTranscriptState.StopSession();
    }

    private async Task AwaitRecordingSnapshotAsync()
    {
        var snapshotTask = _recordingSnapshotTask;
        _recordingSnapshotTask = null;
        if (snapshotTask is null)
            return;

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
                Trace.WriteLine($"[Dictation] Partial transcription dispose wait failed: {ex.Message}");
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
                    _lastSpeechDetectedAtUtc = DateTime.UtcNow;
                else if (ShouldAutoStopForSilence())
                {
                    _silenceStopRequested = true;
                    ReportStatus("Silence detected. Stopping…");
                    FireAndLog(() => Task.Run(StopAsync), "silence auto-stop");
                    return;
                }

                if (DateTime.UtcNow >= nextPartialPollAtUtc)
                {
                    var wav = _audio.GetCurrentBuffer();
                    var plugin = _models.ActiveTranscriptionPlugin;
                    if (wav is not null
                        && wav.Length > 44
                        && plugin is not null
                        && _audio.HasSpeechEnergy)
                    {
                        await PollPartialTranscriptOnceAsync(plugin, wav, sessionVersion, ct);
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
            return false;

        var timeoutSeconds = _settings.Current.SilenceAutoStopSeconds;
        if (timeoutSeconds <= 0)
            return false;

        return DateTime.UtcNow - _lastSpeechDetectedAtUtc >= TimeSpan.FromSeconds(timeoutSeconds);
    }

    private async Task PollPartialTranscriptOnceAsync(
        ITranscriptionEnginePlugin plugin,
        byte[] wav,
        int sessionVersion,
        CancellationToken ct)
    {
        var effectiveLanguage = _recordingProfile?.InputLanguage ?? _settings.Current.Language;
        var languageHint = effectiveLanguage is { Length: > 0 } lang && lang != "auto" ? lang : null;
        var translate = string.Equals(
            _recordingProfile?.SelectedTask ?? _settings.Current.TranscriptionTask,
            "translate",
            StringComparison.OrdinalIgnoreCase);

        try
        {
            var result = await plugin.TranscribeStreamingAsync(
                wav,
                languageHint,
                translate,
                prompt: null,
                onProgress: partial =>
                {
                    TryPublishPartialTranscript(sessionVersion, partial);
                    return !ct.IsCancellationRequested && _audio.IsRecording;
                },
                ct);

            TryPublishPartialTranscript(sessionVersion, result.Text);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Dictation] Partial transcription polling failed: {ex.Message}");
        }
    }

    private void TryPublishPartialTranscript(int sessionVersion, string? text)
    {
        if (!_partialTranscriptState.TryApplyPolling(
                sessionVersion,
                text ?? "",
                _dictionary.ApplyCorrections,
                out var partialText))
        {
            return;
        }

        if (string.Equals(_lastPublishedPartialText, partialText, StringComparison.Ordinal))
            return;

        _lastPublishedPartialText = partialText;
        _models.PluginManager.EventBus.Publish(new PartialTranscriptionUpdateEvent
        {
            PartialText = partialText,
            IsRecording = _audio.IsRecording,
            ElapsedSeconds = _recordingStart == default
                ? 0
                : Math.Max(0, (DateTime.UtcNow - _recordingStart).TotalSeconds)
        });

        SetOverlayState(state => state with
        {
            PartialText = partialText
        });
    }
}
