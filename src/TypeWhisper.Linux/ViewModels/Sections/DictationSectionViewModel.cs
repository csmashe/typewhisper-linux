using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class DictationSectionViewModel : ObservableObject
{
    private readonly DictationOrchestrator _dictation;
    private readonly ModelManagerService _models;
    private readonly AudioRecordingService _audio;
    private readonly ISettingsService _settings;
    private readonly PluginManager _pluginManager;
    private readonly SystemCommandAvailabilityService _commands;
    private bool _previewAttached;

    public ObservableCollection<DictationModelOption> ModelOptions { get; } = [];
    public ObservableCollection<AudioInputDevice> Devices { get; } = [];
    public ObservableCollection<SpokenLanguageOption> LanguageChoices { get; } =
    [
        new("auto", "Auto detect"),
        new("de", "Deutsch"),
        new("en", "English"),
        new("fr", "Français"),
        new("es", "Español"),
        new("it", "Italiano"),
        new("pt", "Português"),
        new("nl", "Nederlands"),
        new("pl", "Polski"),
        new("cs", "Čeština"),
        new("sv", "Svenska"),
        new("da", "Dansk"),
        new("fi", "Suomi"),
    ];
    public ObservableCollection<TranslationTargetOption> TranslationTargetOptions { get; } = [];

    [ObservableProperty] private string _statusText = "Press your hotkey or click Toggle to start recording.";
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string? _lastCapturePath;
    [ObservableProperty] private string? _lastTranscription;
    [ObservableProperty] private string _activeModelLabel = "No model loaded";
    [ObservableProperty] private string _engineName = "No engine selected";
    [ObservableProperty] private string _modelStatusText = "Not ready";
    [ObservableProperty] private bool _modelReady;
    [ObservableProperty] private DictationModelOption? _selectedModel;
    [ObservableProperty] private AudioInputDevice? _selectedDevice;
    [ObservableProperty] private string _language = "auto";
    [ObservableProperty] private string? _translationTargetLanguage;
    [ObservableProperty] private bool _autoPaste;
    [ObservableProperty] private bool _whisperModeEnabled;
    [ObservableProperty] private bool _soundFeedbackEnabled = true;
    [ObservableProperty] private bool _silenceAutoStopEnabled;
    [ObservableProperty] private int _silenceAutoStopSeconds = 10;
    [ObservableProperty] private bool _audioDuckingEnabled;
    [ObservableProperty] private bool _pauseMediaDuringRecording;
    [ObservableProperty] private double _previewLevel;
    [ObservableProperty] private string _microphoneStatus = "";
    [ObservableProperty] private double _audioDuckingLevel = 0.2;

    public bool CanUseAudioDucking => _commands.HasPactl;
    public bool ShowAudioDuckingUnavailableReason => !CanUseAudioDucking;
    public string AudioDuckingUnavailableReason => "Unavailable: pactl is not installed on this system.";

    public bool CanUseMediaPause => _commands.HasPlayerCtl;
    public bool ShowMediaPauseUnavailableReason => !CanUseMediaPause;
    public string MediaPauseUnavailableReason => "Unavailable: playerctl is not installed on this system.";

    public bool CanUseSoundFeedback => _commands.HasCanberraGtkPlay;
    public bool ShowSoundFeedbackUnavailableReason => !CanUseSoundFeedback;
    public string SoundFeedbackUnavailableReason => "Unavailable: canberra-gtk-play is not installed on this system.";

    public TranslationTargetOption? SelectedTranslationTargetOption
    {
        get => TranslationTargetOptions.FirstOrDefault(option =>
            string.Equals(option.Code, TranslationTargetLanguage, StringComparison.Ordinal));
        set
        {
            var code = value?.Code;
            if (string.Equals(code, TranslationTargetLanguage, StringComparison.Ordinal))
                return;

            TranslationTargetLanguage = code;
            OnPropertyChanged();
        }
    }

    public SpokenLanguageOption? SelectedLanguageOption
    {
        get => LanguageChoices.FirstOrDefault(option =>
            string.Equals(option.Code, Language, StringComparison.Ordinal));
        set
        {
            var code = value?.Code ?? "auto";
            if (string.Equals(code, Language, StringComparison.Ordinal))
                return;

            Language = code;
            OnPropertyChanged();
        }
    }

    public DictationSectionViewModel(
        DictationOrchestrator dictation,
        ModelManagerService models,
        AudioRecordingService audio,
        ISettingsService settings,
        PluginManager pluginManager,
        SystemCommandAvailabilityService commands)
    {
        _dictation = dictation;
        _models = models;
        _audio = audio;
        _settings = settings;
        _pluginManager = pluginManager;
        _commands = commands;

        _dictation.RecordingStateChanged += (_, recording) =>
            Dispatcher.UIThread.Post(() =>
            {
                IsRecording = recording;
                StatusText = recording
                    ? "Recording… press the hotkey again to stop."
                    : "Stopped. Processing…";

                if (recording)
                {
                    _audio.StopPreview();
                    PreviewLevel = 0;
                }
                else if (_previewAttached)
                {
                    ActivatePreview();
                }
            });

        _dictation.RecordingCaptured += (_, path) =>
            Dispatcher.UIThread.Post(() => LastCapturePath = path);

        _dictation.TranscriptionCompleted += (_, text) =>
            Dispatcher.UIThread.Post(() => LastTranscription = text);

        _dictation.StatusMessage += (_, msg) =>
            Dispatcher.UIThread.Post(() => StatusText = msg);

        _audio.LevelChanged += OnLevelChanged;
        _models.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(RefreshModelState);
        _pluginManager.PluginStateChanged += (_, _) => Dispatcher.UIThread.Post(RefreshModels);
        _settings.SettingsChanged += settingsValue => Dispatcher.UIThread.Post(() => RefreshFromSettings(settingsValue));

        foreach (var option in TranslationModelInfo.GlobalTargetOptions)
            TranslationTargetOptions.Add(option);

        RefreshModels();
        RefreshDevices();
        RefreshFromSettings(_settings.Current);
    }

    [RelayCommand]
    private async Task Toggle() => await _dictation.ToggleAsync();

    [RelayCommand]
    private void RefreshDevices()
    {
        Devices.Clear();
        foreach (var d in _audio.GetInputDevices())
            Devices.Add(d);

        SelectedDevice = _audio.ResolveConfiguredDevice(
            _settings.Current.SelectedMicrophoneDevice,
            _settings.Current.SelectedMicrophoneDeviceId);

        MicrophoneStatus = Devices.Count == 0
            ? "No input devices detected."
            : $"{Devices.Count} input device(s) available.";
    }

    public void ActivatePreview()
    {
        _previewAttached = true;
        if (!_audio.StartPreview() && Devices.Count > 0)
            MicrophoneStatus = "Could not start live input preview for the selected microphone.";
    }

    public void DeactivatePreview()
    {
        _previewAttached = false;
        _audio.StopPreview();
        PreviewLevel = 0;
    }

    private void RefreshModels()
    {
        var previousSelectedId = SelectedModel?.ModelId ?? _settings.Current.SelectedModelId;

        ModelOptions.Clear();
        foreach (var engine in _pluginManager.TranscriptionEngines)
        {
            foreach (var model in engine.TranscriptionModels)
            {
                var fullModelId = ModelManagerService.GetPluginModelId(engine.PluginId, model.Id);
                ModelOptions.Add(new DictationModelOption(
                    fullModelId,
                    model.DisplayName,
                    engine.ProviderDisplayName));
            }
        }

        SelectedModel = ModelOptions.FirstOrDefault(option => option.ModelId == previousSelectedId);
        RefreshModelState();
    }

    private void RefreshFromSettings(AppSettings settings)
    {
        Language = string.IsNullOrWhiteSpace(settings.Language) ? "auto" : settings.Language;
        TranslationTargetLanguage = settings.TranslationTargetLanguage;
        AutoPaste = settings.AutoPaste;
        WhisperModeEnabled = settings.WhisperModeEnabled;
        SoundFeedbackEnabled = settings.SoundFeedbackEnabled;
        SilenceAutoStopEnabled = settings.SilenceAutoStopEnabled;
        SilenceAutoStopSeconds = settings.SilenceAutoStopSeconds;
        AudioDuckingEnabled = settings.AudioDuckingEnabled;
        AudioDuckingLevel = settings.AudioDuckingLevel;
        PauseMediaDuringRecording = settings.PauseMediaDuringRecording;

        SelectedDevice = _audio.ResolveConfiguredDevice(
            settings.SelectedMicrophoneDevice,
            settings.SelectedMicrophoneDeviceId);
        SelectedModel = ModelOptions.FirstOrDefault(option => option.ModelId == settings.SelectedModelId);

        OnPropertyChanged(nameof(SelectedLanguageOption));
        OnPropertyChanged(nameof(SelectedTranslationTargetOption));
        RefreshModelState();
    }

    private void RefreshModelState()
    {
        var active = _models.ActiveModelId;
        ActiveModelLabel = string.IsNullOrEmpty(active) ? "No model loaded" : $"Active: {active}";

        var selected = SelectedModel;
        if (selected is null)
        {
            EngineName = "No engine selected";
            ModelStatusText = "Not selected";
            ModelReady = false;
            return;
        }

        EngineName = selected.EngineName;
        var status = _models.GetStatus(selected.ModelId);
        ModelReady = status.Type == ModelStatusType.Ready;
        ModelStatusText = status.Type switch
        {
            ModelStatusType.Ready => "Ready",
            ModelStatusType.Loading => "Loading",
            ModelStatusType.Downloading => $"Downloading {status.Progress:P0}",
            ModelStatusType.Error => status.ErrorMessage ?? "Error",
            _ => "Not ready"
        };
    }

    partial void OnSelectedModelChanged(DictationModelOption? value)
    {
        if (value is null || _settings.Current.SelectedModelId == value.ModelId)
        {
            RefreshModelState();
            return;
        }

        _settings.Save(_settings.Current with { SelectedModelId = value.ModelId });
        RefreshModelState();
    }

    partial void OnSelectedDeviceChanged(AudioInputDevice? value)
    {
        if (value is null)
            return;

        _audio.SelectedDeviceIndex = value.Index;
        _settings.Save(_settings.Current with
        {
            SelectedMicrophoneDevice = value.Index,
            SelectedMicrophoneDeviceId = value.PersistentId
        });
    }

    partial void OnLanguageChanged(string value)
    {
        _settings.Save(_settings.Current with { Language = value });
        OnPropertyChanged(nameof(SelectedLanguageOption));
    }

    partial void OnTranslationTargetLanguageChanged(string? value)
    {
        _settings.Save(_settings.Current with { TranslationTargetLanguage = value });
        OnPropertyChanged(nameof(SelectedTranslationTargetOption));
    }

    partial void OnAutoPasteChanged(bool value)
        => _settings.Save(_settings.Current with { AutoPaste = value });

    partial void OnWhisperModeEnabledChanged(bool value)
        => _settings.Save(_settings.Current with { WhisperModeEnabled = value });

    partial void OnSoundFeedbackEnabledChanged(bool value)
        => _settings.Save(_settings.Current with { SoundFeedbackEnabled = value });

    partial void OnSilenceAutoStopEnabledChanged(bool value)
        => _settings.Save(_settings.Current with { SilenceAutoStopEnabled = value });

    partial void OnSilenceAutoStopSecondsChanged(int value)
    {
        if (value <= 0)
            return;

        _settings.Save(_settings.Current with { SilenceAutoStopSeconds = value });
    }

    partial void OnAudioDuckingEnabledChanged(bool value)
        => _settings.Save(_settings.Current with { AudioDuckingEnabled = value });

    partial void OnAudioDuckingLevelChanged(double value)
        => _settings.Save(_settings.Current with { AudioDuckingLevel = (float)Math.Clamp(value, 0d, 0.5d) });

    partial void OnPauseMediaDuringRecordingChanged(bool value)
        => _settings.Save(_settings.Current with { PauseMediaDuringRecording = value });

    private void OnLevelChanged(object? sender, float level)
    {
        Dispatcher.UIThread.Post(() =>
        {
            PreviewLevel = Math.Clamp(level * 8, 0, 1);
        });
    }
}

public sealed record DictationModelOption(
    string ModelId,
    string DisplayName,
    string EngineName)
{
    public string DisplayLabel => $"{EngineName} / {DisplayName}";
}

public sealed record SpokenLanguageOption(
    string Code,
    string DisplayName);
