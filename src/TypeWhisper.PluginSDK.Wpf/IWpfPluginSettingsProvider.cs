using System.Windows.Controls;

namespace TypeWhisper.PluginSDK.Wpf;

/// <summary>
/// Optional interface for plugins that provide a WPF settings view.
/// Windows hosts query this interface to render plugin settings UI.
/// </summary>
public interface IWpfPluginSettingsProvider
{
    /// <summary>Returns a WPF settings view for this plugin, or null if none.</summary>
    UserControl? CreateSettingsView();
}
