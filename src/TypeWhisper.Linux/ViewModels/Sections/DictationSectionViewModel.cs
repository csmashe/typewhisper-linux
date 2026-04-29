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
    private CancellationTokenSource? _modelSelectionCts;

    public ObservableCollection<DictationModelOption> ModelOptions { get; } = [];
    public ObservableCollection<AudioInputDevice> Devices { get; } = [];
    public ObservableCollection<ComputeBackendOption> ComputeBackendOptions { get; } =
    [
        new("cpu", "CPU"),
        new("cuda", "CUDA")
    ];
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
    public ObservableCollection<CleanupLevelOption> CleanupLevelOptions { get; } =
    [
        new(CleanupLevel.None, "None"),
        new(CleanupLevel.Light, "Light"),
        new(CleanupLevel.Medium, "Medium"),
        new(CleanupLevel.High, "High")
    ];
    public ObservableCollection<InsertionStrategyOption> InsertionStrategyOptions { get; } =
    [
        new(TextInsertionStrategy.Auto, "Auto"),
        new(TextInsertionStrategy.ClipboardPaste, "Clipboard paste"),
        new(TextInsertionStrategy.DirectTyping, "Direct typing"),
        new(TextInsertionStrategy.CopyOnly, "Copy only")
    ];
    public ObservableCollection<AppInsertionStrategyRow> AppInsertionStrategies { get; } = [];

    [ObservableProperty] private string _statusText = "Press your hotkey or click Toggle to start recording.";
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string? _lastCapturePath;
    [ObservableProperty] private string? _lastTranscription;
    [ObservableProperty] private string _activeModelLabel = "No model loaded";
    [ObservableProperty] private string _engineName = "No engine selected";
    [ObservableProperty] private string _modelStatusText = "Not ready";
    [ObservableProperty] private bool _modelReady;
    [ObservableProperty] private DictationModelOption? _selectedModel;
    [ObservableProperty] private string _computeBackend = "cpu";
    [ObservableProperty] private AudioInputDevice? _selectedDevice;
    [ObservableProperty] private string _language = "auto";
    [ObservableProperty] private string? _translationTargetLanguage;
    [ObservableProperty] private CleanupLevel _cleanupLevel = CleanupLevel.None;
    [ObservableProperty] private bool _autoPaste;
    [ObservableProperty] private bool _autoAddDictionaryCorrections;
    [ObservableProperty] private string _newInsertionAppProcess = "";
    [ObservableProperty] private TextInsertionStrategy _newInsertionStrategy = TextInsertionStrategy.Auto;
    [ObservableProperty] private bool _whisperModeEnabled;
    [ObservableProperty] private bool _soundFeedbackEnabled = true;
    [ObservableProperty] private bool _transcribeShortQuietClipsAggressively;
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
    public bool CanDeleteSelectedModel => SelectedModel is { } selected && _models.CanDeleteModel(selected.ModelId);
    public bool CanUseCuda => _commands.HasCudaGpu && _commands.HasCudaRuntimeLibraries;
    public string ComputeBackendHint => CanUseCuda
        ? "CUDA uses the whisper.cpp CUDA Linux runtime for compatible NVIDIA GPUs. Other local plugins use CPU."
        : _commands.HasCudaGpu
            ? "CUDA unavailable: NVIDIA is detected, but libcudart.so.12 and libcublas.so.12 are missing. Install the CUDA 12 runtime/toolkit, then restart TypeWhisper."
            : "CUDA unavailable: no NVIDIA GPU/driver was detected. CPU is used.";

    public ComputeBackendOption? SelectedComputeBackendOption
    {
        get => ComputeBackendOptions.FirstOrDefault(option =>
            string.Equals(option.Value, ComputeBackend, StringComparison.OrdinalIgnoreCase));
        set
        {
            var selected = value?.Value ?? "cpu";
            if (string.Equals(selected, ComputeBackend, StringComparison.OrdinalIgnoreCase))
                return;

            ComputeBackend = selected;
            OnPropertyChanged();
        }
    }

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

    public CleanupLevelOption? SelectedCleanupLevelOption
    {
        get => CleanupLevelOptions.FirstOrDefault(option => option.Value == CleanupLevel);
        set
        {
            var selected = value?.Value ?? CleanupLevel.None;
            if (selected == CleanupLevel)
                return;

            CleanupLevel = selected;
        }
    }

    public InsertionStrategyOption? SelectedNewInsertionStrategyOption
    {
        get => InsertionStrategyOptions.FirstOrDefault(option => option.Value == NewInsertionStrategy);
        set
        {
            var selected = value?.Value ?? TextInsertionStrategy.Auto;
            if (selected == NewInsertionStrategy)
                return;

            NewInsertionStrategy = selected;
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
        CleanupLevel = settings.CleanupLevel;
        ComputeBackend = NormalizeComputeBackend(settings.ComputeBackend);
        AutoPaste = settings.AutoPaste;
        AutoAddDictionaryCorrections = settings.AutoAddDictionaryCorrections;
        RefreshAppInsertionStrategies(settings.AppInsertionStrategies);
        WhisperModeEnabled = settings.WhisperModeEnabled;
        SoundFeedbackEnabled = settings.SoundFeedbackEnabled && CanUseSoundFeedback;
        TranscribeShortQuietClipsAggressively = settings.TranscribeShortQuietClipsAggressively;
        SilenceAutoStopEnabled = settings.SilenceAutoStopEnabled;
        SilenceAutoStopSeconds = settings.SilenceAutoStopSeconds;
        AudioDuckingEnabled = settings.AudioDuckingEnabled && CanUseAudioDucking;
        AudioDuckingLevel = settings.AudioDuckingLevel;
        PauseMediaDuringRecording = settings.PauseMediaDuringRecording && CanUseMediaPause;

        SelectedDevice = _audio.ResolveConfiguredDevice(
            settings.SelectedMicrophoneDevice,
            settings.SelectedMicrophoneDeviceId);
        SelectedModel = ModelOptions.FirstOrDefault(option => option.ModelId == settings.SelectedModelId);

        OnPropertyChanged(nameof(SelectedLanguageOption));
        OnPropertyChanged(nameof(SelectedTranslationTargetOption));
        OnPropertyChanged(nameof(SelectedCleanupLevelOption));
        OnPropertyChanged(nameof(SelectedNewInsertionStrategyOption));
        OnPropertyChanged(nameof(SelectedComputeBackendOption));
        RefreshModelState();
    }

    private void RefreshAppInsertionStrategies(IReadOnlyDictionary<string, TextInsertionStrategy>? strategies)
    {
        AppInsertionStrategies.Clear();

        foreach (var strategy in strategies ?? new Dictionary<string, TextInsertionStrategy>())
        {
            if (string.IsNullOrWhiteSpace(strategy.Key))
                continue;

            AppInsertionStrategies.Add(new AppInsertionStrategyRow(
                strategy.Key,
                strategy.Value,
                InsertionStrategyOptions,
                SaveAppInsertionStrategies));
        }
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
            OnPropertyChanged(nameof(CanDeleteSelectedModel));
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
        OnPropertyChanged(nameof(CanDeleteSelectedModel));
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
        _ = DownloadAndLoadSelectedModelAsync(value);
    }

    partial void OnComputeBackendChanged(string value)
    {
        var normalized = NormalizeComputeBackend(value);
        if (normalized == "cuda" && !CanUseCuda)
        {
            ComputeBackend = "cpu";
            StatusText = _commands.HasCudaGpu
                ? "CUDA runtime libraries are missing. Install libcudart.so.12 and libcublas.so.12, then restart TypeWhisper."
                : "CUDA is not available on this system. Using CPU.";
            return;
        }

        if (_settings.Current.ComputeBackend != normalized)
            _settings.Save(_settings.Current with { ComputeBackend = normalized });

        OnPropertyChanged(nameof(SelectedComputeBackendOption));

        if (SelectedModel is { } selected && _models.IsDownloaded(selected.ModelId))
            _ = DownloadAndLoadSelectedModelAsync(selected);
    }

    private async Task DownloadAndLoadSelectedModelAsync(DictationModelOption selected)
    {
        _modelSelectionCts?.Cancel();
        _modelSelectionCts?.Dispose();
        var cts = _modelSelectionCts = new CancellationTokenSource();

        try
        {
            StatusText = _models.IsDownloaded(selected.ModelId)
                ? $"Loading {selected.DisplayLabel}..."
                : $"Downloading {selected.DisplayLabel}...";

            await _models.DownloadAndLoadModelAsync(selected.ModelId, cts.Token);

            if (cts.IsCancellationRequested || SelectedModel?.ModelId != selected.ModelId)
                return;

            StatusText = $"{selected.DisplayLabel} is ready.";
            RefreshModelState();
        }
        catch (OperationCanceledException)
        {
            // A newer model selection replaced this request.
        }
        catch (Exception ex)
        {
            if (SelectedModel?.ModelId == selected.ModelId)
                StatusText = $"Model setup failed: {ex.Message}";
            RefreshModelState();
        }
        finally
        {
            if (ReferenceEquals(_modelSelectionCts, cts))
                _modelSelectionCts = null;
            cts.Dispose();
        }
    }

    private static string NormalizeComputeBackend(string? backend) =>
        string.Equals(backend, "cuda", StringComparison.OrdinalIgnoreCase) ? "cuda" : "cpu";

    public async Task DeleteSelectedModelAsync()
    {
        var selected = SelectedModel;
        if (selected is null || !CanDeleteSelectedModel)
            return;

        _modelSelectionCts?.Cancel();
        StatusText = $"Deleting {selected.DisplayLabel}...";

        try
        {
            await _models.DeleteModelAsync(selected.ModelId);
            if (_settings.Current.SelectedModelId == selected.ModelId)
                _settings.Save(_settings.Current with { SelectedModelId = null });

            SelectedModel = null;
            StatusText = $"{selected.DisplayLabel} was deleted from disk.";
        }
        catch (Exception ex)
        {
            StatusText = $"Model delete failed: {ex.Message}";
        }
        finally
        {
            RefreshModelState();
        }
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

    partial void OnCleanupLevelChanged(CleanupLevel value)
    {
        _settings.Save(_settings.Current with { CleanupLevel = value });
        OnPropertyChanged(nameof(SelectedCleanupLevelOption));
    }

    partial void OnAutoPasteChanged(bool value)
        => _settings.Save(_settings.Current with { AutoPaste = value });

    partial void OnAutoAddDictionaryCorrectionsChanged(bool value)
        => _settings.Save(_settings.Current with { AutoAddDictionaryCorrections = value });

    [RelayCommand]
    private void AddAppInsertionStrategy()
    {
        var processName = NormalizeProcessName(NewInsertionAppProcess);
        if (string.IsNullOrWhiteSpace(processName))
        {
            StatusText = "Enter an app process name before adding an insertion strategy.";
            return;
        }

        var existing = AppInsertionStrategies.FirstOrDefault(row =>
            string.Equals(row.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Strategy = NewInsertionStrategy;
            SaveAppInsertionStrategies();
            NewInsertionAppProcess = "";
            StatusText = $"Updated insertion strategy for {processName}.";
            return;
        }

        AppInsertionStrategies.Add(new AppInsertionStrategyRow(
            processName,
            NewInsertionStrategy,
            InsertionStrategyOptions,
            SaveAppInsertionStrategies));
        SaveAppInsertionStrategies();
        NewInsertionAppProcess = "";
        StatusText = $"Added insertion strategy for {processName}.";
    }

    [RelayCommand]
    private void RemoveAppInsertionStrategy(AppInsertionStrategyRow? row)
    {
        if (row is null)
            return;

        AppInsertionStrategies.Remove(row);
        SaveAppInsertionStrategies();
        StatusText = $"Removed insertion strategy for {row.ProcessName}.";
    }

    private void SaveAppInsertionStrategies()
    {
        var strategies = AppInsertionStrategies
            .Select(row => (ProcessName: NormalizeProcessName(row.ProcessName), row.Strategy))
            .Where(row => !string.IsNullOrWhiteSpace(row.ProcessName))
            .GroupBy(row => row.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.First().ProcessName,
                group => group.Last().Strategy,
                StringComparer.OrdinalIgnoreCase);

        _settings.Save(_settings.Current with { AppInsertionStrategies = strategies });
    }

    private static string NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return "";

        return Path.GetFileNameWithoutExtension(processName.Trim());
    }

    partial void OnWhisperModeEnabledChanged(bool value)
        => _settings.Save(_settings.Current with { WhisperModeEnabled = value });

    partial void OnSoundFeedbackEnabledChanged(bool value)
    {
        if (value && !CanUseSoundFeedback)
        {
            SoundFeedbackEnabled = false;
            return;
        }

        _settings.Save(_settings.Current with { SoundFeedbackEnabled = value });
    }

    partial void OnTranscribeShortQuietClipsAggressivelyChanged(bool value)
        => _settings.Save(_settings.Current with { TranscribeShortQuietClipsAggressively = value });

    partial void OnSilenceAutoStopEnabledChanged(bool value)
        => _settings.Save(_settings.Current with { SilenceAutoStopEnabled = value });

    partial void OnSilenceAutoStopSecondsChanged(int value)
    {
        if (value <= 0)
            return;

        _settings.Save(_settings.Current with { SilenceAutoStopSeconds = value });
    }

    partial void OnAudioDuckingEnabledChanged(bool value)
    {
        if (value && !CanUseAudioDucking)
        {
            AudioDuckingEnabled = false;
            return;
        }

        _settings.Save(_settings.Current with { AudioDuckingEnabled = value });
    }

    partial void OnAudioDuckingLevelChanged(double value)
        => _settings.Save(_settings.Current with { AudioDuckingLevel = (float)Math.Clamp(value, 0d, 0.5d) });

    partial void OnPauseMediaDuringRecordingChanged(bool value)
    {
        if (value && !CanUseMediaPause)
        {
            PauseMediaDuringRecording = false;
            return;
        }

        _settings.Save(_settings.Current with { PauseMediaDuringRecording = value });
    }

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

public sealed record ComputeBackendOption(string Value, string DisplayName);

public sealed record SpokenLanguageOption(
    string Code,
    string DisplayName);

public sealed record CleanupLevelOption(
    CleanupLevel Value,
    string DisplayName);

public sealed record InsertionStrategyOption(
    TextInsertionStrategy Value,
    string DisplayName);

public sealed class AppInsertionStrategyRow : ObservableObject
{
    private readonly Action _changed;
    private string _processName;
    private TextInsertionStrategy _strategy;

    public string ProcessName
    {
        get => _processName;
        set
        {
            if (SetProperty(ref _processName, value))
                _changed();
        }
    }

    public TextInsertionStrategy Strategy
    {
        get => _strategy;
        set
        {
            if (SetProperty(ref _strategy, value))
            {
                OnPropertyChanged(nameof(SelectedStrategyOption));
                _changed();
            }
        }
    }

    public IReadOnlyList<InsertionStrategyOption> StrategyOptions { get; }

    public InsertionStrategyOption? SelectedStrategyOption
    {
        get => StrategyOptions.FirstOrDefault(option => option.Value == Strategy);
        set
        {
            var selected = value?.Value ?? TextInsertionStrategy.Auto;
            if (selected == Strategy)
                return;

            Strategy = selected;
        }
    }

    public AppInsertionStrategyRow(
        string processName,
        TextInsertionStrategy strategy,
        IReadOnlyList<InsertionStrategyOption> strategyOptions,
        Action changed)
    {
        _processName = processName;
        _strategy = strategy;
        StrategyOptions = strategyOptions;
        _changed = changed;
    }
}
