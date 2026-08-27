using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.ViewModels.Sections;

// MVVM Toolkit [ObservableProperty] generates the On<Property>Changed(value) partial hooks; the
// value parameter is part of the generated signature and cannot be dropped even when ignored here.
// ReSharper disable UnusedParameterInPartialMethod
public partial class DictionarySectionViewModel : ObservableObject
{
    private readonly IDictionaryService _dict;
    private readonly ISettingsService _settings;

    [ObservableProperty]
    private bool _caseSensitive;

    [ObservableProperty]
    private DictionaryEntryType _newEntryType = DictionaryEntryType.Correction;

    [ObservableProperty]
    private string _newOriginal = "";

    [ObservableProperty]
    private int _newPriority;

    [ObservableProperty]
    private string _newReplacement = "";

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private int _selectedTab;

    [ObservableProperty]
    private bool _vocabularyBoostingEnabled;

    public DictionarySectionViewModel(IDictionaryService dict, ISettingsService settings)
    {
        _dict = dict;
        _settings = settings;
        _vocabularyBoostingEnabled = settings.Current.VocabularyBoostingEnabled;

        _dict.EntriesChanged += () => Dispatcher.UIThread.Post(Refresh);
        _settings.SettingsChanged += _ =>
            Dispatcher.UIThread.Post(ReconcileEnabledPacksFromSettings);
        InitializePacks();
        Refresh();
    }

    public ObservableCollection<DictionaryEntry> FilteredEntries { get; } = [];
    public ObservableCollection<TermPackItemViewModel> Packs { get; } = [];

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    public int EntryCount =>
        SelectedTab == 3 ? Packs.Count(pack => pack.IsEnabled) : FilteredEntries.Count;

    public int ActiveBoostingTermCount =>
        _dict.Entries.Count(entry =>
            entry is { IsEnabled: true, EntryType: DictionaryEntryType.Term }
        );

    public string VocabularyBoostingStatusText =>
        ActiveBoostingTermCount == 0
            ? Loc.Instance["Dictionary.NoActiveBoostingTerms"]
            : Loc.Instance.GetString(
                "Dictionary.ActiveBoostingTerms",
                ActiveBoostingTermCount
            );

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

    public string EmptyStateTitle =>
        SelectedTab switch
        {
            1 => Loc.Instance["Dictionary.EmptyTitleTerms"],
            2 => Loc.Instance["Dictionary.EmptyTitleCorrections"],
            _ => Loc.Instance["Dictionary.EmptyTitleAll"],
        };

    public string EmptyStateSubtitle =>
        SelectedTab switch
        {
            1 => Loc.Instance["Dictionary.EmptySubtitleTerms"],
            2 => Loc.Instance["Dictionary.EmptySubtitleCorrections"],
            _ => Loc.Instance["Dictionary.EmptySubtitleAll"],
        };

    public bool IsNewTypeCorrection
    {
        get => NewEntryType == DictionaryEntryType.Correction;
        set
        {
            if (value)
            {
                NewEntryType = DictionaryEntryType.Correction;
            }
        }
    }

    public bool IsNewTypeTerm
    {
        get => NewEntryType == DictionaryEntryType.Term;
        set
        {
            if (value)
            {
                NewEntryType = DictionaryEntryType.Term;
            }
        }
    }

    public string ExportToCsv()
    {
        return _dict.ExportToCsv();
    }

    public int ImportFromCsv(string csv)
    {
        return _dict.ImportFromCsv(csv);
    }

    internal void ReconcileEnabledPacksFromSettings()
    {
        var enabledIds = _settings.Current.EnabledPackIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase
        );
        var changed = false;
        foreach (var pack in Packs)
        {
            var shouldBeEnabled = enabledIds.Contains(pack.Pack.Id);
            if (pack.IsEnabled == shouldBeEnabled)
            {
                continue;
            }

            pack.IsEnabled = shouldBeEnabled;
            if (shouldBeEnabled)
            {
                _dict.ActivatePack(pack.Pack);
            }
            else
            {
                _dict.DeactivatePack(pack.Pack.Id);
            }

            changed = true;
        }

