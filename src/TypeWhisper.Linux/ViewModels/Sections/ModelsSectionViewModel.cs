using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class ModelsSectionViewModel : ObservableObject
{
    private readonly ModelManagerService _models;
    private readonly PluginManager _pluginManager;

    public ObservableCollection<ModelRow> Models { get; } = [];

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _activeModelLabel = "No model loaded";

    public ModelsSectionViewModel(ModelManagerService models, PluginManager pluginManager)
    {
        _models = models;
        _pluginManager = pluginManager;
        _pluginManager.PluginStateChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        _models.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(RefreshActive);
        Refresh();
    }

    private void Refresh()
    {
        Models.Clear();
        foreach (var engine in _pluginManager.TranscriptionEngines)
        {
            foreach (var model in engine.TranscriptionModels)
            {
                var modelId = ModelManagerService.GetPluginModelId(engine.PluginId, model.Id);
                var downloaded = engine.SupportsModelDownload
                    ? engine.IsModelDownloaded(model.Id)
                    : engine.IsConfigured;
                Models.Add(new ModelRow(
                    ModelId: modelId,
                    DisplayName: $"{engine.ProviderDisplayName} — {model.DisplayName}",
                    SizeDescription: model.SizeDescription ?? "",
                    IsDownloaded: downloaded,
                    SupportsDownload: engine.SupportsModelDownload));
            }
        }
        RefreshActive();
    }

    private void RefreshActive()
    {
        var active = _models.ActiveModelId;
        ActiveModelLabel = string.IsNullOrEmpty(active) ? "No model loaded" : $"Active: {active}";
    }

    [RelayCommand]
    private async Task LoadModel(ModelRow row)
    {
        try
        {
            StatusText = $"Loading {row.DisplayName}…";
            await _models.LoadModelAsync(row.ModelId);
            StatusText = $"Loaded {row.DisplayName}.";
        }
        catch (Exception ex) { StatusText = $"Load failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task DownloadModel(ModelRow row)
    {
        try
        {
            StatusText = $"Downloading {row.DisplayName}…";
            await _models.DownloadAndLoadModelAsync(row.ModelId);
            StatusText = $"Ready: {row.DisplayName}.";
            Refresh();
        }
        catch (Exception ex) { StatusText = $"Download failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void UnloadModel()
    {
        _models.UnloadModel();
        StatusText = "Unloaded.";
    }
}

public sealed record ModelRow(
    string ModelId,
    string DisplayName,
    string SizeDescription,
    bool IsDownloaded,
    bool SupportsDownload);
