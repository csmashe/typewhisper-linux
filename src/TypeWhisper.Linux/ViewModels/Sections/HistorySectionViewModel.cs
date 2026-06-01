using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;

// ReSharper disable UnusedParameterInPartialMethod

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class HistorySectionViewModel : ObservableObject
{
    private readonly AudioPlaybackService _audioPlayback;
    private readonly CorrectionSuggestionService _correctionSuggestions;
    private readonly IDictionaryService _dictionary;
    private readonly IHistoryService _history;
    private readonly SessionAudioFileService _sessionAudioFiles;
    private readonly ISettingsService _settings;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private string _selectedAppFilter = "All apps";

    [ObservableProperty]
    private string _summary = "0 entries · 0 words";

    // Prevents a full UI rebuild while SaveEdit is mid-flight: the
    // history service fires RecordsChanged synchronously, but we need to
    // finish updating correction suggestions before the rows are torn down.
    private bool _suppressRefresh;

    // History can hold thousands of entries; materializing every row up front
    // is slow, so rows are built in pages and appended as the user scrolls.
    private const int PageSize = 40;
    private readonly List<TranscriptionRecord> _filtered = [];
    private int _shownCount;

    public HistorySectionViewModel(
        IHistoryService history,
        IDictionaryService dictionary,
        ISettingsService settings,
        CorrectionSuggestionService correctionSuggestions,
        SessionAudioFileService sessionAudioFiles,
        AudioPlaybackService audioPlayback
    )
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
            {
                Dispatcher.UIThread.Post(Refresh);
            }
        };
        _audioPlayback.PlaybackStateChanged += () => Dispatcher.UIThread.Post(RefreshPlaybackState);
        _ = LoadAsync();
    }

    public ObservableCollection<HistoryGroupViewModel> Groups { get; } = [];
    public ObservableCollection<string> AvailableApps { get; } = ["All apps"];

    public bool ShowTimeline => !IsLoading && HasVisibleRecords;
    public bool ShowEmptyState => !IsLoading && !HasVisibleRecords;
    public bool HasVisibleRecords => Groups.Any(group => group.Entries.Count > 0);
    public bool HasMore => _shownCount < _filtered.Count;

    public void ClearAll()
    {
        _history.ClearAll();
    }

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

    internal void SaveEdit(HistoryRecordRow record, string newText)
    {
        var originalText = record.Record.FinalText;

        _suppressRefresh = true;
        try
        {
            _history.UpdateRecord(record.Record.Id, newText);

            var suggestions = _correctionSuggestions.GenerateSuggestions(originalText, newText);
            if (_settings.Current.AutoAddDictionaryCorrections)
            {
                LearnCorrections(
                    suggestions.Select(suggestion => new CorrectionSuggestionRow(suggestion))
                );
                if (SetPendingCorrectionSuggestions(record, []))
                {
                    record.SetCorrectionSuggestions([]);
                }
            }
            else
            {
                if (SetPendingCorrectionSuggestions(record, suggestions))
                {
                    record.SetCorrectionSuggestions(suggestions);
                }
            }

            Summary = $"{_history.TotalRecords} entries · {_history.TotalWords} words";
        }
        finally
        {
            _suppressRefresh = false;
        }

        Refresh();
    }

    internal void LearnCorrections(IEnumerable<CorrectionSuggestionRow> suggestions)
    {
        foreach (var suggestion in suggestions.Where(suggestion => suggestion.IsApproved))
        {
            if (
                string.IsNullOrWhiteSpace(suggestion.Original)
                || string.IsNullOrWhiteSpace(suggestion.Replacement)
            )
            {
                continue;
            }

            _dictionary.LearnCorrection(suggestion.Original.Trim(), suggestion.Replacement.Trim());
        }
    }

    internal void AddTermFromHistory(HistoryRecordRow record)
    {
        var term = record.Record.FinalText.Trim();
        if (string.IsNullOrWhiteSpace(term))
        {
            return;
        }

        var exists = _dictionary.Entries.Any(entry =>
            entry.EntryType == DictionaryEntryType.Term
            && string.Equals(entry.Original.Trim(), term, StringComparison.OrdinalIgnoreCase)
        );
        if (exists)
        {
            return;
        }

        _dictionary.AddEntry(
            new DictionaryEntry
            {
                Id = Guid.NewGuid().ToString(),
                EntryType = DictionaryEntryType.Term,
                Original = term,
                Source = DictionaryEntrySource.Manual
            }
        );
    }

    internal bool SetPendingCorrectionSuggestions(
        HistoryRecordRow record,
        IReadOnlyList<CorrectionSuggestion> suggestions
    )
    {
        try
        {
            _history.SetPendingCorrectionSuggestions(record.Record.Id, suggestions);
            record.Record = record.Record with { PendingCorrectionSuggestions = suggestions };
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[HistorySectionViewModel] Failed to persist correction suggestions: {ex}"
            );
            return false;
        }
    }

    internal void CollapseAllExcept(HistoryRecordRow keep)
    {
        foreach (var group in Groups)
        foreach (var entry in group.Entries)
        {
            if (entry == keep)
            {
                continue;
            }

            if (entry.IsExpanded)
            {
                entry.IsEditing = false;
                entry.IsExpanded = false;
            }
        }
    }

    internal bool HasSessionAudio(TranscriptionRecord record)
    {
        return _sessionAudioFiles.HasAudio(record.AudioFileName);
    }

    internal bool IsPlaying(TranscriptionRecord record)
    {
        return _audioPlayback.IsPlaying
               && !string.IsNullOrWhiteSpace(_audioPlayback.CurrentFile)
               && string.Equals(
                   _audioPlayback.CurrentFile,
                   record.AudioFileName,
                   StringComparison.OrdinalIgnoreCase
               );
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

    partial void OnSearchQueryChanged(string value)
    {
        Refresh();
    }

    partial void OnSelectedAppFilterChanged(string value)
    {
        Refresh();
    }

    [RelayCommand]
    private void DeleteRecord(HistoryRecordRow record)
    {
        _history.DeleteRecord(record.Record.Id);
    }

    [RelayCommand]
    private void TogglePlayback(HistoryRecordRow record)
    {
        if (!record.HasSessionAudio || string.IsNullOrWhiteSpace(record.Record.AudioFileName))
        {
            return;
        }

        if (record.IsPlaying)
        {
            _audioPlayback.Stop();
        }
        else
        {
            _audioPlayback.Play(record.Record.AudioFileName);
        }
    }

    private IEnumerable<TranscriptionRecord> GetVisibleRecords()
    {
        IEnumerable<TranscriptionRecord> records = _history.Records;

        if (
            !string.IsNullOrWhiteSpace(SelectedAppFilter)
            && !string.Equals(SelectedAppFilter, "All apps", StringComparison.Ordinal)
        )
        {
            records = records.Where(record =>
                string.Equals(
                    record.AppProcessName,
                    SelectedAppFilter,
                    StringComparison.OrdinalIgnoreCase
                )
            );
        }

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var query = SearchQuery;
            records = records.Where(record =>
                record.FinalText.Contains(query, StringComparison.OrdinalIgnoreCase)
                || record.RawText.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (record.AppName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            );
        }

        return records.OrderByDescending(record => record.Timestamp);
    }

    private void Refresh()
    {
        RebuildAppFilter();

        _filtered.Clear();
        _filtered.AddRange(GetVisibleRecords());
        _shownCount = 0;

        Groups.Clear();
        AppendNextPage();

        Summary = $"{_history.TotalRecords} entries · {_history.TotalWords} words";
        OnPropertyChanged(nameof(HasVisibleRecords));
        OnPropertyChanged(nameof(ShowTimeline));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(HasMore));
    }

    /// <summary>
    /// Materializes the next page of rows. Called on initial load and as the
    /// user scrolls toward the bottom of the timeline.
    /// </summary>
    public void LoadMore()
    {
        if (!HasMore)
        {
            return;
        }

        AppendNextPage();
        OnPropertyChanged(nameof(HasMore));
    }

    private void AppendNextPage()
    {
        var end = Math.Min(_shownCount + PageSize, _filtered.Count);
        for (var i = _shownCount; i < end; i++)
        {
            var record = _filtered[i];
            var groupName = ComputeDateGroup(record.Timestamp);

            // Records are sorted newest-first and date buckets are contiguous
            // along that order, so a record either continues the last group or
            // begins a new one.
            var group =
                Groups.Count > 0 && Groups[^1].Name == groupName ? Groups[^1] : null;
            if (group is null)
            {
                group = new HistoryGroupViewModel(groupName);
                Groups.Add(group);
            }

            group.Entries.Add(new HistoryRecordRow(record, this));
        }

        _shownCount = end;
    }

    private void RefreshPlaybackState()
    {
        foreach (var group in Groups)
        foreach (var record in group.Entries)
        {
            record.NotifyPlaybackStateChanged();
        }
    }

    private void RebuildAppFilter()
    {
        var current = SelectedAppFilter;
        AvailableApps.Clear();
        AvailableApps.Add("All apps");
        foreach (var app in _history.GetDistinctApps())
        {
            AvailableApps.Add(app);
        }

        SelectedAppFilter = AvailableApps.Contains(current) ? current : "All apps";
    }

    private static string ComputeDateGroup(DateTime timestamp)
    {
        var today = DateTime.Today;
        var date = timestamp.Date;

        if (date == today)
        {
            return "Today";
        }

        if (date == today.AddDays(-1))
        {
            return "Yesterday";
        }

        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var thisMonday = today.AddDays(-daysSinceMonday);
        if (date >= thisMonday)
        {
            return "This Week";
        }

        var lastMonday = thisMonday.AddDays(-7);
        if (date >= lastMonday)
        {
            return "Last Week";
        }

        return timestamp.ToString("MMMM yyyy");
    }
}

