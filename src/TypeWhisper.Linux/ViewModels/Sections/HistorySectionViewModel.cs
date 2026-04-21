using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class HistorySectionViewModel : ObservableObject
{
    private readonly IHistoryService _history;

    public ObservableCollection<TranscriptionRecord> Records { get; } = [];

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string _summary = "";

    public HistorySectionViewModel(IHistoryService history)
    {
        _history = history;
        _history.RecordsChanged += () => Dispatcher.UIThread.Post(Refresh);
        _ = _history.EnsureLoadedAsync().ContinueWith(_ => Dispatcher.UIThread.Post(Refresh));
        Refresh();
    }

    private void Refresh()
    {
        Records.Clear();
        var source = string.IsNullOrWhiteSpace(SearchQuery)
            ? _history.Records
            : _history.Search(SearchQuery);
        foreach (var r in source.OrderByDescending(r => r.Timestamp))
            Records.Add(r);
        Summary = $"{_history.TotalRecords} record(s) · {_history.TotalWords} word(s) · {TimeSpan.FromSeconds(_history.TotalDuration):hh\\:mm\\:ss}";
    }

    partial void OnSearchQueryChanged(string value) => Refresh();

    [RelayCommand]
    private void DeleteRecord(TranscriptionRecord record) => _history.DeleteRecord(record.Id);

    [RelayCommand]
    private void ClearAll() => _history.ClearAll();
}
