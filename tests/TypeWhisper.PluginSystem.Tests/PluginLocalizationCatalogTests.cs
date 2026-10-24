using System.Text.Json;
using System.Text.RegularExpressions;

namespace TypeWhisper.PluginSystem.Tests;

/// <summary>
///     Every bundled plugin ships the same interface languages, each with every English key and the
///     same placeholders, so no plugin falls back to English inside a translated interface.
/// </summary>
public sealed partial class PluginLocalizationCatalogTests
{
    private static readonly string[] s_languages = ["de", "es", "ru", "ja", "zh"];

    public static TheoryData<string> PluginLocalizationDirectories()
    {
        var data = new TheoryData<string>();
        foreach (var directory in Directory.EnumerateDirectories(PluginCatalogTestData.PluginsDirectory)
                     .Select(plugin => Path.Join(plugin, "Localization"))
                     .Where(Directory.Exists)
                     .Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(Path.GetDirectoryName(directory))!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PluginLocalizationDirectories))]
    public void EveryLanguage_HasEveryKeyWithTheSamePlaceholders(string plugin)
    {
        var directory = Path.Join(PluginCatalogTestData.PluginsDirectory, plugin, "Localization");
        var english = Load(Path.Join(directory, "en.json"));
        Assert.Equal(
            s_languages.Append("en").Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(directory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Order(StringComparer.Ordinal));

        foreach (var language in s_languages)
        {
            var catalog = Load(Path.Join(directory, language + ".json"));
            Assert.Equal(english.Keys.Order(StringComparer.Ordinal), catalog.Keys.Order(StringComparer.Ordinal));
            foreach (var (key, value) in english)
            {
                Assert.False(string.IsNullOrWhiteSpace(catalog[key]), $"{plugin} {language}.json: {key} is empty");
                Assert.True(
                    Placeholders(value).SequenceEqual(Placeholders(catalog[key])),
                    $"{plugin} {language}.json: {key} placeholders differ from English");
            }
        }
    }

    private static Dictionary<string, string> Load(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;

    private static IEnumerable<string> Placeholders(string text) =>
        PlaceholderPattern().Matches(text).Select(match => match.Value).Order(StringComparer.Ordinal);

    [GeneratedRegex(@"\{\d+(?:[:,][^}]*)?\}")]
    private static partial Regex PlaceholderPattern();
}
