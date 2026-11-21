using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TypeWhisper.Linux.Controls;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.UiTests;

[Collection("ui")]
public sealed class SearchableComboBoxTests(UiSession ui)
{
    private static readonly string[] s_codes = ["de", "en", "es", "fr", "ja", "ru", "uk", "ar", "hy", "pt", "tr"];

    [Fact]
    public async Task LongList_TypingAnEnglishNameAndEnter_PicksTheLanguage() =>
        await WithCombo(LongList(), (window, combo) =>
        {
            Open(combo);
            var search = combo.SearchBox!;
            Assert.True(search.IsEffectivelyVisible);
            Assert.True(search.IsFocused);
            Assert.Equal("Search Spoken language", Avalonia.Automation.AutomationProperties.GetName(search));

            window.KeyTextInput("ger");
            Settle();
            Assert.Equal("de", Code(combo.VisibleChoices[0]));

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Settle();
            Assert.False(combo.IsDropDownOpen);
            Assert.Equal("de", Code(combo.SelectedItem!));
            Assert.True(combo.IsFocused);
        });

    [Fact]
    public async Task ShortList_HasNoSearchField() =>
        await WithCombo(new ObservableCollection<SpokenLanguageOption>(LongList().Take(5)), (_, combo) =>
        {
            Open(combo);
            Assert.False(combo.IsSearchable);
            Assert.False(combo.SearchBox!.IsEffectivelyVisible);
            Assert.Equal(5, combo.VisibleChoices.Count);
        });

    [Fact]
    public async Task ShortList_TypeAheadMovesTheHighlightAndEnterPicksIt() =>
        await WithCombo(new ObservableCollection<SpokenLanguageOption>(LongList().Take(5)), (window, combo) =>
        {
            Open(combo);
            var before = combo.SelectedItem;
            window.KeyTextInput("Fr");
            Settle();
            Assert.Same(before, combo.SelectedItem);
            Assert.Equal("fr", Code(combo.ResultsList!.SelectedItem!));

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Settle();
            Assert.False(combo.IsDropDownOpen);
            Assert.Equal("fr", Code(combo.SelectedItem!));
        });

    [Fact]
    public async Task NoMatch_ShowsAndClearsTheNoMatchesNotice() =>
        await WithCombo(LongList(), (window, combo) =>
        {
            Open(combo);
            window.KeyTextInput("zzzz");
            Settle();
            Assert.Empty(combo.VisibleChoices);
            var notice = NoMatches(combo);
            Assert.True(notice.IsEffectivelyVisible);
            Assert.Equal("No matches.", notice.Text);

            // Enter with nothing found keeps the drop-down open and the selection unchanged.
            var before = combo.SelectedItem;
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Settle();
            Assert.True(combo.IsDropDownOpen);
            Assert.Same(before, combo.SelectedItem);

            combo.SearchBox!.Text = "";
            Settle();
            Assert.False(notice.IsVisible);
            Assert.Equal(LongList().Count, combo.VisibleChoices.Count);
        });

    [Fact]
    public async Task ArrowKeysMoveBetweenSearchAndList_AndTypingOnAChoiceContinuesTheSearch() =>
        await WithCombo(LongList(), (window, combo) =>
        {
            Open(combo);
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Settle();
            Assert.IsType<ListBoxItem>(FocusedElement(window));

            window.KeyTextInput("fr");
            Settle();
            Assert.True(combo.SearchBox!.IsFocused);
            Assert.Equal("fr", combo.SearchBox.Text);
            Assert.Equal("fr", Code(combo.VisibleChoices[0]));

            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Settle();
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Settle();
            Assert.True(combo.SearchBox.IsFocused);
        });

    [Fact]
    public async Task SpaceOnAChoiceDuringASearch_SeparatesWords() =>
        await WithCombo(LongList(), (window, combo) =>
        {
            Open(combo);
            window.KeyTextInput("port");
            Settle();
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Settle();
            Assert.IsType<ListBoxItem>(FocusedElement(window));
            var before = combo.SelectedItem;

            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Settle();
            Assert.Equal("port ", combo.SearchBox!.Text);
            Assert.True(combo.SearchBox.IsFocused);
            Assert.True(combo.IsDropDownOpen);
            Assert.Same(before, combo.SelectedItem);
        });

