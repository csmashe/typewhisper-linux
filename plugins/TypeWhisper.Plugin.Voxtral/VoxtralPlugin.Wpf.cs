#if WINDOWS
using System.Windows;
using System.Windows.Controls;
using TypeWhisper.PluginSDK.Wpf;

namespace TypeWhisper.Plugin.Voxtral;

public sealed partial class VoxtralPlugin : IWpfPluginSettingsProvider
{
    public UserControl? CreateSettingsView()
    {
        var panel = new StackPanel { Margin = new Thickness(8) };
        var label = new TextBlock { Text = "Mistral API Key", Margin = new Thickness(0, 0, 0, 4) };
        var box = new PasswordBox { MaxLength = 200 };
        if (!string.IsNullOrEmpty(_apiKey))
            box.Password = _apiKey;

        var status = new TextBlock { Margin = new Thickness(0, 4, 0, 0), FontSize = 12 };
        var btn = new Button
        {
            Content = "Save",
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        btn.Click += async (_, _) =>
        {
            await SetApiKeyAsync(box.Password);
            status.Text = string.IsNullOrWhiteSpace(box.Password) ? "" : "Saved";
        };

        panel.Children.Add(label);
        panel.Children.Add(box);
        panel.Children.Add(btn);
        panel.Children.Add(status);
        return new UserControl { Content = panel };
    }
}
#endif
