#if WINDOWS
using System.Windows.Controls;
using TypeWhisper.PluginSDK.Wpf;

namespace TypeWhisper.Plugin.Groq;

public sealed partial class GroqPlugin : IWpfPluginSettingsProvider
{
    public UserControl? CreateSettingsView() => new GroqSettingsView(this);
}
#endif
