namespace TypeWhisper.PluginSDK;

/// <summary>
///     Optional interface for plugins that need to know their on-disk data directory
///     before <see cref="ITypeWhisperPlugin.ActivateAsync" /> is called. The host sets
///     the path immediately after loading the plugin, giving it a chance to initialize
///     storage before activation.
/// </summary>
public interface IPluginDataLocationAware
{
    /// <summary>
    ///     Called by the host with the plugin's dedicated data directory path.
    ///     The directory is guaranteed to exist when this method is called.
    /// </summary>
    void SetDataDirectory(string pluginDataDirectory);
}