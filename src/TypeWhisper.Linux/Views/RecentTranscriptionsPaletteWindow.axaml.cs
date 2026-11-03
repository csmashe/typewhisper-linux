using Avalonia.Controls;
using Avalonia.Input;
using TypeWhisper.Linux.ViewModels;

namespace TypeWhisper.Linux.Views;

public partial class RecentTranscriptionsPaletteWindow : Window
{
    private readonly TaskCompletionSource _closed = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly RecentTranscriptionsPaletteViewModel _viewModel;

    // Guards Close() against re-entry: Deactivated can fire again while the
    // window is already tearing down.
    private bool _isClosing;

    // Set while a selection is being committed so the Deactivated handler
    // (which fires as the window loses focus) does not treat it as the user
    // dismissing the palette by clicking away.
    private bool _isSelecting;

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
        Closed += OnClosed;
        KeyDown += OnKeyDown;
    }

    public void RequestClose()
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        Close();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        SearchBox.Focus();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_isSelecting)
        {
            RequestClose();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed.TrySetResult();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Other keys fall through to the SearchBox for normal text input.
        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault -- only the actionable cases are handled; remaining enum values are deliberate no-ops.
        switch (e.Key)
        {
            case Key.Down:
                _viewModel.MoveSelection(1);
                if (_viewModel.SelectedItem is not null)
                {
                    EntriesList.ScrollIntoView(_viewModel.SelectedItem);
                }

                e.Handled = true;
                break;
            case Key.Up:
                _viewModel.MoveSelection(-1);
                if (_viewModel.SelectedItem is not null)
                {
                    EntriesList.ScrollIntoView(_viewModel.SelectedItem);
                }

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
        // ReSharper disable once InvertIf — pattern variable `item` is used in the block;
        // inverting would orphan the binding.
        if ((sender as Control)?.DataContext is RecentTranscriptionPaletteItem item)
        {
            SelectAndClose(item);
            e.Handled = true;
        }
    }

    // ReSharper disable once AsyncVoidMethod -- called from synchronous KeyDown/PointerReleased
    // handlers; awaits _closed.Task so selection runs only after the window has closed.
    private async void SelectAndClose(RecentTranscriptionPaletteItem? item)
    {
        if (item is null || _isSelecting)
        {
            return;
        }

        _isSelecting = true;
        RequestClose();
        await _closed.Task;
        _viewModel.Select(item);
    }
}
