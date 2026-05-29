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

    /// <summary>
    ///     Transitions the palette into its running state after an action is picked:
    ///     locks the UI, shows a status line, and reveals the (initially empty)
    ///     result area that streamed tokens fill via <see cref="UpdateResult" />.
    /// </summary>
    public void BeginRunning(string actionName)
    {
        _running = true;
        ShowStatus($"Running '{actionName}'… (Esc to cancel)");
        ResultText.Text = string.Empty;
        ResultBorder.IsVisible = true;
    }

    /// <summary>
    ///     Registers the token source that drives the running action so closing the
    ///     window (OS close button) or pressing Escape while it runs cancels the
    ///     action and suppresses insertion. If the window was already closed before
    ///     the service attached this, cancels immediately so the race is covered.
    /// </summary>
    public void AttachRunCancellation(CancellationTokenSource runCts)
    {
        _runCts = runCts;
        if (_closed)
        {
            runCts.Cancel();
        }
    }

    /// <summary>
    ///     Renders the accumulated streamed result and keeps the newest text in
    ///     view. Must be called on the UI thread (the service marshals each flush).
    /// </summary>
    public void UpdateResult(string text)
    {
        if (_closed)
        {
            return;
        }

        ResultText.Text = text;
        // Auto-scroll after layout has measured the new text, matching the
        // after-layout pattern used by the dictation overlay's streamed area.
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

        // Closing while an action is running is a user abort: cancel it so the
        // service stops processing and does NOT paste the result into the target
        // window. Harmless on a normal completion close — by then the service has
        // already passed its pre-insert cancellation check and the insert/execute
        // paths use their own tokens, not this one.
        _runCts?.Cancel();

        // Safety net: if the window is closed by the OS or shell before
        // Complete() runs, unblock any awaiting caller with a null result.
        _resultSource?.TrySetResult(null);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // While the action runs the search box is disabled, so its Escape handler
        // is inactive; handle Escape at the window level to abort a streaming
        // action. (Before a pick, the focused search box handles Escape and marks
        // it handled, so this never fires then.)
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
        // TrySetResult returns false if the result was already set (e.g. by
        // OnClosed), so only react on the first successful call.
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

        // An action was picked: keep the window open and locked so the host can
        // stream the LLM result into it. The service closes it (ClosePalette)
        // once the run finishes, before pasting into the target app.
        BeginRunning(action.Name);
    }
}