using System.Collections.ObjectModel;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Views;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Aggregates all sub-view models and controls routed navigation inside the settings shell.
/// </summary>
public sealed partial class SettingsWindowViewModel : ObservableObject
{
    private static SettingsRoute _lastOpenedRoute = SettingsRoute.Dashboard;

    public SettingsViewModel Settings { get; }
    public ModelManagerViewModel ModelManager { get; }
    public HistoryViewModel History { get; }
    public DictionaryViewModel Dictionary { get; }
    public SnippetsViewModel Snippets { get; }
    public ProfilesViewModel Profiles { get; }
    public DashboardViewModel Dashboard { get; }
    public PluginsViewModel Plugins { get; }
    public PromptsViewModel Prompts { get; }
    public AudioRecorderViewModel Recorder { get; }
    public FileTranscriptionViewModel FileTranscription { get; }

    private readonly UpdateService _updateService;
    private readonly IErrorLogService _errorLog;

    [ObservableProperty] private UserControl? _currentSection;
    [ObservableProperty] private SettingsRoute _currentRoute = _lastOpenedRoute;
    [ObservableProperty] private string _currentPageTitle = "";
    [ObservableProperty] private string _currentPageSubtitle = "";
    [ObservableProperty] private SettingsPageMetadata _currentPageMetadata = new(SettingsPageKind.PreferencePage);
    [ObservableProperty] private string _updateStatusText = "";
    [ObservableProperty] private bool _isCheckingForUpdates;
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private int _pendingFileImporterRequestId;

    public string CurrentAppVersion => _updateService.CurrentVersion;
    public double CurrentPageContentWidth => CurrentPageMetadata.ContentWidth;
    public bool CurrentPageShowsSummaryRow => CurrentPageMetadata.ShowsSummaryRow;
    public bool CurrentPageUsesStickyActions => CurrentPageMetadata.UsesStickyActions;
    public ObservableCollection<ErrorLogEntry> ErrorLogEntries { get; } = [];
    public bool HasErrorLogEntries => ErrorLogEntries.Count > 0;
    public ObservableCollection<SettingsNavigationGroup> NavigationGroups { get; } = [];

    private readonly Dictionary<SettingsRoute, Func<UserControl>> _sectionFactories = [];
    private readonly Dictionary<SettingsRoute, UserControl> _sectionCache = [];
    private readonly Dictionary<SettingsRoute, SettingsNavigationItem> _navigationLookup = [];

    public SettingsWindowViewModel(
        SettingsViewModel settings,
        ModelManagerViewModel modelManager,
        HistoryViewModel history,
        DictionaryViewModel dictionary,
        SnippetsViewModel snippets,
        ProfilesViewModel profiles,
        DashboardViewModel dashboard,
        PluginsViewModel plugins,
        PromptsViewModel prompts,
        AudioRecorderViewModel recorder,
        FileTranscriptionViewModel fileTranscription,
        UpdateService updateService,
        IErrorLogService errorLog)
    {
        Settings = settings;
        ModelManager = modelManager;
        History = history;
        Dictionary = dictionary;
        Snippets = snippets;
        Profiles = profiles;
        Dashboard = dashboard;
        Plugins = plugins;
        Prompts = prompts;
        Recorder = recorder;
        FileTranscription = fileTranscription;
        _updateService = updateService;
        _errorLog = errorLog;

        BuildNavigation();
        RefreshErrorLog();
        _errorLog.EntriesChanged += RefreshErrorLog;
        SyncRouteMetadata(CurrentRoute);
        SyncNavigationSelection();
    }

    public string CurrentSectionName => CurrentRoute.ToString();

    [RelayCommand]
    private async Task NavigateToRoute(SettingsRoute route)
    {
        Open(route);
        if (route == SettingsRoute.History)
            await History.LoadAsync();
    }

    [RelayCommand]
    private Task NavigateToItem(SettingsNavigationItem? item)
    {
        if (item is null)
            return Task.CompletedTask;

        return NavigateToRoute(item.Route);
    }

    [RelayCommand]
    private Task OpenHistory() => NavigateToRoute(SettingsRoute.History);

    [RelayCommand]
    private Task OpenIntegrations() => NavigateToRoute(SettingsRoute.Integrations);

    [RelayCommand]
    private Task OpenShortcuts() => NavigateToRoute(SettingsRoute.Shortcuts);

    [RelayCommand]
    private Task OpenSetup() => NavigateToRoute(SettingsRoute.Setup);

