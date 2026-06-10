using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Views;

public partial class PromptPaletteWindow : Window
{
    private IReadOnlyList<PromptAction> _allActions = [];
    private List<PromptAction> _filteredActions = [];
    private TaskCompletionSource<PromptAction?>? _resultSource;
    private bool _closed;
    private bool _running;
    private CancellationTokenSource? _runCts;

    public PromptPaletteWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Deactivated += OnDeactivated;
        Closed += OnClosed;
        KeyDown += OnWindowKeyDown;
    }

    public string SourceText { get; set; } = "";

    public void SetActions(IReadOnlyList<PromptAction> actions)
    {
        _allActions = actions;
        ApplyFilter(string.Empty);
    }

    public Task<PromptAction?> ShowAndWaitAsync()
    {
        _resultSource = new TaskCompletionSource<PromptAction?>();
        Show();
        return _resultSource.Task;
    }

    /// <summary>Shows a status message and locks the UI while the host runs the picked action.</summary>
    public void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusBorder.IsVisible = true;
        ActionListBox.IsEnabled = false;
        SearchBox.IsEnabled = false;
    }

    /// <summary>Locks the UI, shows a status line, and reveals the result area
    ///     for streamed tokens after an action is picked.</summary>
    public void BeginRunning(string actionName)
    {
        _running = true;
        ShowStatus($"Running '{actionName}'… (Esc to cancel)");
        ResultText.Text = string.Empty;
        ResultBorder.IsVisible = true;
    }

    /// <summary>
    ///     Attaches the CTS for the running action so Escape/close cancels it.
    ///     If the window was already closed before the service attached, cancels
    ///     immediately to cover the race.
    /// </summary>
    public void AttachRunCancellation(CancellationTokenSource runCts)
    {
        _runCts = runCts;
        if (_closed)
        {
            runCts.Cancel();
        }
    }

    /// <summary>Updates the streamed result text and auto-scrolls.
    ///     Must be called on the UI thread.</summary>
    public void UpdateResult(string text)
    {
        if (_closed)
        {
            return;
        }

        ResultText.Text = text;
        // Post-layout scroll matches the pattern used by the dictation overlay.
        Dispatcher.UIThread.Post(
            () => ResultScrollViewer.ScrollToEnd(),
            DispatcherPriority.Background);
    }

    /// <summary>Closes the palette window (used by the service after the run completes).</summary>
    public void ClosePalette()
    {
        if (_closed)
        {
            return;
        }

        Close();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(SourceText))
        {
            SourcePreviewText.Text =
                SourceText.Length > 120 ? SourceText[..120] + "..." : SourceText;
            SourcePreviewBorder.IsVisible = true;
        }

        Activate();
        SearchBox.Focus();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // Dismiss without a selection when the user clicks away.
        if (_resultSource?.Task.IsCompleted == false)
        {
            Complete(null);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;

        // Cancel the running action so the service doesn't paste into the target
        // window. Harmless on normal close — the insert paths use their own tokens.
        _runCts?.Cancel();

        // Safety net: unblock any awaiting caller if the OS closed the window first.
        _resultSource?.TrySetResult(null);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // The search box is disabled while running, so handle Escape at the window
        // level. Before a pick, the search box handles Escape and marks it handled.
        if (_running && e.Key == Key.Escape)
        {
            e.Handled = true;
            ClosePalette();
        }
    }

    private void SearchBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyFilter(SearchBox.Text ?? string.Empty);
    }

    private void SearchBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (ActionListBox.SelectedIndex < _filteredActions.Count - 1)
                {
                    ActionListBox.SelectedIndex++;
                }
                else if (ActionListBox.SelectedIndex == -1 && _filteredActions.Count > 0)
                {
                    ActionListBox.SelectedIndex = 0;
                }

                if (ActionListBox.SelectedItem is not null)
                {
                    ActionListBox.ScrollIntoView(ActionListBox.SelectedItem);
                }

                e.Handled = true;
                break;
            case Key.Up:
                if (ActionListBox.SelectedIndex > 0)
                {
                    ActionListBox.SelectedIndex--;
                }

                if (ActionListBox.SelectedItem is not null)
                {
                    ActionListBox.ScrollIntoView(ActionListBox.SelectedItem);
                }

                e.Handled = true;
                break;
            case Key.Enter:
                Complete(ActionListBox.SelectedItem as PromptAction);
                e.Handled = true;
                break;
            case Key.Escape:
                Complete(null);
                e.Handled = true;
                break;
        }
    }

    private void ActionListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Return focus to the search box so keyboard navigation continues
        // working after the user clicks a list item.
        SearchBox.Focus();
    }

    private void ActionListBox_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        Complete(ActionListBox.SelectedItem as PromptAction);
    }

    private void CancelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Complete(null);
    }

    private void RunButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Complete(ActionListBox.SelectedItem as PromptAction);
    }

    private void ApplyFilter(string query)
    {
        _filteredActions = string.IsNullOrWhiteSpace(query)
            ? _allActions.ToList()
            : _allActions
                .Where(action =>
                    action.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || action.SystemPrompt.Contains(query, StringComparison.OrdinalIgnoreCase)
                )
                .ToList();

        ActionListBox.ItemsSource = _filteredActions;
        EmptyText.IsVisible = _filteredActions.Count == 0;
        ActionListBox.SelectedIndex = _filteredActions.Count > 0 ? 0 : -1;
    }

    private void Complete(PromptAction? action)
    {
        // TrySetResult is false if already set (e.g. by OnClosed); only act once.
        if (_resultSource?.TrySetResult(action) != true)
        {
            return;
        }

        if (action is null)
        {
            // Cancel / Escape / dismiss — tear the window down as before.
            Close();
            return;
        }

        // Keep the window open and locked so the host can stream the result;
        // the service calls ClosePalette once the run finishes.
        BeginRunning(action.Name);
    }
}