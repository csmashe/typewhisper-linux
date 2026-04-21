using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels.Sections;

namespace TypeWhisper.Linux.ViewModels;

/// <summary>
/// Onboarding wizard — mirrors the Windows WelcomeWindow flow:
///   1. Pick a transcription model (with recommended default).
///   2. Show available extension plugins and their enable state.
///   3. Confirm hotkey + microphone.
///   4. Done — sets HasCompletedOnboarding.
/// </summary>
public partial class WelcomeWizardViewModel : ObservableObject
{
    private readonly ModelManagerService _models;
    private readonly PluginManager _pluginManager;
    private readonly HotkeyService _hotkey;
    private readonly AudioRecordingService _audio;
    private readonly ISettingsService _settings;

    public ObservableCollection<WizardModelRow> AvailableModels { get; } = [];
    public ObservableCollection<PluginRow> ExtensionPlugins { get; } = [];

    [ObservableProperty] private int _stepIndex;
    [ObservableProperty] private WizardModelRow? _selectedModel;
    [ObservableProperty] private string _modelStatus = "";
    [ObservableProperty] private string _hotkeyText = "";
    [ObservableProperty] private AudioInputDevice? _selectedMic;
    public ObservableCollection<AudioInputDevice> Mics { get; } = [];

    public int StepCount => 4;
    public bool IsFirstStep => StepIndex == 0;
    public bool IsLastStep => StepIndex == StepCount - 1;
    public string NextLabel => IsLastStep ? "Finish" : "Next";

    // Events consumed by the view to close itself.
    public event EventHandler? RequestClose;

    public WelcomeWizardViewModel(
        ModelManagerService models,
        PluginManager pluginManager,
        HotkeyService hotkey,
        AudioRecordingService audio,
        ISettingsService settings)
    {
        _models = models;
        _pluginManager = pluginManager;
        _hotkey = hotkey;
        _audio = audio;
        _settings = settings;

        _pluginManager.PluginStateChanged += (_, _) => Dispatcher.UIThread.Post(RefreshPluginState);
        _models.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(RefreshModelState);

        LoadModels();
        LoadExtensions();
        LoadMics();

        HotkeyText = _hotkey.CurrentHotkeyString;
    }

    private void LoadModels()
    {
        AvailableModels.Clear();
        foreach (var engine in _pluginManager.TranscriptionEngines)
        {
            foreach (var model in engine.TranscriptionModels)
            {
                var modelId = ModelManagerService.GetPluginModelId(engine.PluginId, model.Id);
                var downloaded = engine.SupportsModelDownload
                    ? engine.IsModelDownloaded(model.Id)
                    : engine.IsConfigured;
                AvailableModels.Add(new WizardModelRow(
                    ModelId: modelId,
                    DisplayName: $"{engine.ProviderDisplayName} — {model.DisplayName}",
                    SizeDescription: model.SizeDescription ?? "",
                    IsDownloaded: downloaded,
                    IsRecommended: model.IsRecommended));
            }
        }
        SelectedModel = AvailableModels.FirstOrDefault(m => m.IsRecommended) ?? AvailableModels.FirstOrDefault();
    }

    private void LoadExtensions()
    {
        ExtensionPlugins.Clear();
        foreach (var p in _pluginManager.AllPlugins)
        {
            ExtensionPlugins.Add(new PluginRow(
                Id: p.Manifest.Id,
                Name: p.Manifest.Name,
                Version: p.Manifest.Version,
                Author: p.Manifest.Author ?? "",
                Description: p.Manifest.Description ?? "",
                IsEnabled: _pluginManager.IsEnabled(p.Manifest.Id)));
        }
    }

    private void LoadMics()
    {
        Mics.Clear();
        foreach (var d in _audio.GetInputDevices())
            Mics.Add(d);
        SelectedMic = _audio.ResolveConfiguredDevice(
            _settings.Current.SelectedMicrophoneDevice,
            _settings.Current.SelectedMicrophoneDeviceId);
    }

    private void RefreshPluginState()
    {
        for (var i = 0; i < ExtensionPlugins.Count; i++)
        {
            var existing = ExtensionPlugins[i];
            var isEnabled = _pluginManager.IsEnabled(existing.Id);
            if (isEnabled != existing.IsEnabled)
                ExtensionPlugins[i] = existing with { IsEnabled = isEnabled };
        }
    }

    private void RefreshModelState()
    {
        for (var i = 0; i < AvailableModels.Count; i++)
        {
            var existing = AvailableModels[i];
            var (pluginId, rawModelId) = ModelManagerService.ParsePluginModelId(existing.ModelId);
            var engine = _pluginManager.TranscriptionEngines.FirstOrDefault(e => e.PluginId == pluginId);
            if (engine is null) continue;
            var downloaded = engine.SupportsModelDownload
                ? engine.IsModelDownloaded(rawModelId)
                : engine.IsConfigured;
            if (downloaded != existing.IsDownloaded)
                AvailableModels[i] = existing with { IsDownloaded = downloaded };
        }
    }

    partial void OnStepIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsFirstStep));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(NextLabel));
    }

    [RelayCommand]
    private void Back()
    {
        if (StepIndex > 0) StepIndex--;
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        // Step 0: pick model — download if needed before advancing
        if (StepIndex == 0 && SelectedModel is { IsDownloaded: false } row)
        {
            ModelStatus = $"Downloading {row.DisplayName}…";
            try
            {
                await _models.DownloadAndLoadModelAsync(row.ModelId);
                _settings.Save(_settings.Current with { SelectedModelId = row.ModelId });
                ModelStatus = "Done.";
            }
            catch (Exception ex)
            {
                ModelStatus = $"Failed: {ex.Message}";
                return;
            }
        }
        else if (StepIndex == 0 && SelectedModel is { IsDownloaded: true } ready)
        {
            try { await _models.LoadModelAsync(ready.ModelId); } catch { /* ignore, section shows status */ }
            _settings.Save(_settings.Current with { SelectedModelId = ready.ModelId });
        }

        // Step 2: save hotkey + mic
        if (StepIndex == 2)
        {
            if (_hotkey.TrySetHotkeyFromString(HotkeyText))
                _settings.Save(_settings.Current with { ToggleHotkey = _hotkey.CurrentHotkeyString });
            if (SelectedMic is not null)
            {
                _audio.SelectedDeviceIndex = SelectedMic.Index;
                _settings.Save(_settings.Current with
                {
                    SelectedMicrophoneDevice = SelectedMic.Index,
                    SelectedMicrophoneDeviceId = SelectedMic.PersistentId
                });
            }
        }

        if (IsLastStep)
        {
            _settings.Save(_settings.Current with { HasCompletedOnboarding = true });
            RequestClose?.Invoke(this, EventArgs.Empty);
            return;
        }

        StepIndex++;
    }

    [RelayCommand]
    private void Skip()
    {
        _settings.Save(_settings.Current with { HasCompletedOnboarding = true });
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task TogglePluginEnabledAsync(PluginRow row)
    {
        if (row.IsEnabled)
            await _pluginManager.DisablePluginAsync(row.Id);
        else
            await _pluginManager.EnablePluginAsync(row.Id);
    }
}

public sealed record WizardModelRow(
    string ModelId,
    string DisplayName,
    string SizeDescription,
    bool IsDownloaded,
    bool IsRecommended);
