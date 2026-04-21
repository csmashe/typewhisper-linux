using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Linux.Services.Plugins;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class PluginsSectionViewModel : ObservableObject
{
    private readonly PluginManager _pluginManager;

    public ObservableCollection<PluginRow> Plugins { get; } = [];

    public PluginsSectionViewModel(PluginManager pluginManager)
    {
        _pluginManager = pluginManager;
        _pluginManager.PluginStateChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    private void Refresh()
    {
        Plugins.Clear();
        foreach (var p in _pluginManager.AllPlugins)
        {
            Plugins.Add(new PluginRow(
                Id: p.Manifest.Id,
                Name: p.Manifest.Name,
                Version: p.Manifest.Version,
                Author: p.Manifest.Author ?? "",
                Description: p.Manifest.Description ?? "",
                IsEnabled: _pluginManager.IsEnabled(p.Manifest.Id)));
        }
    }

    [RelayCommand]
    private async Task ToggleEnabled(PluginRow row)
    {
        if (row.IsEnabled)
            await _pluginManager.DisablePluginAsync(row.Id);
        else
            await _pluginManager.EnablePluginAsync(row.Id);
    }
}

public sealed record PluginRow(
    string Id,
    string Name,
    string Version,
    string Author,
    string Description,
    bool IsEnabled);