    [RelayCommand]
    private void OpenFileImporter()
    {
        PendingFileImporterRequestId++;
        Open(SettingsRoute.FileTranscription);
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        UpdateStatusText = Loc.Instance["Update.Checking"];

        await _updateService.CheckForUpdatesAsync();

        IsCheckingForUpdates = false;
        if (_updateService.IsUpdateAvailable)
        {
            IsUpdateAvailable = true;
            UpdateStatusText = Loc.Instance.GetString("Update.AvailableFormat", _updateService.AvailableVersion ?? "");
        }
        else
        {
            IsUpdateAvailable = false;
            UpdateStatusText = Loc.Instance["Update.UpToDate"];
        }
    }

    [RelayCommand]
    private async Task ApplyUpdateAsync()
    {
        UpdateStatusText = Loc.Instance["Update.Downloading"];
        await _updateService.DownloadAndApplyAsync();
        UpdateStatusText = Loc.Instance["Update.Failed"];
    }

    [RelayCommand]
    private void ClearErrorLog()
    {
        _errorLog.ClearAll();
    }

    [RelayCommand]
    private void ExportDiagnostics()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"typewhisper-diagnostics-{DateTime.Now:yyyy-MM-dd-HHmmss}.json",
            DefaultExt = ".json",
            Filter = "JSON|*.json"
        };

        if (dialog.ShowDialog() == true)
        {
            var json = _errorLog.ExportDiagnostics();
            System.IO.File.WriteAllText(dialog.FileName, json);
        }
    }

    [RelayCommand]
    private void OpenSetupWizard()
    {
        var window = App.Services.GetRequiredService<WelcomeWindow>();
        window.Show();
    }

    public void RegisterSection(SettingsRoute route, Func<UserControl> factory)
    {
        _sectionFactories[route] = factory;
    }

    public void NavigateToDefault()
    {
        Open(_lastOpenedRoute);
    }

    public void Open(SettingsRoute route)
    {
        if (!_sectionFactories.ContainsKey(route))
            return;

        if (!_sectionCache.TryGetValue(route, out var section))
        {
            section = _sectionFactories[route]();
            _sectionCache[route] = section;
        }

        CurrentSection = section;
        CurrentRoute = route;
        _lastOpenedRoute = route;

        if (route is SettingsRoute.Dictation or SettingsRoute.Integrations)
            ModelManager.RefreshPluginAvailability();
    }

    public bool TryConsumePendingFileImporterRequest()
    {
        if (PendingFileImporterRequestId == 0)
            return false;

        PendingFileImporterRequestId = 0;
        return true;
    }

    partial void OnCurrentRouteChanged(SettingsRoute value)
    {
        SyncNavigationSelection();
        SyncRouteMetadata(value);
        OnPropertyChanged(nameof(CurrentSectionName));
    }

    private void RefreshErrorLog()
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            ErrorLogEntries.Clear();
            foreach (var entry in _errorLog.Entries)
                ErrorLogEntries.Add(entry);
            OnPropertyChanged(nameof(HasErrorLogEntries));
        });
    }

    private void BuildNavigation()
    {
        NavigationGroups.Clear();
        _navigationLookup.Clear();

        NavigationGroups.Add(CreateGroup(SettingsGroup.Overview, "Overview",
        [
            new SettingsNavigationItem(SettingsRoute.Dashboard, "Dashboard", "\uE80F"),
            new SettingsNavigationItem(SettingsRoute.Setup, "Setup", "\uE82D")
        ]));

        NavigationGroups.Add(CreateGroup(SettingsGroup.Capture, "Capture",
        [
            new SettingsNavigationItem(SettingsRoute.Dictation, "Dictation", "\uE720"),
            new SettingsNavigationItem(SettingsRoute.Shortcuts, "Shortcuts", "\uE765"),
            new SettingsNavigationItem(SettingsRoute.FileTranscription, "File transcription", "\uE8A5"),
            new SettingsNavigationItem(SettingsRoute.Recorder, "Recorder", "\uE189")
        ]));

        NavigationGroups.Add(CreateGroup(SettingsGroup.Library, "Library",
        [
            new SettingsNavigationItem(SettingsRoute.History, "History", "\uE81C"),
            new SettingsNavigationItem(SettingsRoute.Dictionary, "Dictionary", "\uE8D2"),
            new SettingsNavigationItem(SettingsRoute.Snippets, "Snippets", "\uE8C8"),
            new SettingsNavigationItem(SettingsRoute.Profiles, "Profiles", "\uE77B")
        ]));

        NavigationGroups.Add(CreateGroup(SettingsGroup.AI, "AI",
        [
            new SettingsNavigationItem(SettingsRoute.Prompts, "Prompts", "\uE8FD"),
            new SettingsNavigationItem(SettingsRoute.Integrations, "Integrations", "\uE943")
        ]));

        NavigationGroups.Add(CreateGroup(SettingsGroup.System, "System",
        [
            new SettingsNavigationItem(SettingsRoute.General, "General", "\uE713"),
            new SettingsNavigationItem(SettingsRoute.Appearance, "Appearance", "\uE790"),
            new SettingsNavigationItem(SettingsRoute.Advanced, "Advanced", "\uE9CE"),
            new SettingsNavigationItem(SettingsRoute.License, "License", "\uE72E"),
            new SettingsNavigationItem(SettingsRoute.About, "About", "\uE946")
        ]));
    }

    private SettingsNavigationGroup CreateGroup(SettingsGroup group, string title, IReadOnlyList<SettingsNavigationItem> items)
    {
        foreach (var item in items)
            _navigationLookup[item.Route] = item;

        return new SettingsNavigationGroup(group, title, items);
    }

    private void SyncNavigationSelection()
    {
        foreach (var item in _navigationLookup.Values)
            item.IsSelected = item.Route == CurrentRoute;
    }

    private void SyncRouteMetadata(SettingsRoute route)
    {
        CurrentPageMetadata = route switch
        {
            SettingsRoute.Profiles => new SettingsPageMetadata(SettingsPageKind.GuidedEditorPage, 1180, true, true),
            SettingsRoute.Prompts => new SettingsPageMetadata(SettingsPageKind.CollectionPage, 1040),
            SettingsRoute.Snippets => new SettingsPageMetadata(SettingsPageKind.CollectionPage, 1040),
            SettingsRoute.Integrations => new SettingsPageMetadata(SettingsPageKind.CollectionPage, 1120),
            SettingsRoute.History => new SettingsPageMetadata(SettingsPageKind.CollectionPage, 1100),
            _ => new SettingsPageMetadata(SettingsPageKind.PreferencePage, 980)
        };

        CurrentPageTitle = route switch
        {
            SettingsRoute.Dashboard => "Dashboard",
            SettingsRoute.Setup => "Setup",
            SettingsRoute.Dictation => "Dictation",
            SettingsRoute.Shortcuts => "Shortcuts",
            SettingsRoute.FileTranscription => "File transcription",
            SettingsRoute.Recorder => "Recorder",
            SettingsRoute.History => "History",
            SettingsRoute.Dictionary => "Dictionary",
            SettingsRoute.Snippets => "Snippets",
            SettingsRoute.Profiles => "Profiles",
            SettingsRoute.Prompts => "Prompts",
            SettingsRoute.Integrations => "Integrations",
            SettingsRoute.General => "General",
            SettingsRoute.Appearance => "Appearance",
            SettingsRoute.Advanced => "Advanced",
            SettingsRoute.License => "License",
            SettingsRoute.About => "About",
            _ => "Settings"
        };

        CurrentPageSubtitle = route switch
        {
            SettingsRoute.Dashboard => "Health, activity, and the quickest path back into the app.",
            SettingsRoute.Setup => "A guided handoff for first-run checks, permissions, engines, and shortcuts.",
            SettingsRoute.Dictation => "Engines, capture behavior, microphone controls, and translation defaults.",
            SettingsRoute.Shortcuts => "Keyboard-first control over dictation, hold, toggle, and prompt actions.",
            SettingsRoute.FileTranscription => "Transcribe audio or video files without leaving the settings shell.",
            SettingsRoute.Recorder => "A dedicated recorder for longer takes and one-off captures.",
            SettingsRoute.History => "Review, search, and refine your transcription timeline.",
            SettingsRoute.Dictionary => "Corrections, packs, and reusable term quality improvements.",
            SettingsRoute.Snippets => "Fast reusable insertions and placeholder-driven expansions.",
            SettingsRoute.Profiles => "Context-aware overrides by app, website, and workflow.",
            SettingsRoute.Prompts => "Prompt actions and default LLM routing for post-processing.",
            SettingsRoute.Integrations => "Installed plugins, marketplace updates, and provider setup.",
            SettingsRoute.General => "App-wide language, startup, and service controls.",
            SettingsRoute.Appearance => "Overlay layout, widget composition, and indicator previews.",
            SettingsRoute.Advanced => "Memory, retention, unloading, and diagnostic behavior.",
            SettingsRoute.License => "License status and supporter information.",
            SettingsRoute.About => "Versioning, diagnostics, and project credits.",
            _ => string.Empty
        };
    }

    partial void OnCurrentPageMetadataChanged(SettingsPageMetadata value)
    {
        OnPropertyChanged(nameof(CurrentPageContentWidth));
        OnPropertyChanged(nameof(CurrentPageShowsSummaryRow));
        OnPropertyChanged(nameof(CurrentPageUsesStickyActions));
    }
}
