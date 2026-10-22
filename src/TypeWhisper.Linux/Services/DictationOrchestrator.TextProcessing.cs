using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.Services;

/// <summary>A transcript to run through the final dictation pipeline without audio.</summary>
/// <param name="Text">Raw transcript text, as an engine would return it.</param>
/// <param name="Language">Source language tag. Defaults to the configured or profile language.</param>
/// <param name="AppProcess">Target app process, used for profile matching like the focused app in dictation.</param>
/// <param name="Url">Target URL, used for profile matching.</param>
/// <param name="ProfileId">Profile to force, as a profile hotkey would.</param>
public sealed record TextProcessingRequest(
    string Text,
    string? Language = null,
    string? AppProcess = null,
    string? Url = null,
    string? ProfileId = null
);

public sealed record TextProcessingStep(string Name, double Milliseconds, bool Changed, bool Succeeded);

public sealed record TextProcessingLlmCall(string Stage, string ProviderId, string ModelId, bool RanLocally, bool Succeeded);

public sealed record TextProcessingResult(
    string Text,
    string RawText,
    string? Language,
    string? ProfileId,
    string? ProfileName,
    CleanupLevel CleanupLevel,
    string? EngineProviderId,
    string? EngineModelId,
    IReadOnlyList<TextProcessingStep> Steps,
    IReadOnlyList<TextProcessingLlmCall> LlmCalls
);

public sealed partial class DictationOrchestrator
{
    /// <summary>
    ///     Runs <paramref name="request" /> through the final steps of a dictation, minus the
    ///     audio-only ones (as in history recovery). Nothing is inserted, saved, or learned.
    /// </summary>
    public async Task<TextProcessingResult> ProcessTextAsync(TextProcessingRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        var rawText = LinuxDictationFinalTextPolicy.SelectRawText(request.Text);
        if (string.IsNullOrWhiteSpace(rawText))
            throw new InvalidOperationException(Loc.Instance["History.RetryEmpty"]);
        if (request.ProfileId is { } forcedId)
        {
            var forced = _profiles.Profiles.FirstOrDefault(profile => profile.Id == forcedId)
                ?? throw new KeyNotFoundException($"Profile '{forcedId}' was not found.");
            // MatchProfile ignores a disabled forced profile and falls back to normal matching.
            if (!forced.IsEnabled)
                throw new InvalidOperationException($"Profile '{forcedId}' is disabled.");
        }

        var profile = _profiles.MatchProfile(request.AppProcess, request.Url, request.ProfileId).Profile;
        var context = new RecordingContext(-1, DateTime.UtcNow, request.AppProcess, request.AppProcess, request.Url,
            null, profile, "", null, false, null, null, LanguageSelection.Automatic, [], ct)
        {
            Capture = new LlmCallCapture(),
            SuppressStatus = true,
        };

        var requestedSelection = string.IsNullOrWhiteSpace(request.Language)
            ? null
            : LanguageSelectionResolver.Resolve(request.Language);
        var requestLanguageTag = requestedSelection?.LanguageTag;

        var promptAction = ResolvePromptAction(context);
        var promptActionId = profile?.PromptActionId;
        if (IsPromptActionUnavailable(promptActionId, promptAction is not null))
            throw new InvalidOperationException($"Prompt action '{promptActionId}' for this profile is unavailable.");

        var modelId = profile?.TranscriptionModelOverride ?? _settings.Current.SelectedModelId;
        var engine = _models.GetTranscriptionPlugin(modelId);
        var engineModelId = modelId is not null && ModelManagerService.IsPluginModel(modelId)
            ? ModelManagerService.ParsePluginModelId(modelId).ModelId
            : null;

        // A requested language (or "auto") replaces the saved hints, as if the engine had reported it.
        IReadOnlyList<string> languageHints;
        LanguageSelection selection;
        if (requestedSelection is not null)
        {
            languageHints = requestLanguageTag is not null ? [requestLanguageTag] : [];
            selection = requestedSelection;
        }
        else
        {
            languageHints = LanguageSelectionResolver.ResolveHints(profile, _settings.Current);
            selection = LanguageSelectionResolver.ResolvePrimary(languageHints);
            // Capabilities only describe the engine's selected model; selecting another mutates shared state.
            if (engine is not null && engine.SelectedModelId == engineModelId)
                (selection, languageHints) = engine.ResolveEffectiveLanguage(selection, languageHints);
        }

        var configuredLanguage = selection.LanguageTag;
        var translate = ResolveTranscriptionTask(context) == "translate";
        if (translate && _models.GetTranslationRejection(modelId) is { } translationRejection)
            throw new InvalidOperationException(translationRejection);
        var supportsTranslation = _models.SupportsTranslation(modelId);
        var language = ResolvePostProcessingSourceLanguage(requestLanguageTag, configuredLanguage, translate,
            supportsTranslation);
        var engineProviderId = engine?.ProviderId ?? "";

        var timings = new List<(string Name, TimeSpan Elapsed)>();
        var processed = await _pipeline.ProcessAsync(rawText,
            BuildPipelineOptions(context, 0, language, configuredLanguage, languageHints, translate, supportsTranslation,
                false, false, engineProviderId, engineModelId, timings, recordUsage: false), ct);
        var finalText = ApplyProfileStyleFormatting(context, VoiceCommandParser.Parse(processed.Text).Text);

        var stepTimingsQueue = new Queue<(string Name, TimeSpan Elapsed)>(timings);
        var steps = processed.Steps
            .Select(step =>
            {
                var elapsedMs = stepTimingsQueue.Count > 0 && stepTimingsQueue.Peek().Name == step.Name
                    ? stepTimingsQueue.Dequeue().Elapsed.TotalMilliseconds
                    : timings.FirstOrDefault(t => t.Name == step.Name).Elapsed.TotalMilliseconds;

                return new TextProcessingStep(step.Name, elapsedMs, step.Changed, step.Succeeded);
            })
            .ToList();
        var calls = context.Capture.Calls
            .Select(call => new TextProcessingLlmCall(call.Stage, call.ProviderId, call.ModelId, call.RanLocally,
                !string.IsNullOrWhiteSpace(call.ResponseReceived)))
            .ToList();

        return new TextProcessingResult(finalText, request.Text, language, profile?.Id, profile?.Name,
            ResolveCleanupLevel(context, promptAction), engineProviderId == "" ? null : engineProviderId, engineModelId,
            steps, calls);
    }
}