        if (changed)
        {
            Refresh();
        }
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
        {
            return;
        }

        _settings.Update(current => current with { VocabularyBoostingEnabled = value });
    }

    partial void OnNewEntryTypeChanged(DictionaryEntryType value)
    {
        OnPropertyChanged(nameof(IsNewTypeCorrection));
        OnPropertyChanged(nameof(IsNewTypeTerm));
    }

    [RelayCommand]
    private void SetTab(object? tab)
    {
        SelectedTab = tab switch
        {
            int intValue => intValue,
            string stringValue when int.TryParse(stringValue, out var parsed) => parsed,
            // Leave the current tab unchanged for any other value; the
            // [ObservableProperty] setter's equality guard makes this a no-op.
            _ => SelectedTab,
        };
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = "";
    }

    [RelayCommand]
    private void AddEntry()
    {
        if (string.IsNullOrWhiteSpace(NewOriginal))
        {
            return;
        }

        _dict.AddEntry(
            new DictionaryEntry
            {
                Id = Guid.NewGuid().ToString(),
                EntryType = NewEntryType,
                Original = NewOriginal.Trim(),
                Replacement = string.IsNullOrWhiteSpace(NewReplacement)
                    ? null
                    : NewReplacement.Trim(),
                CaseSensitive = CaseSensitive,
                IsEnabled = true,
                Priority = Math.Clamp(NewPriority, 0, 999),
            }
        );

        NewOriginal = "";
        NewReplacement = "";
        CaseSensitive = false;
        NewPriority = 0;
    }

    [RelayCommand]
    private void Delete(DictionaryEntry entry)
    {
        _dict.DeleteEntry(entry.Id);
    }

    [RelayCommand]
    private void ToggleEnabled(DictionaryEntry entry)
    {
        _dict.UpdateEntry(entry with { IsEnabled = !entry.IsEnabled });
    }

    [RelayCommand]
    private void ToggleStarred(DictionaryEntry entry)
    {
        _dict.UpdateEntry(entry with { IsStarred = !entry.IsStarred });
    }

    [RelayCommand]
    private void IncreasePriority(DictionaryEntry entry)
    {
        _dict.UpdateEntry(entry with { Priority = Math.Min(entry.Priority + 1, 999) });
    }

    [RelayCommand]
    private void DecreasePriority(DictionaryEntry entry)
    {
        _dict.UpdateEntry(entry with { Priority = Math.Max(entry.Priority - 1, 0) });
    }

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
        var enabledIds = _settings.Current.EnabledPackIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var pack in TermPack.AllPacks)
        {
            Packs.Add(new TermPackItemViewModel(pack, enabledIds.Contains(pack.Id)));
        }
    }

    private void SaveEnabledPacks()
    {
        var enabledIds = Packs.Where(pack => pack.IsEnabled).Select(pack => pack.Pack.Id).ToArray();
        _settings.Update(current => current with { EnabledPackIds = enabledIds });
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
            _ => entries,
        };

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            entries = entries.Where(entry =>
                entry.Original.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (
                    entry.Replacement?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false
                )
            );
        }

        foreach (
            var entry in entries
                .OrderByDescending(entry => entry.IsStarred)
                .ThenByDescending(entry => entry.Priority)
                .ThenBy(entry => entry.Original, StringComparer.OrdinalIgnoreCase)
        )
        {
            FilteredEntries.Add(entry);
        }

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
    [ObservableProperty]
    private bool _isEnabled;

    public TermPackItemViewModel(TermPack pack, bool isEnabled)
    {
        Pack = pack;
        _isEnabled = isEnabled;
    }

    public TermPack Pack { get; }

    private int TermCount => Pack.Terms.Length;
    public string TermCountLabel => Loc.Instance.GetString("Dictionary.TermCountLabel", TermCount);

    public string TermsPreview =>
        string.Join(", ", Pack.Terms.Take(8))
        + (Pack.Terms.Length > 8 ? $" +{Pack.Terms.Length - 8}" : "");
}
