#if WINDOWS
using System.Windows.Controls;
using TypeWhisper.PluginSDK.Wpf;

namespace TypeWhisper.Plugin.OpenAi;

public sealed partial class OpenAiPlugin : IWpfPluginSettingsProvider
{
    public UserControl? CreateSettingsView() => new OpenAiSettingsView(this);
}
#endif
