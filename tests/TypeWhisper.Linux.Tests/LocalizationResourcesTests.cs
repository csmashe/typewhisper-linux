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
    public void CanonicalCatalog_HasAutostartPreservationMessageWithPathPlaceholder()
    {
        var en = Load(CanonicalLanguage);

        Assert.True(
            en.TryGetValue("General.AutostartEntryPreserved", out var value),
            "Missing canonical key: General.AutostartEntryPreserved"
        );
        Assert.False(string.IsNullOrWhiteSpace(value));
        Assert.Contains("{0}", value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("ru")]
    public void Catalogs_HaveUiOperationFailureStringsWithRequiredPlaceholders(
        string language
    )
    {
        var catalog = Load(language);

        Assert.True(
            catalog.TryGetValue("Common.OperationFailed", out var pattern),
            $"Missing {language} key: Common.OperationFailed"
        );
        Assert.Contains("{0}", pattern, StringComparison.Ordinal);
        Assert.Contains("{1}", pattern, StringComparison.Ordinal);
        Assert.True(
            catalog.TryGetValue("Common.OperationFailedTitle", out var title),
            $"Missing {language} key: Common.OperationFailedTitle"
        );
        Assert.False(string.IsNullOrWhiteSpace(title));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("ru")]
    public void Catalogs_HaveSecretProtectionWarningsWithRequiredPlaceholder(
        string language
    )
    {
        var catalog = Load(language);
        var keys = new[]
        {
            "Security.BackupBlockedByUnresolvedSecrets",
            "Security.SecretMigrationWarning",
            "Security.SecretMigrationWarningTitle",
            "Security.SecretProtectionUnavailable",
        };

        foreach (var key in keys)
        {
            Assert.True(
                catalog.TryGetValue(key, out var value),
                $"Missing {language} key: {key}"
            );
            Assert.False(
                string.IsNullOrWhiteSpace(value),
                $"{language} key is empty: {key}"
            );
        }

        Assert.Contains(
            "{0}",
            catalog["Security.BackupBlockedByUnresolvedSecrets"],
            StringComparison.Ordinal
        );
        Assert.Contains(
            "{0}",
            catalog["Security.SecretMigrationWarning"],
            StringComparison.Ordinal
        );

        if (language == CanonicalLanguage)
        {
            return;
        }

        var en = Load(CanonicalLanguage);
        foreach (var key in keys)
        {
            Assert.NotEqual(en[key], catalog[key]);
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("ru")]
    public void Catalogs_HaveTerminalClipboardFallback(string language)
    {
        var catalog = Load(language);

        Assert.True(
            catalog.TryGetValue("TextInsertion.TerminalClipboardFallback", out var value),
            $"Missing {language} key: TextInsertion.TerminalClipboardFallback"
        );
        Assert.False(
            string.IsNullOrWhiteSpace(value),
            $"{language} key is empty: TextInsertion.TerminalClipboardFallback"
        );
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("ru")]
    public void Catalogs_HaveRecentTranscriptionFeedbackStringsWithRequiredPlaceholders(
        string language
    )
    {
        var catalog = Load(language);
        var keysWithoutPlaceholders = new[]
        {
            "RecentTranscriptions.CopiedToClipboard",
            "RecentTranscriptions.InsertionFailed",
            "RecentTranscriptions.Pasted",
            "RecentTranscriptions.PasteToolInstallHintWayland",
            "RecentTranscriptions.PasteToolInstallHintWaylandYdotool",
            "RecentTranscriptions.PasteToolInstallHintX11",
            "RecentTranscriptions.Typed",
        };

        foreach (var key in keysWithoutPlaceholders)
        {
            Assert.True(
                catalog.TryGetValue(key, out var value),
                $"Missing {language} key: {key}"
            );
            Assert.False(
                string.IsNullOrWhiteSpace(value),
                $"{language} key is empty: {key}"
            );
        }

        Assert.True(
            catalog.TryGetValue("TextInsertion.ClipboardInstallHint", out var clipboardHint),
            $"Missing {language} key: TextInsertion.ClipboardInstallHint"
        );
        Assert.Contains("{0}", clipboardHint, StringComparison.Ordinal);
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
            "Shortcuts.NativeDictationRemovalDeferred",
        };

        foreach (var key in disclosureKeys)
        {
            Assert.True(en.TryGetValue(key, out var value), $"Missing canonical key: {key}");
            Assert.False(string.IsNullOrWhiteSpace(value), $"Canonical key is empty: {key}");
        }

        Assert.DoesNotContain("Shortcuts.EvdevDisabledForIntegration", en.Keys);
        Assert.DoesNotContain("Shortcuts.EvdevStillOffAfterRemoval", en.Keys);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("ru")]
    public void Catalogs_HaveNativeDictationHeuristicDisclosures(string language)
    {
        var catalog = Load(language);
        var disclosureKeys = new[]
        {
            "Shortcuts.NativeDictationInstallDeferred",
            "Shortcuts.NativeDictationRemovalDeferred",
            "Shortcuts.DesktopIntegrationStaleHint",
            "Shortcuts.DesktopIntegrationStaleUnsupported",
        };

        foreach (var key in disclosureKeys)
        {
            Assert.True(catalog.TryGetValue(key, out var value), $"Missing {language} key: {key}");
            Assert.False(string.IsNullOrWhiteSpace(value), $"{language} key is empty: {key}");
        }

        var unsupported = catalog["Shortcuts.DesktopIntegrationStaleUnsupported"];
        Assert.Contains("{0}", unsupported, StringComparison.Ordinal);
        Assert.Contains("{1}", unsupported, StringComparison.Ordinal);

        if (language != CanonicalLanguage)
        {
            return;
        }

        foreach (
            var key in new[]
            {
                "Shortcuts.NativeDictationInstallDeferred",
                "Shortcuts.NativeDictationRemovalDeferred",
            }
        )
        {
            Assert.Contains("reload or re-login", catalog[key], StringComparison.Ordinal);
            Assert.DoesNotContain("startup", catalog[key], StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("ru")]
    public void Catalogs_HaveDynamicHotkeyRejectionStrings(string language)
    {
        var catalog = Load(language);
        // Loc.GetString swallows FormatException, so a dropped placeholder degrades
        // silently at runtime — assert the format args survive translation.
        var blankIdKeys = new[]
        {
            "Shortcuts.ProfileHotkeyInactiveBlankId",
            "Shortcuts.PromptActionHotkeyInactiveBlankId",
        };
        var conflictKeys = new[]
        {
            "Shortcuts.ProfileHotkeyInactiveConflict",
            "Shortcuts.PromptActionHotkeyInactiveConflict",
        };

        foreach (var key in blankIdKeys.Concat(conflictKeys))
        {
            Assert.True(
                catalog.TryGetValue(key, out var value),
                $"Missing {language} key: {key}"
            );
            Assert.False(string.IsNullOrWhiteSpace(value), $"{language} key is empty: {key}");
            Assert.Contains("{0}", value, StringComparison.Ordinal);
        }

        foreach (var key in conflictKeys)
        {
            Assert.Contains("{1}", catalog[key], StringComparison.Ordinal);
        }
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
            "Setup.GlobalHotkeyRevokeButton",
        };

        foreach (var key in keys)
        {
            Assert.True(en.TryGetValue(key, out var value), $"Missing canonical key: {key}");
            Assert.False(string.IsNullOrWhiteSpace(value), $"Canonical key is empty: {key}");
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("ru")]
    public void Catalogs_HaveGlobalHotkeyRevocationMessages(string language)
    {
        var catalog = Load(language);
        var keys = new[]
        {
            "Setup.GlobalHotkeyAccessRevoked",
            "Setup.GlobalHotkeyAddedRelogin",
            "Setup.GlobalHotkeyGroupRevokeFailedDetail",
            "Setup.GlobalHotkeyGroupRevokedDetail",
            "Setup.GlobalHotkeyOptedOutRuleInstalled",
            "Setup.GlobalHotkeyOptedOutRuleInstalledDetail",
            "Setup.GlobalHotkeyReloginToRevoke",
            "Setup.GlobalHotkeyRevokeFailed",
        };

        foreach (var key in keys)
        {
            Assert.True(
                catalog.TryGetValue(key, out var value),
                $"Missing {language} key: {key}"
            );
            Assert.False(string.IsNullOrWhiteSpace(value), $"{language} key is empty: {key}");
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
            "Shortcuts.RefreshDesktopIntegrationOn",
        };

        foreach (var key in keys)
        {
            Assert.True(en.TryGetValue(key, out var value), $"Missing canonical key: {key}");
            Assert.False(string.IsNullOrWhiteSpace(value), $"Canonical key is empty: {key}");
        }

        Assert.Contains("older hotkey or activation mode", en["Shortcuts.DesktopIntegrationStaleHint"]);
        Assert.Contains("old desktop shortcut may stay authoritative", en["Shortcuts.DesktopIntegrationStaleHint"]);
        Assert.Contains("Refresh desktop integration", en["Shortcuts.RefreshDesktopIntegrationOn"]);
    }

    [Theory]
    [InlineData(
        "en",
        "Local",
        "Cloud",
        "Mixed",
        "User controlled",
        "Text-to-Speech",
        "Integrations",
        "Unknown"
    )]
    [InlineData(
        "de",
        "Lokal",
        "Cloud",
        "Gemischt",
        "Benutzergesteuert",
        "Sprachausgabe",
        "Integrationen",
        "Unbekannt"
    )]
    [InlineData(
        "es",
        "Local",
        "Nube",
        "Mixto",
        "Controlado por el usuario",
        "Texto a voz",
        "Integraciones",
        "Desconocido"
    )]
    [InlineData(
        "ru",
        "Локально",
        "Облако",
        "Смешанный",
        "Управляется пользователем",
        "Синтез речи",
        "Интеграции",
        "Неизвестно"
    )]
    public void Catalogs_HaveNetworkAccessAndNewCategoryLabels(
        string language,
        string local,
        string network,
        string mixed,
        string userControlled,
        string tts,
        string integration,
        string unknown
    )
    {
        var catalog = Load(language);

        AssertCatalogValue(catalog, language, "Plugins.BadgeLocal", local);
        AssertCatalogValue(catalog, language, "Plugins.BadgeCloud", network);
        AssertCatalogValue(catalog, language, "Plugins.BadgeMixed", mixed);
        AssertCatalogValue(catalog, language, "Plugins.BadgeUserControlled", userControlled);
        AssertCatalogValue(catalog, language, "Plugins.CategoryTts", tts);
        AssertCatalogValue(catalog, language, "Plugins.CategoryIntegration", integration);
        AssertCatalogValue(catalog, language, "Plugins.CategoryUnknown", unknown);
    }

    private static void AssertCatalogValue(
        Dictionary<string, string> catalog,
        string language,
        string key,
        string expected
    )
    {
        Assert.True(catalog.TryGetValue(key, out var actual), $"Missing {language} key: {key}");
        Assert.Equal(expected, actual);
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
