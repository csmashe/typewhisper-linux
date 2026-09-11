using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

/// <summary>Covers <see cref="TranscriptionOutputLanguageResolver" />: language precedence, unknown values, and region subtags.</summary>
public sealed class TranscriptionOutputLanguageResolverTests
{
    [Theory]
    [InlineData("de", "en-US", true)]
    [InlineData("en", "de", false)]
    public void IsOutputLanguage_TranslationTargetWins(string detected, string target, bool expected)
    {
        Assert.Equal(expected, TranscriptionOutputLanguageResolver.IsOutputLanguage(
            "en", TranscriptionTask.Transcribe, detected, null, [], target));
    }

    [Fact]
    public void IsOutputLanguage_TranslateTaskMeansEnglish()
    {
        Assert.True(TranscriptionOutputLanguageResolver.IsOutputLanguage(
            "en", TranscriptionTask.Translate, "de", "de", ["de"]));
    }

    [Theory]
    [InlineData("en", "de", true)]
    [InlineData("de", "en", false)]
    public void IsOutputLanguage_UsesDetectedBeforeConfigured(string detected, string configured, bool expected)
    {
        Assert.Equal(expected, TranscriptionOutputLanguageResolver.IsOutputLanguage(
            "en", TranscriptionTask.Transcribe, detected, configured, []));
    }

    [Fact]
    public void IsOutputLanguage_FallsBackToFirstCandidate()
    {
        Assert.True(TranscriptionOutputLanguageResolver.IsOutputLanguage(
            "en", TranscriptionTask.Transcribe, null, "auto", ["auto", "en-GB", "de"]));
        Assert.False(TranscriptionOutputLanguageResolver.IsOutputLanguage(
            "en", TranscriptionTask.Transcribe, null, null, ["de", "en"]));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void IsOutputLanguage_TreatsAutoAndBlankAsUnknown(string? language)
    {
        Assert.False(TranscriptionOutputLanguageResolver.IsOutputLanguage(
            "en", TranscriptionTask.Transcribe, language, language, [], language));
    }

    [Theory]
    [InlineData("en_GB")]
    [InlineData("EN-us")]
    public void IsOutputLanguage_StripsRegionSubtag(string language)
    {
        Assert.True(TranscriptionOutputLanguageResolver.IsOutputLanguage(
            "en", TranscriptionTask.Transcribe, language, null, []));
    }
}
