// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK;

/// <summary>
///     Optional interface for plugins that want to report progress or status messages
///     in their settings view. The host UI can display these to the user.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface IPluginSettingsActivity
{
    /// <summary>
    ///     Current progress (0.0 to 1.0) or null if indeterminate.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    double? SettingsProgress { get; }

    /// <summary>
    ///     Raised when the plugin wants to show a progress/status message in settings.
    ///     Message is null when the activity completes.
    /// </summary>
    // ReSharper disable once EventNeverSubscribedTo.Global
    event Action<string?>? SettingsActivityChanged;
}
