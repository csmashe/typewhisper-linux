using Avalonia.Controls;
using TypeWhisper.Linux.ViewModels;

namespace TypeWhisper.Linux;

public partial class MainWindow : Window
{
    // Parameterless ctor for the Avalonia previewer / runtime XAML loader.
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
