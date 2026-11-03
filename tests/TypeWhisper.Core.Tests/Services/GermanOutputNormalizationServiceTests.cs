using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

/// <summary>Covers German output spelling, language gating, and segment normalization.</summary>
public sealed class GermanOutputNormalizationServiceTests
{
    [Fact]
    public void NormalizeText_SwissGerman_ReplacesLowerAndUpperSharpS()
    {
        var result = GermanOutputNormalizationService.NormalizeText(
            "Straße und ẞ",
            GermanOutputVariant.Switzerland,
            detectedLanguage: "de-CH");

        Assert.Equal("Strasse und SS", result);
    }

    [Theory]
    [InlineData(GermanOutputVariant.AsTranscribed)]
    [InlineData(GermanOutputVariant.Germany)]
    [InlineData(GermanOutputVariant.Austria)]
    public void NormalizeText_NonSwissVariants_PreserveSpelling(GermanOutputVariant variant)
    {
        var result = GermanOutputNormalizationService.NormalizeText(
            "Straße und ẞ",
            variant,
            detectedLanguage: "de");

        Assert.Equal("Straße und ẞ", result);
    }

    [Fact]
    public void NormalizeText_KnownNonGermanOutput_PreservesText()
    {
        var result = GermanOutputNormalizationService.NormalizeText(
            "The family name is Groß.",
            GermanOutputVariant.Switzerland,
            detectedLanguage: "en");

        Assert.Equal("The family name is Groß.", result);
    }

    [Fact]
    public void NormalizeText_WithoutGermanLanguageSelection_PreservesSpelling()
    {
        var result = GermanOutputNormalizationService.NormalizeText(
            "Die Straße ist groß.",
            GermanOutputVariant.Switzerland);

        Assert.Equal("Die Straße ist groß.", result);
    }

    [Fact]
    public void NormalizeText_TranslateTaskWithoutTarget_PreservesEnglishOutput()
    {
        var result = GermanOutputNormalizationService.NormalizeText(
            "The family name is Groß.",
            GermanOutputVariant.Switzerland,
            TranscriptionTask.Translate,
            detectedLanguage: "de");

        Assert.Equal("The family name is Groß.", result);
    }

    [Fact]
    public void NormalizeText_GermanTranslationTarget_NormalizesTranslatedOutput()
    {
        var result = GermanOutputNormalizationService.NormalizeText(
            "Die Straße ist groß.",
            GermanOutputVariant.Switzerland,
            detectedLanguage: "en",
            translationTarget: "de-CH");

        Assert.Equal("Die Strasse ist gross.", result);
    }

    [Fact]
    public void NormalizeText_NonGermanTranslationTarget_WinsOverGermanSource()
    {
        var result = GermanOutputNormalizationService.NormalizeText(
            "The family name is Groß.",
            GermanOutputVariant.Switzerland,
            detectedLanguage: "de",
            translationTarget: "en");

        Assert.Equal("The family name is Groß.", result);
    }

    [Theory]
    [InlineData("Öffne /tmp/Straße.txt jetzt")]
    [InlineData("Siehe https://straße.example.de/groß heute")]
    [InlineData("Schreib an groß@example.de bitte")]
    [InlineData("Setze straßen_name und Straße.txt")]
    [InlineData("Version groß2 bleibt")]
    public void NormalizeText_SwissGerman_LeavesPathsUrlsAndIdentifiersAlone(string text)
    {
        var result = GermanOutputNormalizationService.NormalizeText(
            text,
            GermanOutputVariant.Switzerland,
            detectedLanguage: "de");

        Assert.Equal(text, result);
    }

    [Fact]
    public void NormalizeText_SwissGerman_StillRewritesProseNextToLiterals()
    {
        var result = GermanOutputNormalizationService.NormalizeText(
            "Die Straße; groß (Maße): siehe /tmp/Straße.txt",
            GermanOutputVariant.Switzerland,
            detectedLanguage: "de");

        Assert.Equal("Die Strasse; gross (Masse): siehe /tmp/Straße.txt", result);
    }

    [Fact]
    public void NormalizeResult_NormalizesTextAndSegments()
    {
        var transcription = new TranscriptionResult
        {
            Text = "Große Straße",
            DetectedLanguage = "de",
            Segments = [new TranscriptionSegment("Große Straße", 0.25, 1.75)],
        };

        var result = GermanOutputNormalizationService.NormalizeResult(
            transcription,
            GermanOutputVariant.Switzerland);

        Assert.Equal("Grosse Strasse", result.Text);
        var segment = Assert.Single(result.Segments);
        Assert.Equal("Grosse Strasse", segment.Text);
        Assert.Equal(0.25, segment.Start);
        Assert.Equal(1.75, segment.End);
    }
}
