#if WINDOWS
using System.Windows.Controls;
using TypeWhisper.PluginSDK.Wpf;

namespace TypeWhisper.Plugin.OpenAiCompatible;

public sealed partial class OpenAiCompatiblePlugin : IWpfPluginSettingsProvider
{
    public UserControl? CreateSettingsView() => new OpenAiCompatibleSettingsView(this);
}
#endif
