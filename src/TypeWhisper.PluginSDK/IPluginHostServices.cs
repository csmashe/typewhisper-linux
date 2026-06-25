// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
///     Services provided by the host application to plugins during activation.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface IPluginHostServices
{
    /// <summary>Directory where the plugin can store its own data files.</summary>
    string PluginDataDirectory { get; }

    /// <summary>
    ///     Directory for large model and runtime assets. Defaults to
    ///     <see cref="PluginDataDirectory"/>, but the host may redirect it to a
    ///     user-configured model storage location (e.g. a larger drive). Small
    ///     per-plugin config (settings.json) stays under <see cref="PluginDataDirectory"/>.
    /// </summary>
    string PluginAssetDirectory => PluginDataDirectory;

    /// <summary>Process name of the currently active foreground application, or null.</summary>
    // ReSharper disable once UnusedMember.Global
    string? ActiveAppProcessName { get; }

    /// <summary>Display name of the currently active foreground application, or null.</summary>
    // ReSharper disable once UnusedMember.Global
    string? ActiveAppName { get; }

    /// <summary>Event bus for publishing and subscribing to plugin events.</summary>
    IPluginEventBus EventBus { get; }

    /// <summary>Names of all available dictation profiles.</summary>
    // ReSharper disable once UnusedMember.Global
    IReadOnlyList<string> AvailableProfileNames { get; }

    /// <summary>Localization service; loads strings from the plugin's Localization/ subdirectory (e.g. en.json).</summary>
    IPluginLocalization Localization { get; }

    /// <summary>Stores a secret value using the platform secret store, scoped to the plugin.</summary>
    // ReSharper disable once UnusedMemberInSuper.Global
    Task StoreSecretAsync(string key, string value);

    /// <summary>Loads a previously stored secret, or null if not found.</summary>
    // ReSharper disable once UnusedMember.Global
    Task<string?> LoadSecretAsync(string key);

    /// <summary>Deletes a stored secret.</summary>
    // ReSharper disable once UnusedMember.Global
    Task DeleteSecretAsync(string key);

    /// <summary>Gets a per-plugin setting value deserialized from JSON, or default if not found.</summary>
    // ReSharper disable once UnusedMemberInSuper.Global
    T? GetSetting<T>(string key);

    /// <summary>Sets a per-plugin setting value (serialized to JSON).</summary>
    // ReSharper disable once UnusedMemberInSuper.Global
    void SetSetting<T>(string key, T value);

    /// <summary>Logs a message through the host logging system.</summary>
    void Log(PluginLogLevel level, string message);

    /// <summary>
    ///     Notifies the host that the plugin's capabilities have changed (e.g. new models available).
    ///     The host will rebuild its capability indices and update the UI accordingly.
    /// </summary>
    // ReSharper disable once UnusedMemberInSuper.Global
    void NotifyCapabilitiesChanged();

    /// <summary>
    ///     Signals that the plugin is rendering its own streaming overlay.
    ///     While active, the host suppresses its built-in overlay to avoid duplication.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedParameter.Global
    void SetStreamingDisplayActive(bool active) { }
}
