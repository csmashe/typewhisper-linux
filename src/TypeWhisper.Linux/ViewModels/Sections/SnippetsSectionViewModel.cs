using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.ViewModels.Sections;

// MVVM Toolkit [ObservableProperty] generates the On<Property>Changed(value) partial hooks; the
// value parameter is part of the generated signature and cannot be dropped even when ignored here.
// ReSharper disable UnusedParameterInPartialMethod
public partial class SnippetsSectionViewModel : ObservableObject, IDisposable
{
    private readonly IDictionaryService _dictionary;
    private readonly Action _entriesChangedHandler;
    private readonly ISnippetService _snippets;
    private readonly Action _snippetsChangedHandler;

    [ObservableProperty]
    private bool _caseSensitive;

    [ObservableProperty]
    private string? _editingSnippetId;

    [ObservableProperty]
    private string _newProfileIds = "";

    [ObservableProperty]
    private string _newReplacement = "";

    [ObservableProperty]
    private string _newTags = "";

    [ObservableProperty]
    private string _newTrigger = "";

    [ObservableProperty]
    private string _selectedTagFilter = Loc.Instance["Snippets.AllTags"];

    [ObservableProperty]
    private SnippetTriggerMode _selectedTriggerMode = SnippetTriggerMode.Anywhere;

    [ObservableProperty]
    private bool _showEditor;

    public SnippetsSectionViewModel(ISnippetService snippets, IDictionaryService dictionary)
    {
        _snippets = snippets;
        _dictionary = dictionary;
        _snippetsChangedHandler = () => Dispatcher.UIThread.Post(Refresh);
        _entriesChangedHandler = () => Dispatcher.UIThread.Post(NotifyConflictWarningChanged);
        _snippets.SnippetsChanged += _snippetsChangedHandler;
        _dictionary.EntriesChanged += _entriesChangedHandler;
        Refresh();
    }

    public ObservableCollection<Snippet> FilteredSnippets { get; } = [];
    public ObservableCollection<string> AvailableTags { get; } = [Loc.Instance["Snippets.AllTags"]];

    public int SnippetCount => _snippets.Snippets.Count;
    public int EnabledSnippetCount => _snippets.Snippets.Count(snippet => snippet.IsEnabled);
    public string SummaryText =>
        Loc.Instance.GetString("Snippets.SummaryText", SnippetCount, EnabledSnippetCount);
    public bool ShowEmptyState => FilteredSnippets.Count == 0;
    public bool ShowSnippetList => FilteredSnippets.Count > 0;

    public bool HasSelectedTagFilter =>
        !string.Equals(SelectedTagFilter, Loc.Instance["Snippets.AllTags"], StringComparison.Ordinal);

    public bool IsEditingExisting => !string.IsNullOrWhiteSpace(EditingSnippetId);
    public string EditorTitle =>
        IsEditingExisting ? Loc.Instance["Snippets.EditSnippet"] : Loc.Instance["Snippets.NewSnippet"];
    public string EditorSaveText =>
        IsEditingExisting
            ? Loc.Instance["Snippets.SaveChanges"]
            : Loc.Instance["Snippets.CreateSnippet"];
    public string PreviewText => _snippets.PreviewReplacement(NewReplacement);
    public bool ShowPreview => !string.IsNullOrWhiteSpace(NewReplacement);
    public bool HasConflictWarning => !string.IsNullOrWhiteSpace(ConflictWarningText);
    public string ConflictWarningText => BuildConflictWarning(NewTrigger);

    public IReadOnlyList<SnippetTriggerModeOption> TriggerModeOptions { get; } =
    [
        new(SnippetTriggerMode.Anywhere, Loc.Instance["Snippets.TriggerModeAnywhere"]),
        new(SnippetTriggerMode.ExactPhrase, Loc.Instance["Snippets.TriggerModeExactPhrase"])
    ];

    public void Dispose()
    {
        _snippets.SnippetsChanged -= _snippetsChangedHandler;
        _dictionary.EntriesChanged -= _entriesChangedHandler;
        GC.SuppressFinalize(this);
    }

    public string ExportToJson()
    {
        return _snippets.ExportToJson();
    }

