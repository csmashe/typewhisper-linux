using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class SnippetsSectionViewModel : ObservableObject
{
    private readonly ISnippetService _snippets;
    private readonly IDictionaryService _dictionary;

    public ObservableCollection<Snippet> FilteredSnippets { get; } = [];
    public ObservableCollection<string> AvailableTags { get; } = ["All tags"];

    [ObservableProperty] private string _newTrigger = "";
    [ObservableProperty] private string _newReplacement = "";
    [ObservableProperty] private string _newTags = "";
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private SnippetTriggerMode _selectedTriggerMode = SnippetTriggerMode.Anywhere;
    [ObservableProperty] private string _selectedTagFilter = "All tags";
    [ObservableProperty] private bool _showEditor;
    [ObservableProperty] private string? _editingSnippetId;

    public int SnippetCount => _snippets.Snippets.Count;
    public int EnabledSnippetCount => _snippets.Snippets.Count(snippet => snippet.IsEnabled);
    public string SummaryText => $"{SnippetCount} snippets, {EnabledSnippetCount} enabled";
    public bool ShowEmptyState => FilteredSnippets.Count == 0;
    public bool ShowSnippetList => FilteredSnippets.Count > 0;
    public bool HasSelectedTagFilter => !string.Equals(SelectedTagFilter, "All tags", StringComparison.Ordinal);
    public bool IsEditingExisting => !string.IsNullOrWhiteSpace(EditingSnippetId);
    public string EditorTitle => IsEditingExisting ? "Edit snippet" : "New snippet";
    public string EditorSaveText => IsEditingExisting ? "Save changes" : "Create snippet";
    public string PreviewText => _snippets.PreviewReplacement(NewReplacement);
    public bool ShowPreview => !string.IsNullOrWhiteSpace(NewReplacement);
    public bool HasConflictWarning => !string.IsNullOrWhiteSpace(ConflictWarningText);
    public string ConflictWarningText => BuildConflictWarning(NewTrigger);
    public IReadOnlyList<SnippetTriggerModeOption> TriggerModeOptions { get; } =
    [
        new(SnippetTriggerMode.Anywhere, "Anywhere"),
        new(SnippetTriggerMode.ExactPhrase, "Exact phrase")
    ];

    public SnippetsSectionViewModel(ISnippetService snippets, IDictionaryService dictionary)
    {
        _snippets = snippets;
        _dictionary = dictionary;
        _snippets.SnippetsChanged += () => Dispatcher.UIThread.Post(Refresh);
        _dictionary.EntriesChanged += () => Dispatcher.UIThread.Post(NotifyConflictWarningChanged);
        Refresh();
    }

    partial void OnSelectedTagFilterChanged(string value) => Refresh();
    partial void OnNewTriggerChanged(string value) => NotifyConflictWarningChanged();
    partial void OnNewReplacementChanged(string value)
    {
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(ShowPreview));
    }

    partial void OnShowEditorChanged(bool value)
    {
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(EditorSaveText));
    }

    partial void OnEditingSnippetIdChanged(string? value)
    {
        OnPropertyChanged(nameof(IsEditingExisting));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(EditorSaveText));
    }

    [RelayCommand]
    private void ClearTagFilter() => SelectedTagFilter = "All tags";

    [RelayCommand]
    private void SaveSnippet()
    {
        if (string.IsNullOrWhiteSpace(NewTrigger) || string.IsNullOrWhiteSpace(NewReplacement))
            return;

        var existing = !string.IsNullOrWhiteSpace(EditingSnippetId)
            ? _snippets.Snippets.FirstOrDefault(snippet => snippet.Id == EditingSnippetId)
            : null;

        var snippet = new Snippet
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString(),
            Trigger = NewTrigger.Trim(),
            Replacement = NewReplacement.Trim(),
            Tags = NewTags.Trim(),
            CaseSensitive = CaseSensitive,
            TriggerMode = SelectedTriggerMode,
            IsEnabled = existing?.IsEnabled ?? true,
            UsageCount = existing?.UsageCount ?? 0,
            LastUsedAt = existing?.LastUsedAt,
            CreatedAt = existing?.CreatedAt ?? DateTime.UtcNow
        };

        if (existing is null)
            _snippets.AddSnippet(snippet);
        else
            _snippets.UpdateSnippet(snippet);

        CancelEdit();
    }

    [RelayCommand]
    private void Delete(Snippet snippet) => _snippets.DeleteSnippet(snippet.Id);

    [RelayCommand]
    private void ToggleEnabled(Snippet snippet) => _snippets.UpdateSnippet(snippet with { IsEnabled = !snippet.IsEnabled });

    [RelayCommand]
    private void BeginCreate()
    {
        EditingSnippetId = null;
        NewTrigger = "";
        NewReplacement = "";
        NewTags = "";
        CaseSensitive = false;
        SelectedTriggerMode = SnippetTriggerMode.Anywhere;
        ShowEditor = true;
    }

    [RelayCommand]
    private void BeginEdit(Snippet snippet)
    {
        EditingSnippetId = snippet.Id;
        NewTrigger = snippet.Trigger;
        NewReplacement = snippet.Replacement;
        NewTags = snippet.Tags;
        CaseSensitive = snippet.CaseSensitive;
        SelectedTriggerMode = snippet.TriggerMode;
        ShowEditor = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        EditingSnippetId = null;
        NewTrigger = "";
        NewReplacement = "";
        NewTags = "";
        CaseSensitive = false;
        SelectedTriggerMode = SnippetTriggerMode.Anywhere;
        ShowEditor = false;
    }

    public string ExportToJson() => _snippets.ExportToJson();

    public int ImportFromJson(string json) => _snippets.ImportFromJson(json);

    private void Refresh()
    {
        RebuildTagFilter();

        FilteredSnippets.Clear();
        IEnumerable<Snippet> snippets = _snippets.Snippets.OrderBy(snippet => snippet.Trigger, StringComparer.OrdinalIgnoreCase);

        if (HasSelectedTagFilter)
        {
            snippets = snippets.Where(snippet => snippet.Tags
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(tag => string.Equals(tag, SelectedTagFilter, StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var snippet in snippets)
            FilteredSnippets.Add(snippet);

        OnPropertyChanged(nameof(SnippetCount));
        OnPropertyChanged(nameof(EnabledSnippetCount));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowSnippetList));
        OnPropertyChanged(nameof(HasSelectedTagFilter));
    }

    private void NotifyConflictWarningChanged()
    {
        OnPropertyChanged(nameof(ConflictWarningText));
        OnPropertyChanged(nameof(HasConflictWarning));
    }

    private string BuildConflictWarning(string trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger))
            return "";

        var normalized = trigger.Trim();
        var conflict = _dictionary.Entries.FirstOrDefault(entry =>
            entry.IsEnabled
            && string.Equals(entry.Original.Trim(), normalized, StringComparison.OrdinalIgnoreCase));

        return conflict switch
        {
            { EntryType: DictionaryEntryType.Term } =>
                $"This trigger matches an enabled dictionary term: {conflict.Original}.",
            { EntryType: DictionaryEntryType.Correction, Replacement: { Length: > 0 } replacement } =>
                $"This trigger matches a dictionary correction: {conflict.Original} -> {replacement}.",
            { EntryType: DictionaryEntryType.Correction } =>
                $"This trigger matches a dictionary correction: {conflict.Original}.",
            _ => ""
        };
    }

    private void RebuildTagFilter()
    {
        var current = SelectedTagFilter;
        AvailableTags.Clear();
        AvailableTags.Add("All tags");
        foreach (var tag in _snippets.AllTags)
            AvailableTags.Add(tag);

        SelectedTagFilter = AvailableTags.Contains(current) ? current : "All tags";
    }
}

public sealed record SnippetTriggerModeOption(SnippetTriggerMode Value, string Label);
