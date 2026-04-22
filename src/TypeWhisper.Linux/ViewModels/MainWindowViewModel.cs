using System.Collections.ObjectModel;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Linux.Views;

namespace TypeWhisper.Linux.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IServiceProvider _services;

    // All section VMs stay in memory so nav switches are instantaneous.
    public DashboardSectionViewModel Dashboard { get; }
    public DictationSectionViewModel Dictation { get; }
    public ShortcutsSectionViewModel Shortcuts { get; }
    public FileTranscriptionSectionViewModel FileTranscription { get; }
    public RecorderSectionViewModel Recorder { get; }
    public HistorySectionViewModel History { get; }
    public DictionarySectionViewModel Dictionary { get; }
    public SnippetsSectionViewModel Snippets { get; }
    public ProfilesSectionViewModel Profiles { get; }
    public PromptsSectionViewModel Prompts { get; }
    public PluginsSectionViewModel Plugins { get; }
    public GeneralSectionViewModel General { get; }
    public AboutSectionViewModel About { get; }

    public ObservableCollection<NavItem> NavItems { get; }

    [ObservableProperty] private NavItem? _selectedItem;
    [ObservableProperty] private object? _currentSection;

    public string AppTitle => "TypeWhisper";
    public string VersionLabel => About.Version == "dev" ? "dev" : $"v{About.Version}";

    public MainWindowViewModel(
        IServiceProvider services,
        DashboardSectionViewModel dashboard,
        DictationSectionViewModel dictation,
        ShortcutsSectionViewModel shortcuts,
        FileTranscriptionSectionViewModel fileTranscription,
        RecorderSectionViewModel recorder,
        HistorySectionViewModel history,
        DictionarySectionViewModel dictionary,
        SnippetsSectionViewModel snippets,
        ProfilesSectionViewModel profiles,
        PromptsSectionViewModel prompts,
        PluginsSectionViewModel plugins,
        GeneralSectionViewModel general,
        AboutSectionViewModel about)
    {
        _services = services;
        Dashboard = dashboard;
        Dictation = dictation;
        Shortcuts = shortcuts;
        FileTranscription = fileTranscription;
        Recorder = recorder;
        History = history;
        Dictionary = dictionary;
        Snippets = snippets;
        Profiles = profiles;
        Prompts = prompts;
        Plugins = plugins;
        General = general;
        About = about;

        NavItems =
        [
            new NavItem("Overview", null, null, true),
            new NavItem("Dashboard", "\U0001F3E0", Dashboard, false),
            new NavItem("Capture", null, null, true),
            new NavItem("Dictation", "\U0001F3A4", Dictation, false),
            new NavItem("Shortcuts", "⌨️", Shortcuts, false),
            new NavItem("File transcription", "\U0001F4DD", FileTranscription, false),
            new NavItem("Recorder", "\U0001F3B5", Recorder, false),
            new NavItem("Library", null, null, true),
            new NavItem("History", "\U0001F4DC", History, false),
            new NavItem("Dictionary", "\U0001F4D6", Dictionary, false),
            new NavItem("Snippets", "✂️", Snippets, false),
            new NavItem("Profiles", "\U0001F464", Profiles, false),
            new NavItem("AI", null, null, true),
            new NavItem("Prompts", "✨", Prompts, false),
            new NavItem("Plugins", "\U0001F50C", Plugins, false),
            new NavItem("System", null, null, true),
            new NavItem("General", "⚙️", General, false),
            new NavItem("About", "ℹ️", About, false),
        ];

        SelectedItem = NavItems.First(i => i.Content is DashboardSectionViewModel);
        CurrentSection = SelectedItem.Content;
    }

    partial void OnSelectedItemChanged(NavItem? value)
    {
        foreach (var item in NavItems)
            item.IsSelected = item == value;

        if (value is { IsHeader: false, Content: not null })
            CurrentSection = value.Content;
    }

    [RelayCommand]
    private void NavigateToItem(NavItem? item)
    {
        if (item is { IsHeader: false })
            SelectedItem = item;
    }

    public void Navigate<TSection>() where TSection : class
    {
        var target = NavItems.FirstOrDefault(i => i.Content is TSection);
        if (target is not null) SelectedItem = target;
    }

    [RelayCommand]
    public void OpenWizard()
    {
        var wizard = _services.GetRequiredService<WelcomeWizard>();
        wizard.DataContext = _services.GetRequiredService<WelcomeWizardViewModel>();

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } owner)
        {
            wizard.ShowDialog(owner);
        }
        else
        {
            wizard.Show();
        }
    }
}

public partial class NavItem : ObservableObject
{
    public string Label { get; }
    public string? Icon { get; }
    public object? Content { get; }
    public bool IsHeader { get; }

    [ObservableProperty] private bool _isSelected;

    public NavItem(string label, string? icon, object? content, bool isHeader)
    {
        Label = label;
        Icon = icon;
        Content = content;
        IsHeader = isHeader;
    }
}
