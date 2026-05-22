using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Linux.Services;

// Decides which language the post-transcription readback
// (SpeechFeedbackService.AnnounceTranscriptionComplete) should be spoken in.
//
// It walks the post-processing pipeline steps in execution order: the final
// text's language is set by the *last* step that changed it — a translation
// establishes a known target language, while a prompt action or plugin
// post-processor can rewrite the text into any language. If no step changed the
// language, it is the engine translate-task output (English) or the
// engine-detected / configured input language.
//
// Resolving this in the caller — rather than passing nothing and letting
// SpeechFeedbackService substitute the global AppSettings.Language — keeps that
// global fallback a genuine last resort instead of an override that ignores the
// detected language and per-profile input-language settings.
internal static class LinuxDictationReadbackLanguagePolicy
{
    private enum FinalLanguage
    {
        // No post-processing step changed the language.
        Unchanged,

        // A translation step translated the text into the target language.
        TranslatedToTarget,

        // A prompt action / plugin rewrote the text into an unknown language.
        Rewritten
    }

    /// <summary>
    ///     Resolves the spoken language of the final transcription text, or
    ///     <c>null</c> when no specific language context is available (the
    ///     caller then leaves it to the provider to infer from the text).
    /// </summary>
    /// <param name="detectedLanguage">Language reported by the transcription engine.</param>
    /// <param name="configuredSourceLanguage">
    ///     The effective input language (profile override, else global setting);
    ///     <c>"auto"</c> / blank is treated as "no preference".
    /// </param>
    /// <param name="engineTranslatedToEnglish">
    ///     True when the engine ran a translate task, which always emits English.
    /// </param>
    /// <param name="translationTarget">
    ///     Target language of the post-processing translation step, if configured.
    /// </param>
    /// <param name="postProcessingSteps">
    ///     The pipeline step results, in execution order.
    /// </param>
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

        // The translation step short-circuits without translating when the
        // source language already equals the target, so it only establishes the
        // target language when the two differ.
        var translationWouldTranslate =
            target is not null
            && !string.Equals(
                sourceLanguage ?? "auto",
                target,
                StringComparison.OrdinalIgnoreCase
            );

        // Walk in execution order: the last language-changing step wins. Plugin
        // priorities are unbounded, so a plugin post-processor can run either
        // before or after the built-in translation step.
        var finalLanguage = FinalLanguage.Unchanged;
        foreach (var step in postProcessingSteps)
        {
            if (IsSuccessfulTranslation(step))
            {
                // A same-language translation is a no-op and leaves the text
                // (and its language) untouched.
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

    // A translation step that ran to completion without throwing. The pipeline
    // swallows a failing translation step and continues with the untranslated
    // text. The Changed flag is deliberately not checked: a genuine translation
    // can legitimately return text identical to its input.
    private static bool IsSuccessfulTranslation(PostProcessingStepResult step)
    {
        return step.Name == PostProcessingStepNames.Translation && step.Succeeded;
    }

    // The built-in LLM prompt action or an arbitrary plugin post-processor —
    // both can rewrite the text into any language. Only a succeeded step that
    // changed the text counts: a swallowed failure or an identical result
    // leaves the language untouched.
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
}
