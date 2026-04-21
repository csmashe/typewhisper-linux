#if WINDOWS
using System.Windows.Controls;
using TypeWhisper.PluginSDK.Wpf;

namespace TypeWhisper.Plugin.Cerebras;

public sealed partial class CerebrasPlugin : IWpfPluginSettingsProvider
{
    public UserControl? CreateSettingsView() => new CerebrasSettingsView(this);
}
#endif
