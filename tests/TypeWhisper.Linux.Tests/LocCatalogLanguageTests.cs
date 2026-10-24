using System.Globalization;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class LocCatalogLanguageTests
{
    [Theory]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-HK", "zh-Hant")]
    [InlineData("zh-MO", "zh-Hant")]
    [InlineData("zh-Hant", "zh-Hant")]
    [InlineData("zh-Hant-TW", "zh-Hant")]
    [InlineData("zh-CN", "zh")]
    [InlineData("zh-SG", "zh")]
    [InlineData("zh-Hans", "zh")]
    [InlineData("zh", "zh")]
    [InlineData("ja-JP", "ja")]
    [InlineData("de-AT", "de")]
    public void CatalogLanguageFor_KeepsTraditionalChineseOffTheSimplifiedCatalog(string culture, string expected)
    {
        Assert.Equal(expected, Loc.CatalogLanguageFor(CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void AutoOnTraditionalChinese_ResolvesToEnglish()
    {
        var previous = Loc.SystemLanguage;
        try
        {
            Loc.SystemLanguage = Loc.CatalogLanguageFor(CultureInfo.GetCultureInfo("zh-TW"));
            Assert.Equal("en", Loc.Instance.ResolveLanguage(null));

            Loc.SystemLanguage = Loc.CatalogLanguageFor(CultureInfo.GetCultureInfo("zh-CN"));
            Assert.Equal("zh", Loc.Instance.ResolveLanguage(null));
        }
        finally
        {
            Loc.SystemLanguage = previous;
        }
    }

    [Fact]
    public void PluginOnTraditionalChinese_FallsBackToEnglish()
    {
        var directory = Directory.CreateTempSubdirectory("tw-plugin-loc-");
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            var localization = Directory.CreateDirectory(Path.Join(directory.FullName, "Localization"));
            File.WriteAllText(Path.Join(localization.FullName, "en.json"), """{"Greeting": "Hello"}""");
            File.WriteAllText(Path.Join(localization.FullName, "zh.json"), """{"Greeting": "你好"}""");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-TW");

            var sut = new PluginLocalization(directory.FullName);

            Assert.Equal("zh-Hant", sut.CurrentLanguage);
            Assert.Equal("Hello", sut.GetString("Greeting"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
            directory.Delete(recursive: true);
        }
    }
}
