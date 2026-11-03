using TypeWhisper.Core.Services.SpokenFormatting;

namespace TypeWhisper.Core.Tests.Services;

public sealed class SpokenFormattingRulesLoaderTests
{
    [Fact]
    public void LoadsFourLanguagesAndNormalizesRegionalCodes()
    {
        var loader = new SpokenFormattingRulesLoader();
        Assert.Equal(["de", "en", "es", "ru"], loader.SupportedLanguages.Order());
        Assert.Same(loader.RuleSetFor("es"), loader.RuleSetFor("es-MX"));
        Assert.False(loader.Supports("auto"));
    }
}
