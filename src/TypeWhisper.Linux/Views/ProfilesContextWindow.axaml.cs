using Avalonia.Controls;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views;

public partial class ProfilesContextWindow : Window
{
    // ReSharper disable once MemberCanBePrivate.Global
    // x:Class in ProfilesContextWindow.axaml; Avalonia XAML loader/previewer instantiates the parameterless ctor
    public ProfilesContextWindow()
    {
        InitializeComponent();
    }

    public ProfilesContextWindow(ProfilesSectionViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }
}