using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.Views;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class ProfilesSectionViewModel : ObservableObject
{
    private readonly IProfileService _profiles;
    private readonly IActiveWindowService _activeWindow;
    private readonly PluginManager _pluginManager;
    private readonly IPromptActionService _promptActions;
    private readonly DispatcherTimer _windowTimer;
    private readonly string _hostProcessName = Process.GetCurrentProcess().ProcessName;
    private ProfilesContextWindow? _contextWindow;
    private string _lastExternalProcessName = "-";
    private string _lastExternalWindowTitle = "-";
    private string _lastExternalUrl = "-";

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
    [ObservableProperty] private bool? _editWhisperModeOverride;
    [ObservableProperty] private string? _editModelId;
    [ObservableProperty] private string? _editPromptActionId;
    [ObservableProperty] private int _editPriority;
    [ObservableProperty] private bool _editIsEnabled = true;
    [ObservableProperty] private string _processNameInput = "";
    [ObservableProperty] private string _urlPatternInput = "";
    [ObservableProperty] private string _currentProcessName = "-";
    [ObservableProperty] private string _currentWindowTitle = "-";
    [ObservableProperty] private string _currentUrl = "-";
    [ObservableProperty] private string _matchedProfileName = "No profile";
    [ObservableProperty] private bool _hasMatchedProfile;

    public IReadOnlyList<string> LanguageChoices { get; } =
        ["", "auto", "en", "de", "fr", "es", "pt", "ja", "zh", "ko", "it", "nl", "pl", "ru"];

    public IReadOnlyList<string> TaskChoices { get; } =
        ["", "transcribe", "translate"];

    public bool HasSelectedProfile => SelectedProfile is not null;
    public int ProfileCount => Profiles.Count;
    public int EnabledProfileCount => Profiles.Count(static profile => profile.IsEnabled);
    public string Summary => $"{ProfileCount} profile(s), {EnabledProfileCount} enabled";
    public string SelectedProfileSummary => SelectedProfile is null
        ? "Select a profile from the list or create a new one."
        : $"{ProcessNameChips.Count} app rule(s), {UrlPatternChips.Count} URL rule(s)";
    public string SelectedProfileDisplayName => SelectedProfile?.Name ?? "No profile";
    public string MatchStatusText => HasMatchedProfile
        ? $"Matches {MatchedProfileName}"
        : "No active match";
    public bool ShowLiveContextProfileHint => !HasSelectedProfile;
    public bool HasCurrentProcess => !string.IsNullOrWhiteSpace(CurrentProcessName) && CurrentProcessName != "-";
    public bool HasCurrentUrl => !string.IsNullOrWhiteSpace(CurrentUrl) && CurrentUrl != "-";
    public bool ShowNoBrowserUrlHint => !HasCurrentUrl;
    public bool HasCurrentWindowTitle => !string.IsNullOrWhiteSpace(CurrentWindowTitle) && CurrentWindowTitle != "-";
    public string CurrentUrlPattern => TryExtractUrlPattern(CurrentUrl);
    public string EditIsEnabledStatusText => EditIsEnabled ? "On" : "Off";
    public IReadOnlyList<NullableBooleanOption> WhisperModeOptions { get; } =
    [
        new(null, "Use global default"),
        new(true, "Enabled"),
        new(false, "Disabled")
    ];
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

    public ProfileModelOption? SelectedModelOption
    {
        get => ModelOptions.FirstOrDefault(option =>
            string.Equals(option.Value, EditModelId, StringComparison.Ordinal));
        set
        {
            var selected = value?.Value;
            if (string.Equals(selected, EditModelId, StringComparison.Ordinal))
                return;

            EditModelId = selected;
            OnPropertyChanged();
        }
    }

    public PromptActionOption? SelectedPromptActionOption
    {
        get => PromptActionOptions.FirstOrDefault(option =>
            string.Equals(option.Value, EditPromptActionId, StringComparison.Ordinal));
        set
        {
            var selected = value?.Value;
            if (string.Equals(selected, EditPromptActionId, StringComparison.Ordinal))
                return;

            EditPromptActionId = selected;
            OnPropertyChanged();
        }
    }

    public NullableBooleanOption? SelectedWhisperModeOption
    {
        get => WhisperModeOptions.FirstOrDefault(option => option.Value == EditWhisperModeOverride);
        set
        {
            if (value?.Value == EditWhisperModeOverride)
                return;

            EditWhisperModeOverride = value?.Value;
            OnPropertyChanged();
        }
    }

    public ProfilesSectionViewModel(
        IProfileService profiles,
        IActiveWindowService activeWindow,
        PluginManager pluginManager,
        IPromptActionService promptActions)
    {
        _profiles = profiles;
        _activeWindow = activeWindow;
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

        _windowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _windowTimer.Tick += (_, _) => UpdateCurrentWindow();
        UpdateCurrentWindow();
        _windowTimer.Start();
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
            EditWhisperModeOverride = null;
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
        EditWhisperModeOverride = value.WhisperModeOverride;
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

    partial void OnEditModelIdChanged(string? value) =>
        OnPropertyChanged(nameof(SelectedModelOption));

    partial void OnEditPromptActionIdChanged(string? value) =>
        OnPropertyChanged(nameof(SelectedPromptActionOption));

    partial void OnEditWhisperModeOverrideChanged(bool? value) =>
        OnPropertyChanged(nameof(SelectedWhisperModeOption));

    partial void OnEditIsEnabledChanged(bool value) =>
        OnPropertyChanged(nameof(EditIsEnabledStatusText));

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
            WhisperModeOverride = EditWhisperModeOverride,
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
        OnPropertyChanged(nameof(SelectedProfileSummary));
    }

    [RelayCommand]
    private void RemoveProcessNameChip(string chip)
    {
        ProcessNameChips.Remove(chip);
        OnPropertyChanged(nameof(SelectedProfileSummary));
    }

    [RelayCommand]
    private void AddUrlPatternChip()
    {
        var value = UrlPatternInput.Trim();
        if (string.IsNullOrEmpty(value))
            return;

        if (!UrlPatternChips.Contains(value, StringComparer.OrdinalIgnoreCase))
            UrlPatternChips.Add(value);

        UrlPatternInput = "";
        OnPropertyChanged(nameof(SelectedProfileSummary));
    }

    [RelayCommand]
    private void RemoveUrlPatternChip(string chip)
    {
        UrlPatternChips.Remove(chip);
        OnPropertyChanged(nameof(SelectedProfileSummary));
    }

    [RelayCommand]
    private void AddCurrentProcessRule()
    {
        if (!HasCurrentProcess)
            return;

        if (!ProcessNameChips.Contains(CurrentProcessName, StringComparer.OrdinalIgnoreCase))
            ProcessNameChips.Add(CurrentProcessName);

        OnPropertyChanged(nameof(SelectedProfileSummary));
    }

    [RelayCommand]
    private void AddCurrentUrlRule()
    {
        var pattern = CurrentUrlPattern;
        if (string.IsNullOrWhiteSpace(pattern))
            return;

        if (!UrlPatternChips.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            UrlPatternChips.Add(pattern);

        OnPropertyChanged(nameof(SelectedProfileSummary));
    }

    [RelayCommand]
    private void OpenLiveContextWindow()
    {
        if (_contextWindow is { IsVisible: true })
        {
            _contextWindow.Activate();
            return;
        }

        _contextWindow = new ProfilesContextWindow(this);
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } owner)
        {
            _contextWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _contextWindow.Show(owner);
        }
        else
        {
            _contextWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            _contextWindow.Show();
        }

        _contextWindow.Closed += (_, _) => _contextWindow = null;
    }

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

    private void UpdateCurrentWindow()
    {
        var processName = _activeWindow.GetActiveWindowProcessName();
        var title = _activeWindow.GetActiveWindowTitle();
        var url = _activeWindow.GetBrowserUrl();

        if (string.IsNullOrWhiteSpace(processName)
            || string.Equals(processName, _hostProcessName, StringComparison.OrdinalIgnoreCase))
        {
            processName = _lastExternalProcessName;
            title = _lastExternalWindowTitle;
            url = _lastExternalUrl;
        }
        else
        {
            _lastExternalProcessName = processName ?? "-";
            _lastExternalWindowTitle = title ?? "-";
            _lastExternalUrl = url ?? "-";
        }

        CurrentProcessName = processName ?? "-";
        CurrentWindowTitle = title ?? "-";
        CurrentUrl = url ?? "-";

        var matched = _profiles.MatchProfile(processName, url);
        HasMatchedProfile = matched is not null;
        MatchedProfileName = matched?.Name ?? "No profile";

        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(HasSelectedProfile));
        OnPropertyChanged(nameof(ProfileCount));
        OnPropertyChanged(nameof(EnabledProfileCount));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SelectedProfileDisplayName));
        OnPropertyChanged(nameof(SelectedProfileSummary));
        OnPropertyChanged(nameof(SelectedTranslationTargetOption));
        OnPropertyChanged(nameof(SelectedModelOption));
        OnPropertyChanged(nameof(SelectedPromptActionOption));
        OnPropertyChanged(nameof(SelectedWhisperModeOption));
        OnPropertyChanged(nameof(MatchStatusText));
        OnPropertyChanged(nameof(ShowLiveContextProfileHint));
        OnPropertyChanged(nameof(HasCurrentProcess));
        OnPropertyChanged(nameof(HasCurrentUrl));
        OnPropertyChanged(nameof(ShowNoBrowserUrlHint));
        OnPropertyChanged(nameof(HasCurrentWindowTitle));
        OnPropertyChanged(nameof(CurrentUrlPattern));
        OnPropertyChanged(nameof(EditIsEnabledStatusText));
    }

    private static string TryExtractUrlPattern(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl) || rawUrl == "-")
            return string.Empty;

        if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return uri.Host;

        return rawUrl;
    }
}

public sealed record ProfileModelOption(string? Value, string Label);

public sealed record PromptActionOption(string? Value, string Label);

public sealed record NullableBooleanOption(bool? Value, string Label);
