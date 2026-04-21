using Avalonia.Controls;
using TypeWhisper.Linux.ViewModels;

namespace TypeWhisper.Linux.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(SettingsWindowViewModel vm) : this()
    {
        DataContext = vm;
    }
}
