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
    private readonly IDetectionFailureTracker _failureTracker;
    private readonly GnomeWindowCallsSetupHelper _gnomeSetup;
    private readonly BrowserAccessibilitySetupHelper _browserSetup;
    private readonly DispatcherTimer _windowTimer;
    private readonly string _hostProcessName = Process.GetCurrentProcess().ProcessName;
    private ProfilesContextWindow? _contextWindow;
    private string _lastExternalProcessName = "-";
    private string _lastExternalWindowTitle = "-";
    private string _lastExternalUrl = "-";
    private MatchResult _lastMatchResult = MatchResult.NoMatch;

    public ObservableCollection<Profile> Profiles { get; } = [];
    public ObservableCollection<ProfileModelOption> ModelOptions { get; } = [];
    public ObservableCollection<PromptActionOption> PromptActionOptions { get; } = [];
    public ObservableCollection<TranslationTargetOption> TranslationTargetOptions { get; } = [];
    public ObservableCollection<ProfileStylePresetOption> StylePresetOptions { get; } =
    [
        new(ProfileStylePreset.Raw, "Raw"),
        new(ProfileStylePreset.Clean, "Clean"),
        new(ProfileStylePreset.Concise, "Concise"),
        new(ProfileStylePreset.FormalEmail, "Formal email"),
        new(ProfileStylePreset.CasualMessage, "Casual message"),
        new(ProfileStylePreset.Developer, "Developer"),
        new(ProfileStylePreset.TerminalSafe, "Terminal-safe"),
        new(ProfileStylePreset.MeetingNotes, "Meeting notes")
    ];
    public ObservableCollection<NullableCleanupLevelOption> CleanupOverrideOptions { get; } =
    [
        new(null, "Use style preset"),
        new(CleanupLevel.None, "None"),
        new(CleanupLevel.Light, "Light"),
        new(CleanupLevel.Medium, "Medium"),
        new(CleanupLevel.High, "High")
    ];
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
    [ObservableProperty] private ProfileStylePreset _editStylePreset = ProfileStylePreset.Raw;
    [ObservableProperty] private CleanupLevel? _editCleanupLevelOverride;
    [ObservableProperty] private bool? _editDeveloperFormattingOverride;
    [ObservableProperty] private int _editPriority;
    [ObservableProperty] private bool _editIsEnabled = true;
    [ObservableProperty] private string _processNameInput = "";
    [ObservableProperty] private string _urlPatternInput = "";
    [ObservableProperty] private string _currentProcessName = "-";
    [ObservableProperty] private string _currentWindowTitle = "-";
    [ObservableProperty] private string _currentUrl = "-";
    [ObservableProperty] private string _matchedProfileName = "No profile";
    [ObservableProperty] private bool _hasMatchedProfile;
    [ObservableProperty] private string? _waylandDetectionWarning;
    [ObservableProperty] private bool _canInstallWindowCallsExtension;
    [ObservableProperty] private string? _browserAccessibilityStatusMessage;
    [ObservableProperty] private bool _canEnableBrowserAccessibility;
    [ObservableProperty] private bool _canRevertBrowserAccessibility;

    /// <summary>
    /// True when the URL Patterns block in the profile editor should be
    /// rendered. URL-based profile rules need a working URL extraction
    /// path: on X11 that's always available (xdotool + xclip); on Wayland
    /// it needs browser accessibility configured. Hiding the section when
    /// it can't possibly work avoids leading the user into building rules
    /// that silently never match — but we keep the section visible when
    /// the profile already has saved URL patterns so existing data isn't
    /// orphaned from view.
    /// </summary>
    public bool IsUrlPatternsSectionVisible =>
        !_browserSetup.IsApplicable()
        || _browserSetup.IsCurrentlyConfigured().IsFullyConfigured
        || UrlPatternChips.Count > 0;

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

    public ProfileStylePresetOption? SelectedStylePresetOption
    {
        get => StylePresetOptions.FirstOrDefault(option => option.Value == EditStylePreset);
        set
        {
            var selected = value?.Value ?? ProfileStylePreset.Raw;
            if (selected == EditStylePreset)
                return;

            EditStylePreset = selected;
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

    public NullableBooleanOption? SelectedDeveloperFormattingOverrideOption
    {
        get => WhisperModeOptions.FirstOrDefault(option => option.Value == EditDeveloperFormattingOverride);
        set
        {
            if (value?.Value == EditDeveloperFormattingOverride)
                return;

            EditDeveloperFormattingOverride = value?.Value;
            OnPropertyChanged();
        }
    }

    public NullableCleanupLevelOption? SelectedCleanupOverrideOption
    {
        get => CleanupOverrideOptions.FirstOrDefault(option => option.Value == EditCleanupLevelOverride);
        set
        {
            if (value?.Value == EditCleanupLevelOverride)
                return;

            EditCleanupLevelOverride = value?.Value;
            OnPropertyChanged();
        }
    }

    public ProfilesSectionViewModel(
        IProfileService profiles,
        IActiveWindowService activeWindow,
        PluginManager pluginManager,
        IPromptActionService promptActions,
        IDetectionFailureTracker failureTracker,
        GnomeWindowCallsSetupHelper gnomeSetup,
        BrowserAccessibilitySetupHelper browserSetup)
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
        _pluginManager.PluginStateChanged += (_, _) => Dispatcher.UIThread.Post(RefreshModelOptions);
        _promptActions.ActionsChanged += () => Dispatcher.UIThread.Post(RefreshPromptActionOptions);
        UrlPatternChips.CollectionChanged += (_, _) =>
            OnPropertyChanged(nameof(IsUrlPatternsSectionVisible));
        _failureTracker.OnFailure += (_, e) =>
        {
            if (!e.ShouldShowPersistentBanner) return;
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
        EditStylePreset = value.StylePreset;
        EditCleanupLevelOverride = value.CleanupLevelOverride;
        EditDeveloperFormattingOverride = value.DeveloperFormattingOverride;
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

    partial void OnEditStylePresetChanged(ProfileStylePreset value) =>
        OnPropertyChanged(nameof(SelectedStylePresetOption));

    partial void OnEditCleanupLevelOverrideChanged(CleanupLevel? value) =>
        OnPropertyChanged(nameof(SelectedCleanupOverrideOption));

    partial void OnEditDeveloperFormattingOverrideChanged(bool? value) =>
        OnPropertyChanged(nameof(SelectedDeveloperFormattingOverrideOption));

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
    private void CaptureCurrentProcessName()
    {
        // Read from the live-context cache (CurrentProcessName) rather than
        // re-querying the active-window service — when the user clicks this
        // button, *TypeWhisper* is the focused window, so a fresh query
        // would return our own process name and the self-host guard would
        // bail. The live-context timer tracks the most-recent non-host
        // process name and exposes it via CurrentProcessName.
        if (string.IsNullOrWhiteSpace(CurrentProcessName)
            || CurrentProcessName == "-"
            || string.Equals(CurrentProcessName, _hostProcessName, StringComparison.OrdinalIgnoreCase))
            return;

        ProcessNameInput = CurrentProcessName;
    }

    [RelayCommand]
    private void CaptureCurrentUrlPattern()
    {
        // Same rationale as CaptureCurrentProcessName: use the live-context
        // value, not a fresh GetBrowserUrl call (which would target the
        // TypeWhisper window itself and return null).
        if (string.IsNullOrWhiteSpace(CurrentUrl) || CurrentUrl == "-")
            return;

        var pattern = TryExtractUrlPattern(CurrentUrl);
        if (string.IsNullOrWhiteSpace(pattern))
            return;

        UrlPatternInput = pattern;
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
        var url = _activeWindow.GetBrowserUrl(allowInteractiveCapture: false);

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

        _lastMatchResult = _profiles.MatchProfile(processName, url);
        HasMatchedProfile = _lastMatchResult.Profile is not null;
        MatchedProfileName = _lastMatchResult.Profile?.Name ?? "No profile";

        WaylandDetectionWarning = _failureTracker.ShouldShowPersistentBanner
            ? _failureTracker.LastFailureReason
            : null;

        CanInstallWindowCallsExtension = WaylandDetectionWarning is not null
            && _gnomeSetup.IsApplicable()
            && !_gnomeSetup.IsCurrentlyInstalled();

        NotifyStateChanged();
    }

    [RelayCommand]
    private void InstallWindowCallsExtension()
    {
        if (!_gnomeSetup.TryOpenInstallPage())
            return;

        // The provider picks the extension up automatically on the next
        // snapshot tick — no app restart needed. Recheck so the button
        // disappears as soon as the user finishes installing.
        Dispatcher.UIThread.Post(() =>
        {
            CanInstallWindowCallsExtension = !_gnomeSetup.IsCurrentlyInstalled();
        }, DispatcherPriority.Background);
    }

    [RelayCommand]
    private async Task EnableBrowserAccessibility()
    {
        // Show the user every file path we're about to touch before doing
        // anything — modifying browser launchers and Firefox profile
        // overrides is the kind of "magic" that deserves explicit
        // consent. Items already done in a prior run are omitted from
        // the list so the dialog is honest about what's left.
        var actions = _browserSetup.DescribePendingActions();
        if (actions.Count == 0)
        {
            RefreshBrowserAccessibilityStatus();
            return;
        }

        var message =
            "TypeWhisper will make the following changes to enable URL-based profile rules on Wayland:\n\n"
            + string.Join("\n\n", actions)
            + "\n\nAll changes are user-local and can be reverted from this panel. "
            + "Fully quit and relaunch the affected browsers afterward to pick up the changes.";

        var dialog = new MessageDialogWindow();
        var confirmed = await dialog.ShowConfirmationAsync(
            "Enable browser URL detection",
            message,
            confirmText: "Apply changes",
            cancelText: "Cancel");

        if (!confirmed)
            return;

        var result = await _browserSetup.SetUpAsync(CancellationToken.None).ConfigureAwait(true);
        // Re-evaluate the whole panel state — SetUpAsync just flipped both
        // "is configured" and "has installed changes", so Enable should
        // disappear and Revert should appear in the same beat.
        RefreshBrowserAccessibilityStatus();
        BrowserAccessibilityStatusMessage = result.Success
            ? $"{result.Message} Restart your browsers (or log out + back in) for the change to take effect."
            : $"{result.Message} {result.Detail}";
    }

    private void RefreshBrowserAccessibilityStatus()
    {
        // X11 has xdotool + xclip Ctrl+L for URL capture, so the AT-SPI
        // setup is Wayland-only. Suppress the whole panel elsewhere.
        if (!_browserSetup.IsApplicable())
        {
            BrowserAccessibilityStatusMessage = null;
            CanEnableBrowserAccessibility = false;
            CanRevertBrowserAccessibility = false;
            return;
        }

        var status = _browserSetup.IsCurrentlyConfigured();
        var hasAnyInstall = _browserSetup.HasInstalledChanges();

        if (status.IsFullyConfigured)
        {
            BrowserAccessibilityStatusMessage =
                "Browser accessibility is configured. If URL detection still fails, restart the browser.";
            CanEnableBrowserAccessibility = false;
        }
        else if (hasAnyInstall)
        {
            // Partial state — typically happens when a new browser is
            // installed after the user ran Enable, or when one of the
            // multi-profile / multi-launcher pieces wasn't covered the
            // first time. The confirmation dialog lists only the
            // missing pieces, so the user can finish the job without
            // re-touching the parts already in place.
            BrowserAccessibilityStatusMessage =
                "Browser accessibility is partially configured — at least one browser, profile, or launcher "
                + "still needs setup. Click Enable to finish (the confirmation dialog will list only what's missing), "
                + "or Revert to undo everything TypeWhisper installed.";
            CanEnableBrowserAccessibility = true;
        }
        else
        {
            BrowserAccessibilityStatusMessage =
                "URL-based profile rules require enabling browser accessibility. "
                + "Click below to see exactly what TypeWhisper will change.";
            CanEnableBrowserAccessibility = true;
        }

        // Revert is offered whenever there's anything we installed — even
        // in a partial state. That gives the user an escape hatch if they
        // ran Enable and want to back out before completing.
        CanRevertBrowserAccessibility = hasAnyInstall;

        OnPropertyChanged(nameof(IsUrlPatternsSectionVisible));
    }

    [RelayCommand]
    private async Task RevertBrowserAccessibility()
    {
        var actions = _browserSetup.DescribeRevertActions();
        if (actions.Count == 0)
        {
            RefreshBrowserAccessibilityStatus();
            return;
        }

        var message =
            "TypeWhisper will revert the following browser-accessibility changes:\n\n"
            + string.Join("\n\n", actions)
            + "\n\nFirefox prefs.js entries you set yourself via about:config will be left alone — "
            + "this only removes the changes TypeWhisper made. "
            + "Fully restart the affected browsers afterward to drop the previous settings.";

        var dialog = new MessageDialogWindow();
        var confirmed = await dialog.ShowConfirmationAsync(
            "Revert browser URL detection",
            message,
            confirmText: "Revert",
            cancelText: "Cancel");

        if (!confirmed)
            return;

        var result = await _browserSetup.RemoveAsync(CancellationToken.None).ConfigureAwait(true);
        BrowserAccessibilityStatusMessage = result.Success
            ? $"{result.Message} Restart your browsers for the change to take effect."
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
            return string.Empty;

        if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return uri.Host;

        return rawUrl;
    }
}

public sealed record ProfileModelOption(string? Value, string Label);

public sealed record PromptActionOption(string? Value, string Label);

public sealed record ProfileStylePresetOption(ProfileStylePreset Value, string Label);

public sealed record NullableBooleanOption(bool? Value, string Label);

public sealed record NullableCleanupLevelOption(CleanupLevel? Value, string Label);
