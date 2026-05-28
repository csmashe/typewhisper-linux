namespace TypeWhisper.PluginSDK;

/// <summary>
///     Base interface for all TypeWhisper plugins. The host owns the plugin's lifetime:
///     <c>ActivateAsync</c> is invoked once after construction, <c>DeactivateAsync</c>
///     before <c>Dispose</c>. Plugins must not block these methods — long-running work
///     belongs on a background task — or the host UI will hang during startup/shutdown.
/// </summary>
public interface ITypeWhisperPlugin : IDisposable
{
    /// <summary>Unique identifier for the plugin (e.g. "com.example.my-plugin").</summary>
    string PluginId { get; }

    /// <summary>Human-readable display name.</summary>
    string PluginName { get; }

    /// <summary>Semantic version string (e.g. "1.0.0").</summary>
    string PluginVersion { get; }

    /// <summary>Called when the plugin is activated by the host.</summary>
    Task ActivateAsync(IPluginHostServices host);

    /// <summary>Called when the plugin is deactivated.</summary>
    Task DeactivateAsync();
}