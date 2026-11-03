using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.FillerWords;

/// <summary>Removes filler words such as "um" and "uh" from transcribed text.</summary>
public sealed class FillerWordsPlugin : IPostProcessorPlugin, IPluginSettingsProvider, IPluginLocalizationAware
{
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
        [new("words", Loc.L("Settings.Title"), Description: Loc.L("Settings.Hint"), Kind: PluginSettingKind.Multiline)];

    public Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(key == "words" ? Settings?.WordsText ?? FillerWordsSettingsStore.DefaultWordsText : null);

    public Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default)
    {
        if (key != "words" || Settings is null)
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

    /// <summary>Removes the configured filler words from the transcription.</summary>
    public Task<string> ProcessAsync(string text, PostProcessingContext context, CancellationToken ct) =>
        Task.FromResult(
            FillerWordFilter.Remove(
                text,
                Settings?.WordsFor(context.SourceLanguage) ?? FillerWordFilter.DefaultWordsFor(context.SourceLanguage)));

    /// <summary>Releases plugin resources.</summary>
    public void Dispose()
    {
        Settings = null;
        _host = null;
    }
}
