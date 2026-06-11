using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Linq;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.Views.Sections;

public partial class DictationSection : UserControl
{
    public DictationSection()
    {
        InitializeComponent();
        // Activate the live mic-level preview only while this section is
        // actually on screen; deactivate when navigated away from to avoid
        // holding the audio device open unnecessarily.
        AttachedToVisualTree += (_, _) =>
            (DataContext as DictationSectionViewModel)?.ActivatePreview();
        DetachedFromVisualTree += (_, _) =>
            (DataContext as DictationSectionViewModel)?.DeactivatePreview();
    }

    // Re-poll providers for current models whenever the model dropdown opens,
    // so newly added models appear without a manual "Validate".
    private void OnModelDropDownOpened(object? sender, System.EventArgs e)
    {
        if (DataContext is DictationSectionViewModel viewModel)
        {
            _ = viewModel.RefreshProviderModelsAsync();
        }
    }

    private async void OnDeleteSelectedModel(
        object? sender,
        RoutedEventArgs e
    )
    {
        if (
            DataContext is not DictationSectionViewModel viewModel
            || viewModel.SelectedModel is not { } selected
        )
        {
            return;
        }

        var dialog = new MessageDialogWindow();
        var confirmed = await dialog.ShowConfirmationAsync(
            "Delete model files?",
            $"Delete {selected.DisplayLabel} from your hard drive? It can be downloaded again later.",
            "Delete"
        );

        if (confirmed)
        {
            await viewModel.DeleteSelectedModelAsync();
        }
    }

    private async void OnChangeModelStorage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DictationSectionViewModel viewModel)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Choose model storage folder",
                AllowMultiple = false,
            }
        );

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await viewModel.ChangeModelStorageAsync(path);
        }
    }
}