    public int ImportFromJson(string json)
    {
        try
        {
            return _snippets.ImportFromJson(json);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SnippetsSectionViewModel] Failed to import snippets: {ex}");
            Refresh();
            throw;
        }
    }

    partial void OnSelectedTagFilterChanged(string value)
    {
        Refresh();
    }

    partial void OnNewTriggerChanged(string value)
    {
        NotifyConflictWarningChanged();
    }

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
    private void ClearTagFilter()
    {
        SelectedTagFilter = Loc.Instance["Snippets.AllTags"];
    }

    [RelayCommand]
    private void SaveSnippet()
    {
        if (string.IsNullOrWhiteSpace(NewTrigger) || string.IsNullOrWhiteSpace(NewReplacement))
        {
            return;
        }

        var existing = !string.IsNullOrWhiteSpace(EditingSnippetId)
            ? _snippets.Snippets.FirstOrDefault(snippet => snippet.Id == EditingSnippetId)
            : null;

        var snippet = new Snippet
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString(),
            Trigger = NewTrigger.Trim(),
            Replacement = NewReplacement.Trim(),
            Tags = NewTags.Trim(),
            ProfileIds = ParseProfileIds(NewProfileIds),
            CaseSensitive = CaseSensitive,
            TriggerMode = SelectedTriggerMode,
            IsEnabled = existing?.IsEnabled ?? true,
            UsageCount = existing?.UsageCount ?? 0,
            LastUsedAt = existing?.LastUsedAt,
            CreatedAt = existing?.CreatedAt ?? DateTime.UtcNow
        };

        if (existing is null)
        {
            if (!TryMutate(() => _snippets.AddSnippet(snippet), "add a snippet"))
            {
                return;
            }
        }
        else
        {
            if (!TryMutate(() => _snippets.UpdateSnippet(snippet), "update a snippet"))
            {
                return;
            }
        }

        CancelEdit();
    }

    [RelayCommand]
    private void Delete(Snippet snippet)
    {
        TryMutate(() => _snippets.DeleteSnippet(snippet.Id), "delete a snippet");
    }

    [RelayCommand]
    private void ToggleEnabled(Snippet snippet)
    {
        TryMutate(
            () => _snippets.UpdateSnippet(snippet with { IsEnabled = !snippet.IsEnabled }),
            "toggle a snippet"
        );
    }

    [RelayCommand]
    private void BeginCreate()
    {
        EditingSnippetId = null;
        NewTrigger = "";
        NewReplacement = "";
        NewTags = "";
        NewProfileIds = "";
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
        NewProfileIds = string.Join(", ", snippet.ProfileIds ?? []);
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
        NewProfileIds = "";
        CaseSensitive = false;
        SelectedTriggerMode = SnippetTriggerMode.Anywhere;
        ShowEditor = false;
    }

    private void Refresh()
    {
        RebuildTagFilter();

        FilteredSnippets.Clear();
        IEnumerable<Snippet> snippets = _snippets.Snippets.OrderBy(
            snippet => snippet.Trigger,
            StringComparer.OrdinalIgnoreCase
        );

        if (HasSelectedTagFilter)
        {
            snippets = snippets.Where(snippet =>
                snippet
                    .Tags.Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    )
                    .Any(tag =>
                        string.Equals(tag, SelectedTagFilter, StringComparison.OrdinalIgnoreCase)
                    )
            );
        }

        foreach (var snippet in snippets)
        {
            FilteredSnippets.Add(snippet);
        }

        OnPropertyChanged(nameof(SnippetCount));
        OnPropertyChanged(nameof(EnabledSnippetCount));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowSnippetList));
        OnPropertyChanged(nameof(HasSelectedTagFilter));
    }

    private bool TryMutate(Action mutation, string operation)
    {
        try
        {
            mutation();
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SnippetsSectionViewModel] Failed to {operation}: {ex}");
            Refresh();
            return false;
        }
    }

    private void NotifyConflictWarningChanged()
    {
        OnPropertyChanged(nameof(ConflictWarningText));
        OnPropertyChanged(nameof(HasConflictWarning));
    }

    private string BuildConflictWarning(string trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger))
        {
            return "";
        }

        var normalized = trigger.Trim();
        var conflict = _dictionary.Entries.FirstOrDefault(entry =>
            entry.IsEnabled
            && string.Equals(entry.Original.Trim(), normalized, StringComparison.OrdinalIgnoreCase)
        );

        return conflict switch
        {
            { EntryType: DictionaryEntryType.Term } => Loc.Instance.GetString(
                "Snippets.ConflictTerm",
                conflict.Original
            ),
            {
                    EntryType: DictionaryEntryType.Correction,
                    Replacement: { Length: > 0 } replacement
                } => Loc.Instance.GetString(
                "Snippets.ConflictCorrectionReplacement",
                conflict.Original,
                replacement
            ),
            { EntryType: DictionaryEntryType.Correction } => Loc.Instance.GetString(
                "Snippets.ConflictCorrection",
                conflict.Original
            ),
            _ => ""
        };
    }

    private void RebuildTagFilter()
    {
        var current = SelectedTagFilter;
        AvailableTags.Clear();
        AvailableTags.Add(Loc.Instance["Snippets.AllTags"]);
        foreach (var tag in _snippets.AllTags)
        {
            AvailableTags.Add(tag);
        }

        SelectedTagFilter =
            AvailableTags.Contains(current) ? current : Loc.Instance["Snippets.AllTags"];
    }

    private static List<string> ParseProfileIds(string value)
    {
        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed record SnippetTriggerModeOption(SnippetTriggerMode Value, string Label);
