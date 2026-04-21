#if WINDOWS
using System.Windows.Controls;
using TypeWhisper.PluginSDK.Wpf;

namespace TypeWhisper.Plugin.Deepgram;

public sealed partial class DeepgramPlugin : IWpfPluginSettingsProvider
{
    public UserControl? CreateSettingsView() => new DeepgramSettingsView(this);
}
#endif
