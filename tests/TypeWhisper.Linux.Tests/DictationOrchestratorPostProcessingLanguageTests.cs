using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationOrchestratorPostProcessingLanguageTests
{
    [Fact]
    public void Returns_En_WhenEngineTranslatedToEnglish_RegardlessOfDetectedLanguage()
    {
        var result = DictationOrchestrator.ResolvePostProcessingSourceLanguage(
            "fr",
            "fr",
            translateRequested: true,
            engineSupportsTranslation: true
        );

        Assert.Equal("en", result);
    }

    [Fact]
    public void Returns_En_WhenEngineTranslated_AndNoDetectedLanguage()
    {
        var result = DictationOrchestrator.ResolvePostProcessingSourceLanguage(
            null,
            "fr",
            translateRequested: true,
            engineSupportsTranslation: true
        );

        Assert.Equal("en", result);
    }

    [Fact]
    public void Returns_DetectedLanguage_WhenNotTranslated()
    {
        var result = DictationOrchestrator.ResolvePostProcessingSourceLanguage(
            "fr",
            "de",
            translateRequested: false,
            engineSupportsTranslation: true
        );

        Assert.Equal("fr", result);
    }

    [Fact]
    public void FallsBackToConfiguredLanguage_WhenNotTranslated_AndNoDetectedLanguage()
    {
        var result = DictationOrchestrator.ResolvePostProcessingSourceLanguage(
            null,
            "de",
            translateRequested: false,
            engineSupportsTranslation: true
        );

        Assert.Equal("de", result);
    }

    [Fact]
    public void ReturnsNull_WhenNotTranslated_AndNoLanguageInfoAtAll()
    {
        var result = DictationOrchestrator.ResolvePostProcessingSourceLanguage(
            null,
            null,
            translateRequested: false,
            engineSupportsTranslation: true
        );

        Assert.Null(result);
    }

    [Fact]
    public void Returns_SourceLanguage_WhenTranslateRequested_ButEngineDoesNotSupportTranslation()
    {
        // Distinct codes so the assertion pins down *which* input wins: no engine translation
        // happened, so the detected language is the source, not the configured hint.
        var result = DictationOrchestrator.ResolvePostProcessingSourceLanguage(
            "de",
            "fr",
            translateRequested: true,
            engineSupportsTranslation: false
        );

        Assert.Equal("de", result);
    }

    [Fact]
    public void Returns_DetectedLanguage_WhenEngineSupportsTranslation_ButTranslateNotRequested()
    {
        var result = DictationOrchestrator.ResolvePostProcessingSourceLanguage(
            "de",
            "fr",
            translateRequested: false,
            engineSupportsTranslation: true
        );

        Assert.Equal("de", result);
    }
}
