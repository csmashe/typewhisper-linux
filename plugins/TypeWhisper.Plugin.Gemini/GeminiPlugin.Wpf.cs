#if WINDOWS
using System.Windows.Controls;
using TypeWhisper.PluginSDK.Wpf;

namespace TypeWhisper.Plugin.Gemini;

public sealed partial class GeminiPlugin : IWpfPluginSettingsProvider
{
    public UserControl? CreateSettingsView() => new GeminiSettingsView(this);
}
#endif
