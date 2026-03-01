namespace TypeWhisper.PluginSDK;

/// <summary>
/// Provides localized strings for a plugin from JSON files in the Localization/ subdirectory.
/// Files are named by language code (e.g. en.json, de.json) and contain flat key-value pairs.
/// </summary>
public interface IPluginLocalization
{
    /// <summary>Current language code (e.g. "de", "en").</summary>
    string CurrentLanguage { get; }

    /// <summary>All available language codes for this plugin.</summary>
    IReadOnlyList<string> AvailableLanguages { get; }

    /// <summary>
    /// Returns the localized string for the given key.
    /// Falls back to English, then returns the key itself if not found.
    /// </summary>
    string GetString(string key);

    /// <summary>
    /// Returns the localized string for the given key with format arguments.
    /// Falls back to English, then returns the key itself if not found.
    /// </summary>
    string GetString(string key, params object[] args);
}
