using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;

// ReSharper disable UnusedParameterInPartialMethod

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class HistorySectionViewModel : ObservableObject
{
    // Thousands of entries: rows are materialized in pages and appended on scroll.
    private const int PageSize = 40;
    private readonly AudioPlaybackService _audioPlayback;
    private readonly IDictionaryService _dictionary;
    private readonly List<TranscriptionRecord> _filtered = [];
    private readonly IHistoryService _history;
    private readonly SessionAudioFileService _sessionAudioFiles;
    private readonly ISettingsService _settings;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private string _selectedAppFilter = Loc.Instance["History.AllApps"];

    private int _shownCount;

    [ObservableProperty]
    private string _summary = Loc.Instance.GetString("History.Summary", 0, 0);

    // Suppresses RecordsChanged-triggered refresh while SaveEdit is mid-flight:
    // correction suggestions must be updated before rows are rebuilt.
    private bool _suppressRefresh;

    public HistorySectionViewModel(
        IHistoryService history,
        IDictionaryService dictionary,
        ISettingsService settings,
        SessionAudioFileService sessionAudioFiles,
        AudioPlaybackService audioPlayback
    )
    {
        _history = history;
        _dictionary = dictionary;
        _settings = settings;
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
    public ObservableCollection<string> AvailableApps { get; } = [Loc.Instance["History.AllApps"]];

    public bool ShowTimeline => !IsLoading && HasVisibleRecords;
    public bool ShowEmptyState => !IsLoading && !HasVisibleRecords;

    // Exposed to entry rows so the Inspect panel can explain an empty prompt list as
    // "capture is off" (and point at the setting) rather than implying the LLM simply
    // wasn't used.
    public bool CaptureLlmProvenanceEnabled => _settings.Current.CaptureLlmProvenance;
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

    /// <summary>
    ///     Materializes the next page of rows. Called on initial load and as the
    ///     user scrolls toward the bottom of the timeline.
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

    internal void SaveEdit(HistoryRecordRow record, string newText)
    {
        var originalText = record.Record.FinalText;

        _suppressRefresh = true;
        try
        {
            _history.UpdateRecord(record.Record.Id, newText);

            var suggestions = CorrectionSuggestionService.GenerateSuggestions(originalText, newText);
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

            Summary = Loc.Instance.GetString(
                "History.Summary",
                _history.TotalRecords,
                _history.TotalWords
            );
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

            if (!entry.IsExpanded)
            {
                continue;
            }

            entry.IsEditing = false;
            entry.IsExpanded = false;
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
            && !string.Equals(
                SelectedAppFilter,
                Loc.Instance["History.AllApps"],
                StringComparison.Ordinal
            )
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

        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            return records.OrderByDescending(record => record.Timestamp);
        }

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

        Summary = Loc.Instance.GetString(
            "History.Summary",
            _history.TotalRecords,
            _history.TotalWords
        );
        OnPropertyChanged(nameof(HasVisibleRecords));
        OnPropertyChanged(nameof(ShowTimeline));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(HasMore));
    }

    private void AppendNextPage()
    {
        var end = Math.Min(_shownCount + PageSize, _filtered.Count);
        for (var i = _shownCount; i < end; i++)
        {
            var record = _filtered[i];
            var groupName = ComputeDateGroup(record.Timestamp);

            // Records are newest-first; each record either extends the last group or starts a new one.
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
        var allApps = Loc.Instance["History.AllApps"];
        AvailableApps.Clear();
        AvailableApps.Add(allApps);
        foreach (var app in _history.GetDistinctApps())
        {
            AvailableApps.Add(app);
        }

        SelectedAppFilter = AvailableApps.Contains(current) ? current : allApps;
    }

    private static string ComputeDateGroup(DateTime timestamp)
    {
        var today = DateTime.Today;
        var date = timestamp.Date;

        if (date == today)
        {
            return Loc.Instance["History.GroupToday"];
        }

        if (date == today.AddDays(-1))
        {
            return Loc.Instance["History.GroupYesterday"];
        }

        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var thisMonday = today.AddDays(-daysSinceMonday);
        if (date >= thisMonday)
        {
            return Loc.Instance["History.GroupThisWeek"];
        }

        var lastMonday = thisMonday.AddDays(-7);
        return date >= lastMonday ? Loc.Instance["History.GroupLastWeek"] 
            : timestamp.ToString("MMMM yyyy");
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
    private bool _isInspectorVisible;

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
    public bool IsSpokenCommand => Record.IsSpokenCommand;
    public bool HasLanguage => !string.IsNullOrWhiteSpace(Record.Language);
    public bool HasSessionAudio => _owner.HasSessionAudio(Record);
    public bool IsPlaying => _owner.IsPlaying(Record);
    public string PlaybackButtonText =>
        IsPlaying ? Loc.Instance["History.Stop"] : Loc.Instance["History.Play"];
    public bool ShowReadOnlyText => IsExpanded && !IsEditing;
    public bool ShowEditPanel => IsExpanded && IsEditing;
    public bool ShowExpandedMeta => IsExpanded && !IsEditing;
    public bool ShowExpandedActions => IsExpanded && !IsEditing;

    public bool HasCorrectionSuggestions =>
        IsExpanded && !IsEditing && CorrectionSuggestions.Count > 0;

    public bool HasLlmCalls => Record.LlmCalls.Count > 0;

    // Distinguishes "capture is off" (guide the user to the setting) from "capture is
    // on but this entry genuinely made no LLM call" (e.g. a raw dictation).
    public string NoLlmCallsMessage =>
        _owner.CaptureLlmProvenanceEnabled
            ? Loc.Instance["History.Inspect.NoLlmCalls"]
            : Loc.Instance["History.Inspect.CaptureOff"];

    public bool ShowRawVsFinal =>
        !string.Equals(Record.RawText, Record.FinalText, StringComparison.Ordinal);

    public bool HasInspectorContent => HasLlmCalls || ShowRawVsFinal;
    public bool ShowInspectorToggle => IsExpanded && !IsEditing && HasInspectorContent;
    public bool ShowInspector =>
        IsExpanded && !IsEditing && IsInspectorVisible && HasInspectorContent;

    // Cached: a virtualized list recycles containers and re-evaluates these
    // bindings whenever a row scrolls back into view, so recomputing the LCS diff
    // and projection every access would be wasteful. Invalidated in
    // OnRecordChanged when the underlying record is reassigned (e.g. after edit).
    private IReadOnlyList<DiffSegment>? _rawVsFinalDiffCache;
    private IReadOnlyList<LlmCallDisplay>? _inspectorCallsCache;

    public IReadOnlyList<DiffSegment> RawVsFinalDiff =>
        _rawVsFinalDiffCache ??= WordDiff.Compute(Record.RawText, Record.FinalText);

    public IReadOnlyList<LlmCallDisplay> InspectorCalls =>
        _inspectorCallsCache ??= Record.LlmCalls.Select(call => new LlmCallDisplay(call)).ToList();

    partial void OnRecordChanged(TranscriptionRecord value)
    {
        _rawVsFinalDiffCache = null;
        _inspectorCallsCache = null;
        OnPropertyChanged(nameof(RawVsFinalDiff));
        OnPropertyChanged(nameof(InspectorCalls));
    }

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
            IsInspectorVisible = false;
            CorrectionSuggestions.Clear();
        }

        NotifyExpansionStateChanged();
    }

    partial void OnIsEditingChanged(bool value)
    {
        NotifyExpansionStateChanged();
    }

    partial void OnIsInspectorVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowInspector));
    }

    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
    }

    [RelayCommand]
    private void ToggleInspector()
    {
        IsInspectorVisible = !IsInspectorVisible;
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
        // Editing changes the final text, so raw≠final and the diff can change.
        OnPropertyChanged(nameof(ShowRawVsFinal));
        OnPropertyChanged(nameof(HasInspectorContent));
        OnPropertyChanged(nameof(ShowInspectorToggle));
        OnPropertyChanged(nameof(RawVsFinalDiff));
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
        if (!_owner.SetPendingCorrectionSuggestions(this, []))
        {
            return;
        }

        CorrectionSuggestions.Clear();
        OnPropertyChanged(nameof(HasCorrectionSuggestions));
    }

    [RelayCommand]
    private void DismissCorrectionSuggestions()
    {
        if (!_owner.SetPendingCorrectionSuggestions(this, []))
        {
            return;
        }

        CorrectionSuggestions.Clear();
        OnPropertyChanged(nameof(HasCorrectionSuggestions));
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
        OnPropertyChanged(nameof(ShowInspectorToggle));
        OnPropertyChanged(nameof(ShowInspector));
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

/// <summary>
///     Read-only display projection of one <see cref="LlmCallProvenance" /> entry
///     for the history Inspect panel: localized stage/badge labels plus the raw
///     prompt text and per-block visibility flags.
/// </summary>
public sealed class LlmCallDisplay
{
    private readonly LlmCallProvenance _call;

    public LlmCallDisplay(LlmCallProvenance call)
    {
        _call = call;
    }

    public string StageLabel =>
        _call.Stage switch
        {
            "Cleanup" => Loc.Instance["History.Inspect.StageCleanup"],
            "Translation" => Loc.Instance["History.Inspect.StageTranslation"],
            "Memory" => Loc.Instance["History.Inspect.StageMemory"],
            _ => Loc.Instance["History.Inspect.StagePromptAction"]
        };

    public string ProviderModelLabel => $"{_call.ProviderName} · {_call.ModelId}";

    public bool RanLocally => _call.RanLocally;

    public string NetworkBadgeText =>
        _call.RanLocally
            ? Loc.Instance["History.Inspect.StayedLocal"]
            : Loc.Instance.GetString("History.Inspect.SentToProvider", _call.ProviderName);

    public string SystemPromptSent => _call.SystemPromptSent;
    public string UserPromptSent => _call.UserPromptSent;
    public string? InjectedMemoryContext => _call.InjectedMemoryContext;
    public string? ResponseReceived => _call.ResponseReceived;

    public bool HasSystemPrompt => !string.IsNullOrWhiteSpace(_call.SystemPromptSent);
    public bool HasUserPrompt => !string.IsNullOrWhiteSpace(_call.UserPromptSent);
    public bool HasInjectedContext => !string.IsNullOrWhiteSpace(_call.InjectedMemoryContext);
    public bool HasResponse => !string.IsNullOrWhiteSpace(_call.ResponseReceived);
}