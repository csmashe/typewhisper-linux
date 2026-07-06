using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Guards the interface-language JSON catalogs. English is the canonical
///     key set; other languages may be incomplete (missing keys fall back to
///     English at runtime via <c>Loc</c>) but must never contain orphan keys
///     that don't exist in English — those are typos or stale keys.
///     This replaces the compile-time safety a resx designer would have given.
/// </summary>
public sealed class LocalizationResourcesTests
{
    private const string CanonicalLanguage = "en";

    private static readonly JsonSerializerOptions s_jsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void CanonicalCatalogLoadsAndIsNonEmpty()
    {
        var en = Load(CanonicalLanguage);
        Assert.NotEmpty(en);
        Assert.DoesNotContain(en, kv => string.IsNullOrWhiteSpace(kv.Value));
    }

    [Theory]
    [MemberData(nameof(NonCanonicalLanguages))]
    public void TranslationKeysAreSubsetOfEnglish(string lang)
    {
        var en = Load(CanonicalLanguage);
        var translation = Load(lang);

        var orphans = translation.Keys.Where(k => !en.ContainsKey(k)).ToList();

        Assert.True(
            orphans.Count == 0,
            $"{lang}.json has keys not present in {CanonicalLanguage}.json: {string.Join(", ", orphans)}"
        );
    }

    [Theory]
    [MemberData(nameof(NonCanonicalLanguages))]
    public void TranslationValuesAreNonEmpty(string lang)
    {
        var translation = Load(lang);
        Assert.DoesNotContain(translation, kv => string.IsNullOrWhiteSpace(kv.Value));
    }

    public static TheoryData<string> NonCanonicalLanguages()
    {
        var data = new TheoryData<string>();
        var languages = Directory
            .EnumerateFiles(LocalizationDir(), "*.json")
            .Select(file => Path.GetFileNameWithoutExtension(file))
            .Where(lang => !string.Equals(lang, CanonicalLanguage, StringComparison.Ordinal));
        foreach (var lang in languages)
        {
            data.Add(lang);
        }

        return data;
    }

    private static Dictionary<string, string> Load(string lang)
    {
        var path = Path.Join(LocalizationDir(), $"{lang}.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), s_jsonOptions)!;
    }

    // Resolve the source-tree localization folder relative to THIS test file,
    // so the test doesn't depend on Content files being copied to test output.
    private static string LocalizationDir([CallerFilePath] string thisFile = "")
    {
        var testDir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(
            Path.Join(testDir, "..", "..", "src", "TypeWhisper.Linux", "Resources", "Localization")
        );
    }
}
