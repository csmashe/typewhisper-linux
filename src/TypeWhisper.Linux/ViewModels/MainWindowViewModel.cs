using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;

namespace TypeWhisper.Linux.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly PluginLoader _pluginLoader;
    private readonly PluginManager _pluginManager;
    private readonly ModelManagerService _models;
    private readonly DictationOrchestrator _dictation;

    [ObservableProperty]
    private string _statusText = "Ready. Press Ctrl+Shift+Space (or click Toggle Recording) to capture audio.";

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private string? _lastCapturePath;

    [ObservableProperty]
    private string? _lastTranscription;

    [ObservableProperty]
    private string _activeModelLabel = "No model loaded";

    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];
    public ObservableCollection<TranscriptionModelItemViewModel> Models { get; } = [];

    public string PluginsPath => TypeWhisperEnvironment.PluginsPath;

    public MainWindowViewModel(
        PluginLoader pluginLoader,
        PluginManager pluginManager,
        ModelManagerService models,
        DictationOrchestrator dictation)
    {
        _pluginLoader = pluginLoader;
        _pluginManager = pluginManager;
        _models = models;
        _dictation = dictation;

        _dictation.RecordingStateChanged += (_, recording) =>
            Dispatcher.UIThread.Post(() =>
            {
                IsRecording = recording;
                StatusText = recording
                    ? "Recording… press the hotkey again to stop."
                    : "Stopped. Processing…";
            });

        _dictation.RecordingCaptured += (_, path) =>
            Dispatcher.UIThread.Post(() => LastCapturePath = path);

        _dictation.TranscriptionCompleted += (_, text) =>
            Dispatcher.UIThread.Post(() => LastTranscription = text);

        _dictation.StatusMessage += (_, msg) =>
            Dispatcher.UIThread.Post(() => StatusText = msg);

        _models.PropertyChanged += (_, _) =>
            Dispatcher.UIThread.Post(RefreshActiveModel);

        _pluginManager.PluginStateChanged += (_, _) =>
            Dispatcher.UIThread.Post(() =>
            {
                RefreshModels();
                RefreshActiveModel();
            });

        // Populate from current state.
        RefreshPlugins();
        RefreshModels();
        RefreshActiveModel();
    }

    [RelayCommand]
    private async Task ToggleRecording() => await _dictation.ToggleAsync();

    [RelayCommand]
    private async Task LoadModel(TranscriptionModelItemViewModel item)
    {
        try
        {
            StatusText = $"Loading {item.DisplayName}…";
            await _models.LoadModelAsync(item.ModelId);
            StatusText = $"Loaded {item.DisplayName}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DownloadModel(TranscriptionModelItemViewModel item)
    {
        try
        {
            StatusText = $"Downloading {item.DisplayName} (this may take a while)…";
            await _models.DownloadAndLoadModelAsync(item.ModelId);
            StatusText = $"Ready: {item.DisplayName}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Download failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void LoadPlugins()
    {
        RefreshPlugins();
        StatusText = Plugins.Count == 0
            ? $"No plugins found in {TypeWhisperEnvironment.PluginsPath}."
            : $"Loaded {Plugins.Count} plugin(s).";
    }

    private void RefreshPlugins()
    {
        Plugins.Clear();
        foreach (var p in _pluginManager.AllPlugins)
        {
            Plugins.Add(new PluginItemViewModel(
                Id: p.Manifest.Id,
                Name: p.Manifest.Name,
                Version: p.Manifest.Version,
                Path: p.PluginDirectory,
                IsEnabled: _pluginManager.IsEnabled(p.Manifest.Id)));
        }
    }

    private void RefreshModels()
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

                Models.Add(new TranscriptionModelItemViewModel(
                    ModelId: modelId,
                    DisplayName: $"{engine.ProviderDisplayName} — {model.DisplayName}",
                    SizeDescription: model.SizeDescription ?? "",
                    IsDownloaded: downloaded,
                    SupportsDownload: engine.SupportsModelDownload));
            }
        }
    }

    private void RefreshActiveModel()
    {
        var active = _models.ActiveModelId;
        ActiveModelLabel = string.IsNullOrEmpty(active)
            ? "No model loaded"
            : $"Active: {active}";
    }
}

public sealed record PluginItemViewModel(
    string Id,
    string Name,
    string Version,
    string Path,
    bool IsEnabled);

public sealed record TranscriptionModelItemViewModel(
    string ModelId,
    string DisplayName,
    string SizeDescription,
    bool IsDownloaded,
    bool SupportsDownload);
