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

    [Fact]
    public void CanonicalCatalog_HasNativeDictationDisclosuresWithoutObsoleteEvdevClaims()
    {
        var en = Load(CanonicalLanguage);
        var disclosureKeys = new[]
        {
            "Shortcuts.NativeDictationOwnershipActive",
            "Shortcuts.NativeDictationInstallDeferred",
            "Shortcuts.NativeDictationRemovalActive",
            "Shortcuts.NativeDictationRemovalDeferred"
        };

        foreach (var key in disclosureKeys)
        {
            Assert.True(en.TryGetValue(key, out var value), $"Missing canonical key: {key}");
            Assert.False(string.IsNullOrWhiteSpace(value), $"Canonical key is empty: {key}");
        }

        Assert.DoesNotContain("Shortcuts.EvdevDisabledForIntegration", en.Keys);
        Assert.DoesNotContain("Shortcuts.EvdevStillOffAfterRemoval", en.Keys);
    }

    [Fact]
    public void CanonicalCatalog_HasGlobalHotkeyOptOutMessages()
    {
        // GlobalHotkeySetupTaskTests assert through Loc.Instance, which returns the key itself
        // when it is missing — so the catalog needs its own explicit coverage.
        var en = Load(CanonicalLanguage);
        var keys = new[]
        {
            "Setup.GlobalHotkeyOptedOut",
            "Setup.GlobalHotkeyOptedOutRuleInstalled",
            "Setup.GlobalHotkeyOptedOutRuleInstalledDetail",
            "Setup.GlobalHotkeyRevokeButton"
        };

        foreach (var key in keys)
        {
            Assert.True(en.TryGetValue(key, out var value), $"Missing canonical key: {key}");
            Assert.False(string.IsNullOrWhiteSpace(value), $"Canonical key is empty: {key}");
        }
    }

    [Fact]
    public void CanonicalCatalog_HasDesktopIntegrationStaleAndRefreshMessages()
    {
        var en = Load(CanonicalLanguage);
        var keys = new[]
        {
            "Shortcuts.DesktopIntegrationStale",
            "Shortcuts.DesktopIntegrationStaleHint",
            "Shortcuts.DesktopIntegrationStaleUnsupported",
            "Shortcuts.RefreshDesktopIntegrationOn"
        };

        foreach (var key in keys)
        {
            Assert.True(en.TryGetValue(key, out var value), $"Missing canonical key: {key}");
            Assert.False(string.IsNullOrWhiteSpace(value), $"Canonical key is empty: {key}");
        }

        Assert.Contains("older hotkey or activation mode", en["Shortcuts.DesktopIntegrationStaleHint"]);
        Assert.Contains("old desktop shortcut may remain active", en["Shortcuts.DesktopIntegrationStaleHint"]);
        Assert.Contains("Refresh desktop integration", en["Shortcuts.RefreshDesktopIntegrationOn"]);
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
