using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views.Sections;

public partial class ProfilesSection : UserControl
{
    public ProfilesSection() => InitializeComponent();

    private void ProfileToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Profile profile }
            && DataContext is SettingsWindowViewModel vm)
        {
            vm.Profiles.ToggleProfileEnabledCommand.Execute(profile);
        }
    }

    private void ProcessNameInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is SettingsWindowViewModel vm)
        {
            vm.Profiles.AddProcessNameChipCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void UrlPatternInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is SettingsWindowViewModel vm)
        {
            vm.Profiles.AddUrlPatternChipCommand.Execute(null);
            e.Handled = true;
        }
    }
}
