using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.Windows.ViewModels;

public record WelcomeModelItem(string FullModelId, string DisplayName, string? SizeDescription, bool IsRecommended);

public partial class WelcomeViewModel : ObservableObject
{
    private readonly ModelManagerService _modelManager;
    private readonly ISettingsService _settings;
    private readonly AudioRecordingService _audio;
    private readonly PluginRegistryService _registry;

    [ObservableProperty] private int _currentStep; // 0=Extensions+Model, 1=Mic, 2=Hotkey, 3=Done
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _downloadStatus = "";
    [ObservableProperty] private string? _selectedModelId;
    [ObservableProperty] private float _micLevel;
    [ObservableProperty] private bool _micWorking;
    [ObservableProperty] private string _toggleHotkey;
    [ObservableProperty] private string _pushToTalkHotkey;
    [ObservableProperty] private bool _isLoadingPlugins;

    public ObservableCollection<RegistryPluginItemViewModel> Plugins { get; } = [];
    public ObservableCollection<WelcomeModelItem> AvailableModels { get; } = [];
    public ObservableCollection<MicrophoneItem> Microphones { get; } = [];
    public event EventHandler? Completed;

    public WelcomeViewModel(
        ModelManagerService modelManager,
        ISettingsService settings,
        AudioRecordingService audio,
        PluginRegistryService registry)
    {
        _modelManager = modelManager;
        _settings = settings;
        _audio = audio;
        _registry = registry;

        _toggleHotkey = settings.Current.ToggleHotkey;
        _pushToTalkHotkey = settings.Current.PushToTalkHotkey;

        _modelManager.PluginManager.PluginStateChanged += (_, _) =>
            Application.Current?.Dispatcher.Invoke(RefreshModels);

        RefreshMicrophones();
        _ = LoadPluginsAsync();
    }

    private async Task LoadPluginsAsync()
    {
        IsLoadingPlugins = true;

        try
        {
            var registryPlugins = await _registry.FetchRegistryAsync();

            Application.Current?.Dispatcher.Invoke(() =>
            {
                Plugins.Clear();
                foreach (var rp in registryPlugins)
                    Plugins.Add(new RegistryPluginItemViewModel(rp, _registry));
            });
        }
        finally
        {
            IsLoadingPlugins = false;
        }

        RefreshModels();
    }

    private void RefreshModels()
    {
        AvailableModels.Clear();

        foreach (var engine in _modelManager.PluginManager.TranscriptionEngines)
        {
            foreach (var model in engine.TranscriptionModels)
            {
                var fullId = ModelManagerService.GetPluginModelId(engine.PluginId, model.Id);
                var name = $"{model.DisplayName} ({model.SizeDescription})";
                if (model.IsRecommended)
                    name += $" — {Loc.Instance["Welcome.Recommended"]}";
                AvailableModels.Add(new WelcomeModelItem(fullId, name, model.SizeDescription, model.IsRecommended));
            }
        }

        // Auto-select recommended or first
        if (SelectedModelId is null || !AvailableModels.Any(m => m.FullModelId == SelectedModelId))
        {
            SelectedModelId = AvailableModels.FirstOrDefault(m => m.IsRecommended)?.FullModelId
                              ?? AvailableModels.FirstOrDefault()?.FullModelId;
        }
    }

    [RelayCommand]
    private async Task DownloadModel()
    {
        if (string.IsNullOrEmpty(SelectedModelId)) return;

        IsDownloading = true;
        DownloadStatus = Loc.Instance["Welcome.DownloadProgress"];

        _modelManager.PropertyChanged += OnModelManagerPropertyChanged;

        try
        {
            await _modelManager.DownloadAndLoadModelAsync(SelectedModelId);
            DownloadStatus = Loc.Instance["Welcome.Done"];
            _settings.Save(_settings.Current with { SelectedModelId = SelectedModelId });
        }
        catch (Exception ex)
        {
            DownloadStatus = Loc.Instance.GetString("Status.ErrorFormat", ex.Message);
            return;
        }
        finally
        {
            IsDownloading = false;
            _modelManager.PropertyChanged -= OnModelManagerPropertyChanged;
        }
    }

    private void OnModelManagerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ModelManagerService.GetStatus) || SelectedModelId is null)
            return;

        var status = _modelManager.GetStatus(SelectedModelId);
        if (status.Type == ModelStatusType.Downloading)
        {
            DownloadProgress = status.Progress;
            DownloadStatus = $"Download: {status.Progress:P0}";
        }
        else if (status.Type == ModelStatusType.Loading)
        {
            DownloadStatus = Loc.Instance["Welcome.LoadingModel"];
        }
    }

    [RelayCommand]
    private void NextStep()
    {
        if (CurrentStep < 3)
            CurrentStep++;

        if (CurrentStep == 1)
            StartMicTest();
        else if (CurrentStep == 3)
            Finish();
    }

    [RelayCommand]
    private void PreviousStep()
    {
        if (CurrentStep > 0)
        {
            if (CurrentStep == 1)
                StopMicTest();
            CurrentStep--;
        }
    }

    [RelayCommand]
    private void Skip()
    {
        Finish();
    }

    private void StartMicTest()
    {
        _audio.AudioLevelChanged += OnMicLevel;
        if (!_audio.HasDevice) return;
        _audio.WarmUp();
        _audio.StartRecording();
    }

    private void StopMicTest()
    {
        _audio.AudioLevelChanged -= OnMicLevel;
        _audio.StopRecording();
    }

    private void OnMicLevel(object? sender, AudioLevelEventArgs e)
    {
        MicLevel = e.RmsLevel;
        if (e.RmsLevel > 0.01f)
            MicWorking = true;
    }

    private void RefreshMicrophones()
    {
        Microphones.Clear();
        Microphones.Add(new MicrophoneItem(null, Loc.Instance["Microphone.Default"]));
        foreach (var (number, name) in AudioRecordingService.GetAvailableDevices())
            Microphones.Add(new MicrophoneItem(number, name));
    }

    private void Finish()
    {
        StopMicTest();

        _settings.Save(_settings.Current with
        {
            ToggleHotkey = ToggleHotkey,
            PushToTalkHotkey = PushToTalkHotkey
        });

        Completed?.Invoke(this, EventArgs.Empty);
    }
}
