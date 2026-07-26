extern alias SherpaOnnx;

using SherpaOnnxPlugin = SherpaOnnx::TypeWhisper.Plugin.SherpaOnnx.SherpaOnnxPlugin;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SherpaOnnxLanguageTests
{
    [Theory]
    [InlineData("en", "en")]
    [InlineData(" EN ", "en")]
    [InlineData("de", "de")]
    [InlineData("\tDe", "de")]
    [InlineData("fr", "fr")]
    [InlineData("FR\n", "fr")]
    [InlineData("es", "es")]
    [InlineData(" Es ", "es")]
    public void NormalizeCanaryLanguage_SupportedLanguage_ReturnsNormalizedCode(
        string language,
        string expected
    )
    {
        Assert.Equal(expected, SherpaOnnxPlugin.NormalizeCanaryLanguage(language));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData(" AuTo ")]
    public void NormalizeCanaryLanguage_AutomaticLanguage_ThrowsWithSupportedSet(
        string? language
    )
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => SherpaOnnxPlugin.NormalizeCanaryLanguage(language)
        );

        Assert.Equal(
            "Sherpa ONNX Canary requires an explicit source language from the supported set: en, de, fr, es.",
            exception.Message
        );
    }

    [Fact]
    public void NormalizeCanaryLanguage_UnsupportedLanguage_ThrowsWithSupportedSet()
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => SherpaOnnxPlugin.NormalizeCanaryLanguage(" ja ")
        );

        Assert.Equal(
            "Sherpa ONNX Canary requires an explicit source language from the supported set: en, de, fr, es.",
            exception.Message
        );
    }
}
