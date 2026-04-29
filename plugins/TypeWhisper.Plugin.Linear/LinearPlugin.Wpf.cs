#if WINDOWS
using System.Windows.Controls;
using TypeWhisper.PluginSDK.Wpf;

namespace TypeWhisper.Plugin.Linear;

public sealed partial class LinearPlugin : IWpfPluginSettingsProvider
{
    public UserControl? CreateSettingsView() => new LinearSettingsView(this);
}
#endif
