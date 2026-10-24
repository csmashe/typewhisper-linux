using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services.Plugins;

/// <summary>
///     loads localized strings from a plugin's Localization/ subdirectory.
///     Without an override the language follows the host interface language, live, so plugin
///     text switches with it; outside the app (no host catalogs) it uses the UI culture.
/// </summary>
public sealed class PluginLocalization : IPluginLocalization
{
    private const string FallbackLanguage = "en";
    private const string LocalizationFolder = "Localization";

    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly Dictionary<string, Dictionary<string, string>> _strings = [];
    // ReSharper disable once ReplaceWithFieldKeyword -- assigned in the constructor, where `field` is inaccessible; CurrentLanguage must stay computed to follow the host language.
    private readonly string? _languageOverride;

    public PluginLocalization(string pluginDirectory, string? languageOverride = null)
    {
        var localizationDir = Path.Join(pluginDirectory, LocalizationFolder);
        _languageOverride = languageOverride;

        var available = new List<string>();

        if (Directory.Exists(localizationDir))
        {
            foreach (var file in Directory.EnumerateFiles(localizationDir, "*.json"))
            {
                var lang = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrEmpty(lang))
                {
                    continue;
                }

                try
                {
                    var json = File.ReadAllText(file);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(
                        json,
                        s_jsonOptions
                    );
                    if (dict is not null)
                    {
                        _strings[lang] = dict;
                        available.Add(lang);
                    }
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[PluginLocalization] Failed to load {file}: {ex.Message}");
                }
            }
        }

        AvailableLanguages = available;
    }

    // Loc resolves Auto (including Traditional Chinese to English) before it sets this.
    public string CurrentLanguage =>
        _languageOverride
        ?? (Loc.Instance.AvailableLanguages.Count > 0
            ? Loc.Instance.CurrentLanguage
            : Loc.CatalogLanguageFor(CultureInfo.CurrentUICulture));
    public IReadOnlyList<string> AvailableLanguages { get; }

    public string GetString(string key)
    {
        var language = CurrentLanguage;
        if (
            _strings.TryGetValue(language, out var currentDict)
            && currentDict.TryGetValue(key, out var value)
        )
        {
            return value;
        }

        if (
            language != FallbackLanguage
            && _strings.TryGetValue(FallbackLanguage, out var fallbackDict)
            && fallbackDict.TryGetValue(key, out var fallbackValue)
        )
        {
            return fallbackValue;
        }

        // Return the raw key rather than an empty string or throwing so
        // missing translations degrade gracefully — the key is usually
        // human-readable and visible in the UI as a hint for translators.
        return key;
    }

    public string GetString(string key, params object[] args)
    {
        var template = GetString(key);
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}