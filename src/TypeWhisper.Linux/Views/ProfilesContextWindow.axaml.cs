using Avalonia.Controls;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views;

public partial class ProfilesContextWindow : Window
{
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
