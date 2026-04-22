using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class ProfilesSectionViewModel : ObservableObject
{
    private readonly IProfileService _profiles;
    private readonly PluginManager _pluginManager;
    private readonly IPromptActionService _promptActions;

    public ObservableCollection<Profile> Profiles { get; } = [];
    public ObservableCollection<ProfileModelOption> ModelOptions { get; } = [];
    public ObservableCollection<PromptActionOption> PromptActionOptions { get; } = [];
    public ObservableCollection<TranslationTargetOption> TranslationTargetOptions { get; } = [];
    public ObservableCollection<string> ProcessNameChips { get; } = [];
    public ObservableCollection<string> UrlPatternChips { get; } = [];

    [ObservableProperty] private Profile? _selectedProfile;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string? _editLanguage;
    [ObservableProperty] private string? _editTask;
    [ObservableProperty] private string? _editTranslationTarget;
    [ObservableProperty] private string? _editModelId;
    [ObservableProperty] private string? _editPromptActionId;
    [ObservableProperty] private int _editPriority;
    [ObservableProperty] private bool _editIsEnabled = true;
    [ObservableProperty] private string _processNameInput = "";
    [ObservableProperty] private string _urlPatternInput = "";

    public IReadOnlyList<string> LanguageChoices { get; } =
        ["", "auto", "en", "de", "fr", "es", "pt", "ja", "zh", "ko", "it", "nl", "pl", "ru"];

    public IReadOnlyList<string> TaskChoices { get; } =
        ["", "transcribe", "translate"];

    public bool HasSelectedProfile => SelectedProfile is not null;
    public int ProfileCount => Profiles.Count;
    public int EnabledProfileCount => Profiles.Count(static profile => profile.IsEnabled);
    public string Summary => $"{ProfileCount} profile(s), {EnabledProfileCount} enabled";
    public TranslationTargetOption? SelectedTranslationTargetOption
    {
        get => TranslationTargetOptions.FirstOrDefault(option =>
            string.Equals(option.Code, EditTranslationTarget, StringComparison.Ordinal));
        set
        {
            var code = value?.Code;
            if (string.Equals(code, EditTranslationTarget, StringComparison.Ordinal))
                return;

            EditTranslationTarget = code;
            OnPropertyChanged();
        }
    }

    public ProfilesSectionViewModel(
        IProfileService profiles,
        PluginManager pluginManager,
        IPromptActionService promptActions)
    {
        _profiles = profiles;
        _pluginManager = pluginManager;
        _promptActions = promptActions;

        _profiles.ProfilesChanged += () => Dispatcher.UIThread.Post(RefreshProfiles);
        _pluginManager.PluginStateChanged += (_, _) => Dispatcher.UIThread.Post(RefreshModelOptions);
        _promptActions.ActionsChanged += () => Dispatcher.UIThread.Post(RefreshPromptActionOptions);

        RefreshModelOptions();
        RefreshPromptActionOptions();
        foreach (var option in TranslationModelInfo.ProfileTargetOptions)
            TranslationTargetOptions.Add(option);
        RefreshProfiles();
    }

    partial void OnSelectedProfileChanged(Profile? value)
    {
        ProcessNameChips.Clear();
        UrlPatternChips.Clear();
        ProcessNameInput = "";
        UrlPatternInput = "";

        if (value is null)
        {
            EditName = "";
            EditLanguage = null;
            EditTask = null;
            EditTranslationTarget = null;
            EditModelId = null;
            EditPromptActionId = null;
            EditPriority = 0;
            EditIsEnabled = true;
            NotifyStateChanged();
            return;
        }

        EditName = value.Name;
        EditLanguage = value.InputLanguage;
        EditTask = value.SelectedTask;
        EditTranslationTarget = value.TranslationTarget;
        EditModelId = value.TranscriptionModelOverride;
        EditPromptActionId = value.PromptActionId;
        EditPriority = value.Priority;
        EditIsEnabled = value.IsEnabled;
        OnPropertyChanged(nameof(SelectedTranslationTargetOption));

        foreach (var processName in value.ProcessNames)
            ProcessNameChips.Add(processName);
        foreach (var urlPattern in value.UrlPatterns)
            UrlPatternChips.Add(urlPattern);

        NotifyStateChanged();
    }

    partial void OnEditTranslationTargetChanged(string? value) =>
        OnPropertyChanged(nameof(SelectedTranslationTargetOption));

    [RelayCommand]
    private void AddProfile()
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = "New profile",
            IsEnabled = true,
            Priority = 0,
            ProcessNames = [],
            UrlPatterns = []
        };

        _profiles.AddProfile(profile);
        RefreshProfiles();
        SelectById(profile.Id);
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (SelectedProfile is null || string.IsNullOrWhiteSpace(EditName))
            return;

        var updated = SelectedProfile with
        {
            Name = EditName.Trim(),
            ProcessNames = [.. ProcessNameChips],
            UrlPatterns = [.. UrlPatternChips],
            InputLanguage = string.IsNullOrWhiteSpace(EditLanguage) ? null : EditLanguage,
            SelectedTask = string.IsNullOrWhiteSpace(EditTask) ? null : EditTask,
            TranslationTarget = EditTranslationTarget,
            TranscriptionModelOverride = string.IsNullOrWhiteSpace(EditModelId) ? null : EditModelId,
            PromptActionId = string.IsNullOrWhiteSpace(EditPromptActionId) ? null : EditPromptActionId,
            Priority = EditPriority,
            IsEnabled = EditIsEnabled
        };

        var selectedId = SelectedProfile.Id;
        _profiles.UpdateProfile(updated);
        RefreshProfiles();
        SelectById(selectedId);
    }

    [RelayCommand]
    private void DuplicateProfile()
    {
        if (SelectedProfile is null)
            return;

        var duplicate = SelectedProfile with
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"{SelectedProfile.Name} Copy",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _profiles.AddProfile(duplicate);
        RefreshProfiles();
        SelectById(duplicate.Id);
    }

    [RelayCommand]
    private void DeleteSelectedProfile()
    {
        if (SelectedProfile is null)
            return;

        _profiles.DeleteProfile(SelectedProfile.Id);
        RefreshProfiles();
        SelectedProfile = null;
    }

    [RelayCommand]
    private void ToggleProfileEnabled(Profile? profile)
    {
        if (profile is null)
            return;

        _profiles.UpdateProfile(profile with { IsEnabled = !profile.IsEnabled });
        RefreshProfiles();
    }

    [RelayCommand]
    private void AddProcessNameChip()
    {
        var value = ProcessNameInput.Trim();
        if (string.IsNullOrEmpty(value))
            return;

        if (!ProcessNameChips.Contains(value, StringComparer.OrdinalIgnoreCase))
            ProcessNameChips.Add(value);

        ProcessNameInput = "";
    }

    [RelayCommand]
    private void RemoveProcessNameChip(string chip) => ProcessNameChips.Remove(chip);

    [RelayCommand]
    private void AddUrlPatternChip()
    {
        var value = UrlPatternInput.Trim();
        if (string.IsNullOrEmpty(value))
            return;

        if (!UrlPatternChips.Contains(value, StringComparer.OrdinalIgnoreCase))
            UrlPatternChips.Add(value);

        UrlPatternInput = "";
    }

    [RelayCommand]
    private void RemoveUrlPatternChip(string chip) => UrlPatternChips.Remove(chip);

    private void RefreshProfiles()
    {
        var selectedId = SelectedProfile?.Id;

        Profiles.Clear();
        foreach (var profile in _profiles.Profiles)
            Profiles.Add(profile);

        if (selectedId is not null)
        {
            SelectById(selectedId);
            return;
        }

        if (Profiles.Count > 0 && SelectedProfile is null)
            SelectedProfile = Profiles[0];
        else
            NotifyStateChanged();
    }

    private void RefreshModelOptions()
    {
        var selected = EditModelId;
        ModelOptions.Clear();
        ModelOptions.Add(new ProfileModelOption(null, "Use global default"));

        foreach (var engine in _pluginManager.TranscriptionEngines)
        {
            foreach (var model in engine.TranscriptionModels)
            {
                ModelOptions.Add(new ProfileModelOption(
                    ModelManagerService.GetPluginModelId(engine.PluginId, model.Id),
                    $"{engine.ProviderDisplayName} — {model.DisplayName}"));
            }
        }

        EditModelId = ModelOptions.Any(option => option.Value == selected) ? selected : null;
    }

    private void RefreshPromptActionOptions()
    {
        var selected = EditPromptActionId;
        PromptActionOptions.Clear();
        PromptActionOptions.Add(new PromptActionOption(null, "No prompt action"));

        foreach (var action in _promptActions.Actions.OrderBy(action => action.SortOrder).ThenBy(action => action.Name))
            PromptActionOptions.Add(new PromptActionOption(action.Id, action.Name));

        EditPromptActionId = PromptActionOptions.Any(option => option.Value == selected) ? selected : null;
    }

    private void SelectById(string id)
    {
        var match = Profiles.FirstOrDefault(profile => profile.Id == id);
        if (match is not null)
            SelectedProfile = match;
        else
            NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(HasSelectedProfile));
        OnPropertyChanged(nameof(ProfileCount));
        OnPropertyChanged(nameof(EnabledProfileCount));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SelectedTranslationTargetOption));
    }
}

public sealed record ProfileModelOption(string? Value, string Label);

public sealed record PromptActionOption(string? Value, string Label);
