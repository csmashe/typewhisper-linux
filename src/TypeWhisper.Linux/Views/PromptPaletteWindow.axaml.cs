using Avalonia.Controls;
using Avalonia.Input;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Views;

public partial class PromptPaletteWindow : Window
{
    private IReadOnlyList<PromptAction> _allActions = [];
    private List<PromptAction> _filteredActions = [];
    private TaskCompletionSource<PromptAction?>? _resultSource;

    public string SourceText { get; set; } = "";

    public PromptPaletteWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Deactivated += OnDeactivated;
        Closed += OnClosed;
    }

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

    private void OnOpened(object? sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(SourceText))
        {
            SourcePreviewText.Text = SourceText.Length > 120
                ? SourceText[..120] + "..."
                : SourceText;
            SourcePreviewBorder.IsVisible = true;
        }

        Activate();
        SearchBox.Focus();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_resultSource?.Task.IsCompleted == false)
            Complete(null);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _resultSource?.TrySetResult(null);
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
                    ActionListBox.SelectedIndex++;
                else if (ActionListBox.SelectedIndex == -1 && _filteredActions.Count > 0)
                    ActionListBox.SelectedIndex = 0;
                if (ActionListBox.SelectedItem is not null)
                    ActionListBox.ScrollIntoView(ActionListBox.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                if (ActionListBox.SelectedIndex > 0)
                    ActionListBox.SelectedIndex--;
                if (ActionListBox.SelectedItem is not null)
                    ActionListBox.ScrollIntoView(ActionListBox.SelectedItem);
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
        SearchBox.Focus();
    }

    private void ActionListBox_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        Complete(ActionListBox.SelectedItem as PromptAction);
    }

    private void CancelButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Complete(null);
    }

    private void RunButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Complete(ActionListBox.SelectedItem as PromptAction);
    }

    private void ApplyFilter(string query)
    {
        _filteredActions = string.IsNullOrWhiteSpace(query)
            ? _allActions.ToList()
            : _allActions
                .Where(action => action.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                              || action.SystemPrompt.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        ActionListBox.ItemsSource = _filteredActions;
        EmptyText.IsVisible = _filteredActions.Count == 0;
        ActionListBox.SelectedIndex = _filteredActions.Count > 0 ? 0 : -1;
    }

    private void Complete(PromptAction? action)
    {
        if (_resultSource?.TrySetResult(action) != true)
            return;

        Close();
    }

    public void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusBorder.IsVisible = true;
        ActionListBox.IsEnabled = false;
        SearchBox.IsEnabled = false;
    }
}
