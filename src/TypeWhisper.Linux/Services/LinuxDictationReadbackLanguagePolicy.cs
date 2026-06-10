using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Linux.Services;

// Decides which language the post-transcription readback
// (SpeechFeedbackService.AnnounceTranscriptionComplete) should be spoken in.
// The last pipeline step that changed the language wins. Resolving this here
// keeps the global AppSettings.Language a true last resort rather than an
// override that ignores detected / per-profile input-language settings.
internal static class LinuxDictationReadbackLanguagePolicy
{
    /// <summary>
    ///     Resolves the spoken language of the final transcription text, or
    ///     <c>null</c> when no language context is available.
    /// </summary>
    /// <param name="detectedLanguage">Language reported by the transcription engine.</param>
    /// <param name="configuredSourceLanguage">Effective input language; "auto"/blank = no preference.</param>
    /// <param name="engineTranslatedToEnglish">True when the engine ran a translate task (always emits English).</param>
    /// <param name="translationTarget">Post-processing translation target language, if configured.</param>
    /// <param name="postProcessingSteps">Pipeline step results in execution order.</param>
    public static string? Resolve(
        string? detectedLanguage,
        string? configuredSourceLanguage,
        bool engineTranslatedToEnglish,
        string? translationTarget,
        IReadOnlyList<PostProcessingStepResult> postProcessingSteps
    )
    {
        var sourceLanguage = Normalize(detectedLanguage) ?? Normalize(configuredSourceLanguage);
        var target = Normalize(translationTarget);

        // Translation is a no-op when source == target, so it only establishes
        // the target language when the two actually differ.
        var translationWouldTranslate =
            target is not null
            && !string.Equals(
                sourceLanguage ?? "auto",
                target,
                StringComparison.OrdinalIgnoreCase
            );

        // Last language-changing step wins. Plugin priorities are unbounded,
        // so a plugin post-processor can run before or after built-in translation.
        var finalLanguage = FinalLanguage.Unchanged;
        foreach (var step in postProcessingSteps)
        {
            if (IsSuccessfulTranslation(step))
            {
                if (translationWouldTranslate)
                {
                    finalLanguage = FinalLanguage.TranslatedToTarget;
                }
            }
            else if (IsArbitraryTransform(step))
            {
                finalLanguage = FinalLanguage.Rewritten;
            }
        }

        return finalLanguage switch
        {
            FinalLanguage.TranslatedToTarget => target,
            FinalLanguage.Rewritten => null,
            _ => engineTranslatedToEnglish ? "en" : sourceLanguage
        };
    }

    // A translation step that completed without throwing. Changed is not
    // checked — a translation can legitimately return text identical to its input.
    private static bool IsSuccessfulTranslation(PostProcessingStepResult step)
    {
        return step.Name == PostProcessingStepNames.Translation && step.Succeeded;
    }

    // LLM prompt action or plugin post-processor — both can rewrite into any language.
    // Only a succeeded step that actually changed the text counts.
    private static bool IsArbitraryTransform(PostProcessingStepResult step)
    {
        return step.Succeeded
               && step.Changed
               && (
                   step.Name == PostProcessingStepNames.Llm
                   || step.Name.StartsWith(
                       PostProcessingStepNames.PluginPrefix,
                       StringComparison.Ordinal
                   )
               );
    }

    private static string? Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var trimmed = language.Trim();
        return string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    private enum FinalLanguage
    {
        Unchanged,          // No post-processing step changed the language.
        TranslatedToTarget, // Translation step ran and changed the language.
        Rewritten           // Prompt/plugin rewrote into an unknown language.
    }
}