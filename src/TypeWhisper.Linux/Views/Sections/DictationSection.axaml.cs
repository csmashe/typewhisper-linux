using Avalonia.Controls;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Linux.Views;

namespace TypeWhisper.Linux.Views.Sections;

public partial class DictationSection : UserControl
{
    public DictationSection()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => (DataContext as DictationSectionViewModel)?.ActivatePreview();
        DetachedFromVisualTree += (_, _) => (DataContext as DictationSectionViewModel)?.DeactivatePreview();
    }

    private async void OnDeleteSelectedModel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not DictationSectionViewModel viewModel || viewModel.SelectedModel is not { } selected)
            return;

        var dialog = new MessageDialogWindow();
        var confirmed = await dialog.ShowConfirmationAsync(
            "Delete model files?",
            $"Delete {selected.DisplayLabel} from your hard drive? It can be downloaded again later.",
            "Delete",
            "Cancel");

        if (confirmed)
            await viewModel.DeleteSelectedModelAsync();
    }
}
