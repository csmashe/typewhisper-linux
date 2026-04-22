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
    private readonly IPostProcessingPipeline _pipeline;
    private readonly ITranslationService _translation;
    private readonly PromptProcessingService _promptProcessing;
    private readonly StreamingTranscriptState _partialTranscriptState = new();
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
    private string? _lastPublishedPartialText;
    private bool _initialized;
    private bool _disposed;

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
        IPostProcessingPipeline pipeline,
        ITranslationService translation,
        PromptProcessingService promptProcessing)
    {
        _hotkey = hotkey;
        _audio = audio;
        _sessionAudioFiles = sessionAudioFiles;
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
        _pipeline = pipeline;
        _translation = translation;
        _promptProcessing = promptProcessing;
    }

    public void Initialize()
    {
        if (_initialized || _disposed) return;
        _hotkey.DictationToggleRequested += (_, _) => _ = ToggleAsync();
        _hotkey.DictationStartRequested += (_, _) => _ = StartAsync();
        _hotkey.DictationStopRequested += (_, _) => _ = StopAsync();
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
        if (!await _toggleGate.WaitAsync(0)) return;
        try
        {
            if (_audio.IsRecording) return;

            _audio.WhisperModeEnabled = _settings.Current.WhisperModeEnabled;

            // Start capturing audio immediately — the user's finger is on the
            // key and they may already be speaking (especially in PTT).
            _recordingStart = DateTime.UtcNow;
            _audio.StartRecording();
            if (!_audio.IsRecording)
                return;

            if (_settings.Current.AudioDuckingEnabled)
                _audioDucking.DuckAudio(_settings.Current.AudioDuckingLevel);
            if (_settings.Current.PauseMediaDuringRecording)
                _mediaPause.PauseMedia();
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
        finally
        {
            _toggleGate.Release();
        }

        // Snapshot the active window off the hot path. Each xdotool call is a
        // subprocess (~100 ms); doing them synchronously before StartRecording
        // clipped the first chunk of speech on PTT. This races the user's
        // speech but even worst case we'll have the metadata by the time
        // transcription completes.
        _recordingAppProcess = null;
        _recordingAppTitle = null;
        _recordingAppUrl = null;
        _recordingWindowId = _activeWindow.GetActiveWindowId();
        _recordingProfile = null;
        _ = Task.Run(() =>
        {
            try
            {
                _recordingAppProcess = _activeWindow.GetActiveWindowProcessName();
                _recordingAppTitle = _activeWindow.GetActiveWindowTitle();
                _recordingAppUrl = _activeWindow.GetBrowserUrl();
                _recordingProfile = _profiles.MatchProfile(_recordingAppProcess, _recordingAppUrl);
                // Keep recording start hot, then converge to the effective
                // Windows-style profile->global whisper-mode precedence as
                // soon as context matching finishes.
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
    }

    public async Task StopAsync()
    {
        if (!await _toggleGate.WaitAsync(0)) return;
        try
        {
            if (!_audio.IsRecording) return;

            var wav = _audio.StopRecording();
            await StopPartialTranscriptionSessionAsync();
            _audioDucking.RestoreAudio();
            _mediaPause.ResumeMedia();
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
            var duration = ComputeDurationSeconds(wav);
            _models.PluginManager.EventBus.Publish(new RecordingStoppedEvent
            {
                DurationSeconds = duration
            });
            if (wav.Length == 0) return;

            var path = _sessionAudioFiles.SaveDictationCapture(wav);
            RecordingCaptured?.Invoke(this, path);
            Trace.WriteLine($"[Dictation] Captured → {path} ({wav.Length} bytes)");

            await TranscribeAndInsertAsync(wav, path, duration);
        }
        finally
        {
            _audioDucking.RestoreAudio();
            _mediaPause.ResumeMedia();
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
                    SnippetExpander = text => _snippets.ApplySnippets(text),
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

            var finalText = pipelineResult.Text;

            TranscriptionCompleted?.Invoke(this, finalText);
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
            var insertion = actionPlugin is null
                ? await _textInsertion.InsertTextAsync(finalText, _settings.Current.AutoPaste, _recordingWindowId)
                : await ExecuteActionPluginAsync(actionPlugin, finalText, rawText, result?.DetectedLanguage);

            var completionMessage = insertion switch
            {
                InsertionResult.Pasted => $"Typed {finalText.Length} char(s).",
                InsertionResult.CopiedToClipboard => "Copied to clipboard (paste with Ctrl+V).",
                InsertionResult.ActionHandled => "Action completed.",
                InsertionResult.Failed => "Text insertion failed — is xdotool installed?",
                _ => "Done.",
            };
            ReportStatus(completionMessage);
            ShowFeedback(
                insertion is InsertionResult.Failed ? completionMessage : "Dictation completed.",
                isError: insertion is InsertionResult.Failed);

            if (insertion is InsertionResult.Pasted or InsertionResult.CopiedToClipboard)
            {
                _models.PluginManager.EventBus.Publish(new TextInsertedEvent
                {
                    Text = finalText,
                    TargetApp = _recordingAppProcess
                });
            }

            // Write to history last so stats reflect the just-completed capture.
            if (_settings.Current.SaveToHistoryEnabled)
                AddHistoryRecord(rawText, finalText, duration, result, wavPath);
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

        return InsertionResult.ActionHandled;
    }

    private void AddHistoryRecord(string rawText, string finalText, double duration,
        PluginTranscriptionResult? result, string wavPath)
    {
        try
        {
            var engine = _models.ActiveTranscriptionPlugin?.ProviderId ?? "unknown";
            var model = _models.ActiveTranscriptionPlugin?.SelectedModelId;
            var language = result?.DetectedLanguage
                ?? (_settings.Current.Language is { Length: > 0 } l && l != "auto" ? l : null);

            _history.AddRecord(new TranscriptionRecord
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = _recordingStart == default ? DateTime.UtcNow : _recordingStart,
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
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Dictation] AddHistoryRecord failed: {ex.Message}");
        }
    }

    private static double ComputeDurationSeconds(byte[] wav)
    {
        // WAV: 44-byte standard PCM header, 16-bit mono at 16kHz → 32000 bytes/sec.
        if (wav.Length <= 44) return 0;
        return (wav.Length - 44) / 32000.0;
    }

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

    private void SetOverlayState(Func<DictationOverlayState, DictationOverlayState> updater)
    {
        _overlayState = updater(_overlayState);
        OverlayStateChanged?.Invoke(this, _overlayState);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _partialTranscriptionCts?.Cancel();
        _partialTranscriptionCts?.Dispose();
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

    private async Task RunPartialTranscriptionLoopAsync(int sessionVersion, CancellationToken ct)
    {
        var pollInterval = TimeSpan.FromSeconds(3);

        try
        {
            await Task.Delay(pollInterval, ct);

            while (!ct.IsCancellationRequested && _audio.IsRecording)
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

                await Task.Delay(pollInterval, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Dictation] Partial transcription loop failed: {ex.Message}");
        }
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
