using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
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
    private void OnModelDropDownOpened(object? sender, EventArgs e)
    {
        if (DataContext is DictationSectionViewModel viewModel)
        {
            _ = viewModel.RefreshProviderModelsAsync();
        }
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; void return is mandated by the RoutedEventHandler/EventHandler delegate signature.
    private async void OnDeleteSelectedModel(
        object? sender,
        RoutedEventArgs e
    )
    {
        if (
            DataContext is not DictationSectionViewModel { SelectedModel: { } selected } viewModel
        )
        {
            return;
        }

        // Only the confirmation dialog is uncontained here; DeleteSelectedModelAsync
        // catches its own file-system failures internally, so Window alone is correct.
        await UiOperations.RunAsync(
            "confirm and delete model",
            Loc.Instance["Common.Delete"],
            UiFailureKind.Window,
            async () =>
            {
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
        );
    }

    // ReSharper disable once AsyncVoidEventHandlerMethod -- Avalonia UI event handler; void return is mandated by the RoutedEventHandler/EventHandler delegate signature.
    private async void OnChangeModelStorage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DictationSectionViewModel viewModel)
        {
            return;
        }

        await UiOperations.RunAsync(
            "select model storage folder",
            Loc.Instance["Dictation.ModelStorage"],
            UiFailureKind.StorageProvider | UiFailureKind.FileSystem,
            async () =>
            {
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

                var path = (folders.Count > 0 ? folders[0] : null)?.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    await viewModel.ChangeModelStorageAsync(path);
                }
            },
            presenter: message =>
            {
                viewModel.ModelStorageStatusText = message;
                return Task.CompletedTask;
            }
        );
    }

    private static UiOperationGuard UiOperations =>
        Program.Services.GetRequiredService<UiOperationGuard>();
}
