using Avalonia.Controls;
using Avalonia.Input;
using TypeWhisper.Linux.ViewModels;

namespace TypeWhisper.Linux.Views;

public partial class RecentTranscriptionsPaletteWindow : Window
{
    private readonly RecentTranscriptionsPaletteViewModel _viewModel;
    private bool _isSelecting;
    private bool _isClosing;

    public RecentTranscriptionsPaletteWindow()
        : this(new RecentTranscriptionsPaletteViewModel([], _ => { }))
    {
    }

    public RecentTranscriptionsPaletteWindow(RecentTranscriptionsPaletteViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Opened += OnOpened;
        Deactivated += OnDeactivated;
        KeyDown += OnKeyDown;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        SearchBox.Focus();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_isSelecting)
            RequestClose();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _viewModel.MoveSelection(1);
                if (_viewModel.SelectedItem is not null)
                    EntriesList.ScrollIntoView(_viewModel.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                _viewModel.MoveSelection(-1);
                if (_viewModel.SelectedItem is not null)
                    EntriesList.ScrollIntoView(_viewModel.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                SelectAndClose(_viewModel.SelectedItem);
                e.Handled = true;
                break;
            case Key.Escape:
                RequestClose();
                e.Handled = true;
                break;
        }
    }

    private void Entry_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RecentTranscriptionPaletteItem item)
        {
            SelectAndClose(item);
            e.Handled = true;
        }
    }

    private void SelectAndClose(RecentTranscriptionPaletteItem? item)
    {
        if (item is null)
            return;

        _isSelecting = true;
        RequestClose();
        _viewModel.Select(item);
    }

    public void RequestClose()
    {
        if (_isClosing)
            return;

        _isClosing = true;
        Close();
    }
}
