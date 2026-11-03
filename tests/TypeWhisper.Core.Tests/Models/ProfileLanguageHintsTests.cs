using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Tests.Models;

public sealed class ProfileLanguageHintsTests
{
    private static Profile Create(string? language = null) => new() { Id = "test", Name = "Test", InputLanguage = language };

    [Fact]
    public void GetLanguageHints_StoredHintsExtendTheProfileLanguage() =>
        Assert.Equal(["de", "fr", "en"], (Create("de") with { InputLanguageHints = [" fr ", "en", "DE"] }).GetLanguageHints(["it"]));

    [Fact]
    public void GetLanguageHints_NullStoredHints_AreIgnored() =>
        Assert.Equal(["de"], (Create("de") with { InputLanguageHints = null! }).GetLanguageHints(["it"]));

    [Fact]
    public void GetLanguageHints_AutoInputLanguage_IgnoresStoredHints() =>
        Assert.Empty((Create("auto") with { InputLanguageHints = ["fr", "en"] }).GetLanguageHints(["de"]));

    [Fact]
    public void GetLanguageHints_BlankInputLanguage_IgnoresStoredHints() =>
        Assert.Equal(["de"], (Create() with { InputLanguageHints = ["fr"] }).GetLanguageHints(["de"]));

    [Fact]
    public void GetLanguageHints_BlankInputLanguage_InheritsGlobal()
    {
        Assert.Equal(["de", "en"], Create().GetLanguageHints([" de ", "en", "DE"]));
        Assert.Equal(["de"], Create(" ").GetLanguageHints(["de"]));
    }

    [Fact]
    public void GetLanguageHints_AutoInputLanguage_ClearsHints() => Assert.Empty(Create("auto").GetLanguageHints(["de"]));

    [Fact]
    public void GetLanguageHints_SingleInputLanguage_OverridesGlobal() => Assert.Equal(["fr"], Create("fr").GetLanguageHints(["de", "en"]));
}
