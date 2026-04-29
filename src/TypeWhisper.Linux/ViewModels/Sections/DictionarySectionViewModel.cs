using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class DictionarySectionViewModel : ObservableObject
{
    private readonly IDictionaryService _dict;
    private readonly ISettingsService _settings;

    public ObservableCollection<DictionaryEntry> FilteredEntries { get; } = [];
    public ObservableCollection<TermPackItemViewModel> Packs { get; } = [];

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _vocabularyBoostingEnabled;
    [ObservableProperty] private string _newOriginal = "";
    [ObservableProperty] private string _newReplacement = "";
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private int _newPriority;
    [ObservableProperty] private DictionaryEntryType _newEntryType = DictionaryEntryType.Correction;

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);
    public int EntryCount => SelectedTab == 3 ? Packs.Count(pack => pack.IsEnabled) : FilteredEntries.Count;
    public int ActiveBoostingTermCount => _dict.Entries.Count(entry => entry.IsEnabled && entry.EntryType == DictionaryEntryType.Term);
    public string VocabularyBoostingStatusText => ActiveBoostingTermCount == 0
        ? "No active terms available for boosting"
        : $"{ActiveBoostingTermCount} active term(s) available for boosting";

    public bool IsAllTabSelected => SelectedTab == 0;
    public bool IsTermsTabSelected => SelectedTab == 1;
    public bool IsCorrectionsTabSelected => SelectedTab == 2;
    public bool IsPacksTabSelected => SelectedTab == 3;

    public bool ShowEntriesList => SelectedTab != 3 && FilteredEntries.Count > 0;
    public bool ShowPacksList => SelectedTab == 3 && Packs.Count > 0;
    public bool ShowEmptyState => SelectedTab != 3 && FilteredEntries.Count == 0;
    public bool ShowAddBar => SelectedTab != 3;
    public bool ShowSearchBox => SelectedTab != 3;
    public bool ShowFilterRow => SelectedTab != 3;
    public bool ShowPacksHeader => SelectedTab == 3;
    public string EmptyStateTitle => SelectedTab switch
    {
        1 => "No terms yet",
        2 => "No corrections yet",
        _ => "No entries yet"
    };
    public string EmptyStateSubtitle => SelectedTab switch
    {
        1 => "Add terms or enable a pack",
        2 => "Add corrections to rewrite recognized text",
        _ => "Add terms and corrections or enable a pack"
    };

    public DictionarySectionViewModel(IDictionaryService dict, ISettingsService settings)
    {
        _dict = dict;
        _settings = settings;
        _vocabularyBoostingEnabled = settings.Current.VocabularyBoostingEnabled;

        _dict.EntriesChanged += () => Dispatcher.UIThread.Post(Refresh);
        InitializePacks();
        Refresh();
    }

    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsAllTabSelected));
        OnPropertyChanged(nameof(IsTermsTabSelected));
        OnPropertyChanged(nameof(IsCorrectionsTabSelected));
        OnPropertyChanged(nameof(IsPacksTabSelected));
        Refresh();
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchText));
        Refresh();
    }

    partial void OnVocabularyBoostingEnabledChanged(bool value)
    {
        if (_settings.Current.VocabularyBoostingEnabled == value)
            return;

        _settings.Save(_settings.Current with { VocabularyBoostingEnabled = value });
    }

    partial void OnNewEntryTypeChanged(DictionaryEntryType value)
    {
        OnPropertyChanged(nameof(IsNewTypeCorrection));
        OnPropertyChanged(nameof(IsNewTypeTerm));
    }

    public bool IsNewTypeCorrection
    {
        get => NewEntryType == DictionaryEntryType.Correction;
        set
        {
            if (value)
                NewEntryType = DictionaryEntryType.Correction;
        }
    }

    public bool IsNewTypeTerm
    {
        get => NewEntryType == DictionaryEntryType.Term;
        set
        {
            if (value)
                NewEntryType = DictionaryEntryType.Term;
        }
    }

    [RelayCommand]
    private void SetTab(object? tab)
    {
        switch (tab)
        {
            case int intValue:
                SelectedTab = intValue;
                break;
            case string stringValue when int.TryParse(stringValue, out var parsed):
                SelectedTab = parsed;
                break;
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    public string ExportToCsv() => _dict.ExportToCsv();

    public int ImportFromCsv(string csv) => _dict.ImportFromCsv(csv);

    [RelayCommand]
    private void AddEntry()
    {
        if (string.IsNullOrWhiteSpace(NewOriginal))
            return;

        _dict.AddEntry(new DictionaryEntry
        {
            Id = Guid.NewGuid().ToString(),
            EntryType = NewEntryType,
            Original = NewOriginal.Trim(),
            Replacement = string.IsNullOrWhiteSpace(NewReplacement) ? null : NewReplacement.Trim(),
            CaseSensitive = CaseSensitive,
            IsEnabled = true,
            Priority = NewPriority,
        });

        NewOriginal = "";
        NewReplacement = "";
        CaseSensitive = false;
        NewPriority = 0;
    }

    [RelayCommand]
    private void Delete(DictionaryEntry entry) => _dict.DeleteEntry(entry.Id);

    [RelayCommand]
    private void ToggleEnabled(DictionaryEntry entry) =>
        _dict.UpdateEntry(entry with { IsEnabled = !entry.IsEnabled });

    [RelayCommand]
    private void ToggleStarred(DictionaryEntry entry) =>
        _dict.UpdateEntry(entry with { IsStarred = !entry.IsStarred });

    [RelayCommand]
    private void IncreasePriority(DictionaryEntry entry) =>
        _dict.UpdateEntry(entry with { Priority = Math.Min(entry.Priority + 1, 999) });

    [RelayCommand]
    private void DecreasePriority(DictionaryEntry entry) =>
        _dict.UpdateEntry(entry with { Priority = Math.Max(entry.Priority - 1, 0) });

    [RelayCommand]
    private void TogglePack(TermPackItemViewModel pack)
    {
        if (pack.IsEnabled)
        {
            _dict.DeactivatePack(pack.Pack.Id);
            pack.IsEnabled = false;
        }
        else
        {
            _dict.ActivatePack(pack.Pack);
            pack.IsEnabled = true;
        }

        SaveEnabledPacks();
        Refresh();
    }

    private void InitializePacks()
    {
        Packs.Clear();
        var enabledIds = _settings.Current.EnabledPackIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in TermPack.AllPacks)
            Packs.Add(new TermPackItemViewModel(pack, enabledIds.Contains(pack.Id)));
    }

    private void SaveEnabledPacks()
    {
        var enabledIds = Packs.Where(pack => pack.IsEnabled).Select(pack => pack.Pack.Id).ToArray();
        _settings.Save(_settings.Current with { EnabledPackIds = enabledIds });
    }

    private void Refresh()
    {
        FilteredEntries.Clear();

        IEnumerable<DictionaryEntry> entries = _dict.Entries;

        entries = SelectedTab switch
        {
            1 => entries.Where(entry => entry.EntryType == DictionaryEntryType.Term),
            2 => entries.Where(entry => entry.EntryType == DictionaryEntryType.Correction),
            3 => [],
            _ => entries
        };

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            entries = entries.Where(entry =>
                entry.Original.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (entry.Replacement?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        foreach (var entry in entries
                     .OrderByDescending(entry => entry.IsStarred)
                     .ThenByDescending(entry => entry.Priority)
                     .ThenBy(entry => entry.Original, StringComparer.OrdinalIgnoreCase))
            FilteredEntries.Add(entry);

        OnPropertyChanged(nameof(EntryCount));
        OnPropertyChanged(nameof(ActiveBoostingTermCount));
        OnPropertyChanged(nameof(VocabularyBoostingStatusText));
        OnPropertyChanged(nameof(ShowEntriesList));
        OnPropertyChanged(nameof(ShowPacksList));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowAddBar));
        OnPropertyChanged(nameof(ShowSearchBox));
        OnPropertyChanged(nameof(ShowFilterRow));
        OnPropertyChanged(nameof(ShowPacksHeader));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateSubtitle));
    }
}

public partial class TermPackItemViewModel : ObservableObject
{
    public TermPack Pack { get; }

    [ObservableProperty] private bool _isEnabled;

    public int TermCount => Pack.Terms.Length;
    public string TermCountLabel => $"{TermCount} Terms";
    public string TermsPreview => string.Join(", ", Pack.Terms.Take(8)) +
                                  (Pack.Terms.Length > 8 ? $" +{Pack.Terms.Length - 8}" : "");

    public TermPackItemViewModel(TermPack pack, bool isEnabled)
    {
        Pack = pack;
        _isEnabled = isEnabled;
    }
}
