using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class LinuxDictationReadbackLanguagePolicyTests
{
    private static PostProcessingStepResult Translation(bool changed = true, bool succeeded = true)
    {
        return new PostProcessingStepResult(
            PostProcessingStepNames.Translation,
            changed,
            succeeded,
            succeeded ? null : "translation failed"
        );
    }

    private static PostProcessingStepResult PromptAction(bool changed = true, bool succeeded = true)
    {
        return new PostProcessingStepResult(
            PostProcessingStepNames.Llm,
            changed,
            succeeded,
            succeeded ? null : "prompt action failed"
        );
    }

    private static PostProcessingStepResult Plugin(bool changed = true, bool succeeded = true)
    {
        return new PostProcessingStepResult(
            $"{PostProcessingStepNames.PluginPrefix}950)",
            changed,
            succeeded,
            succeeded ? null : "plugin failed"
        );
    }

    [Fact]
    public void Resolve_UsesEffectiveSourceLanguage_OverConfiguredLanguage()
    {
        // Regression: global Language="de" but the effective engine output is
        // English — the readback must follow the effective source language.
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            "en",
            "de",
            engineTranslatedToEnglish: false,
            translationTarget: null,
            postProcessingSteps: []
        );

        Assert.Equal("en", language);
    }

    [Fact]
    public void Resolve_FallsBackToConfiguredLanguage_WhenNoEffectiveSourceLanguage()
    {
        // Regression: a profile sets an English input language while the global
        // setting is "de"; the caller passes the profile-effective value.
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            effectiveSourceLanguage: null,
            "en",
            engineTranslatedToEnglish: false,
            translationTarget: null,
            postProcessingSteps: []
        );

        Assert.Equal("en", language);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenConfiguredLanguageIsAuto()
    {
        // No effective source language and an "auto" input language: leave the
        // language unset for the provider to infer from the text.
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            effectiveSourceLanguage: null,
            "auto",
            engineTranslatedToEnglish: false,
            translationTarget: null,
            postProcessingSteps: []
        );

        Assert.Null(language);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenNothingIsKnown()
    {
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            effectiveSourceLanguage: null,
            configuredSourceLanguage: null,
            engineTranslatedToEnglish: false,
            translationTarget: null,
            postProcessingSteps: []
        );

        Assert.Null(language);
    }

    [Fact]
    public void Resolve_ReturnsEnglish_WhenEngineTranslatedToEnglish()
    {
        // A translate task always emits English, regardless of the source.
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            "de",
            "de",
            engineTranslatedToEnglish: true,
            translationTarget: null,
            postProcessingSteps: []
        );

        Assert.Equal("en", language);
    }

    [Fact]
    public void Resolve_UsesTranslationTarget_WhenEngineTranslationIsTranslatedBack()
    {
        // French audio was translated to English by the engine, then the
        // pipeline translated that effective English output back to French.
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            effectiveSourceLanguage: "en",
            configuredSourceLanguage: "fr",
            engineTranslatedToEnglish: true,
            translationTarget: "fr",
            postProcessingSteps: [Translation()]
        );

        Assert.Equal("fr", language);
    }

    [Fact]
    public void Resolve_UsesTranslationTarget_WhenTranslateBackTextIsUnchanged()
    {
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            effectiveSourceLanguage: "en",
            configuredSourceLanguage: "fr",
            engineTranslatedToEnglish: true,
            translationTarget: "fr",
            postProcessingSteps: [Translation(changed: false)]
        );

        Assert.Equal("fr", language);
    }

    [Fact]
    public void Resolve_UsesEffectiveSource_WhenEngineDidNotTranslate()
    {
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            effectiveSourceLanguage: "de",
            configuredSourceLanguage: "fr",
            engineTranslatedToEnglish: false,
            translationTarget: null,
            postProcessingSteps: []
        );

        Assert.Equal("de", language);
    }

    [Fact]
    public void Resolve_UsesTranslationTarget_WhenPipelineTranslated()
    {
        // Source "de" differs from target "fr", so the translation step ran.
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            "de",
            "de",
            engineTranslatedToEnglish: false,
            "fr",
            postProcessingSteps: [Translation()]
        );

        Assert.Equal("fr", language);
    }

    [Fact]
    public void Resolve_UsesTranslationTarget_EvenWhenTranslatedTextIsUnchanged()
    {
        // A genuine translation can legitimately return text identical to its
        // input (short utterances, proper nouns); the unchanged flag must not be
        // read as "translation did not happen".
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            "de",
            "de",
            engineTranslatedToEnglish: false,
            "fr",
            postProcessingSteps: [Translation(changed: false)]
        );

        Assert.Equal("fr", language);
    }

    [Fact]
    public void Resolve_PrefersTranslationTarget_OverEngineTranslateTask()
    {
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            "de",
            "de",
            engineTranslatedToEnglish: true,
            "es",
            postProcessingSteps: [Translation()]
        );

        Assert.Equal("es", language);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenPromptActionRewroteText()
    {
        // Regression: a prompt action (e.g. "Translate to English") rewrites the
        // text into an unknown language.
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            "de",
            "de",
            engineTranslatedToEnglish: false,
            translationTarget: null,
            postProcessingSteps: [PromptAction()]
        );

        Assert.Null(language);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenPluginPostProcessorRewroteText()
    {
        // Regression: a plugin post-processor (e.g. a translation script) can
        // rewrite the text into another language with no built-in translation
        // step afterward.
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            "de",
            "de",
            engineTranslatedToEnglish: false,
            translationTarget: null,
            postProcessingSteps: [Plugin()]
        );

        Assert.Null(language);
    }

    [Fact]
    public void Resolve_UsesTranslationTarget_WhenTranslationRunsAfterAnArbitraryTransform()
    {
        // The prompt action ran first, then translation — translation has the
        // final say on the language.
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            "de",
            "de",
            engineTranslatedToEnglish: false,
            "fr",
            postProcessingSteps: [PromptAction(), Translation()]
        );

        Assert.Equal("fr", language);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenPluginRewritesTextAfterTranslation()
    {
        // Regression: plugin priorities are unbounded, so a plugin can run after
        // the built-in translation step and rewrite the text — the earlier
        // translation must not mask that.
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            "de",
            "de",
            engineTranslatedToEnglish: false,
            "fr",
            postProcessingSteps: [Translation(), Plugin()]
        );

        Assert.Null(language);
    }

    [Fact]
    public void Resolve_UsesTranslationTarget_WhenAPluginRunsAfterItButChangesNothing()
    {
        // A plugin that runs after translation but does not change the text
        // leaves the translated language intact.
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            "de",
            "de",
            engineTranslatedToEnglish: false,
            "fr",
            postProcessingSteps: [Translation(), Plugin(changed: false)]
        );

        Assert.Equal("fr", language);
    }

    [Fact]
    public void Resolve_IgnoresTranslationTarget_WhenTranslationFailed()
    {
        // Regression: the translation step failed, so the pipeline kept the
        // untranslated text. The readback must use the source language.
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            "de",
            "de",
            engineTranslatedToEnglish: false,
            "fr",
            postProcessingSteps: [Translation(succeeded: false)]
        );

        Assert.Equal("de", language);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenTranslationFailedAfterPromptAction()
    {
        // Translation failed, so the text is whatever the prompt action emitted
        // — an unknown language, not the configured translation target.
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            "de",
            "de",
            engineTranslatedToEnglish: false,
            "fr",
            postProcessingSteps: [PromptAction(), Translation(succeeded: false)]
        );

        Assert.Null(language);
    }

    [Fact]
    public void Resolve_IgnoresTranslationTarget_WhenTargetEqualsSourceLanguage()
    {
        // Regression: the pipeline records a same-language translation as a
        // succeeded step even though it short-circuits without translating.
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            "de",
            "de",
            engineTranslatedToEnglish: false,
            "de",
            postProcessingSteps: [Translation()]
        );

        Assert.Equal("de", language);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenSameLanguageTranslationFollowsPromptAction()
    {
        // The same-language translation short-circuits, so the final text is
        // whatever the prompt action emitted — an unknown language.
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            "de",
            "de",
            engineTranslatedToEnglish: false,
            "de",
            postProcessingSteps: [PromptAction(), Translation()]
        );

        Assert.Null(language);
    }

    [Fact]
    public void Resolve_IgnoresFailedPromptAction()
    {
        // Regression: the pipeline swallows a failing LLM step and keeps the
        // original text, which is still in the source language.
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            "de",
            "de",
            engineTranslatedToEnglish: false,
            translationTarget: null,
            postProcessingSteps: [PromptAction(succeeded: false)]
        );

        Assert.Equal("de", language);
    }

    [Fact]
    public void Resolve_IgnoresFailedPluginPostProcessor()
    {
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            "de",
            "de",
            engineTranslatedToEnglish: false,
            translationTarget: null,
            postProcessingSteps: [Plugin(succeeded: false)]
        );

        Assert.Equal("de", language);
    }

    [Fact]
    public void Resolve_IgnoresArbitraryStepThatDidNotChangeText()
    {
        // A prompt action / plugin that returns identical text left the language
        // unchanged.
        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            "de",
            "de",
            engineTranslatedToEnglish: false,
            translationTarget: null,
            postProcessingSteps: [PromptAction(changed: false), Plugin(changed: false)]
        );

        Assert.Equal("de", language);
    }

    [Fact]
    public void Resolve_IgnoresLanguagePreservingSteps()
    {
        // Formatting / dictionary / snippet steps change the text but never its
        // language, so they must not be treated as arbitrary transforms.
        IReadOnlyList<PostProcessingStepResult> steps =
        [
            new(PostProcessingStepNames.Formatting, Changed: true),
            new(PostProcessingStepNames.Cleanup, Changed: true),
            new(PostProcessingStepNames.Dictionary, Changed: true),
            new(PostProcessingStepNames.Snippets, Changed: true),
        ];

        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            "de",
            "de",
            engineTranslatedToEnglish: false,
            translationTarget: null,
            steps
        );

        Assert.Equal("de", language);
    }

    [Fact]
    public void Resolve_ComposedWithEffectiveLanguageHelper_TranslateBackReachesTarget()
    {
        // Chains the real §2 M1 helper into the policy: fr audio, engine translate
        // to English, pipeline translates back to fr — readback must say fr.
        var effective = DictationOrchestrator.ResolvePostProcessingSourceLanguage(
            "fr",
            "fr",
            translateRequested: true,
            engineSupportsTranslation: true
        );

        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            effective,
            "fr",
            engineTranslatedToEnglish: true,
            translationTarget: "fr",
            [Translation()]
        );

        Assert.Equal("fr", language);
    }

    [Fact]
    public void Resolve_ComposedWithEffectiveLanguageHelper_UnsupportedEngineKeepsSource()
    {
        // Deepgram-shaped engine: translate requested but SupportsTranslation=false,
        // so the effective language stays the detected one and readback follows it.
        var effective = DictationOrchestrator.ResolvePostProcessingSourceLanguage(
            "de",
            "fr",
            translateRequested: true,
            engineSupportsTranslation: false
        );

        var language = LinuxDictationReadbackLanguagePolicy.Resolve(
            effective,
            "fr",
            engineTranslatedToEnglish: false,
            translationTarget: null,
            []
        );

        Assert.Equal("de", language);
    }
}
