#if WINDOWS
using System.Windows;
using System.Windows.Controls;
using TypeWhisper.PluginSDK.Wpf;

namespace TypeWhisper.Plugin.CloudflareAsr;

public sealed partial class CloudflareAsrPlugin : IWpfPluginSettingsProvider
{
    public UserControl? CreateSettingsView()
    {
        var panel = new StackPanel { Margin = new Thickness(8) };
        var accountLabel = new TextBlock { Text = "Account ID", Margin = new Thickness(0, 0, 0, 4) };
        var accountBox = new TextBox { MaxLength = 200 };
        if (!string.IsNullOrEmpty(_accountId))
            accountBox.Text = _accountId;

        var tokenLabel = new TextBlock { Text = "API Token", Margin = new Thickness(0, 12, 0, 4) };
        var tokenBox = new PasswordBox { MaxLength = 200 };
        if (!string.IsNullOrEmpty(_apiToken))
            tokenBox.Password = _apiToken;

        var btn = new Button
        {
            Content = "Save",
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        btn.Click += async (_, _) =>
        {
            await SetAccountIdAsync(accountBox.Text);
            await SetApiTokenAsync(tokenBox.Password);
        };

        panel.Children.Add(accountLabel);
        panel.Children.Add(accountBox);
        panel.Children.Add(tokenLabel);
        panel.Children.Add(tokenBox);
        panel.Children.Add(btn);
        return new UserControl { Content = panel };
    }
}
#endif
