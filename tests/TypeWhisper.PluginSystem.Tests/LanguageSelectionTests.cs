using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class LanguageSelectionTests
{
    [Theory]
    [InlineData("auto")]
    [InlineData(" auto ")]
    [InlineData("AUTO")]
    public void TryParse_AutomaticSentinel_IsTrimmedAndCaseInsensitive(string raw)
    {
        Assert.True(LanguageSelection.TryParse(raw, out var selection));
        Assert.Same(LanguageSelection.Automatic, selection);
        Assert.True(selection.IsAutomatic);
        Assert.Null(selection.LanguageTag);
    }

    [Theory]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData(" de-de ", "de-DE")]
    [InlineData("zh-hANS-cn", "zh-Hans-CN")]
    [InlineData("es-419", "es-419")]
    [InlineData("sl-ROZAJ-biske", "sl-rozaj-biske")]
    [InlineData("de-CH-1901", "de-CH-1901")]
    [InlineData("en-x-Company-Product", "en-x-company-product")]
    public void TryParse_ValidTag_ReturnsCanonicalExplicitSelection(
        string raw,
        string expected
    )
    {
        Assert.True(LanguageSelection.TryParse(raw, out var selection));
        Assert.False(selection.IsAutomatic);
        Assert.Equal(expected, selection.LanguageTag);
        Assert.Equal(expected, selection.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    [InlineData("notalang")]
    [InlineData("zz-QQ-!!")]
    [InlineData("en-")]
    [InlineData("en-x")]
    [InlineData("en-x-")]
    [InlineData("en-abcdefghi")]
    [InlineData("en-u-ca-gregory")]
    public void TryParse_InvalidOrBlankInput_ReturnsFalse(string? raw)
    {
        Assert.False(LanguageSelection.TryParse(raw, out var selection));
        Assert.Null(selection);
    }

    [Fact]
    public void Explicit_RejectsAutomaticAndInvalidInput()
    {
        Assert.Throws<ArgumentException>(() => LanguageSelection.Explicit("auto"));
        Assert.Throws<ArgumentException>(() => LanguageSelection.Explicit("notalang"));
    }
}
