using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.ViewModels;

public partial class SettingsWindowViewModel : ObservableObject
{
    public GeneralSectionViewModel General { get; }
    public ShortcutsSectionViewModel Shortcuts { get; }
    public AudioSectionViewModel Audio { get; }
    public ModelsSectionViewModel Models { get; }
    public PluginsSectionViewModel Plugins { get; }
    public HistorySectionViewModel History { get; }
    public DictionarySectionViewModel Dictionary { get; }
    public SnippetsSectionViewModel Snippets { get; }
    public ProfilesSectionViewModel Profiles { get; }
    public PromptsSectionViewModel Prompts { get; }

    public SettingsWindowViewModel(
        GeneralSectionViewModel general,
        ShortcutsSectionViewModel shortcuts,
        AudioSectionViewModel audio,
        ModelsSectionViewModel models,
        PluginsSectionViewModel plugins,
        HistorySectionViewModel history,
        DictionarySectionViewModel dictionary,
        SnippetsSectionViewModel snippets,
        ProfilesSectionViewModel profiles,
        PromptsSectionViewModel prompts)
    {
        General = general;
        Shortcuts = shortcuts;
        Audio = audio;
        Models = models;
        Plugins = plugins;
        History = history;
        Dictionary = dictionary;
        Snippets = snippets;
        Profiles = profiles;
        Prompts = prompts;
    }
}
