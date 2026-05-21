using System.Text.Json;
using TypeWhisper.Linux.Services.Plugins;

namespace TypeWhisper.PluginSystem.Tests;

public class PluginLocalizationTests : IDisposable
{
    private readonly string _locDir;
    private readonly string _pluginDir;

    public PluginLocalizationTests()
    {
        _pluginDir = Path.Combine(Path.GetTempPath(), $"tw-loc-test-{Guid.NewGuid():N}");
        _locDir = Path.Combine(_pluginDir, "Localization");
        Directory.CreateDirectory(_locDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_pluginDir, true);
        }
        catch
        {
            /* best effort */
        }
    }

    private void WriteLocale(string lang, Dictionary<string, string> strings)
    {
        var json = JsonSerializer.Serialize(strings);
        File.WriteAllText(Path.Combine(_locDir, $"{lang}.json"), json);
    }

    [Fact]
    public void GetString_ReturnsKeyWhenNoLocalizationFolder()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), $"tw-loc-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyDir);

        try
        {
            var loc = new PluginLocalization(emptyDir, "de");
            Assert.Equal("greeting", loc.GetString("greeting"));
            Assert.Empty(loc.AvailableLanguages);
        }
        finally
        {
            Directory.Delete(emptyDir, true);
        }
    }

    [Fact]
    public void GetString_ReturnsLocalizedValue()
    {
        WriteLocale("de", new Dictionary<string, string> { ["greeting"] = "Hallo", ["farewell"] = "Tschuess" });

        var loc = new PluginLocalization(_pluginDir, "de");

        Assert.Equal("Hallo", loc.GetString("greeting"));
        Assert.Equal("Tschuess", loc.GetString("farewell"));
    }

    [Fact]
    public void GetString_FallsBackToEnglish()
    {
        WriteLocale("en", new Dictionary<string, string> { ["greeting"] = "Hello", ["only_en"] = "English only" });
        WriteLocale("de", new Dictionary<string, string> { ["greeting"] = "Hallo" });

        var loc = new PluginLocalization(_pluginDir, "de");

        Assert.Equal("Hallo", loc.GetString("greeting"));
        Assert.Equal("English only", loc.GetString("only_en"));
    }

    [Fact]
    public void GetString_ReturnsKeyWhenNotInAnyLanguage()
    {
        WriteLocale("en", new Dictionary<string, string> { ["greeting"] = "Hello" });

        var loc = new PluginLocalization(_pluginDir, "de");

        Assert.Equal("unknown.key", loc.GetString("unknown.key"));
    }

    [Fact]
    public void GetString_WithFormatArgs()
    {
        WriteLocale("en", new Dictionary<string, string> { ["welcome"] = "Welcome, {0}! You have {1} items." });

        var loc = new PluginLocalization(_pluginDir, "en");

        Assert.Equal("Welcome, Marco! You have 5 items.", loc.GetString("welcome", "Marco", 5));
    }

    [Fact]
    public void GetString_WithFormatArgs_InvalidFormat_ReturnsTemplate()
    {
        WriteLocale("en", new Dictionary<string, string> { ["broken"] = "No placeholders here" });

        var loc = new PluginLocalization(_pluginDir, "en");

        Assert.Equal("No placeholders here", loc.GetString("broken", "extra"));
    }

    [Fact]
    public void AvailableLanguages_ListsAllLoadedFiles()
    {
        WriteLocale("en", new Dictionary<string, string> { ["a"] = "A" });
        WriteLocale("de", new Dictionary<string, string> { ["a"] = "A" });
        WriteLocale("fr", new Dictionary<string, string> { ["a"] = "A" });

        var loc = new PluginLocalization(_pluginDir, "en");

        Assert.Equal(3, loc.AvailableLanguages.Count);
        Assert.Contains("en", loc.AvailableLanguages);
        Assert.Contains("de", loc.AvailableLanguages);
        Assert.Contains("fr", loc.AvailableLanguages);
    }

    [Fact]
    public void CurrentLanguage_ReturnsOverride()
    {
        var loc = new PluginLocalization(_pluginDir, "fr");
        Assert.Equal("fr", loc.CurrentLanguage);
    }

    [Fact]
    public void GetString_EnglishFallback_NotUsedWhenCurrentIsEnglish()
    {
        WriteLocale("en", new Dictionary<string, string> { ["greeting"] = "Hello" });

        var loc = new PluginLocalization(_pluginDir, "en");

        Assert.Equal("Hello", loc.GetString("greeting"));
        Assert.Equal("missing", loc.GetString("missing"));
    }

    [Fact]
    public void GetString_MalformedJson_SkipsFile()
    {
        File.WriteAllText(Path.Combine(_locDir, "bad.json"), "{ not valid json }}}");
        WriteLocale("en", new Dictionary<string, string> { ["ok"] = "OK" });

        var loc = new PluginLocalization(_pluginDir, "en");

        Assert.DoesNotContain("bad", loc.AvailableLanguages);
        Assert.Contains("en", loc.AvailableLanguages);
        Assert.Equal("OK", loc.GetString("ok"));
    }
}