using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core;
using TypeWhisper.Linux.Services.Plugins;

namespace TypeWhisper.Linux.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly PluginLoader _pluginLoader;

    [ObservableProperty]
    private string _statusText = "Ready. Click Load Plugins to scan the plugin directory.";

    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];

    public string PluginsPath => TypeWhisperEnvironment.PluginsPath;

    public MainWindowViewModel(PluginLoader pluginLoader)
    {
        _pluginLoader = pluginLoader;
    }

    [RelayCommand]
    private void LoadPlugins()
    {
        Plugins.Clear();
        var loaded = _pluginLoader.DiscoverAndLoad([TypeWhisperEnvironment.PluginsPath]);

        foreach (var p in loaded)
        {
            Plugins.Add(new PluginItemViewModel(
                Id: p.Manifest.Id,
                Name: p.Manifest.Name,
                Version: p.Manifest.Version,
                Path: p.PluginDirectory));
        }

        StatusText = loaded.Count == 0
            ? $"No plugins found in {TypeWhisperEnvironment.PluginsPath}."
            : $"Loaded {loaded.Count} plugin(s).";
    }
}

public sealed record PluginItemViewModel(string Id, string Name, string Version, string Path);
