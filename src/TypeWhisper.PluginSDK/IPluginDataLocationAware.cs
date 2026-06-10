namespace TypeWhisper.PluginSDK;

/// <summary>
///     Optional interface for plugins that need their on-disk data directory set
///     before <see cref="ITypeWhisperPlugin.ActivateAsync" /> is called. The host
///     calls <see cref="SetDataDirectory" /> immediately after loading the plugin.
/// </summary>
public interface IPluginDataLocationAware
{
    /// <summary>
    ///     Called by the host with the plugin's dedicated data directory path.
    ///     The directory is guaranteed to exist when this method is called.
    /// </summary>
    void SetDataDirectory(string pluginDataDirectory);
}