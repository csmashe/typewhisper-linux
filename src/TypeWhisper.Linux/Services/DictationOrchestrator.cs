using System.Diagnostics;
using System.IO;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// Glues hotkey, recorder, transcription engine, post-processing, and text
/// injection into a single dictation loop:
///   hotkey → start recording → hotkey → stop → save WAV → transcribe via
///   the active transcription plugin → apply dictionary + snippets →
///   xdotool types the result into the focused window → history record.
///
/// If no transcription plugin/model is loaded the WAV is still written so
/// the user can inspect what was captured.
/// </summary>
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
    private readonly StreamingTranscriptState _partialTranscriptState = new();
    private readonly VoiceCommandParser _voiceCommands = new();
    private readonly DeveloperFormattingService _developerFormatting = new();
    private readonly SemaphoreSlim _toggleGate = new(1, 1);
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
    private string? _lastPublishedPartialText;
    private DateTime _lastSpeechDetectedAtUtc;
    private bool _silenceStopRequested;
    private bool _initialized;
    private bool _disposed;

    private EventHandler? _toggleHandler;
    private EventHandler? _startHandler;
    private EventHandler? _stopHandler;
    private EventHandler<string>? _hookFailedHandler;

    public event EventHandler<string>? RecordingCaptured; // arg = WAV file path
    public event EventHandler<bool>? RecordingStateChanged;
    public event EventHandler<string>? TranscriptionCompleted;
    public event EventHandler<string>? StatusMessage;
    public event EventHandler<DictationOverlayState>? OverlayStateChanged;

    public bool IsRecording => _audio.IsRecording;

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
        IdeFileReferenceService ideFileReferences)
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
    }

    public void Initialize()
    {
        if (_initialized || _disposed) return;
        _toggleHandler = (_, _) => FireAndLog(ToggleAsync, nameof(ToggleAsync));
        _startHandler = (_, _) => FireAndLog(StartAsync, nameof(StartAsync));
        _stopHandler = (_, _) => FireAndLog(StopAsync, nameof(StopAsync));
        _hookFailedHandler = (_, message) =>
        {
            Trace.WriteLine($"[Dictation] Hotkey hook unavailable: {message}");
            ReportStatus("Global hotkey disabled.");
            ShowFeedback("Global hotkey disabled. Check libuiohook/X11 permissions.", isError: true);
        };
        _hotkey.DictationToggleRequested += _toggleHandler;
        _hotkey.DictationStartRequested += _startHandler;
        _hotkey.DictationStopRequested += _stopHandler;
        _hotkey.HookFailed += _hookFailedHandler;
        _hotkey.Initialize();
        _initialized = true;
    }

    public async Task ToggleAsync()
    {
        if (_audio.IsRecording) await StopAsync();
        else await StartAsync();
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

            // Publish the snapshot task before releasing the toggle gate so a
            // near-immediate StopAsync can reliably observe and await it.
            _recordingAppProcess = null;
            _recordingAppTitle = null;
            _recordingAppUrl = null;
            _recordingWindowId = _activeWindow.GetActiveWindowId();
            _recordingProfile = null;
            recordingSnapshotTask = Task.Run(() =>
            {
                try
                {
                    _recordingAppProcess = _activeWindow.GetActiveWindowProcessName();
                    _recordingAppTitle = _activeWindow.GetActiveWindowTitle();
                    _recordingAppUrl = _activeWindow.GetBrowserUrl();
                    _recordingProfile = _profiles.MatchProfile(_recordingAppProcess, _recordingAppUrl);
                    _audio.WhisperModeEnabled =
                        _recordingProfile?.WhisperModeOverride ?? _settings.Current.WhisperModeEnabled;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Dictation] Active-window snapshot failed: {ex.Message}");
                }
                finally
                {
                    SetOverlayState(state => state with
                    {
                        ActiveProfileName = _recordingProfile?.Name,
                        ActiveAppName = _recordingAppTitle
                    });
                    _models.PluginManager.EventBus.Publish(new RecordingStartedEvent
                    {
                        AppName = _recordingAppTitle,
                        AppProcessName = _recordingAppProcess
                    });
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
        try
        {
            if (!_audio.IsRecording) return;

            var wav = await _audio.StopRecordingAsync();
            await StopPartialTranscriptionSessionAsync();
            await AwaitRecordingSnapshotAsync();
            _audioDucking.RestoreAudio();
            _mediaPause.ResumeMedia();
            earlyCleanupDone = true;
            if (_settings.Current.SoundFeedbackEnabled)
                _soundFeedback.PlayRecordingStopped();
            RecordingStateChanged?.Invoke(this, false);
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

            await TranscribeAndInsertAsync(wav, path, duration);
        }
        finally
        {
            if (!earlyCleanupDone)
            {
                _audioDucking.RestoreAudio();
                _mediaPause.ResumeMedia();
            }
            _toggleGate.Release();
        }
    }

    private async Task TranscribeAndInsertAsync(byte[] wav, string wavPath, double duration)
    {
        var effectiveModelId = _recordingProfile?.TranscriptionModelOverride ?? _settings.Current.SelectedModelId;
        if (!string.IsNullOrWhiteSpace(effectiveModelId) && _models.ActiveModelId != effectiveModelId)
        {
            try
            {
                var loaded = await _models.EnsureModelLoadedAsync(effectiveModelId);
                if (!loaded)
                {
                    ReportStatus($"Configured model '{effectiveModelId}' is not available.");
                    ShowFeedback("Model unavailable.", isError: true);
                    return;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Dictation] Failed to load effective model '{effectiveModelId}': {ex}");
                ReportStatus($"Failed to load configured model: {ex.Message}");
                ShowFeedback("Model load failed.", isError: true);
                return;
            }
        }

        var plugin = _models.ActiveTranscriptionPlugin;
        if (plugin is null)
        {
            ReportStatus("No transcription model loaded. WAV saved for review.");
            ShowFeedback("No transcription model loaded.", isError: true);
            return;
        }

        ReportStatus($"Transcribing via {plugin.ProviderDisplayName}…");

        try
        {
            var effectiveLanguage = _recordingProfile?.InputLanguage ?? _settings.Current.Language;
            var languageHint = effectiveLanguage is { Length: > 0 } lang && lang != "auto" ? lang : null;
            var translate = string.Equals(
                _recordingProfile?.SelectedTask ?? _settings.Current.TranscriptionTask,
                "translate",
                StringComparison.OrdinalIgnoreCase);

            var result = await plugin.TranscribeAsync(
                wavAudio: wav, language: languageHint, translate: translate,
                prompt: null, ct: CancellationToken.None);

            var rawText = result?.Text?.Trim();
            if (string.IsNullOrEmpty(rawText))
            {
                ReportStatus("Transcription returned no text.");
                ShowFeedback("Transcription returned no text.", isError: true);
                return;
            }

            if (result?.NoSpeechProbability is > 0.8f && !_settings.Current.TranscribeShortQuietClipsAggressively)
            {
                ReportStatus("No speech detected.");
                ShowFeedback("No speech detected.", isError: true);
                return;
            }

            var pipelineContext = new PostProcessingContext
            {
                SourceLanguage = result?.DetectedLanguage ?? languageHint,
                ActiveAppName = _recordingAppTitle,
                ActiveAppProcessName = _recordingAppProcess,
                ProfileName = _recordingProfile?.Name,
                AudioDurationSeconds = duration
            };

            var promptAction = ResolvePromptAction();
            var translationTarget = _recordingProfile?.TranslationTarget ?? _settings.Current.TranslationTargetLanguage;
            var cleanupLevel = ResolveCleanupLevel();

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
                    TargetProcessName = _recordingAppProcess,
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
                                ReportStatus(message);
                                return Task.CompletedTask;
                            },
                            token),
                    SnippetExpander = text => _snippets.ApplySnippets(text, profileId: _recordingProfile?.Id),
                    LlmHandler = promptAction is not null
                        ? (text, token) => _promptProcessing.ProcessAsync(promptAction, text, token)
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
                        ReportStatus(status == "AI"
                            ? "Processing prompt action…"
                            : $"Processing {status}…");
                        return Task.CompletedTask;
                    }
                },
                CancellationToken.None);

            var commandResult = _voiceCommands.Parse(pipelineResult.Text);
            var finalText = ApplyProfileStyleFormatting(commandResult.Text);

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
                ProfileName = _recordingProfile?.Name,
                AppName = _recordingAppTitle,
                AppProcessName = _recordingAppProcess,
                Url = _recordingAppUrl
            });

            var actionPlugin = ResolveActionPlugin(promptAction);
            var insertion = commandResult.CancelInsertion
                ? InsertionResult.NoText
                : actionPlugin is null
                ? await _textInsertion.InsertTextAsync(new TextInsertionRequest(
                    Text: finalText,
                    AutoPaste: _settings.Current.AutoPaste,
                    TargetWindowId: _recordingWindowId,
                    TargetProcessName: _recordingAppProcess,
                    TargetWindowTitle: _recordingAppTitle,
                    AutoEnter: commandResult.AutoEnter,
                    Strategy: ResolveInsertionStrategy(_recordingAppProcess)))
                : await ExecuteActionPluginAsync(actionPlugin, finalText, rawText, result?.DetectedLanguage);

            var completionMessage = insertion switch
            {
                InsertionResult.Pasted when commandResult.AutoEnter && finalText.Length == 0 => "Pressed Enter.",
                InsertionResult.Pasted => $"Typed {finalText.Length} char(s).",
                InsertionResult.Typed => $"Typed {finalText.Length} char(s).",
                InsertionResult.CopiedToClipboard => "Copied to clipboard (paste with Ctrl+V).",
                InsertionResult.ActionHandled => "Action completed.",
                InsertionResult.ActionFailed => "Action failed.",
                InsertionResult.MissingClipboardTool => ClipboardToolMissingMessage(),
                InsertionResult.MissingPasteTool => "Text insertion failed. Install xdotool to enable automatic paste.",
                InsertionResult.Failed => "Text insertion failed. Dictated text could not be copied or pasted.",
                InsertionResult.NoText when commandResult.CancelInsertion => "Dictation canceled.",
                _ => "Done.",
            };
            var isError = insertion is InsertionResult.Failed
                or InsertionResult.ActionFailed
                or InsertionResult.MissingClipboardTool
                or InsertionResult.MissingPasteTool;
            ReportStatus(completionMessage);
            ShowFeedback(
                completionMessage,
                isError: isError);

            if (insertion is InsertionResult.Pasted or InsertionResult.Typed or InsertionResult.CopiedToClipboard)
            {
                _models.PluginManager.EventBus.Publish(new TextInsertedEvent
                {
                    Text = finalText,
                    TargetApp = _recordingAppProcess
                });
            }

            var transcriptionId = Guid.NewGuid().ToString();
            var timestamp = _recordingStart == default ? DateTime.UtcNow : _recordingStart;
            _recentTranscriptions.RecordTranscription(
                transcriptionId,
                finalText,
                timestamp,
                _recordingAppTitle,
                _recordingAppProcess);

            // Write to history last so stats reflect the just-completed capture.
            if (_settings.Current.SaveToHistoryEnabled)
                AddHistoryRecord(
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
        catch (Exception ex)
        {
            Trace.WriteLine($"[Dictation] Transcription failed: {ex}");
            _models.PluginManager.EventBus.Publish(new TranscriptionFailedEvent
            {
                ErrorMessage = ex.Message,
                ModelId = plugin.SelectedModelId,
                AppName = _recordingAppTitle
            });
            ReportStatus($"Transcription failed: {ex.Message}");
            _speechFeedback.AnnounceError(ex.Message);
            ShowFeedback("Transcription failed.", isError: true);
        }
        finally
        {
            _models.ScheduleAutoUnload();
        }
    }

    private PromptAction? ResolvePromptAction()
    {
        var promptActionId = _recordingProfile?.PromptActionId;
        if (string.IsNullOrWhiteSpace(promptActionId))
            return null;

        return _promptActions.EnabledActions.FirstOrDefault(action => action.Id == promptActionId);
    }

    private CleanupLevel ResolveCleanupLevel()
    {
        if (_recordingProfile is null)
            return _settings.Current.CleanupLevel;

        var style = ProfileStylePresetService.Resolve(_recordingProfile.StylePreset);
        return _recordingProfile.CleanupLevelOverride ?? style.CleanupLevel;
    }

    private string ApplyProfileStyleFormatting(string text)
    {
        if (_recordingProfile is null)
            return text;

        var style = ProfileStylePresetService.Resolve(_recordingProfile.StylePreset);
        var developerFormattingEnabled = _recordingProfile.DeveloperFormattingOverride
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
        string inputText,
        string rawText,
        string? detectedLanguage)
    {
        var result = await actionPlugin.ExecuteAsync(
            inputText,
            new ActionContext(
                _recordingAppTitle,
                _recordingAppProcess,
                _recordingAppUrl,
                detectedLanguage,
                rawText),
            CancellationToken.None);

        _models.PluginManager.EventBus.Publish(new ActionCompletedEvent
        {
            ActionId = actionPlugin.ActionId,
            Success = result.Success,
            Message = result.Message,
            AppName = _recordingAppTitle
        });

        if (!string.IsNullOrWhiteSpace(result.Message))
            ReportStatus(result.Message);

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
                AppName = _recordingAppTitle,
                AppProcessName = _recordingAppProcess,
                AppUrl = _recordingAppUrl,
                DurationSeconds = duration,
                Language = language,
                ProfileName = _recordingProfile?.Name,
                EngineUsed = engine,
                ModelUsed = model,
                AudioFileName = Path.GetFileName(wavPath),
                InsertionStatus = ToTextInsertionStatus(insertion),
                InsertionFailureReason = InsertionFailureReasonFor(insertion),
                CleanupLevelUsed = cleanupLevel,
                CleanupApplied = WasPipelineStepChanged(pipelineResult, PostProcessingStepNames.Cleanup),
                SnippetApplied = WasPipelineStepChanged(pipelineResult, PostProcessingStepNames.Snippets),
                DictionaryCorrectionApplied = WasPipelineStepChanged(pipelineResult, PostProcessingStepNames.Dictionary),
                PromptActionApplied = WasPipelineStepChanged(pipelineResult, PostProcessingStepNames.Llm),
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
        _overlayState = updater(_overlayState);
        OverlayStateChanged?.Invoke(this, _overlayState);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_toggleHandler is not null) _hotkey.DictationToggleRequested -= _toggleHandler;
        if (_startHandler is not null) _hotkey.DictationStartRequested -= _startHandler;
        if (_stopHandler is not null) _hotkey.DictationStopRequested -= _stopHandler;
        if (_hookFailedHandler is not null) _hotkey.HookFailed -= _hookFailedHandler;

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
            await snapshotTask.WaitAsync(TimeSpan.FromMilliseconds(500));
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
