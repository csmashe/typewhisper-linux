using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Linux.Views;

namespace TypeWhisper.Linux.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly UpdateCheckService _updateCheck;

    [ObservableProperty]
    private object? _currentSection;

    [ObservableProperty]
    private NavItem? _selectedItem;

    [ObservableProperty]
    private string _updateBannerText = string.Empty;

    [ObservableProperty]
    private bool _updateBannerVisible;

    public MainWindowViewModel(
        IServiceProvider services,
        UpdateCheckService updateCheck,
        DashboardSectionViewModel dashboard,
        DictationSectionViewModel dictation,
        ShortcutsSectionViewModel shortcuts,
        TextInsertionSectionViewModel textInsertion,
        FileTranscriptionSectionViewModel fileTranscription,
        RecorderSectionViewModel recorder,
        HistorySectionViewModel history,
        DictionarySectionViewModel dictionary,
        SnippetsSectionViewModel snippets,
        ProfilesSectionViewModel profiles,
        PromptsSectionViewModel prompts,
        PluginsSectionViewModel plugins,
        GeneralSectionViewModel general,
        AppearanceSectionViewModel appearance,
        AdvancedSectionViewModel advanced,
        AboutSectionViewModel about
    )
    {
        _services = services;
        _updateCheck = updateCheck;
        _updateCheck.ResultChanged += OnUpdateResultChanged;
        Loc.Instance.LanguageChanged += (_, _) => RefreshUpdateBannerText();
        ApplyUpdateResult(_updateCheck.LastResult);
        Dashboard = dashboard;
        Dictation = dictation;
        Shortcuts = shortcuts;
        TextInsertion = textInsertion;
        FileTranscription = fileTranscription;
        Recorder = recorder;
        History = history;
        Dictionary = dictionary;
        Snippets = snippets;
        Profiles = profiles;
        Prompts = prompts;
        Plugins = plugins;
        General = general;
        Appearance = appearance;
        Advanced = advanced;
        About = about;

        NavItems =
        [
            new NavItem("Nav.GroupOverview", null, null, true),
            new NavItem("Nav.Dashboard", Symbol.Home, Dashboard, false),
            new NavItem("Nav.GroupCapture", null, null, true),
            new NavItem("Nav.Dictation", Symbol.Mic, Dictation, false),
            new NavItem("Nav.Shortcuts", Symbol.Keyboard, Shortcuts, false),
            new NavItem("Nav.TextInsertion", Symbol.TextAlignLeft, TextInsertion, false),
            new NavItem("Nav.FileTranscription", Symbol.DocumentText, FileTranscription, false),
            new NavItem("Nav.Recorder", Symbol.Record, Recorder, false),
            new NavItem("Nav.GroupLibrary", null, null, true),
            new NavItem("Nav.History", Symbol.History, History, false),
            new NavItem("Nav.Dictionary", Symbol.Book, Dictionary, false),
            new NavItem("Nav.Snippets", Symbol.Cut, Snippets, false),
            new NavItem("Nav.Profiles", Symbol.Person, Profiles, false),
            new NavItem("Nav.GroupAi", null, null, true),
            new NavItem("Nav.Prompts", Symbol.Prompt, Prompts, false),
            new NavItem("Nav.Plugins", Symbol.PlugConnected, Plugins, false),
            new NavItem("Nav.GroupSystem", null, null, true),
            new NavItem("Nav.General", Symbol.Settings, General, false),
            new NavItem("Nav.Appearance", Symbol.Color, Appearance, false),
            new NavItem("Nav.Advanced", Symbol.AppsSettings, Advanced, false),
            new NavItem("Nav.About", Symbol.Info, About, false)
        ];

        SelectedItem = NavItems.First(i => i.Content is DashboardSectionViewModel);
        CurrentSection = SelectedItem.Content;
    }

    // All section VMs stay in memory so nav switches are instantaneous.
    private DashboardSectionViewModel Dashboard { get; }
    private DictationSectionViewModel Dictation { get; }
    private ShortcutsSectionViewModel Shortcuts { get; }
    private TextInsertionSectionViewModel TextInsertion { get; }
    private FileTranscriptionSectionViewModel FileTranscription { get; }
    private RecorderSectionViewModel Recorder { get; }
    private HistorySectionViewModel History { get; }
    private DictionarySectionViewModel Dictionary { get; }
    private SnippetsSectionViewModel Snippets { get; }
    private ProfilesSectionViewModel Profiles { get; }
    private PromptsSectionViewModel Prompts { get; }
    private PluginsSectionViewModel Plugins { get; }
    private GeneralSectionViewModel General { get; }
    private AppearanceSectionViewModel Appearance { get; }
    private AdvancedSectionViewModel Advanced { get; }
    private AboutSectionViewModel About { get; }

    public ObservableCollection<NavItem> NavItems { get; }

    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public string AppTitle => "TypeWhisper";
    public string VersionLabel => About.Version == "dev" ? "dev" : $"v{About.Version}";

    public void Navigate<TSection>()
        where TSection : class
    {
        var target = NavItems.FirstOrDefault(i => i.Content is TSection);
        if (target is not null)
        {
            SelectedItem = target;
        }
    }

    [RelayCommand]
    public void OpenWizard()
    {
        var wizard = _services.GetRequiredService<WelcomeWizard>();
        wizard.DataContext = _services.GetRequiredService<WelcomeWizardViewModel>();

        if (
            Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            wizard.ShowDialog(owner);
        }
        else
        {
            wizard.Show();
        }
    }

    partial void OnSelectedItemChanged(NavItem? value)
    {
        foreach (var item in NavItems)
        {
            item.IsSelected = item == value;
        }

        if (value is { IsHeader: false, Content: not null })
        {
            CurrentSection = value.Content;
        }
    }

    [RelayCommand]
    private void NavigateToItem(NavItem? item)
    {
        if (item is { IsHeader: false })
        {
            SelectedItem = item;
        }
    }

    [RelayCommand]
    private void OpenUpdate()
    {
        // Take the user to About, where the full status and a Download button
        // live. Non-destructive — just navigates.
        Navigate<AboutSectionViewModel>();
    }

    [RelayCommand]
    private void DismissUpdate()
    {
        _updateCheck.DismissUpdate(_updateCheck.LastResult.LatestVersion);
        UpdateBannerVisible = false;
    }

    private void OnUpdateResultChanged(UpdateCheckResult result)
    {
        // The startup check raises this off the UI thread.
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyUpdateResult(result);
        }
        else
        {
            Dispatcher.UIThread.Post(() => ApplyUpdateResult(result));
        }
    }

    private string? _updateVersion;

    private void ApplyUpdateResult(UpdateCheckResult result)
    {
        var show =
            result is { Checked: true, UpdateAvailable: true }
            && !_updateCheck.IsDismissed(result.LatestVersion);

        UpdateBannerVisible = show;
        _updateVersion = show ? result.LatestVersion : null;
        RefreshUpdateBannerText();
    }

    private void RefreshUpdateBannerText()
    {
        UpdateBannerText = _updateVersion is null
            ? string.Empty
            : Loc.Instance.GetString("Update.Available", _updateVersion);
    }
}

public partial class NavItem : ObservableObject
{
    // ReSharper disable once ReplaceWithFieldKeyword -- set in the constructor (where `field` is inaccessible); Label must stay a computed property to re-resolve localization on language change.
    private readonly string _labelKey;

    [ObservableProperty]
    private bool _isSelected;

    public NavItem(string labelKey, Symbol? icon, object? content, bool isHeader)
    {
        _labelKey = labelKey;
        Icon = icon ?? (isHeader ? null : Symbol.Home);
        Content = content;
        IsHeader = isHeader;
        // Refresh the label whenever the interface language changes. NavItems
        // live for the app's lifetime, so this handler is never orphaned.
        Loc.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Label));
    }

    public string Label => Loc.Instance[_labelKey];
    public Symbol? Icon { get; }
    public object? Content { get; }
    public bool IsHeader { get; }
}