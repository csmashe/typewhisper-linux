namespace TypeWhisper.PluginSDK;

/// <summary>
/// Optional interface for plugins that expose user-configurable settings.
/// The host renders a generic settings UI driven by the definitions returned here.
/// All setting values are stored and retrieved as plain strings; plugins are
/// responsible for parsing them into their native types.
/// </summary>
public interface IPluginSettingsProvider
{
    /// <summary>Returns the list of settings this plugin exposes to the host UI.</summary>
    IReadOnlyList<PluginSettingDefinition> GetSettingDefinitions();

    /// <summary>Returns the current value for the given setting key, or null if unset.</summary>
    Task<string?> GetSettingValueAsync(string key, CancellationToken ct = default);

    /// <summary>Persists a new value for the given setting key. Null clears the value.</summary>
    Task SetSettingValueAsync(string key, string? value, CancellationToken ct = default);

    /// <summary>
    /// Validates the current settings (e.g. connectivity check, key format check).
    /// Returns a result with <see cref="PluginSettingsValidationResult.IsSuccess"/> false
    /// on failure, or null to skip validation entirely.
    /// </summary>
    Task<PluginSettingsValidationResult?> ValidateAsync(CancellationToken ct = default)
        => Task.FromResult<PluginSettingsValidationResult?>(null);
}

/// <summary>
/// Controls how the host renders a setting field.
/// <see cref="Auto"/> lets the host infer the kind from other definition properties.
/// </summary>
public enum PluginSettingKind
{
    Auto,
    Text,
    Secret,
    Dropdown,
    Boolean,
    Multiline
}

/// <summary>
/// Describes a single configurable setting exposed by a plugin.
/// </summary>
/// <param name="Key">Unique key for this setting within the plugin.</param>
/// <param name="Label">Display label shown in the settings UI.</param>
/// <param name="IsSecret">If true, the value is masked in the UI and stored securely.</param>
/// <param name="Placeholder">Hint text shown when the field is empty.</param>
/// <param name="Description">Optional description shown below the field.</param>
/// <param name="Options">Allowed values for <see cref="PluginSettingKind.Dropdown"/> settings.</param>
/// <param name="Kind">How the host should render this setting field.</param>
public sealed record PluginSettingDefinition(
    string Key,
    string Label,
    bool IsSecret = false,
    string? Placeholder = null,
    string? Description = null,
    IReadOnlyList<PluginSettingOption>? Options = null,
    PluginSettingKind Kind = PluginSettingKind.Auto);

/// <summary>A selectable option for a dropdown setting.</summary>
public sealed record PluginSettingOption(
    string Value,
    string Label);

/// <summary>Outcome of a plugin settings validation pass.</summary>
public sealed record PluginSettingsValidationResult(
    bool IsSuccess,
    string Message);
