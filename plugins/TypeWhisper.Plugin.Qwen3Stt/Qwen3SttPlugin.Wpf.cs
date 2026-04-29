#if WINDOWS
using System.Windows;
using System.Windows.Controls;
using TypeWhisper.PluginSDK.Wpf;

namespace TypeWhisper.Plugin.Qwen3Stt;

public sealed partial class Qwen3SttPlugin : IWpfPluginSettingsProvider
{
    public UserControl? CreateSettingsView()
    {
        var panel = new StackPanel { Margin = new Thickness(8) };
        var urlLabel = new TextBlock { Text = "Base URL", Margin = new Thickness(0, 0, 0, 4) };
        var urlBox = new TextBox { Text = _baseUrl ?? DefaultBaseUrl, MaxLength = 500 };
        var keyLabel = new TextBlock { Text = "API Key (optional)", Margin = new Thickness(0, 12, 0, 4) };
        var keyBox = new PasswordBox { MaxLength = 200 };
        if (!string.IsNullOrEmpty(_apiKey))
            keyBox.Password = _apiKey;

        var status = new TextBlock { Margin = new Thickness(0, 4, 0, 0), FontSize = 12 };
        var btn = new Button
        {
            Content = "Save",
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        btn.Click += async (_, _) =>
        {
            SetBaseUrl(urlBox.Text);
            await SetApiKeyAsync(keyBox.Password);
            status.Text = "Saved";
        };

        panel.Children.Add(urlLabel);
        panel.Children.Add(urlBox);
        panel.Children.Add(keyLabel);
        panel.Children.Add(keyBox);
        panel.Children.Add(btn);
        panel.Children.Add(status);
        return new UserControl { Content = panel };
    }
}
#endif
