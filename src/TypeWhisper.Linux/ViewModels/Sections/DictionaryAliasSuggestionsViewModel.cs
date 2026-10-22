using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.ViewModels.Sections;

/// <summary>Spoken language sent to the model by its English name.</summary>
public sealed record AliasLanguageOption(string Code, string PromptName, string DisplayKey)
{
    public override string ToString() => Loc.Instance[DisplayKey];
}

/// <summary>One generated variant awaiting review; unavailable when a mapping already exists.</summary>
public partial class AliasSuggestionItem(string alias, string? note) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public string Alias { get; } = alias;
    public bool IsAvailable => note is null;
    public string Label => note is null ? Alias : $"{Alias} · {note}";
}

/// <summary>
///     Reviewed alias suggestions from a local LLM. Nothing is written until Save; editing the
///     term, language or model, closing, or a newer request discards an in-flight result.
/// </summary>
// MVVM Toolkit [ObservableProperty] generates the On<Property>Changed(value) partial hooks; the
// value parameter is part of the generated signature and cannot be dropped even when ignored here.
// ReSharper disable UnusedParameterInPartialMethod
public partial class DictionaryAliasSuggestionsViewModel : ObservableObject
{
    private readonly IDictionaryService _dict;
    private readonly DictionaryAliasSuggestionService _service;
    private readonly ISettingsService _settings;
    private CancellationTokenSource? _request;
    private int _requestVersion;
    private bool _refreshingModels;
    private string _reviewedTerm = "";

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _term = "";

    [ObservableProperty]
    private AliasLanguageOption? _selectedLanguage;

    [ObservableProperty]
    private LocalAliasModel? _selectedModel;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isReviewed;

    [ObservableProperty]
    private string _status = "";

    public DictionaryAliasSuggestionsViewModel(
        IDictionaryService dict,
        DictionaryAliasSuggestionService service,
        ISettingsService settings
    )
    {
        _dict = dict;
        _service = service;
        _settings = settings;
        Languages =
        [
            new AliasLanguageOption("en", "English", "Dictionary.AliasLanguageEnglish"),
            new AliasLanguageOption("de", "German", "Dictionary.AliasLanguageGerman"),
            new AliasLanguageOption("es", "Spanish", "Dictionary.AliasLanguageSpanish"),
            new AliasLanguageOption("ru", "Russian", "Dictionary.AliasLanguageRussian"),
        ];
    }

    /// <summary>Raised after aliases are saved, with the term they now correct to.</summary>
    public event Action<string>? Saved;

    public IReadOnlyList<AliasLanguageOption> Languages { get; }
    public ObservableCollection<LocalAliasModel> Models { get; } = [];
    public ObservableCollection<AliasSuggestionItem> Suggestions { get; } = [];

    public bool HasNoModels => Models.Count == 0;
    public bool IsIdle => !IsBusy;
    public bool HasStatus => !string.IsNullOrEmpty(Status);
    public bool CanRegenerate => IsReviewed && !IsBusy;

    public string PrimaryButtonText =>
        IsBusy ? Loc.Instance["Dictionary.AliasesGenerating"]
        : IsReviewed ? Loc.Instance["Dictionary.AliasesSave"]
        : Loc.Instance["Dictionary.AliasesGenerate"];

    public bool CanRunPrimary =>
        !IsBusy
        && (IsReviewed
            ? Suggestions.Any(item => item is { IsAvailable: true, IsSelected: true })
            : SelectedModel is not null
              && SelectedLanguage is not null
              && DictionaryAliasSuggestions.IsTerm(DictionaryAliasSuggestions.Normalize(Term)));

    [RelayCommand]
    private void Open(string? term)
    {
        Discard();
        Term = term ?? "";
        SelectedLanguage = DefaultLanguage();
        RefreshModels();
        Status = HasNoModels ? Loc.Instance["Dictionary.AliasesNoModel"] : "";
        IsOpen = true;
    }

    [RelayCommand]
    private void Close()
    {
        Discard();
        IsOpen = false;
    }

    /// <summary>Re-reads the ready local models, keeping the selection while it is still offered.</summary>
    public void RefreshModels()
    {
        var current = SelectedModel;
        // Clearing the list makes the bound ComboBox write a null selection back; that transient
        // change must not cancel a running request or drop the review.
        _refreshingModels = true;
        try
        {
            Models.Clear();
            foreach (var model in _service.GetLocalModels())
            {
                Models.Add(model);
            }

            SelectedModel = Models.FirstOrDefault(model => model == current) ?? Models.FirstOrDefault();
        }
        finally
        {
            _refreshingModels = false;
        }

        if (SelectedModel != current)
        {
            ResetReview();
        }

        OnPropertyChanged(nameof(HasNoModels));
        OnPropertyChanged(nameof(CanRunPrimary));
    }

    [RelayCommand]
    private Task RunPrimaryAsync()
    {
        if (!CanRunPrimary)
        {
            return Task.CompletedTask;
        }

        if (!IsReviewed)
        {
            return GenerateAsync();
        }

        Save();
        return Task.CompletedTask;
    }

    /// <summary>Cancels the running request and keeps the panel open.</summary>
    [RelayCommand]
    private void Stop() => _request?.Cancel();

    [RelayCommand]
    private Task RegenerateAsync() => IsBusy ? Task.CompletedTask : GenerateAsync();