public sealed class HistoryGroupViewModel
{
    public HistoryGroupViewModel(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public ObservableCollection<HistoryRecordRow> Entries { get; } = [];
}

public partial class HistoryRecordRow : ObservableObject
{
    private readonly HistorySectionViewModel _owner;

    [ObservableProperty]
    private string _editText = "";

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private TranscriptionRecord _record;

    public HistoryRecordRow(TranscriptionRecord record, HistorySectionViewModel owner)
    {
        _record = record;
        _owner = owner;
        SetCorrectionSuggestions(record.PendingCorrectionSuggestions);
    }

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

    public bool HasCorrectionSuggestions =>
        IsExpanded && !IsEditing && CorrectionSuggestions.Count > 0;

    internal void SetCorrectionSuggestions(IEnumerable<CorrectionSuggestion> suggestions)
    {
        CorrectionSuggestions.Clear();
        foreach (var suggestion in suggestions)
        {
            CorrectionSuggestions.Add(new CorrectionSuggestionRow(suggestion));
        }

        OnPropertyChanged(nameof(HasCorrectionSuggestions));
    }

    internal void NotifyPlaybackStateChanged()
    {
        OnPropertyChanged(nameof(HasSessionAudio));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(PlaybackButtonText));
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            _owner.CollapseAllExcept(this);
            SetCorrectionSuggestions(Record.PendingCorrectionSuggestions);
        }
        else
        {
            IsEditing = false;
            CorrectionSuggestions.Clear();
        }

