using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class ProfilesSection : UserControl
{
    public ProfilesSection()
    {
        InitializeComponent();
    }

    private void OnProcessNameKeyDown(object? sender, KeyEventArgs e)
    {
        // Commit the typed process name as a chip when the user presses Enter,
        // matching the UX convention of chip-style tag inputs.
        if (e.Key != Key.Enter || DataContext is not ProfilesSectionViewModel viewModel)
        {
            return;
        }

        if (viewModel.AddProcessNameChipCommand.CanExecute(null))
        {
            viewModel.AddProcessNameChipCommand.Execute(null);
        }

        e.Handled = true;
    }

    private void OnUrlPatternKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not ProfilesSectionViewModel viewModel)
        {
            return;
        }

        if (viewModel.AddUrlPatternChipCommand.CanExecute(null))
        {
            viewModel.AddUrlPatternChipCommand.Execute(null);
        }

        e.Handled = true;
    }

    private async void OnDeleteProfile(object? sender, RoutedEventArgs e)
    {
        if (
            DataContext is not ProfilesSectionViewModel viewModel
            || !viewModel.DeleteSelectedProfileCommand.CanExecute(null)
        )
        {
            return;
        }

        var dialog = new MessageDialogWindow();
        var confirmed = await dialog.ShowConfirmationAsync(
            "Delete profile",
            "Delete the selected profile?",
            "Delete"
        );

        if (!confirmed)
        {
            return;
        }

        viewModel.DeleteSelectedProfileCommand.Execute(null);
    }
}