using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.FillerWords;

/// <summary>Removes filler words such as "um" and "uh" from transcribed text and collapses stutters such as "I I I".</summary>
public sealed class FillerWordsPlugin : IPostProcessorPlugin, IPluginSettingsProvider, IPluginLocalizationAware
{
    private const string WordsKey = "words";
    private const string CollapseRepeatedWordsKey = "collapseRepeatedWords";

    private IPluginHostServices? _host;
    private IPluginLocalization? _injectedLocalization;

    public void SetLocalization(IPluginLocalization localization) =>
        _injectedLocalization = localization;

    /// <summary>Gets the stable plugin identifier used by the host.</summary>
    public string PluginId => "com.typewhisper.filler-words";

    /// <summary>Gets the plugin display name shown by the host.</summary>
    public string PluginName => "Filler Words";

    /// <summary>Gets the plugin version reported to the host.</summary>
    public string PluginVersion => PluginBuildInfo.Version;

    /// <summary>Gets the processor name shown in the post-processing pipeline.</summary>
    public string ProcessorName => "Filler Words";

    /// <summary>Gets the post-processing priority. Lower values run first.</summary>
    // After Spoken Commands (50) and Punctuation (60), so a filler next to a command word goes
    // once the command is consumed, and before number normalization, cleanup and the LLM.
    public int Priority => 70;

    /// <summary>Gets the settings store, or null when the plugin is not activated.</summary>
    public FillerWordsSettingsStore? Settings { get; private set; }

    private IPluginLocalization? Loc => _host?.Localization ?? _injectedLocalization;

    /// <summary>Activates the plugin and loads the configured filler word list.</summary>
    public Task ActivateAsync(IPluginHostServices host)
    {
        _host = host;
        Settings = new FillerWordsSettingsStore(host);
        return Task.CompletedTask;
    }

    /// <summary>Deactivates the plugin.</summary>
    public Task DeactivateAsync()
    {
        Settings = null;
        _host = null;
        return Task.CompletedTask;
    }

    public IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions() =>
    [
        new(WordsKey, Loc.L("Settings.Title"), Description: Loc.L("Settings.Hint"), Kind: PluginSettingKind.Multiline),
        new(
            CollapseRepeatedWordsKey,
            Loc.L("Settings.CollapseRepeatedWords"),
            Description: Loc.L("Settings.CollapseRepeatedWordsHint"),
            Kind: PluginSettingKind.Boolean),
    ];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(key switch
        {
            WordsKey => Settings?.WordsText ?? FillerWordsSettingsStore.DefaultWordsText,
            CollapseRepeatedWordsKey => Settings?.CollapseRepeatedWords ?? true ? "true" : "false",
            _ => null,
        });

    public Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
    {
        if (Settings is null)
        {
            return Task.CompletedTask;
        }

        if (key == CollapseRepeatedWordsKey)
        {
            // Null resets to the default (on).
            Settings.CollapseRepeatedWords = value is null || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            return Task.CompletedTask;
        }

        if (key != WordsKey)
        {
            return Task.CompletedTask;
        }

        if (value is null)
        {
            Settings.ResetToDefaults();
        }
        else
        {
            Settings.WordsText = value;
        }

        return Task.CompletedTask;
    }

    public Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
    {
        var count = Settings?.WordCount ?? FillerWordFilter.DefaultFillerWords.Count;
        return Task.FromResult<PluginSettingsValidationResult?>(
            new PluginSettingsValidationResult(true, Loc.L("Settings.WordCount", count)));
    }

    /// <summary>Removes the configured filler words and, unless turned off, collapses stutters.</summary>
    public Task<string> ProcessAsync(string text, PostProcessingContext context, CancellationToken ct)
    {
        var settings = Settings;
        var language = FillerWordFilter.ResolveLanguage(context.SourceLanguage, text);
        var result = FillerWordFilter.Remove(
            text,
            settings?.WordsFor(language) ?? FillerWordFilter.DefaultWordsFor(language));

        return Task.FromResult(settings?.CollapseRepeatedWords ?? true ? RepeatedWordCollapser.Collapse(result) : result);
    }

    /// <summary>Releases plugin resources.</summary>
    public void Dispose()
    {
        Settings = null;
        _host = null;
    }
}
