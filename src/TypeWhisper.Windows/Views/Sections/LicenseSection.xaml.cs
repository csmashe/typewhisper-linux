using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.Windows.Views.Sections;

public partial class LicenseSection : UserControl
{
    public LicenseSection() => InitializeComponent();

    private async void OnActivateClick(object sender, RoutedEventArgs e)
    {
        var key = LicenseKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            LicenseStatus.Text = "Please enter a license key.";
            return;
        }

        try
        {
            LicenseStatus.Text = "Activating...";
            var license = App.Services.GetRequiredService<LicenseService>();
            await license.ActivateAsync(key);
            LicenseStatus.Text = $"Activated! Tier: {license.Tier}";
            LicenseStatus.Foreground = FindResource("ActiveIndicatorBrush") as System.Windows.Media.Brush
                ?? LicenseStatus.Foreground;
        }
        catch (Exception ex)
        {
            LicenseStatus.Text = $"Activation failed: {ex.Message}";
        }
    }
}
