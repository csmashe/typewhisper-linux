using Avalonia.Controls;
using Avalonia.Interactivity;
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
}