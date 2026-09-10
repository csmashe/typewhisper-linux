using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services;

public sealed partial class DictationOrchestrator
{
    private readonly Lock _recoveryLock = new();
    private CancellationTokenSource? _recoveryCts;
    private TaskCompletionSource? _recoveryCompletion;

    private PipelineOptions BuildPipelineOptions(
        RecordingContext context,
        double duration,
        string? postProcessingLanguage,
        string? configuredLanguage,
        IReadOnlyList<string> languageHints,
        bool translate,
        bool engineSupportsTranslation,
        bool usedPreviewFallback
    )
    {
        var pipelineContext = new PostProcessingContext
        {
            SourceLanguage = postProcessingLanguage,
            ActiveAppName = context.AppTitle,
            ActiveAppProcessName = context.AppProcess,
            ProfileName = context.Profile?.Name,
            AudioDurationSeconds = duration,
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

        return new PipelineOptions
        {
            NormalizeSpokenLineBreaks = true,
            NormalizeSpokenPunctuation = true,
            AppFormatter = AppFormatterService.Format,
            TargetProcessName = context.AppProcess,
            DictionaryCorrector = SelectFinalDictionaryCorrector(
                usedPreviewFallback,
                _dictionary.ApplyCorrections
            ),
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
            RequireTranslationSuccess = !string.IsNullOrWhiteSpace(translationTarget),
            EffectiveSourceLanguage = postProcessingLanguage,
            DetectedLanguage = postProcessingLanguage,
            // Same rule as postProcessingLanguage above: an engine that ignores the
            // translate task returns source-language text, and reporting Translate
            // would make number normalization treat it as English.
            TranscriptionTask =
                translate && engineSupportsTranslation
                    ? TranscriptionTask.Translate
                    : TranscriptionTask.Transcribe,
            ConfiguredLanguage = configuredLanguage,
            ConfiguredLanguageCandidates = languageHints,
            TranscriptionNumberNormalizationEnabled =
                _settings.Current.TranscriptionNumberNormalizationEnabled,
            EnglishOutputVariant = _settings.Current.EnglishOutputVariant,
            GermanOutputVariant = _settings.Current.GermanOutputVariant,
            ShortUtterancePunctuationEnabled = _settings.Current.ShortUtterancePunctuationEnabled,
            PluginPostProcessors = pluginProcessors,
            StatusCallback = status =>
            {
                ReportStatus(
                    context,
                    status == "AI" ? "Processing prompt action…" : $"Processing {status}…"
                );
                return Task.CompletedTask;
            },
        };
    }

    private string ResolveTranscriptionTask(RecordingContext context) =>
        string.Equals(context.TranscriptionTaskUsed ?? context.Profile?.SelectedTask ?? _settings.Current.TranscriptionTask,
            "translate", StringComparison.OrdinalIgnoreCase) ? "translate" : "transcribe";

    private void AddFailedHistoryRecord(
        RecordingContext context, string wavPath, double duration, string rawText,
        TranscriptionRecordStatus status, string message, string engine, string? model,
        string? prompt = null)
    {
        if (!_settings.Current.SaveToHistoryEnabled)
            return;

        try
        {
            _history.AddRecord(BuildHistoryRecord(context, Guid.NewGuid().ToString(),
                context.RecordingStart == default ? DateTime.UtcNow : context.RecordingStart,
                rawText, rawText, duration, null, wavPath, engine, model) with
            {
                Status = status,
                FailureMessage = FailureMessageSanitizer.Sanitize(message, rawText, prompt,
                    context.StreamingFinalText, context.RecoveredPartialPreview),
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Dictation] Failed to save recovery record: {ex.GetType().Name}");
        }
    }

    internal async Task CancelRecoveryAndDrainAsync()
    {
        Task cancellation;
        Task completion;
        lock (_recoveryLock)
        {
            cancellation = _recoveryCts?.CancelAsync() ?? Task.CompletedTask;
            completion = _recoveryCompletion?.Task ?? Task.CompletedTask;
        }
        await cancellation.ConfigureAwait(false);
        await completion.ConfigureAwait(false);
    }

    /// <summary>Sanitized failure text for display; blank exception messages fall back to a localized string.</summary>
    private static string SanitizeForDisplay(string? message, params string?[] secrets) =>
        FailureMessageSanitizer.Sanitize(message, secrets) ?? Loc.Instance["Common.UnknownError"];

    /// <summary>
    ///     Re-runs a history record's saved capture through transcription and post-processing and
    ///     replaces that record in place with the result. The recovered text is copied to the
    ///     clipboard rather than pasted, so it can never land in whatever the user is typing in now.
    ///     Throws when the capture or the record is gone, when nothing usable comes back, or when
    ///     a dictation or another retry is already running.
    /// </summary>
    public async Task<TranscriptionRecord> RetryFromHistoryAsync(string recordId, CancellationToken ct = default)
    {
        CancellationTokenSource recoveryCts;
        TaskCompletionSource completion;
        lock (_recoveryLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_inFlightTracker.TryBeginRecovery())
                throw new InvalidOperationException(Loc.Instance["History.RetryBusy"]);
            recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _recoveryCts = recoveryCts;
            _recoveryCompletion = completion;
        }
        ct = recoveryCts.Token;

        try
        {
            _hotkey.IsCancelShortcutEnabled = true;
            var record = _history.Records.FirstOrDefault(candidate => candidate.Id == recordId);
            var path = _sessionAudioFiles.GetAudioPath(record?.AudioFileName);
            if (record is null || path is null)
                throw new FileNotFoundException(Loc.Instance["History.RetryAudioMissing"]);

            var profile = _profiles.Profiles.FirstOrDefault(candidate => candidate.Name == record.ProfileName);
            var context = new RecordingContext(-1, record.Timestamp, record.AppProcessName,
                record.AppName, record.AppUrl, null, profile, "", null, false, null, null,
                LanguageSelection.Automatic, [], ct)
            {
                Capture = _settings.Current.CaptureLlmProvenance ? new LlmCallCapture() : null,
                TranscriptionTaskUsed = record.TranscriptionTaskUsed,
            };
            var rawText = "";
            var finalText = "";
            var originalRecord = record;
            var failureStatus = TranscriptionRecordStatus.TranscriptionFailed;
            string? prompt = null;
            try
            {
                var wav = await File.ReadAllBytesAsync(path, ct);
                var languageHints = LanguageSelectionResolver.ResolveHints(profile, _settings.Current);
                var selection = LanguageSelectionResolver.ResolvePrimary(languageHints);
                var translate = ResolveTranscriptionTask(context) == "translate";
                PluginTranscriptionResult result;
                bool supportsTranslation;
                await using (var lease = await _models.AcquireTranscriptionAsync(
                    profile?.TranscriptionModelOverride ?? _settings.Current.SelectedModelId,
                    cancellationToken: ct))
                {
                    var plugin = lease.Plugin;
                    record = record with { EngineUsed = plugin.ProviderId, ModelUsed = plugin.SelectedModelId };
                    supportsTranslation = plugin.SupportsTranslation;
                    result = await plugin.TranscribeAsync(wav, selection, languageHints, translate, null, ct);
                }
                rawText = SelectRawTextWithPreviewFallback(result.Text, "", out _);
                if (string.IsNullOrWhiteSpace(rawText))
                    throw new InvalidOperationException(Loc.Instance["History.RetryEmpty"]);

                failureStatus = TranscriptionRecordStatus.ProcessingFailed;
                var promptAction = ResolvePromptAction(context);
                prompt = promptAction?.SystemPrompt;
                var duration = LinuxDictationShortSpeechPolicy.ComputeDurationSeconds(wav);
                var language = ResolvePostProcessingSourceLanguage(result.DetectedLanguage,
                    selection.LanguageTag, translate, supportsTranslation);
                var processed = await _pipeline.ProcessAsync(rawText,
                    BuildPipelineOptions(context, duration, language, selection.LanguageTag,
                        languageHints, translate, supportsTranslation, false), ct);
                finalText = ApplyProfileStyleFormatting(context, VoiceCommandParser.Parse(processed.Text).Text);
                if (string.IsNullOrWhiteSpace(finalText))
                    throw new InvalidOperationException(Loc.Instance["History.RetryEmpty"]);

                ct.ThrowIfCancellationRequested();
                var insertion = await _textInsertion.InsertTextAsync(new TextInsertionRequest(finalText, AutoPaste: false));
                if (insertion != InsertionResult.CopiedToClipboard)
                    throw new InvalidOperationException(Loc.Instance["History.RetryClipboardFailed"]);

                record = record with
                {
                    Status = TranscriptionRecordStatus.Succeeded,
                    FailureMessage = null,
                    RawText = rawText,
                    FinalText = finalText,
                    DurationSeconds = duration,
                    Language = result.DetectedLanguage
                        ?? record.Language
                        ?? LanguageSelectionResolver
                            .ResolveOrAutomatic(profile?.InputLanguage, _settings.Current.Language)
                            .LanguageTag,
                    InsertionStatus = TextInsertionStatus.CopiedToClipboard,
                    InsertionFailureReason = null,
                    CleanupLevelUsed = ResolveCleanupLevel(context, promptAction),
                    CleanupApplied = WasPipelineStepChanged(processed, PostProcessingStepNames.Cleanup),
                    SnippetApplied = WasPipelineStepChanged(processed, PostProcessingStepNames.Snippets),
                    DictionaryCorrectionApplied = WasPipelineStepChanged(processed, PostProcessingStepNames.Dictionary),
                    PromptActionApplied = WasPipelineStepSucceeded(processed, PostProcessingStepNames.Llm),
                    TranslationApplied = WasPipelineStepChanged(processed, PostProcessingStepNames.Translation),
                    PendingCorrectionSuggestions = [],
                    LlmCalls = context.Capture?.Calls ?? [],
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var message = SanitizeForDisplay(ex.Message, rawText, finalText, prompt);
                var hasNewRawText = !string.IsNullOrWhiteSpace(rawText);
                if (!hasNewRawText)
                    record = originalRecord;
                var status = hasNewRawText ? failureStatus : record.Status;
                record = record with
                {
                    Status = status,
                    // A record that still reads as Succeeded keeps its own message: HasFailure
                    // gates the display, so writing one here would only hide it.
                    FailureMessage = status == TranscriptionRecordStatus.Succeeded
                        ? record.FailureMessage : message,
                    RawText = hasNewRawText ? rawText : record.RawText,
                    FinalText = !string.IsNullOrWhiteSpace(finalText) ? finalText
                        : !string.IsNullOrWhiteSpace(record.FinalText) ? record.FinalText
                        : hasNewRawText ? rawText : record.FinalText,
                };
                if (!_history.TryReplaceRecord(record))
                    throw new InvalidOperationException(Loc.Instance["History.RetryRecordMissing"]);
                throw new InvalidOperationException(message);
            }

            return _history.TryReplaceRecord(record)
                ? record
                : throw new InvalidOperationException(Loc.Instance["History.RetryRecordMissing"]);
        }
        finally
        {
            lock (_recoveryLock)
            {
                _hotkey.IsCancelShortcutEnabled = false;
                _recoveryCts = null;
                _recoveryCompletion = null;
                recoveryCts.Dispose();
                _inFlightTracker.EndRecovery();
                completion.TrySetResult();
            }
        }
    }
}
