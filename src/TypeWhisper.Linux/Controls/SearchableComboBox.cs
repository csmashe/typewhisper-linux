using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.Controls;

/// <summary>
///     A ComboBox whose drop-down gets a search field once it has <see cref="ChoiceSearch.Threshold" />
///     or more choices. The drop-down shows its own list, so filtering never touches the bound items or selection.
/// </summary>
[TemplatePart("PART_SearchBox", typeof(TextBox))]
[TemplatePart("PART_ResultsList", typeof(ListBox))]
[TemplatePart("PART_NoMatches", typeof(TextBlock))]
public class SearchableComboBox : ComboBox
{
    // ReSharper disable once MemberCanBePrivate.Global -- Avalonia property identifiers stay public so styles and templates can target IsSearchable.
    public static readonly DirectProperty<SearchableComboBox, bool> IsSearchableProperty =
        AvaloniaProperty.RegisterDirect<SearchableComboBox, bool>(nameof(IsSearchable), control => control.IsSearchable);

    private Popup? _popup;
    private bool _refreshQueued;

    public SearchableComboBox()
    {
        Items.CollectionChanged += OnItemsChanged;
    }

    /// <summary>True when the list is long enough to show the search field.</summary>
    public bool IsSearchable
    {
        get;
        private set => SetAndRaise(IsSearchableProperty, ref field, value);
    }

    /// <summary>The choices the drop-down shows for the current search, best first.</summary>
    internal IReadOnlyList<object> VisibleChoices => ResultsList?.ItemsSource as IReadOnlyList<object> ?? [];

    internal TextBox? SearchBox { get; private set; }

    internal TextBlock? NoMatchesNotice { get; private set; }

    internal ListBox? ResultsList { get; private set; }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (SearchBox is not null)
        {
            SearchBox.TextChanged -= OnSearchTextChanged;
            SearchBox.RemoveHandler(KeyDownEvent, OnSearchKeyDown);
        }
        if (ResultsList is not null)
        {
            ResultsList.RemoveHandler(KeyDownEvent, OnResultsKeyDown);
            ResultsList.RemoveHandler(TextInputEvent, OnResultsTextInput);
            ResultsList.Tapped -= OnResultsTapped;
        }
        if (_popup is not null)
            _popup.Opened -= OnPopupOpened;

        base.OnApplyTemplate(e);