    [Fact]
    public async Task ListRefilledWhileSearching_KeepsTheSearchFocused() =>
        await WithCombo(LongList(), (window, combo) =>
        {
            Open(combo);
            window.KeyTextInput("fr");
            Settle();
            var items = (ObservableCollection<SpokenLanguageOption>)combo.ItemsSource!;
            var snapshot = items.ToList();
            items.Clear();
            foreach (var item in snapshot)
                items.Add(item);
            Settle();

            Assert.True(combo.SearchBox!.IsEffectivelyVisible);
            Assert.True(combo.SearchBox.IsFocused);
            Assert.Equal("fr", Code(combo.VisibleChoices[0]));
        });

    [Fact]
    public async Task ListEmptiedWhileSearching_ClosesAndRefocusesThePicker() =>
        await WithCombo(LongList(), (window, combo) =>
        {
            Open(combo);
            window.KeyTextInput("fr");
            Settle();
            ((ObservableCollection<SpokenLanguageOption>)combo.ItemsSource!).Clear();
            Settle();

            Assert.False(combo.IsDropDownOpen);
            Assert.True(combo.IsFocused);
        });

    [Fact]
    public async Task ClickingAFilteredChoice_PicksIt() =>
        await WithCombo(LongList(), (window, combo) =>
        {
            Open(combo);
            window.KeyTextInput("rus");
            Settle();
            var target = combo.VisibleChoices[0];
            Assert.Equal("ru", Code(target));

            var row = combo.GetLogicalListItems().First(item => Equals(item.DataContext, target));
            var popup = TopLevel.GetTopLevel(row)!;
            var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), popup)!.Value;
            popup.MouseDown(point, MouseButton.Left);
            popup.MouseUp(point, MouseButton.Left);
            Settle();

            Assert.False(combo.IsDropDownOpen);
            Assert.Same(target, combo.SelectedItem);
        });

    [Fact]
    public async Task ItemsChangingWhileOpen_RefreshTheFilteredList() =>
        await WithCombo(LongList(), (window, combo) =>
        {
            Open(combo);
            window.KeyTextInput("ital");
            Settle();
            Assert.Empty(combo.VisibleChoices);

            ((ObservableCollection<SpokenLanguageOption>)combo.ItemsSource!).Add(new SpokenLanguageOption("it", "Italiano"));
            Settle();
            Assert.Equal("it", Code(Assert.Single(combo.VisibleChoices)));
        });

    private static ObservableCollection<SpokenLanguageOption> LongList() =>
        [.. DictationSectionViewModel.CreateLanguageChoices(s_codes)];

    private static string Code(object item) => ((SpokenLanguageOption)item).Code;

    private static TextBlock NoMatches(SearchableComboBox combo) => combo.NoMatchesNotice!;

    private static object? FocusedElement(Window window) => window.FocusManager.GetFocusedElement();

    private static void Open(SearchableComboBox combo)
    {
        combo.Focus();
        combo.IsDropDownOpen = true;
        Settle();
    }

    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
            Dispatcher.UIThread.RunJobs();
    }

    private async Task WithCombo(ObservableCollection<SpokenLanguageOption> items, Action<Window, SearchableComboBox> test)
    {
        await ui.Session.Dispatch(() =>
        {
            var originalLanguage = Loc.Instance.CurrentLanguage;
            Loc.Instance.CurrentLanguage = "en";
            var combo = new SearchableComboBox { Width = 260, ItemsSource = items, SelectedIndex = 0 };
            Avalonia.Automation.AutomationProperties.SetName(combo, "Spoken language");
            var window = new Window { Width = 400, Height = 600, Content = new StackPanel { Children = { combo } } };
            window.Show();
            try
            {
                Settle();
                test(window, combo);
            }
            finally
            {
                window.Close();
                Loc.Instance.CurrentLanguage = originalLanguage;
                Settle();
            }
        }, CancellationToken.None);
    }
}

internal static class SearchableComboBoxTestExtensions
{
    internal static IEnumerable<ListBoxItem> GetLogicalListItems(this SearchableComboBox combo) =>
        combo.ResultsList!.GetVisualDescendants().OfType<ListBoxItem>();
}