        NotifyExpansionStateChanged();
    }

    partial void OnIsEditingChanged(bool value)
    {
        NotifyExpansionStateChanged();
    }

    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
    }

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
    private void CancelEdit()
    {
        IsEditing = false;
    }

    [RelayCommand]
    private void Delete()
    {
        _owner.DeleteRecordCommand.Execute(this);
    }

    [RelayCommand]
    private void TogglePlayback()
    {
        _owner.TogglePlaybackCommand.Execute(this);
    }

    [RelayCommand]
    private void SaveApprovedCorrections()
    {
        _owner.LearnCorrections(CorrectionSuggestions);
        if (_owner.SetPendingCorrectionSuggestions(this, []))
        {
            CorrectionSuggestions.Clear();
            OnPropertyChanged(nameof(HasCorrectionSuggestions));
        }
    }

    [RelayCommand]
    private void DismissCorrectionSuggestions()
    {
        if (_owner.SetPendingCorrectionSuggestions(this, []))
        {
            CorrectionSuggestions.Clear();
            OnPropertyChanged(nameof(HasCorrectionSuggestions));
        }
    }

    [RelayCommand]
    private void AddToDictionary()
    {
        _owner.AddTermFromHistory(this);
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
    [ObservableProperty]
    private bool _isApproved = true;

    [ObservableProperty]
    private string _original;

    [ObservableProperty]
    private string _replacement;

    public CorrectionSuggestionRow(CorrectionSuggestion suggestion)
    {
        _original = suggestion.Original;
        _replacement = suggestion.Replacement;
        Confidence = suggestion.Confidence;
    }

    private double Confidence { get; }
    public string ConfidenceLabel => Confidence > 0 ? $"{Confidence:P0}" : "";
}