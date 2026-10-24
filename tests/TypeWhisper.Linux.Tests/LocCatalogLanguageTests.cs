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
    public void Plugin_FollowsALiveInterfaceLanguageSwitch()
    {
        var originalLanguage = Loc.Instance.CurrentLanguage;
        var directory = CreatePluginCatalogs();
        try
        {
            var sut = new PluginLocalization(directory.FullName);

            Loc.Instance.CurrentLanguage = "en";
            Assert.Equal("Hello", sut.GetString("Greeting"));

            Loc.Instance.CurrentLanguage = "de";
            Assert.Equal("de", sut.CurrentLanguage);
            Assert.Equal("Hallo", sut.GetString("Greeting"));
            Assert.Equal("Hallo Ada", sut.GetString("GreetingName", "Ada"));
        }
        finally
        {
            Loc.Instance.CurrentLanguage = originalLanguage;
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void PluginOnTraditionalChineseAuto_FallsBackToEnglish()
    {
        var originalLanguage = Loc.Instance.CurrentLanguage;
        var originalSystem = Loc.SystemLanguage;
        var directory = CreatePluginCatalogs();
        try
        {
            Loc.SystemLanguage = Loc.CatalogLanguageFor(CultureInfo.GetCultureInfo("zh-TW"));
            Loc.Instance.CurrentLanguage = Loc.Instance.ResolveLanguage(null);

            var sut = new PluginLocalization(directory.FullName);

            Assert.Equal("en", sut.CurrentLanguage);
            Assert.Equal("Hello", sut.GetString("Greeting"));
        }
        finally
        {
            Loc.SystemLanguage = originalSystem;
            Loc.Instance.CurrentLanguage = originalLanguage;
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Plugin_LanguageOverrideIgnoresTheInterfaceLanguage()
    {
        var originalLanguage = Loc.Instance.CurrentLanguage;
        var directory = CreatePluginCatalogs();
        try
        {
            Loc.Instance.CurrentLanguage = "en";
            var sut = new PluginLocalization(directory.FullName, "zh");

            Assert.Equal("你好", sut.GetString("Greeting"));
        }
        finally
        {
            Loc.Instance.CurrentLanguage = originalLanguage;
            directory.Delete(recursive: true);
        }
    }

    private static DirectoryInfo CreatePluginCatalogs()
    {
        var directory = Directory.CreateTempSubdirectory("plugin-loc-");
        var localization = Directory.CreateDirectory(Path.Join(directory.FullName, "Localization"));
        File.WriteAllText(Path.Join(localization.FullName, "en.json"), """{"Greeting": "Hello", "GreetingName": "Hello {0}"}""");
        File.WriteAllText(Path.Join(localization.FullName, "de.json"), """{"Greeting": "Hallo", "GreetingName": "Hallo {0}"}""");
        File.WriteAllText(Path.Join(localization.FullName, "zh.json"), """{"Greeting": "你好", "GreetingName": "你好{0}"}""");
        return directory;
    }
}
