using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

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
    ///     Runs <paramref name="request" /> through the same profile matching, post-processing
    ///     pipeline, voice-command parsing, and profile formatting as the final step of a
    ///     dictation. Audio-only steps (vocabulary rescoring against token timings) are skipped,
    ///     as in history recovery. Nothing is inserted, saved to history, or learned.
    /// </summary>
    public async Task<TextProcessingResult> ProcessTextAsync(TextProcessingRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (request.ProfileId is { } forcedId && _profiles.Profiles.All(profile => profile.Id != forcedId))
            throw new KeyNotFoundException($"Profile '{forcedId}' was not found.");

        var profile = _profiles.MatchProfile(request.AppProcess, request.Url, request.ProfileId).Profile;
        var context = new RecordingContext(-1, DateTime.UtcNow, request.AppProcess, request.AppProcess, request.Url,
            null, profile, "", null, false, null, null, LanguageSelection.Automatic, [], ct)
        {
            Capture = new LlmCallCapture(),
        };

        string? requestLanguageTag = null;
        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            var requestedSelection = LanguageSelectionResolver.Resolve(request.Language);
            if (!requestedSelection.IsAutomatic)
            {
                requestLanguageTag = requestedSelection.LanguageTag;
            }
        }

        var promptAction = ResolvePromptAction(context);
        if (IsPromptActionUnavailable(profile?.PromptActionId, promptAction is not null))
            throw new InvalidOperationException($"Prompt action '{profile?.PromptActionId}' for this profile is unavailable.");

        var languageHints = requestLanguageTag is not null
            ? (IReadOnlyList<string>)[requestLanguageTag]
            : LanguageSelectionResolver.ResolveHints(profile, _settings.Current);
        var configuredLanguage = requestLanguageTag ?? LanguageSelectionResolver.ResolvePrimary(languageHints).LanguageTag;
        var language = ResolvePostProcessingSourceLanguage(requestLanguageTag, configuredLanguage, false, false);

        var modelId = profile?.TranscriptionModelOverride ?? _settings.Current.SelectedModelId;
        var engine = _models.GetTranscriptionPlugin(modelId);
        var engineModelId = modelId is not null && ModelManagerService.IsPluginModel(modelId)
            ? ModelManagerService.ParsePluginModelId(modelId).ModelId
            : null;
        var engineProviderId = engine?.ProviderId ?? "";

        var timings = new List<(string Name, TimeSpan Elapsed)>();
        var processed = await _pipeline.ProcessAsync(request.Text,
            BuildPipelineOptions(context, 0, language, configuredLanguage, languageHints, false, false, false, false,
                engineProviderId, engineModelId, timings), ct);
        var finalText = ApplyProfileStyleFormatting(context, VoiceCommandParser.Parse(processed.Text).Text);

        var stepTimingsQueue = new Queue<(string Name, TimeSpan Elapsed)>(timings);
        var steps = processed.Steps
            .Select(step =>
            {
                double elapsedMs = 0;
                if (stepTimingsQueue.Count > 0 && stepTimingsQueue.Peek().Name == step.Name)
                {
                    elapsedMs = stepTimingsQueue.Dequeue().Elapsed.TotalMilliseconds;
                }
                else
                {
                    var match = timings.FirstOrDefault(t => t.Name == step.Name);
                    elapsedMs = match.Elapsed.TotalMilliseconds;
                }

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
