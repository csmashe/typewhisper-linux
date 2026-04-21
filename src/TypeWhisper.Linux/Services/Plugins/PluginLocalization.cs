using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.Services.Plugins;

/// <summary>
/// Loads localized strings from JSON files in a plugin's Localization/ subdirectory.
/// Mirrors the Windows implementation but drops the WPF-backed Loc global;
/// current language defaults to CultureInfo.CurrentUICulture or an explicit override.
/// </summary>
public sealed class PluginLocalization : IPluginLocalization
{
    private const string FallbackLanguage = "en";
    private const string LocalizationFolder = "Localization";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, Dictionary<string, string>> _strings = [];

    public string CurrentLanguage { get; }
    public IReadOnlyList<string> AvailableLanguages { get; }

    public PluginLocalization(string pluginDirectory, string? languageOverride = null)
    {
        var localizationDir = Path.Combine(pluginDirectory, LocalizationFolder);
        CurrentLanguage = languageOverride
            ?? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        var available = new List<string>();

        if (Directory.Exists(localizationDir))
        {
            foreach (var file in Directory.EnumerateFiles(localizationDir, "*.json"))
            {
                var lang = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrEmpty(lang)) continue;

                try
                {
                    var json = File.ReadAllText(file);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
                    if (dict is not null)
                    {
                        _strings[lang] = dict;
                        available.Add(lang);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PluginLocalization] Failed to load {file}: {ex.Message}");
                }
            }
        }

        AvailableLanguages = available;
    }

    public string GetString(string key)
    {
        if (_strings.TryGetValue(CurrentLanguage, out var currentDict) &&
            currentDict.TryGetValue(key, out var value))
        {
            return value;
        }

        if (CurrentLanguage != FallbackLanguage &&
            _strings.TryGetValue(FallbackLanguage, out var fallbackDict) &&
            fallbackDict.TryGetValue(key, out var fallbackValue))
        {
            return fallbackValue;
        }

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
