#if WINDOWS
using System.Windows.Controls;
using TypeWhisper.PluginSDK.Wpf;

namespace TypeWhisper.Plugin.Obsidian;

public sealed partial class ObsidianPlugin : IWpfPluginSettingsProvider
{
    public UserControl? CreateSettingsView() => new ObsidianSettingsView(this);
}
#endif