    internal async Task GenerateAsync()
    {
        if (SelectedModel is not { } model || SelectedLanguage is not { } language)
        {
            return;
        }

        var term = DictionaryAliasSuggestions.Normalize(Term);
        if (!DictionaryAliasSuggestions.IsTerm(term))
        {
            Status = Loc.Instance["Dictionary.AliasesInvalidTerm"];
            return;
        }

        Discard();
        var version = _requestVersion;
        using var request = _request = new CancellationTokenSource();
        IsBusy = true;
        Status = Loc.Instance["Dictionary.AliasesGenerating"];
        try
        {
            var aliases = await _service.SuggestAsync(term, language.PromptName, model, request.Token);
            if (version != _requestVersion)
            {
                return;
            }

            ShowReview(term, aliases);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            if (version == _requestVersion)
            {
                Status = Loc.Instance["Dictionary.AliasesCanceled"];
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[DictionaryAliasSuggestions] Generation failed: {ex.Message}");
            if (version == _requestVersion)
            {
                Status = ex switch
                {
                    FormatException => Loc.Instance["Dictionary.AliasesUnusable"],
                    InvalidOperationException => Loc.Instance["Dictionary.AliasesModelGone"],
                    _ => Loc.Instance["Dictionary.AliasesFailed"],
                };
            }
        }
        finally
        {
            if (ReferenceEquals(_request, request))
            {
                _request = null;
                IsBusy = false;
            }
        }
    }

    private void ShowReview(string term, IReadOnlyList<string> aliases)
    {
        var entries = _dict.Entries;
        _reviewedTerm = term;
        Suggestions.Clear();
        foreach (var alias in aliases)
        {
            var item = new AliasSuggestionItem(alias, DescribeExisting(entries, alias, term));
            item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanRunPrimary));
            Suggestions.Add(item);
        }

        IsReviewed = true;
        Status = aliases.Count == 0 ? Loc.Instance["Dictionary.AliasesNone"]
            : Suggestions.All(item => !item.IsAvailable) ? Loc.Instance["Dictionary.AliasesAllExisting"]
            : Loc.Instance.GetString("Dictionary.AliasesReview", term);
        OnPropertyChanged(nameof(CanRunPrimary));
    }

    private static string? DescribeExisting(IReadOnlyList<DictionaryEntry> entries, string alias, string term)
    {
        if (entries.Any(entry => entry.EntryType == DictionaryEntryType.Term
                                 && DictionaryAliasSuggestions.Normalize(entry.Original)
                                     .Equals(alias, StringComparison.OrdinalIgnoreCase)))
        {
            return Loc.Instance["Dictionary.AliasesExistingTerm"];
        }

        var existing = DictionaryAliasSuggestions.FindExistingCorrection(entries, alias);
        return existing is null ? null
            : string.Equals(existing.Replacement, term, StringComparison.Ordinal)
                ? Loc.Instance["Dictionary.AliasesExistingSame"]
                : Loc.Instance.GetString("Dictionary.AliasesExistingOther", existing.Replacement ?? "");
    }

    private void Save()
    {
        var selected = Suggestions
            .Where(item => item is { IsAvailable: true, IsSelected: true })
            .Select(item => item.Alias)
            .ToList();
        var result = _dict.SaveSuggestedAliases(_reviewedTerm, selected);
        if (result.Status != DictionaryAliasSaveStatus.Saved)
        {
            Status = result.Status switch
            {
                DictionaryAliasSaveStatus.InvalidTerm => Loc.Instance["Dictionary.AliasesInvalidTerm"],
                DictionaryAliasSaveStatus.NothingSelected => Loc.Instance["Dictionary.AliasesNothingSelected"],
                DictionaryAliasSaveStatus.Conflict => Loc.Instance.GetString("Dictionary.AliasesConflict", result.Alias ?? ""),
                DictionaryAliasSaveStatus.StoreUnavailable => Loc.Instance["Dictionary.AliasesStoreUnavailable"],
                _ => Loc.Instance["Dictionary.AliasesInvalidSelection"],
            };
            return;
        }

        var term = _reviewedTerm;
        Close();
        Saved?.Invoke(term);
    }

    // Cancels any request and clears the review; the version bump makes a late result a no-op.
    private void Discard()
    {
        _requestVersion++;
        var request = _request;
        _request = null;
        request?.Cancel();
        IsBusy = false;
        IsReviewed = false;
        _reviewedTerm = "";
        Suggestions.Clear();
        OnPropertyChanged(nameof(CanRunPrimary));
    }

    private AliasLanguageOption DefaultLanguage()
    {
        foreach (var code in new[] { _settings.Current.Language, Loc.Instance.CurrentLanguage })
        {
            var match = Languages.FirstOrDefault(option =>
                code.StartsWith(option.Code, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return Languages[0];
    }

    private void ResetReview()
    {
        if (!IsOpen)
        {
            return;
        }

        Discard();
        if (!HasNoModels)
        {
            Status = "";
        }
    }

    partial void OnTermChanged(string value) => ResetReview();

    partial void OnSelectedLanguageChanged(AliasLanguageOption? value) => ResetReview();

    partial void OnSelectedModelChanged(LocalAliasModel? value)
    {
        if (!_refreshingModels)
        {
            ResetReview();
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(CanRegenerate));
        OnPropertyChanged(nameof(PrimaryButtonText));
        OnPropertyChanged(nameof(CanRunPrimary));
    }

    partial void OnIsReviewedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRegenerate));
        OnPropertyChanged(nameof(PrimaryButtonText));
        OnPropertyChanged(nameof(CanRunPrimary));
    }

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));
}
