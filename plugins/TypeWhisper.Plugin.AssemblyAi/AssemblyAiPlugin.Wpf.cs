#if WINDOWS
using System.Windows.Controls;
using TypeWhisper.PluginSDK.Wpf;

namespace TypeWhisper.Plugin.AssemblyAi;

public sealed partial class AssemblyAiPlugin : IWpfPluginSettingsProvider
{
    public UserControl? CreateSettingsView() => new AssemblyAiSettingsView(this);
}
#endif
