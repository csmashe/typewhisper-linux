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
    private readonly TextInsertionService _textInsertion;
    private readonly IAudioDuckingService _audioDucking;
    private readonly IMediaPauseService _mediaPause;
    private readonly ModelManagerService _models;
    private readonly IHistoryService _history;
    private readonly ISettingsService _settings;
    private readonly IActiveWindowService _activeWindow;
    private readonly IProfileService _profiles;
    private readonly IPromptActionService _promptActions;
    private readonly IDictionaryService _dictionary;
    private readonly ISnippetService _snippets;
    private readonly IVocabularyBoostingService _vocabularyBoosting;
    private readonly IPostProcessingPipeline _pipeline;
    private readonly PromptProcessingService _promptProcessing;
    private readonly SemaphoreSlim _toggleGate = new(1, 1);
    private DateTime _recordingStart;
    private string? _recordingAppProcess;
    private string? _recordingAppTitle;
    private string? _recordingAppUrl;
    private Profile? _recordingProfile;
    private bool _initialized;
    private bool _disposed;

    public event EventHandler<string>? RecordingCaptured; // arg = WAV file path
    public event EventHandler<bool>? RecordingStateChanged;
    public event EventHandler<string>? TranscriptionCompleted;
    public event EventHandler<string>? StatusMessage;

    public bool IsRecording => _audio.IsRecording;

    public DictationOrchestrator(
        HotkeyService hotkey,
        AudioRecordingService audio,
        TextInsertionService textInsertion,
        IAudioDuckingService audioDucking,
        IMediaPauseService mediaPause,
        ModelManagerService models,
        IHistoryService history,
        ISettingsService settings,
        IActiveWindowService activeWindow,
        IProfileService profiles,
        IPromptActionService promptActions,
        IDictionaryService dictionary,
        ISnippetService snippets,
        IVocabularyBoostingService vocabularyBoosting,
        IPostProcessingPipeline pipeline,
        PromptProcessingService promptProcessing)
    {
        _hotkey = hotkey;
        _audio = audio;
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
        _recordingProfile = null;
        _ = Task.Run(() =>
        {
            try
            {
                _recordingAppProcess = _activeWindow.GetActiveWindowProcessName();
                _recordingAppTitle = _activeWindow.GetActiveWindowTitle();
                _recordingAppUrl = _activeWindow.GetBrowserUrl();
                _recordingProfile = _profiles.MatchProfile(_recordingAppProcess, _recordingAppUrl);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Dictation] Active-window snapshot failed: {ex.Message}");
            }
            finally
            {
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
            _audioDucking.RestoreAudio();
            _mediaPause.ResumeMedia();
            RecordingStateChanged?.Invoke(this, false);
            var duration = ComputeDurationSeconds(wav);
            _models.PluginManager.EventBus.Publish(new RecordingStoppedEvent
            {
                DurationSeconds = duration
            });
            if (wav.Length == 0) return;

            var path = SaveWav(wav);
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
                    StatusMessage?.Invoke(this, $"Configured model '{effectiveModelId}' is not available.");
                    return;
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Dictation] Failed to load effective model '{effectiveModelId}': {ex}");
                StatusMessage?.Invoke(this, $"Failed to load configured model: {ex.Message}");
                return;
            }
        }

        var plugin = _models.ActiveTranscriptionPlugin;
        if (plugin is null)
        {
            StatusMessage?.Invoke(this, "No transcription model loaded. WAV saved for review.");
            return;
        }

        StatusMessage?.Invoke(this, $"Transcribing via {plugin.ProviderDisplayName}…");

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
                StatusMessage?.Invoke(this, "Transcription returned no text.");
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
                    EffectiveSourceLanguage = languageHint,
                    DetectedLanguage = result?.DetectedLanguage,
                    PluginPostProcessors = pluginProcessors,
                    StatusCallback = status =>
                    {
                        StatusMessage?.Invoke(this, status == "AI"
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
                ? await _textInsertion.InsertTextAsync(finalText, _settings.Current.AutoPaste)
                : await ExecuteActionPluginAsync(actionPlugin, finalText, rawText, result?.DetectedLanguage);

            StatusMessage?.Invoke(this, insertion switch
            {
                InsertionResult.Pasted => $"Typed {finalText.Length} char(s).",
                InsertionResult.CopiedToClipboard => "Copied to clipboard (paste with Ctrl+V).",
                InsertionResult.ActionHandled => "Action completed.",
                InsertionResult.Failed => "Text insertion failed — is xdotool installed?",
                _ => "Done.",
            });

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
            StatusMessage?.Invoke(this, $"Transcription failed: {ex.Message}");
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
            StatusMessage?.Invoke(this, result.Message);

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

    private static string SaveWav(byte[] wav)
    {
        Directory.CreateDirectory(TypeWhisperEnvironment.AudioPath);
        var name = $"dictation-{DateTime.Now:yyyyMMdd-HHmmss}.wav";
        var path = Path.Combine(TypeWhisperEnvironment.AudioPath, name);
        File.WriteAllBytes(path, wav);
        return path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _toggleGate.Dispose();
    }
}