        SearchBox = e.NameScope.Find<TextBox>("PART_SearchBox");
        ResultsList = e.NameScope.Find<ListBox>("PART_ResultsList");
        NoMatchesNotice = e.NameScope.Find<TextBlock>("PART_NoMatches");
        _popup = e.NameScope.Find<Popup>("PART_Popup");
        if (SearchBox is not null)
        {
            SearchBox.TextChanged += OnSearchTextChanged;
            SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        }
        if (ResultsList is not null)
        {
            ResultsList.AddHandler(KeyDownEvent, OnResultsKeyDown, RoutingStrategies.Tunnel);
            ResultsList.AddHandler(TextInputEvent, OnResultsTextInput, RoutingStrategies.Tunnel);
            ResultsList.Tapped += OnResultsTapped;
        }
        if (_popup is not null)
            _popup.Opened += OnPopupOpened;
        UpdateNames();
        UpdateSearchable();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsDropDownOpenProperty)
        {
            if (change.GetNewValue<bool>())
                PrepareDropDown();
            else
                RestoreFocusAfterClose();
        }
        else if (change.Property == AutomationProperties.NameProperty)
        {
            UpdateNames();
        }
    }

    private void PrepareDropDown()
    {
        UpdateNames();
        UpdateSearchable();
        SearchBox?.Text = "";
        ApplyFilter();
    }

    private void OnPopupOpened(object? sender, EventArgs e) =>
        // After layout, so the highlighted choice can be scrolled into view.
        Dispatcher.UIThread.Post(FocusOnOpen, DispatcherPriority.Loaded);

    private void FocusOnOpen()
    {
        if (!IsDropDownOpen || ResultsList is null)
            return;
        if (ResultsList.SelectedItem is { } highlighted)
            ResultsList.ScrollIntoView(highlighted);
        if (IsSearchable && SearchBox is not null)
        {
            SearchBox.Focus(NavigationMethod.Tab);
            return;
        }
        FocusHighlighted();
    }

    private void FocusHighlighted()
    {
        if (ResultsList is null)
            return;
        var index = Math.Max(ResultsList.SelectedIndex, 0);
        if (ResultsList.ItemCount == 0)
            return;
        ResultsList.SelectedIndex = index;
        ResultsList.ScrollIntoView(index);
        (ResultsList.ContainerFromIndex(index) ?? ResultsList).Focus(NavigationMethod.Directional);
    }

    // The popup took focus with it; give it back so keyboard users stay on the picker.
    private void RestoreFocusAfterClose()
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager.GetFocusedElement() is not Visual focused
            || _popup?.IsInsidePopup(focused) == true || TopLevel.GetTopLevel(focused) is null)
            Focus();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!IsDropDownOpen)
        {
            UpdateSearchable();
            return;
        }
        if (_refreshQueued)
            return;
        // Items can change while a selection is being committed, and a refresh clears the list
        // before refilling it; filter once it settles so the search field is not hidden meanwhile.
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            if (!IsDropDownOpen)
                return;
            var searching = SearchBox?.IsFocused == true;
            UpdateSearchable();
            ApplyFilter();
            if (ItemCount == 0)
                SetCurrentValue(IsDropDownOpenProperty, false);
            else if (searching && !IsSearchable)
                FocusHighlighted();
        });
    }

    private void UpdateSearchable() => IsSearchable = ItemCount >= ChoiceSearch.Threshold;

    private string Query => IsSearchable ? SearchBox?.Text?.Trim() ?? "" : "";

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (ResultsList is null)
            return;
        var query = Query;
        var matches = ChoiceSearch.Filter(Items, query);
        ResultsList.ItemsSource = matches;
        // With a search the best match is highlighted, so Enter takes it; otherwise the current choice.
        ResultsList.SelectedItem = query.Length > 0
            ? matches.FirstOrDefault()
            : matches.FirstOrDefault(item => Equals(item, SelectedItem));
        if (ResultsList.SelectedItem is { } highlighted)
            ResultsList.ScrollIntoView(highlighted);
        var empty = matches.Count == 0 && query.Length > 0;
        if (NoMatchesNotice is null || NoMatchesNotice.IsVisible == empty)
            return;
        NoMatchesNotice.IsVisible = empty;
        NoMatchesNotice.Text = empty ? Loc.Instance["Common.NoMatches"] : "";
    }

    private void Choose(object item)
    {
        SetCurrentValue(SelectedItemProperty, item);
        SetCurrentValue(IsDropDownOpenProperty, false);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault -- other keys go on to the text box.
        switch (e.Key)
        {
            case Key.Enter:
                if (ResultsList?.SelectedItem is { } best)
                    Choose(best);
                else if (Query.Length == 0)
                    SetCurrentValue(IsDropDownOpenProperty, false);
                e.Handled = true;
                break;
            case Key.Down when e.KeyModifiers == KeyModifiers.None:
                FocusHighlighted();
                e.Handled = true;
                break;
            case Key.Up when e.KeyModifiers == KeyModifiers.None:
                e.Handled = true;
                break;
        }
    }

    private void OnResultsKeyDown(object? sender, KeyEventArgs e)
    {
        if (ResultsList is null)
            return;
        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault -- other keys go on to the list.
        switch (e.Key)
        {
            case Key.Enter:
            case Key.Space when !ContinuesSearch:
                if (ResultsList.SelectedItem is { } item)
                    Choose(item);
                e.Handled = true;
                break;
            // A handled key produces no TextInput on X11, so the space is added here.
            case Key.Space:
                ContinueSearch(" ");
                e.Handled = true;
                break;
            case Key.Up when e.KeyModifiers == KeyModifiers.None && IsSearchable && ResultsList.SelectedIndex <= 0:
                SearchBox?.Focus(NavigationMethod.Directional);
                e.Handled = true;
                break;
        }
    }

    private void OnResultsTextInput(object? sender, TextInputEventArgs e)
    {
        if (!IsSearchable || string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl) || e.Text.All(char.IsWhiteSpace))
            return;
        ContinueSearch(e.Text);
        e.Handled = true;
    }

    private void ContinueSearch(string text)
    {
        if (SearchBox is null)
            return;
        SearchBox.Text += text;
        SearchBox.CaretIndex = SearchBox.Text.Length;
        SearchBox.Focus(NavigationMethod.Directional);
    }

    // A space picks the focused choice, unless a search is underway: then it separates words.
    private bool ContinuesSearch => Query.Length > 0;

    private void OnResultsTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: { } item })
            Choose(item);
    }

    private void UpdateNames()
    {
        var name = AutomationProperties.GetName(this);
        if (SearchBox is not null)
        {
            var label = string.IsNullOrWhiteSpace(name)
                ? Loc.Instance["Common.SearchPlaceholder"]
                : Loc.Instance.GetString("Common.SearchNamed", name);
            AutomationProperties.SetName(SearchBox, label);
        }
        if (ResultsList is not null && !string.IsNullOrWhiteSpace(name))
            AutomationProperties.SetName(ResultsList, name);
    }
}
