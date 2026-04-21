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

    public ObservableCollection<Profile> Profiles { get; } = [];
    public ObservableCollection<ProfileModelOption> ModelOptions { get; } = [];

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newProcessNames = "";
    [ObservableProperty] private string _newUrlPatterns = "";
    [ObservableProperty] private string _newInputLanguage = "";
    [ObservableProperty] private string _newSelectedTask = "";
    [ObservableProperty] private string _newModelId = "";

    public IReadOnlyList<string> LanguageChoices { get; } =
        ["", "auto", "en", "de", "fr", "es", "pt", "ja", "zh", "ko", "it", "nl", "pl", "ru"];

    public IReadOnlyList<string> TaskChoices { get; } =
        ["", "transcribe", "translate"];

    public ProfilesSectionViewModel(IProfileService profiles, PluginManager pluginManager)
    {
        _profiles = profiles;
        _pluginManager = pluginManager;
        _profiles.ProfilesChanged += () => Dispatcher.UIThread.Post(Refresh);
        _pluginManager.PluginStateChanged += (_, _) => Dispatcher.UIThread.Post(RefreshModelOptions);
        RefreshModelOptions();
        Refresh();
    }

    private void Refresh()
    {
        Profiles.Clear();
        foreach (var p in _profiles.Profiles.OrderBy(p => p.Priority).ThenBy(p => p.Name))
            Profiles.Add(p);
    }

    private void RefreshModelOptions()
    {
        ModelOptions.Clear();
        ModelOptions.Add(new ProfileModelOption("", "Use global default"));

        foreach (var engine in _pluginManager.TranscriptionEngines)
        {
            foreach (var model in engine.TranscriptionModels)
            {
                var modelId = ModelManagerService.GetPluginModelId(engine.PluginId, model.Id);
                var label = $"{engine.ProviderDisplayName} — {model.DisplayName}";
                ModelOptions.Add(new ProfileModelOption(modelId, label));
            }
        }

        if (!ModelOptions.Any(option => option.Value == NewModelId))
            NewModelId = "";
    }

    [RelayCommand]
    private void AddProfile()
    {
        if (string.IsNullOrWhiteSpace(NewName)) return;
        var procs = NewProcessNames
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        var urls = NewUrlPatterns
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        _profiles.AddProfile(new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = NewName.Trim(),
            IsEnabled = true,
            Priority = 0,
            ProcessNames = procs,
            UrlPatterns = urls,
            InputLanguage = string.IsNullOrWhiteSpace(NewInputLanguage) ? null : NewInputLanguage,
            SelectedTask = string.IsNullOrWhiteSpace(NewSelectedTask) ? null : NewSelectedTask,
            TranscriptionModelOverride = string.IsNullOrWhiteSpace(NewModelId) ? null : NewModelId,
        });
        NewName = "";
        NewProcessNames = "";
        NewUrlPatterns = "";
        NewInputLanguage = "";
        NewSelectedTask = "";
        NewModelId = "";
    }

    [RelayCommand]
    private void Delete(Profile p) => _profiles.DeleteProfile(p.Id);

    [RelayCommand]
    private void ToggleEnabled(Profile p) => _profiles.UpdateProfile(p with { IsEnabled = !p.IsEnabled });
}

public sealed record ProfileModelOption(string Value, string Label);
