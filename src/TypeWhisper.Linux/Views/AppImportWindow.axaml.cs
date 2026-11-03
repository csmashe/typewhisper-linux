using Avalonia.Controls;
using Avalonia.Interactivity;
using TypeWhisper.Linux.ViewModels;

namespace TypeWhisper.Linux.Views;

/// <summary>Review dialog for <see cref="AppImportViewModel" />; closes with the result message, or null when canceled.</summary>
public partial class AppImportWindow : Window
{
    // ReSharper disable once MemberCanBePrivate.Global -- the Avalonia XAML loader and previewer need a public parameterless constructor.
    public AppImportWindow()
    {
        InitializeComponent();
    }

    public AppImportWindow(AppImportViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Opened += async (_, _) => await viewModel.LoadAsync();
        // Closing by any route, including the window manager, stops a read still in progress.
        Closing += (_, _) => viewModel.Cancel();
    }

    private void AddButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AppImportViewModel viewModel)
        {
            Close(viewModel.Commit());
        }
    }

    private void CancelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
