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

    public ObservableCollection<DictionaryEntry> Entries { get; } = [];

    [ObservableProperty] private string _newOriginal = "";
    [ObservableProperty] private string _newReplacement = "";
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private DictionaryEntryType _newEntryType = DictionaryEntryType.Term;

    public IReadOnlyList<DictionaryEntryType> EntryTypes { get; } =
        [DictionaryEntryType.Term, DictionaryEntryType.Correction];

    public DictionarySectionViewModel(IDictionaryService dict)
    {
        _dict = dict;
        _dict.EntriesChanged += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    private void Refresh()
    {
        Entries.Clear();
        foreach (var e in _dict.Entries.OrderBy(e => e.Original, StringComparer.OrdinalIgnoreCase))
            Entries.Add(e);
    }

    [RelayCommand]
    private void AddEntry()
    {
        if (string.IsNullOrWhiteSpace(NewOriginal)) return;
        _dict.AddEntry(new DictionaryEntry
        {
            Id = Guid.NewGuid().ToString(),
            EntryType = NewEntryType,
            Original = NewOriginal.Trim(),
            Replacement = string.IsNullOrWhiteSpace(NewReplacement) ? null : NewReplacement.Trim(),
            CaseSensitive = CaseSensitive,
            IsEnabled = true,
        });
        NewOriginal = "";
        NewReplacement = "";
    }

    [RelayCommand]
    private void Delete(DictionaryEntry entry) => _dict.DeleteEntry(entry.Id);

    [RelayCommand]
    private void ToggleEnabled(DictionaryEntry entry)
        => _dict.UpdateEntry(entry with { IsEnabled = !entry.IsEnabled });
}
