using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.Views;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class ProfilesSectionViewModel : ObservableObject
{
    private readonly IActiveWindowService _activeWindow;
    private readonly BrowserAccessibilitySetupHelper _browserSetup;
    private readonly IDetectionFailureTracker _failureTracker;
    private readonly GnomeWindowCallsSetupHelper _gnomeSetup;
    private readonly string _hostProcessName = Process.GetCurrentProcess().ProcessName;
    private readonly PluginManager _pluginManager;
    private readonly IProfileService _profiles;
    private readonly IPromptActionService _promptActions;
    private readonly DispatcherTimer _windowTimer;

    [ObservableProperty]
    private string? _browserAccessibilityStatusMessage;

    [ObservableProperty]
    private bool _canEnableBrowserAccessibility;

    [ObservableProperty]
    private bool _canInstallWindowCallsExtension;

    [ObservableProperty]
    private bool _canRevertBrowserAccessibility;

    private ProfilesContextWindow? _contextWindow;

    [ObservableProperty]
    private string _currentProcessName = "-";

    [ObservableProperty]
    private string _currentUrl = "-";

    [ObservableProperty]
    private string _currentWindowTitle = "-";

    [ObservableProperty]
    private CleanupLevel? _editCleanupLevelOverride;

    [ObservableProperty]
    private bool? _editDeveloperFormattingOverride;

    [ObservableProperty]
    private ProfileHotkeyBehavior _editHotkeyBehavior = ProfileHotkeyBehavior.StartDictation;

    [ObservableProperty]
    private string? _editHotkeyData;

    [ObservableProperty]
    private bool _editIsEnabled = true;

    [ObservableProperty]
    private string? _editLanguage;

    [ObservableProperty]
    private string? _editModelId;

    [ObservableProperty]
    private string _editName = "";

    [ObservableProperty]
    private int _editPriority;

    [ObservableProperty]
    private string? _editPromptActionId;

    [ObservableProperty]
    private ProfileStylePreset _editStylePreset = ProfileStylePreset.Raw;

    [ObservableProperty]
    private string? _editTask;

    [ObservableProperty]
    private string? _editTranslationTarget;

    [ObservableProperty]
    private bool? _editWhisperModeOverride;

    [ObservableProperty]
    private bool _hasMatchedProfile;

    // Cache the last non-host window so the live-context display stays stable
    // when TypeWhisper itself is focused (querying the service then returns our own process).
    private string _lastExternalProcessName = "-";
    private string _lastExternalUrl = "-";
    private string _lastExternalWindowTitle = "-";
    private MatchResult _lastMatchResult = MatchResult.NoMatch;

    [ObservableProperty]
    private string _matchedProfileName = Loc.Instance["Profiles.NoProfile"];

    [ObservableProperty]
    private string _processNameInput = "";

    [ObservableProperty]
    private Profile? _selectedProfile;

    [ObservableProperty]
    private string _urlPatternInput = "";

    [ObservableProperty]
    private string? _waylandDetectionWarning;

    public ProfilesSectionViewModel(
        IProfileService profiles,
        IActiveWindowService activeWindow,
        PluginManager pluginManager,
        IPromptActionService promptActions,
        IDetectionFailureTracker failureTracker,
        GnomeWindowCallsSetupHelper gnomeSetup,
        BrowserAccessibilitySetupHelper browserSetup
    )
    {
        _profiles = profiles;
        _activeWindow = activeWindow;
        _pluginManager = pluginManager;
        _promptActions = promptActions;
        _failureTracker = failureTracker;
        _gnomeSetup = gnomeSetup;
        _browserSetup = browserSetup;
        RefreshBrowserAccessibilityStatus();

        _profiles.ProfilesChanged += () => Dispatcher.UIThread.Post(RefreshProfiles);
        _pluginManager.PluginStateChanged += (_, _) =>
            Dispatcher.UIThread.Post(RefreshModelOptions);
        _promptActions.ActionsChanged += () => Dispatcher.UIThread.Post(RefreshPromptActionOptions);
        UrlPatternChips.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsUrlPatternsSectionVisible));
            OnPropertyChanged(nameof(IsGlobalFallbackProfile));
        };
        ProcessNameChips.CollectionChanged += (_, _) =>
            OnPropertyChanged(nameof(IsGlobalFallbackProfile));
        _failureTracker.OnFailure += (_, e) =>
        {
            if (!e.ShouldShowPersistentBanner)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                WaylandDetectionWarning = e.Reason;
                CanInstallWindowCallsExtension =
                    _gnomeSetup.IsApplicable() && !_gnomeSetup.IsCurrentlyInstalled();
            });
        };

        RefreshModelOptions();
        RefreshPromptActionOptions();
        foreach (var option in TranslationModelInfo.ProfileTargetOptions)
        {
            TranslationTargetOptions.Add(option);
        }

        RefreshProfiles();

        _windowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _windowTimer.Tick += (_, _) => UpdateCurrentWindow();
        UpdateCurrentWindow();
        _windowTimer.Start();
    }

    public ObservableCollection<Profile> Profiles { get; } = [];
    public ObservableCollection<ProfileModelOption> ModelOptions { get; } = [];
    public ObservableCollection<PromptActionOption> PromptActionOptions { get; } = [];
    public ObservableCollection<TranslationTargetOption> TranslationTargetOptions { get; } = [];

    public ObservableCollection<ProfileStylePresetOption> StylePresetOptions { get; } =
    [
        new(ProfileStylePreset.Raw, Loc.Instance["Profiles.StylePresetRaw"]),
        new(ProfileStylePreset.Clean, Loc.Instance["Profiles.StylePresetClean"]),
        new(ProfileStylePreset.Concise, Loc.Instance["Profiles.StylePresetConcise"]),
        new(ProfileStylePreset.FormalEmail, Loc.Instance["Profiles.StylePresetFormalEmail"]),
        new(ProfileStylePreset.CasualMessage, Loc.Instance["Profiles.StylePresetCasualMessage"]),
        new(ProfileStylePreset.Developer, Loc.Instance["Profiles.StylePresetDeveloper"]),
        new(ProfileStylePreset.TerminalSafe, Loc.Instance["Profiles.StylePresetTerminalSafe"]),
        new(ProfileStylePreset.MeetingNotes, Loc.Instance["Profiles.StylePresetMeetingNotes"])
    ];

    public ObservableCollection<ProfileHotkeyBehaviorOption> HotkeyBehaviorOptions { get; } =
    [
        new(ProfileHotkeyBehavior.StartDictation, Loc.Instance["Profiles.HotkeyBehaviorStartDictation"]),
        new(ProfileHotkeyBehavior.ProcessSelectedText, Loc.Instance["Profiles.HotkeyBehaviorProcessSelectedText"])
    ];

    public ObservableCollection<NullableCleanupLevelOption> CleanupOverrideOptions { get; } =
    [
        new(null, Loc.Instance["Profiles.CleanupUseStylePreset"]),
        new(CleanupLevel.None, Loc.Instance["Profiles.CleanupNone"]),
        new(CleanupLevel.Light, Loc.Instance["Profiles.CleanupLight"]),
        new(CleanupLevel.Medium, Loc.Instance["Profiles.CleanupMedium"]),
        new(CleanupLevel.High, Loc.Instance["Profiles.CleanupHigh"])
    ];

    public ObservableCollection<string> ProcessNameChips { get; } = [];
    public ObservableCollection<string> UrlPatternChips { get; } = [];

    // URL rules always work on X11; on Wayland they need browser accessibility configured.
    // Keep the section visible when the profile already has saved patterns to avoid data loss.
    public bool IsUrlPatternsSectionVisible =>
        !BrowserAccessibilitySetupHelper.IsApplicable()
        || BrowserAccessibilitySetupHelper.IsCurrentlyConfigured().IsFullyConfigured
        || UrlPatternChips.Count > 0;

    /// <summary>
    ///     True when the edited profile has no app matchers and no URL patterns.
    ///     <see cref="IProfileService.MatchProfile" />'s Global tier then picks it up for any window
    ///     no other profile matches, making it the de-facto fallback. A profile with a hotkey is
    ///     hotkey-only (excluded from the Global tier) and is NOT a fallback.
    /// </summary>
    public bool IsGlobalFallbackProfile =>
        ProcessNameChips.Count == 0
        && UrlPatternChips.Count == 0
        && string.IsNullOrWhiteSpace(EditHotkeyData);

    public IReadOnlyList<string> LanguageChoices { get; } =
        ["", "auto", "en", "de", "fr", "es", "pt", "ja", "zh", "ko", "it", "nl", "pl", "ru"];

    public IReadOnlyList<string> TaskChoices { get; } = ["", "transcribe", "translate"];

    public bool HasSelectedProfile => SelectedProfile is not null;
    public int ProfileCount => Profiles.Count;
    public int EnabledProfileCount => Profiles.Count(static profile => profile.IsEnabled);
    public string Summary =>
        Loc.Instance.GetString("Profiles.Summary", ProfileCount, EnabledProfileCount);

    public string SelectedProfileSummary =>
        SelectedProfile is null
            ? Loc.Instance["Profiles.SelectProfileHint"]
            : Loc.Instance.GetString(
                "Profiles.RulesSummary",
                ProcessNameChips.Count,
                UrlPatternChips.Count
            );

    public string SelectedProfileDisplayName =>
        SelectedProfile?.Name ?? Loc.Instance["Profiles.NoProfile"];

    public string MatchStatusText =>
        HasMatchedProfile
            ? Loc.Instance.GetString("Profiles.Matches", MatchedProfileName)
            : Loc.Instance["Profiles.NoActiveMatch"];

    public bool ShowLiveContextProfileHint => !HasSelectedProfile;

    public bool HasCurrentProcess =>
        !string.IsNullOrWhiteSpace(CurrentProcessName) && CurrentProcessName != "-";

    public bool HasCurrentUrl => !string.IsNullOrWhiteSpace(CurrentUrl) && CurrentUrl != "-";
    public bool ShowNoBrowserUrlHint => !HasCurrentUrl;

    public bool HasCurrentWindowTitle =>
        !string.IsNullOrWhiteSpace(CurrentWindowTitle) && CurrentWindowTitle != "-";

    public string CurrentUrlPattern => TryExtractUrlPattern(CurrentUrl);
    public string EditIsEnabledStatusText =>
        EditIsEnabled ? Loc.Instance["Common.On"] : Loc.Instance["Common.Off"];

    public IReadOnlyList<NullableBooleanOption> WhisperModeOptions { get; } =
    [
        new(null, Loc.Instance["Profiles.UseGlobalDefault"]),
        new(true, Loc.Instance["Common.Enabled"]),
        new(false, Loc.Instance["Common.Disabled"])
    ];

    public TranslationTargetOption? SelectedTranslationTargetOption
    {
        get =>
            TranslationTargetOptions.FirstOrDefault(option =>
                string.Equals(option.Code, EditTranslationTarget, StringComparison.Ordinal)
            );
        set
        {
            var code = value?.Code;
            if (string.Equals(code, EditTranslationTarget, StringComparison.Ordinal))
            {
                return;
            }

            EditTranslationTarget = code;
            OnPropertyChanged();
        }
    }

    public ProfileModelOption? SelectedModelOption
    {
        get =>
            ModelOptions.FirstOrDefault(option =>
                string.Equals(option.Value, EditModelId, StringComparison.Ordinal)
            );
        set
        {
            var selected = value?.Value;
            if (string.Equals(selected, EditModelId, StringComparison.Ordinal))
            {
                return;
            }

            EditModelId = selected;
            OnPropertyChanged();
        }
    }

    public PromptActionOption? SelectedPromptActionOption
    {
        get =>
            PromptActionOptions.FirstOrDefault(option =>
                string.Equals(option.Value, EditPromptActionId, StringComparison.Ordinal)
            );
        set
        {
            var selected = value?.Value;
            if (string.Equals(selected, EditPromptActionId, StringComparison.Ordinal))
            {
                return;
            }

            EditPromptActionId = selected;
            OnPropertyChanged();
        }
    }

    public ProfileStylePresetOption? SelectedStylePresetOption
    {
        get => StylePresetOptions.FirstOrDefault(option => option.Value == EditStylePreset);
        set
        {
            var selected = value?.Value ?? ProfileStylePreset.Raw;
            if (selected == EditStylePreset)
            {
                return;
            }

            EditStylePreset = selected;
            OnPropertyChanged();
        }
    }

    public ProfileHotkeyBehaviorOption? SelectedHotkeyBehaviorOption
    {
        get => HotkeyBehaviorOptions.FirstOrDefault(o => o.Value == EditHotkeyBehavior);
        set
        {
            var selected = value?.Value ?? ProfileHotkeyBehavior.StartDictation;
            if (selected == EditHotkeyBehavior)
            {
                return;
            }

            EditHotkeyBehavior = selected;
        }
    }

    public NullableBooleanOption? SelectedWhisperModeOption
    {
        get => WhisperModeOptions.FirstOrDefault(option => option.Value == EditWhisperModeOverride);
        set
        {
            if (value?.Value == EditWhisperModeOverride)
            {
                return;
            }

            EditWhisperModeOverride = value?.Value;
            OnPropertyChanged();
        }
    }

    public NullableBooleanOption? SelectedDeveloperFormattingOverrideOption
    {
        get =>
            WhisperModeOptions.FirstOrDefault(option =>
                option.Value == EditDeveloperFormattingOverride
            );
        set
        {
            if (value?.Value == EditDeveloperFormattingOverride)
            {
                return;
            }

            EditDeveloperFormattingOverride = value?.Value;
            OnPropertyChanged();
        }
    }

    public NullableCleanupLevelOption? SelectedCleanupOverrideOption
    {
        get =>
            CleanupOverrideOptions.FirstOrDefault(option =>
                option.Value == EditCleanupLevelOverride
            );
        set
        {
            if (value?.Value == EditCleanupLevelOverride)
            {
                return;
            }

            EditCleanupLevelOverride = value?.Value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    ///     Re-polls providers when a model dropdown opens so newly added models appear
    ///     without a manual "Validate". Debounce/guard live in <see cref="PluginManager" />.
    /// </summary>
    public Task RefreshProviderModelsAsync()
    {
        return _pluginManager.RefreshProviderModelsAsync();
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
            EditHotkeyData = null;
            EditHotkeyBehavior = ProfileHotkeyBehavior.StartDictation;
            EditStylePreset = ProfileStylePreset.Raw;
            EditCleanupLevelOverride = null;
            EditDeveloperFormattingOverride = null;
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
        EditHotkeyData = value.HotkeyData;
        EditHotkeyBehavior = value.HotkeyBehavior;
        EditStylePreset = value.StylePreset;
        EditCleanupLevelOverride = value.CleanupLevelOverride;
        EditDeveloperFormattingOverride = value.DeveloperFormattingOverride;
        EditPriority = value.Priority;
        EditIsEnabled = value.IsEnabled;
        OnPropertyChanged(nameof(SelectedTranslationTargetOption));

        foreach (var processName in value.ProcessNames)
        {
            ProcessNameChips.Add(processName);
        }

        foreach (var urlPattern in value.UrlPatterns)
        {
            UrlPatternChips.Add(urlPattern);
        }

        NotifyStateChanged();
    }

    partial void OnEditTranslationTargetChanged(string? value)
    {
        OnPropertyChanged(nameof(SelectedTranslationTargetOption));
    }

    partial void OnEditModelIdChanged(string? value)
    {
        OnPropertyChanged(nameof(SelectedModelOption));
    }

    partial void OnEditPromptActionIdChanged(string? value)
    {
        OnPropertyChanged(nameof(SelectedPromptActionOption));
    }

    partial void OnEditStylePresetChanged(ProfileStylePreset value)
    {
        OnPropertyChanged(nameof(SelectedStylePresetOption));
    }

    partial void OnEditHotkeyBehaviorChanged(ProfileHotkeyBehavior value)
    {
        OnPropertyChanged(nameof(SelectedHotkeyBehaviorOption));
    }

    partial void OnEditHotkeyDataChanged(string? value)
    {
        // A hotkey turns an empty-matcher profile into a hotkey-only profile,
        // which is no longer the global fallback — refresh the editor hint.
        OnPropertyChanged(nameof(IsGlobalFallbackProfile));
    }

    partial void OnEditCleanupLevelOverrideChanged(CleanupLevel? value)
    {
        OnPropertyChanged(nameof(SelectedCleanupOverrideOption));
    }

    partial void OnEditDeveloperFormattingOverrideChanged(bool? value)
    {
        OnPropertyChanged(nameof(SelectedDeveloperFormattingOverrideOption));
    }

    partial void OnEditWhisperModeOverrideChanged(bool? value)
    {
        OnPropertyChanged(nameof(SelectedWhisperModeOption));
    }

    partial void OnEditIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(EditIsEnabledStatusText));
    }

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
        {
            return;
        }

        var updated = SelectedProfile with
        {
            Name = EditName.Trim(),
            ProcessNames = [.. ProcessNameChips],
            UrlPatterns = [.. UrlPatternChips],
            InputLanguage = string.IsNullOrWhiteSpace(EditLanguage) ? null : EditLanguage,
            SelectedTask = string.IsNullOrWhiteSpace(EditTask) ? null : EditTask,
            TranslationTarget = EditTranslationTarget,
            WhisperModeOverride = EditWhisperModeOverride,
            TranscriptionModelOverride = string.IsNullOrWhiteSpace(EditModelId)
                ? null
                : EditModelId,
            PromptActionId = string.IsNullOrWhiteSpace(EditPromptActionId)
                ? null
                : EditPromptActionId,
            HotkeyData = string.IsNullOrWhiteSpace(EditHotkeyData) ? null : EditHotkeyData.Trim(),
            HotkeyBehavior = EditHotkeyBehavior,
            StylePreset = EditStylePreset,
            CleanupLevelOverride = EditCleanupLevelOverride,
            DeveloperFormattingOverride = EditDeveloperFormattingOverride,
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
        {
            return;
        }

        var duplicate = SelectedProfile with
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"{SelectedProfile.Name} Copy",
            // Drop the hotkey: two profiles can't share a chord (SetProfileHotkeys rejects collisions),
            // so a copied hotkey would be silently dead.
            HotkeyData = null,
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
        {
            return;
        }

        _profiles.DeleteProfile(SelectedProfile.Id);
        RefreshProfiles();
        SelectedProfile = null;
    }

    [RelayCommand]
    private void ToggleProfileEnabled(Profile? profile)
    {
        if (profile is null)
        {
            return;
        }

        _profiles.UpdateProfile(profile with { IsEnabled = !profile.IsEnabled });
        RefreshProfiles();
    }

    [RelayCommand]
    private void AddProcessNameChip()
    {
        var value = ProcessNameInput.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (!ProcessNameChips.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            ProcessNameChips.Add(value);
        }

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
        {
            return;
        }

        if (!UrlPatternChips.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            UrlPatternChips.Add(value);
        }

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
    private void CaptureCurrentProcessName()
    {
        // Use the live-context cache rather than re-querying: when this button is clicked,
        // TypeWhisper is focused so a fresh query would return our own process name.
        if (
            string.IsNullOrWhiteSpace(CurrentProcessName)
            || CurrentProcessName == "-"
            || string.Equals(
                CurrentProcessName,
                _hostProcessName,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return;
        }

        ProcessNameInput = CurrentProcessName;
    }

    [RelayCommand]
    private void CaptureCurrentUrlPattern()
    {
        // Same rationale as CaptureCurrentProcessName: TypeWhisper is focused so
        // a fresh GetBrowserUrl call would return null.
        if (string.IsNullOrWhiteSpace(CurrentUrl) || CurrentUrl == "-")
        {
            return;
        }

        var pattern = TryExtractUrlPattern(CurrentUrl);
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return;
        }

        UrlPatternInput = pattern;
    }

    [RelayCommand]
    private void AddCurrentProcessRule()
    {
        if (!HasCurrentProcess)
        {
            return;
        }

        if (!ProcessNameChips.Contains(CurrentProcessName, StringComparer.OrdinalIgnoreCase))
        {
            ProcessNameChips.Add(CurrentProcessName);
        }

        OnPropertyChanged(nameof(SelectedProfileSummary));
    }

    [RelayCommand]
    private void AddCurrentUrlRule()
    {
        var pattern = CurrentUrlPattern;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return;
        }

        if (!UrlPatternChips.Contains(pattern, StringComparer.OrdinalIgnoreCase))
        {
            UrlPatternChips.Add(pattern);
        }

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
        if (
            Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
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
        {
            Profiles.Add(profile);
        }

        if (selectedId is not null)
        {
            SelectById(selectedId);
            return;
        }

        if (Profiles.Count > 0 && SelectedProfile is null)
        {
            SelectedProfile = Profiles[0];
        }
        else
        {
            NotifyStateChanged();
        }
    }

    private void RefreshModelOptions()
    {
        var selected = EditModelId;
        ModelOptions.Clear();
        ModelOptions.Add(new ProfileModelOption(null, Loc.Instance["Profiles.UseGlobalDefault"]));

        foreach (var engine in _pluginManager.TranscriptionEngines)
        {
            foreach (var model in engine.TranscriptionModels)
            {
                ModelOptions.Add(
                    new ProfileModelOption(
                        ModelManagerService.GetPluginModelId(engine.GetTranscriptionSelectionId(), model.Id),
                        $"{engine.ProviderDisplayName} — {model.DisplayName}"
                    )
                );
            }
        }

        EditModelId = ModelOptions.Any(option => option.Value == selected) ? selected : null;
    }

    private void RefreshPromptActionOptions()
    {
        var selected = EditPromptActionId;
        PromptActionOptions.Clear();
        PromptActionOptions.Add(new PromptActionOption(null, Loc.Instance["Profiles.NoPromptAction"]));

        foreach (
            var action in _promptActions
                .Actions.Where(action => !action.IsManualOnly)
                .OrderBy(action => action.SortOrder)
                .ThenBy(action => action.Name)
        )
        {
            PromptActionOptions.Add(new PromptActionOption(action.Id, action.Name));
        }

        EditPromptActionId = PromptActionOptions.Any(option => option.Value == selected)
            ? selected
            : null;
    }

    private void SelectById(string id)
    {
        var match = Profiles.FirstOrDefault(profile => profile.Id == id);
        if (match is not null)
        {
            SelectedProfile = match;
        }
        else
        {
            NotifyStateChanged();
        }
    }

    private void UpdateCurrentWindow()
    {
        var processName = _activeWindow.GetActiveWindowProcessName();
        var title = _activeWindow.GetActiveWindowTitle();
        var url = _activeWindow.GetBrowserUrl(false);

        if (
            string.IsNullOrWhiteSpace(processName)
            || string.Equals(processName, _hostProcessName, StringComparison.OrdinalIgnoreCase)
        )
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

        _lastMatchResult = _profiles.MatchProfile(processName, url);
        HasMatchedProfile = _lastMatchResult.Profile is not null;
        MatchedProfileName = _lastMatchResult.Profile?.Name ?? Loc.Instance["Profiles.NoProfile"];

        WaylandDetectionWarning = _failureTracker.ShouldShowPersistentBanner
            ? _failureTracker.LastFailureReason
            : null;

        CanInstallWindowCallsExtension =
            WaylandDetectionWarning is not null
            && _gnomeSetup.IsApplicable()
            && !_gnomeSetup.IsCurrentlyInstalled();

        NotifyStateChanged();
    }

    [RelayCommand]
    private void InstallWindowCallsExtension()
    {
        if (!_gnomeSetup.TryOpenInstallPage())
        {
            return;
        }

        // The provider picks up the extension on the next snapshot tick; recheck
        // so the button disappears as soon as the user finishes installing.
        Dispatcher.UIThread.Post(
            () =>
            {
                CanInstallWindowCallsExtension = !_gnomeSetup.IsCurrentlyInstalled();
            },
            DispatcherPriority.Background
        );
    }

    [RelayCommand]
    private async Task EnableBrowserAccessibility()
    {
        // Show every path we'll touch before acting — modifying browser launchers
        // and Firefox profile overrides deserves explicit consent. Already-done
        // items from prior runs are omitted so the dialog only shows what's left.
        var actions = _browserSetup.DescribePendingActions();
        if (actions.Count == 0)
        {
            RefreshBrowserAccessibilityStatus();
            return;
        }

        var message =
            Loc.Instance["Profiles.BrowserSetupIntro"]
            + "\n\n"
            + string.Join("\n\n", actions)
            + "\n\n"
            + Loc.Instance["Profiles.BrowserSetupOutro"];

        var dialog = new MessageDialogWindow();
        var confirmed = await dialog.ShowConfirmationAsync(
            Loc.Instance["Profiles.EnableBrowserUrlDetection"],
            message,
            Loc.Instance["Profiles.ApplyChanges"]
        );

        if (!confirmed)
        {
            return;
        }

        var result = await _browserSetup.SetUpAsync(CancellationToken.None).ConfigureAwait(true);
        // Re-evaluate panel state: Enable should disappear and Revert should appear together.
        RefreshBrowserAccessibilityStatus();
        BrowserAccessibilityStatusMessage = result.Success
            ? Loc.Instance.GetString("Profiles.BrowserSetupSuccess", result.Message)
            : $"{result.Message} {result.Detail}";
    }

    private void RefreshBrowserAccessibilityStatus()
    {
        // AT-SPI browser setup is Wayland-only (X11 uses xdotool + xclip Ctrl+L).
        if (!BrowserAccessibilitySetupHelper.IsApplicable())
        {
            BrowserAccessibilityStatusMessage = null;
            CanEnableBrowserAccessibility = false;
            CanRevertBrowserAccessibility = false;
            return;
        }

        var status = BrowserAccessibilitySetupHelper.IsCurrentlyConfigured();
        var hasAnyInstall = BrowserAccessibilitySetupHelper.HasInstalledChanges();

        if (status.IsFullyConfigured)
        {
            BrowserAccessibilityStatusMessage = Loc.Instance["Profiles.BrowserStatusConfigured"];
            CanEnableBrowserAccessibility = false;
        }
        else if (hasAnyInstall)
        {
            // Partial state: new browser installed after Enable, or a multi-profile/launcher
            // piece was missed. The confirmation dialog lists only the missing pieces.
            BrowserAccessibilityStatusMessage = Loc.Instance["Profiles.BrowserStatusPartial"];
            CanEnableBrowserAccessibility = true;
        }
        else
        {
            BrowserAccessibilityStatusMessage = Loc.Instance["Profiles.BrowserStatusDisabled"];
            CanEnableBrowserAccessibility = true;
        }

        // Offer Revert whenever anything was installed, including partial state.
        CanRevertBrowserAccessibility = hasAnyInstall;

        OnPropertyChanged(nameof(IsUrlPatternsSectionVisible));
    }

    [RelayCommand]
    private async Task RevertBrowserAccessibility()
    {
        var actions = BrowserAccessibilitySetupHelper.DescribeRevertActions();
        if (actions.Count == 0)
        {
            RefreshBrowserAccessibilityStatus();
            return;
        }

        var message =
            Loc.Instance["Profiles.BrowserRevertIntro"]
            + "\n\n"
            + string.Join("\n\n", actions)
            + "\n\n"
            + Loc.Instance["Profiles.BrowserRevertOutro"];

        var dialog = new MessageDialogWindow();
        var confirmed = await dialog.ShowConfirmationAsync(
            Loc.Instance["Profiles.RevertBrowserUrlDetection"],
            message,
            Loc.Instance["Common.Revert"]
        );

        if (!confirmed)
        {
            return;
        }

        var result = await BrowserAccessibilitySetupHelper.RemoveAsync(CancellationToken.None).ConfigureAwait(true);
        BrowserAccessibilityStatusMessage = result.Success
            ? Loc.Instance.GetString("Profiles.BrowserRevertSuccess", result.Message)
            : $"{result.Message} {result.Detail}";
        RefreshBrowserAccessibilityStatus();
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
        OnPropertyChanged(nameof(SelectedStylePresetOption));
        OnPropertyChanged(nameof(SelectedCleanupOverrideOption));
        OnPropertyChanged(nameof(SelectedDeveloperFormattingOverrideOption));
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
        {
            return string.Empty;
        }

        if (
            Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.Host)
        )
        {
            return uri.Host;
        }

        return rawUrl;
    }
}

public sealed record ProfileModelOption(string? Value, string Label);

public sealed record PromptActionOption(string? Value, string Label);

public sealed record ProfileStylePresetOption(ProfileStylePreset Value, string Label);

public sealed record ProfileHotkeyBehaviorOption(ProfileHotkeyBehavior Value, string Label);

public sealed record NullableBooleanOption(bool? Value, string Label);

public sealed record NullableCleanupLevelOption(CleanupLevel? Value, string Label);