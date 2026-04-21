#if WINDOWS
using System.Windows.Controls;
using TypeWhisper.PluginSDK.Wpf;

namespace TypeWhisper.Plugin.OpenRouter;

public sealed partial class OpenRouterPlugin : IWpfPluginSettingsProvider
{
    public UserControl? CreateSettingsView() => new OpenRouterSettingsView(this);
}
#endif
