using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class HistorySectionViewModel : ObservableObject
{
    private readonly IHistoryService _history;
    private readonly IDictionaryService _dictionary;
    private readonly ISettingsService _settings;
    private readonly CorrectionSuggestionService _correctionSuggestions;
    private readonly SessionAudioFileService _sessionAudioFiles;
    private readonly AudioPlaybackService _audioPlayback;
    private bool _suppressRefresh;

    public ObservableCollection<HistoryGroupViewModel> Groups { get; } = [];
    public ObservableCollection<string> AvailableApps { get; } = ["All apps"];

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string _selectedAppFilter = "All apps";
    [ObservableProperty] private string _summary = "0 entries · 0 words";
    [ObservableProperty] private bool _isLoading;

    public bool ShowTimeline => !IsLoading && HasVisibleRecords;
    public bool ShowEmptyState => !IsLoading && !HasVisibleRecords;
    public bool HasVisibleRecords => Groups.Any(group => group.Entries.Count > 0);

    public HistorySectionViewModel(
        IHistoryService history,
        IDictionaryService dictionary,
        ISettingsService settings,
        CorrectionSuggestionService correctionSuggestions,
        SessionAudioFileService sessionAudioFiles,
        AudioPlaybackService audioPlayback)
    {
        _history = history;
        _dictionary = dictionary;
        _settings = settings;
        _correctionSuggestions = correctionSuggestions;
        _sessionAudioFiles = sessionAudioFiles;
        _audioPlayback = audioPlayback;

        _history.RecordsChanged += () =>
        {
            if (!_suppressRefresh)
                Dispatcher.UIThread.Post(Refresh);
        };
        _audioPlayback.PlaybackStateChanged += () => Dispatcher.UIThread.Post(RefreshPlaybackState);
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            await _history.EnsureLoadedAsync();
            Refresh();
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(ShowTimeline));
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    partial void OnSearchQueryChanged(string value) => Refresh();
    partial void OnSelectedAppFilterChanged(string value) => Refresh();

    public void ClearAll() => _history.ClearAll();

    [RelayCommand]
    private void DeleteRecord(HistoryRecordRow record) => _history.DeleteRecord(record.Record.Id);

    [RelayCommand]
    private void TogglePlayback(HistoryRecordRow record)
    {
        if (!record.HasSessionAudio || string.IsNullOrWhiteSpace(record.Record.AudioFileName))
            return;

        if (record.IsPlaying)
            _audioPlayback.Stop();
        else
            _audioPlayback.Play(record.Record.AudioFileName);
    }

    internal void SaveEdit(HistoryRecordRow record, string newText)
    {
        var originalText = record.Record.FinalText;

        _suppressRefresh = true;
        try
        {
            _history.UpdateRecord(record.Record.Id, newText);
        }
        finally
        {
            _suppressRefresh = false;
        }

        var suggestions = _correctionSuggestions.GenerateSuggestions(originalText, newText);
        if (_settings.Current.AutoAddDictionaryCorrections)
        {
            LearnCorrections(suggestions.Select(suggestion => new CorrectionSuggestionRow(suggestion)));
            _history.SetPendingCorrectionSuggestions(record.Record.Id, []);
            record.SetCorrectionSuggestions([]);
        }
        else
        {
            _history.SetPendingCorrectionSuggestions(record.Record.Id, suggestions);
            record.SetCorrectionSuggestions(suggestions);
        }

        Summary = $"{_history.TotalRecords} entries · {_history.TotalWords} words";
    }

    internal void LearnCorrections(IEnumerable<CorrectionSuggestionRow> suggestions)
    {
        foreach (var suggestion in suggestions.Where(suggestion => suggestion.IsApproved))
        {
            if (string.IsNullOrWhiteSpace(suggestion.Original)
                || string.IsNullOrWhiteSpace(suggestion.Replacement))
                continue;

            _dictionary.LearnCorrection(suggestion.Original.Trim(), suggestion.Replacement.Trim());
        }
    }

    internal void AddTermFromHistory(HistoryRecordRow record)
    {
        var term = record.Record.FinalText.Trim();
        if (string.IsNullOrWhiteSpace(term))
            return;

        var exists = _dictionary.Entries.Any(entry =>
            entry.EntryType == DictionaryEntryType.Term
            && string.Equals(entry.Original.Trim(), term, StringComparison.OrdinalIgnoreCase));
        if (exists)
            return;

        _dictionary.AddEntry(new DictionaryEntry
        {
            Id = Guid.NewGuid().ToString(),
            EntryType = DictionaryEntryType.Term,
            Original = term,
            Source = DictionaryEntrySource.Manual
        });
    }

    internal void SetPendingCorrectionSuggestions(
        HistoryRecordRow record,
        IReadOnlyList<CorrectionSuggestion> suggestions)
    {
        _history.SetPendingCorrectionSuggestions(record.Record.Id, suggestions);
        record.Record = record.Record with { PendingCorrectionSuggestions = suggestions };
    }

    internal void CollapseAllExcept(HistoryRecordRow keep)
    {
        foreach (var group in Groups)
        foreach (var entry in group.Entries)
        {
            if (entry == keep)
                continue;

            if (entry.IsExpanded)
            {
                entry.IsEditing = false;
                entry.IsExpanded = false;
            }
        }
    }

    internal bool HasSessionAudio(TranscriptionRecord record) => _sessionAudioFiles.HasAudio(record.AudioFileName);

    internal bool IsPlaying(TranscriptionRecord record) =>
        _audioPlayback.IsPlaying
        && !string.IsNullOrWhiteSpace(_audioPlayback.CurrentFile)
        && string.Equals(_audioPlayback.CurrentFile, record.AudioFileName, StringComparison.OrdinalIgnoreCase);

    public string BuildExportContent(string extension)
    {
        var visibleRecords = GetVisibleRecords().ToList();
        return extension.ToLowerInvariant() switch
        {
            ".csv" => _history.ExportToCsv(visibleRecords),
            ".md" => _history.ExportToMarkdown(visibleRecords),
            ".json" => _history.ExportToJson(visibleRecords),
            _ => _history.ExportToText(visibleRecords)
        };
    }

    private IEnumerable<TranscriptionRecord> GetVisibleRecords()
    {
        IEnumerable<TranscriptionRecord> records = _history.Records;

        if (!string.IsNullOrWhiteSpace(SelectedAppFilter) && !string.Equals(SelectedAppFilter, "All apps", StringComparison.Ordinal))
        {
            records = records.Where(record =>
                string.Equals(record.AppProcessName, SelectedAppFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var query = SearchQuery;
            records = records.Where(record =>
                record.FinalText.Contains(query, StringComparison.OrdinalIgnoreCase)
                || record.RawText.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (record.AppName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return records.OrderByDescending(record => record.Timestamp);
    }

    private void Refresh()
    {
        RebuildAppFilter();

        Groups.Clear();
        foreach (var group in GetVisibleRecords().GroupBy(record => ComputeDateGroup(record.Timestamp)))
        {
            var groupViewModel = new HistoryGroupViewModel(group.Key);
            foreach (var record in group)
                groupViewModel.Entries.Add(new HistoryRecordRow(record, this));

            Groups.Add(groupViewModel);
        }

        Summary = $"{_history.TotalRecords} entries · {_history.TotalWords} words";
        OnPropertyChanged(nameof(HasVisibleRecords));
        OnPropertyChanged(nameof(ShowTimeline));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void RefreshPlaybackState()
    {
        foreach (var group in Groups)
        foreach (var record in group.Entries)
            record.NotifyPlaybackStateChanged();
    }

    private void RebuildAppFilter()
    {
        var current = SelectedAppFilter;
        AvailableApps.Clear();
        AvailableApps.Add("All apps");
        foreach (var app in _history.GetDistinctApps())
            AvailableApps.Add(app);

        SelectedAppFilter = AvailableApps.Contains(current) ? current : "All apps";
    }

    private static string ComputeDateGroup(DateTime timestamp)
    {
        var today = DateTime.Today;
        var date = timestamp.Date;

        if (date == today)
            return "Today";

        if (date == today.AddDays(-1))
            return "Yesterday";

        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var thisMonday = today.AddDays(-daysSinceMonday);
        if (date >= thisMonday)
            return "This Week";

        var lastMonday = thisMonday.AddDays(-7);
        if (date >= lastMonday)
            return "Last Week";

        return timestamp.ToString("MMMM yyyy");
    }
}

public sealed class HistoryGroupViewModel
{
    public string Name { get; }
    public ObservableCollection<HistoryRecordRow> Entries { get; } = [];

    public HistoryGroupViewModel(string name)
    {
        Name = name;
    }
}

public partial class HistoryRecordRow : ObservableObject
{
    private readonly HistorySectionViewModel _owner;

    [ObservableProperty] private TranscriptionRecord _record;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editText = "";

    public ObservableCollection<CorrectionSuggestionRow> CorrectionSuggestions { get; } = [];

    public string TimeLabel => Record.Timestamp.ToString("HH:mm");
    public string DurationLabel => $"{Record.DurationSeconds:F1}s";
    public bool HasProfileName => !string.IsNullOrWhiteSpace(Record.ProfileName);
    public bool HasAppProcessName => !string.IsNullOrWhiteSpace(Record.AppProcessName);
    public bool HasLanguage => !string.IsNullOrWhiteSpace(Record.Language);
    public bool HasSessionAudio => _owner.HasSessionAudio(Record);
    public bool IsPlaying => _owner.IsPlaying(Record);
    public string PlaybackButtonText => IsPlaying ? "Stop" : "Play";
    public bool ShowReadOnlyText => IsExpanded && !IsEditing;
    public bool ShowEditPanel => IsExpanded && IsEditing;
    public bool ShowExpandedMeta => IsExpanded && !IsEditing;
    public bool ShowExpandedActions => IsExpanded && !IsEditing;
    public bool HasCorrectionSuggestions => IsExpanded && !IsEditing && CorrectionSuggestions.Count > 0;

    public HistoryRecordRow(TranscriptionRecord record, HistorySectionViewModel owner)
    {
        _record = record;
        _owner = owner;
        SetCorrectionSuggestions(record.PendingCorrectionSuggestions);
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            _owner.CollapseAllExcept(this);
        }
        else
        {
            IsEditing = false;
            CorrectionSuggestions.Clear();
        }

        NotifyExpansionStateChanged();
    }

    partial void OnIsEditingChanged(bool value) => NotifyExpansionStateChanged();

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void StartEdit()
    {
        EditText = Record.FinalText;
        IsEditing = true;
    }

    [RelayCommand]
    private void SaveEdit()
    {
        _owner.SaveEdit(this, EditText);
        Record = Record with { FinalText = EditText };
        IsEditing = false;
        OnPropertyChanged(nameof(HasLanguage));
        OnPropertyChanged(nameof(HasProfileName));
        OnPropertyChanged(nameof(HasAppProcessName));
        OnPropertyChanged(nameof(HasCorrectionSuggestions));
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private void Delete() => _owner.DeleteRecordCommand.Execute(this);

    [RelayCommand]
    private void TogglePlayback() => _owner.TogglePlaybackCommand.Execute(this);

    [RelayCommand]
    private void SaveApprovedCorrections()
    {
        _owner.LearnCorrections(CorrectionSuggestions);
        CorrectionSuggestions.Clear();
        _owner.SetPendingCorrectionSuggestions(this, []);
        OnPropertyChanged(nameof(HasCorrectionSuggestions));
    }

    [RelayCommand]
    private void DismissCorrectionSuggestions()
    {
        CorrectionSuggestions.Clear();
        _owner.SetPendingCorrectionSuggestions(this, []);
        OnPropertyChanged(nameof(HasCorrectionSuggestions));
    }

    [RelayCommand]
    private void AddToDictionary() => _owner.AddTermFromHistory(this);

    internal void SetCorrectionSuggestions(IEnumerable<CorrectionSuggestion> suggestions)
    {
        CorrectionSuggestions.Clear();
        foreach (var suggestion in suggestions)
            CorrectionSuggestions.Add(new CorrectionSuggestionRow(suggestion));

        OnPropertyChanged(nameof(HasCorrectionSuggestions));
    }

    internal void NotifyPlaybackStateChanged()
    {
        OnPropertyChanged(nameof(HasSessionAudio));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(PlaybackButtonText));
    }

    private void NotifyExpansionStateChanged()
    {
        OnPropertyChanged(nameof(ShowReadOnlyText));
        OnPropertyChanged(nameof(ShowEditPanel));
        OnPropertyChanged(nameof(ShowExpandedMeta));
        OnPropertyChanged(nameof(ShowExpandedActions));
        OnPropertyChanged(nameof(HasCorrectionSuggestions));
    }
}

public partial class CorrectionSuggestionRow : ObservableObject
{
    [ObservableProperty] private bool _isApproved = true;
    [ObservableProperty] private string _original;
    [ObservableProperty] private string _replacement;

    public double Confidence { get; }
    public string ConfidenceLabel => Confidence > 0 ? $"{Confidence:P0}" : "";

    public CorrectionSuggestionRow(CorrectionSuggestion suggestion)
    {
        _original = suggestion.Original;
        _replacement = suggestion.Replacement;
        Confidence = suggestion.Confidence;
    }
}
