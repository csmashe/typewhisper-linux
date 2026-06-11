using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class DictationSectionViewModel : ObservableObject
{
    private readonly AudioRecordingService _audio;
    private readonly SystemCommandAvailabilityService _commands;
    private readonly DictationOrchestrator _dictation;
    private readonly ModelManagerService _models;
    private readonly PluginManager _pluginManager;
    private readonly ISettingsService _settings;
    private readonly LocalModelStorageService _modelStorage;

    [ObservableProperty]
    private string _activeModelLabel = "No model loaded";

    [ObservableProperty]
    private string _modelStoragePath = "";

    [ObservableProperty]
    private bool _isUsingCustomModelStorage;

    [ObservableProperty]
    private bool _isMigratingModelStorage;

    [ObservableProperty]
    private string _modelStorageStatusText = "";

    [ObservableProperty]
    private bool _audioDuckingEnabled;

    [ObservableProperty]
    private double _audioDuckingLevel = 0.2;

    [ObservableProperty]
    private bool _autoAddDictionaryCorrections;

    [ObservableProperty]
    private bool _autoPaste;

    [ObservableProperty]
    private CleanupLevel _cleanupLevel = CleanupLevel.None;

    [ObservableProperty]
    private string _cudaSetupStatus = "";

    [ObservableProperty]
    private string _engineName = "No engine selected";

    [ObservableProperty]
    private bool _isModelDownloading;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private string _language = "auto";

    [ObservableProperty]
    private string? _lastCapturePath;

    [ObservableProperty]
    private string? _lastTranscription;

    [ObservableProperty]
    private bool _liveTranscriptionEnabled;

    [ObservableProperty]
    private bool _liveTranscriptionStreamingEnabled;

    [ObservableProperty]
    private string _localModelAcceleration = AppSettings.LocalModelAccelerationAuto;

    [ObservableProperty]
    private string _microphoneStatus = "";

    [ObservableProperty]
    private double _modelDownloadProgress;

    [ObservableProperty]
    private bool _modelReady;

    private CancellationTokenSource? _modelSelectionCts;

    [ObservableProperty]
    private string _modelStatusText = "Not ready";

    [ObservableProperty]
    private string _newInsertionAppProcess = "";

    [ObservableProperty]
    private TextInsertionStrategy _newInsertionStrategy = TextInsertionStrategy.Auto;

    [ObservableProperty]
    private bool _onlineAsrBatchLiveTranscriptionEnabled;

    [ObservableProperty]
    private bool _pauseMediaDuringRecording;

    // True when the Dictation page is visible; restarts mic preview after recording
    // ends so the level meter doesn't go dark while the page is still open.
    private bool _previewAttached;

    [ObservableProperty]
    private double _previewLevel;

    [ObservableProperty]
    private AudioInputDevice? _selectedDevice;

    [ObservableProperty]
    private DictationModelOption? _selectedModel;

    [ObservableProperty]
    private bool _silenceAutoStopEnabled;

    [ObservableProperty]
    private int _silenceAutoStopSeconds = 10;

    [ObservableProperty]
    private bool _soundFeedbackEnabled = true;

    [ObservableProperty]
    private string _statusText = "Press your hotkey or click Toggle to start recording.";

    [ObservableProperty]
    private bool _transcribeShortQuietClipsAggressively;

    [ObservableProperty]
    private string? _translationTargetLanguage;

    [ObservableProperty]
    private bool _whisperModeEnabled;

    public DictationSectionViewModel(
        DictationOrchestrator dictation,
        ModelManagerService models,
        AudioRecordingService audio,
        ISettingsService settings,
        PluginManager pluginManager,
        SystemCommandAvailabilityService commands
    )
    {
        _dictation = dictation;
        _models = models;
        _audio = audio;
        _settings = settings;
        _pluginManager = pluginManager;
        _commands = commands;
        // Unload the active local model before moving its files so the source
        // path isn't held open during migration.
        _modelStorage = new LocalModelStorageService(_settings, () => _models.UnloadModel());

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

        _dictation.StatusMessage += (_, msg) => Dispatcher.UIThread.Post(() => StatusText = msg);

        _audio.LevelChanged += OnLevelChanged;
        _models.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(OnModelStatusChanged);
        _pluginManager.PluginStateChanged += (_, _) => Dispatcher.UIThread.Post(RefreshModels);
        _settings.SettingsChanged += settingsValue =>
            Dispatcher.UIThread.Post(() => RefreshFromSettings(settingsValue));

        foreach (var option in TranslationModelInfo.GlobalTargetOptions)
        {
            TranslationTargetOptions.Add(option);
        }

        RefreshModels();
        RefreshDevices();
        RefreshFromSettings(_settings.Current);
    }

    public ObservableCollection<DictationModelOption> ModelOptions { get; } = [];
    public ObservableCollection<AudioInputDevice> Devices { get; } = [];

    public ObservableCollection<AccelerationOption> AccelerationOptions { get; } =
    [
        new(AppSettings.LocalModelAccelerationAuto, "Auto"),
        new(AppSettings.LocalModelAccelerationCpu, "CPU"),
        new(AppSettings.LocalModelAccelerationNvidiaCuda, "NVIDIA CUDA")
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
        new("fi", "Suomi")
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

    public bool CanUseAudioDucking => _commands.HasPactl;
    public bool ShowAudioDuckingUnavailableReason => !CanUseAudioDucking;

    public string AudioDuckingUnavailableReason =>
        "Unavailable: pactl is not installed on this system.";

    public bool CanUseMediaPause => _commands.HasPlayerCtl;
    public bool ShowMediaPauseUnavailableReason => !CanUseMediaPause;

    public string MediaPauseUnavailableReason =>
        "Unavailable: playerctl is not installed on this system.";

    public bool CanUseSoundFeedback => _commands.HasAudioPlayer;
    public bool ShowSoundFeedbackUnavailableReason => !CanUseSoundFeedback;

    public string SoundFeedbackUnavailableReason =>
        "Unavailable: no audio player (pw-play, paplay, or aplay) is installed on this system.";

    public bool CanDeleteSelectedModel =>
        SelectedModel is { } selected && _models.CanDeleteModel(selected.ModelId);

    public bool ShowCudaLibraryPathAction =>
        _commands.HasCudaGpu && !CanUseCuda && FindCuda12LibraryPath() is not null;

    public string CudaLibraryPathActionText => "Fix CUDA path";
    public bool CanUseCuda => _commands.HasCudaGpu && _commands.HasCudaRuntimeLibraries;

    public string AccelerationStatusText
    {
        get
        {
            var status = _models.ActiveTranscriptionPlugin?.AccelerationStatus;
            if (status is null)
            {
                return LocalModelAcceleration switch
                {
                    AppSettings.LocalModelAccelerationCpu => "CPU mode is active.",
                    AppSettings.LocalModelAccelerationNvidiaCuda when !CanUseCuda =>
                        FindCuda12LibraryPath() is null
                            ? "CUDA 12 runtime libraries are not installed yet."
                            : "CUDA 12 is installed, but TypeWhisper cannot see it yet.",
                    AppSettings.LocalModelAccelerationNvidiaCuda =>
                        "CUDA is ready for whisper.cpp models. Other local plugins use CPU.",
                    _ => "Auto: CUDA will be used when available, otherwise CPU."
                };
            }

            var text = status.DisplayText;
            if (!string.IsNullOrWhiteSpace(status.Detail))
            {
                text += " — " + status.Detail;
            }

            if (status.RequiresRestart)
            {
                text += " Restart TypeWhisper to apply.";
            }

            return text;
        }
    }

    public AccelerationOption? SelectedAccelerationOption
    {
        get =>
            AccelerationOptions.FirstOrDefault(option =>
                string.Equals(option.Value, LocalModelAcceleration, StringComparison.OrdinalIgnoreCase)
            );
        set
        {
            var selected = value?.Value ?? AppSettings.LocalModelAccelerationAuto;
            if (string.Equals(selected, LocalModelAcceleration, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            LocalModelAcceleration = selected;
            OnPropertyChanged();
        }
    }

    public TranslationTargetOption? SelectedTranslationTargetOption
    {
        get =>
            TranslationTargetOptions.FirstOrDefault(option =>
                string.Equals(option.Code, TranslationTargetLanguage, StringComparison.Ordinal)
            );
        set
        {
            var code = value?.Code;
            if (string.Equals(code, TranslationTargetLanguage, StringComparison.Ordinal))
            {
                return;
            }

            TranslationTargetLanguage = code;
            OnPropertyChanged();
        }
    }

    public SpokenLanguageOption? SelectedLanguageOption
    {
        get =>
            LanguageChoices.FirstOrDefault(option =>
                string.Equals(option.Code, Language, StringComparison.Ordinal)
            );
        set
        {
            var code = value?.Code ?? "auto";
            if (string.Equals(code, Language, StringComparison.Ordinal))
            {
                return;
            }

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
            {
                return;
            }

            CleanupLevel = selected;
        }
    }

    public InsertionStrategyOption? SelectedNewInsertionStrategyOption
    {
        get =>
            InsertionStrategyOptions.FirstOrDefault(option => option.Value == NewInsertionStrategy);
        set
        {
            var selected = value?.Value ?? TextInsertionStrategy.Auto;
            if (selected == NewInsertionStrategy)
            {
                return;
            }

            NewInsertionStrategy = selected;
            OnPropertyChanged();
        }
    }

    public string ModelDownloadPercentText => $"{ModelDownloadProgress * 100:0}%";

    public void ActivatePreview()
    {
        _previewAttached = true;
        if (!_audio.StartPreview() && Devices.Count > 0)
        {
            MicrophoneStatus = "Could not start live input preview for the selected microphone.";
        }
    }

    public void DeactivatePreview()
    {
        _previewAttached = false;
        _audio.StopPreview();
        PreviewLevel = 0;
    }

    public async Task DeleteSelectedModelAsync()
    {
        var selected = SelectedModel;
        if (selected is null || !CanDeleteSelectedModel)
        {
            return;
        }

        _modelSelectionCts?.Cancel();
        StatusText = $"Deleting {selected.DisplayLabel}...";

        try
        {
            await _models.DeleteModelAsync(selected.ModelId);
            if (_settings.Current.SelectedModelId == selected.ModelId)
            {
                _settings.Save(_settings.Current with { SelectedModelId = null });
            }

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

    /// <summary>
    ///     Re-polls providers when the model dropdown opens so newly added models appear
    ///     without a manual "Validate". Debounce/guard live in <see cref="PluginManager" />.
    /// </summary>
    public Task RefreshProviderModelsAsync()
    {
        return _pluginManager.RefreshProviderModelsAsync();
    }

    [RelayCommand]
    private async Task Toggle()
    {
        await _dictation.ToggleAsync();
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        Devices.Clear();
        foreach (var d in _audio.GetInputDevices())
        {
            Devices.Add(d);
        }

        SelectedDevice = _audio.ResolveConfiguredDevice(
            _settings.Current.SelectedMicrophoneDevice,
            _settings.Current.SelectedMicrophoneDeviceId
        );

        MicrophoneStatus =
            Devices.Count == 0
                ? "No input devices detected."
                : $"{Devices.Count} input device(s) available.";
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
                ModelOptions.Add(
                    new DictationModelOption(
                        fullModelId,
                        model.DisplayName,
                        engine.ProviderDisplayName
                    )
                );
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
        LocalModelAcceleration = AppSettings.NormalizeLocalModelAcceleration(
            settings.LocalModelAcceleration
        );
        ModelStoragePath = _modelStorage.ResolvedModelStoragePath;
        IsUsingCustomModelStorage =
            AppSettings.NormalizeLocalModelStoragePath(settings.LocalModelStoragePath) is not null;
        AutoPaste = settings.AutoPaste;
        AutoAddDictionaryCorrections = settings.AutoAddDictionaryCorrections;
        LiveTranscriptionEnabled = settings.LiveTranscriptionEnabled;
        OnlineAsrBatchLiveTranscriptionEnabled = settings.OnlineAsrBatchLiveTranscriptionEnabled;
        LiveTranscriptionStreamingEnabled = settings.LiveTranscriptionStreamingEnabled;
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
            settings.SelectedMicrophoneDeviceId
        );
        SelectedModel = ModelOptions.FirstOrDefault(option =>
            option.ModelId == settings.SelectedModelId
        );

        OnPropertyChanged(nameof(SelectedLanguageOption));
        OnPropertyChanged(nameof(SelectedTranslationTargetOption));
        OnPropertyChanged(nameof(SelectedCleanupLevelOption));
        OnPropertyChanged(nameof(SelectedNewInsertionStrategyOption));
        OnPropertyChanged(nameof(SelectedAccelerationOption));
        OnPropertyChanged(nameof(AccelerationStatusText));
        RefreshModelState();
    }

    private void RefreshAppInsertionStrategies(
        IReadOnlyDictionary<string, TextInsertionStrategy>? strategies
    )
    {
        AppInsertionStrategies.Clear();

        foreach (var strategy in strategies ?? new Dictionary<string, TextInsertionStrategy>())
        {
            if (string.IsNullOrWhiteSpace(strategy.Key))
            {
                continue;
            }

            AppInsertionStrategies.Add(
                new AppInsertionStrategyRow(
                    strategy.Key,
                    strategy.Value,
                    InsertionStrategyOptions,
                    SaveAppInsertionStrategies
                )
            );
        }
    }

    // Progress ticks are unthrottled; RefreshModelState on every tick would saturate the UI.
    // Drive the cheap progress update each tick and refresh the status badge only when settled.
    private void OnModelStatusChanged()
    {
        UpdateDownloadProgress();

        if (!IsModelDownloading)
        {
            RefreshModelState();
        }
    }

    private void UpdateDownloadProgress()
    {
        if (SelectedModel is not { } model)
        {
            IsModelDownloading = false;
            return;
        }

        var status = _models.GetStatus(model.ModelId);
        IsModelDownloading = status.Type == ModelStatusType.Downloading;
        if (IsModelDownloading)
        {
            ModelDownloadProgress = Math.Clamp(status.Progress, 0, 1);
        }
    }

    partial void OnModelDownloadProgressChanged(double value)
    {
        OnPropertyChanged(nameof(ModelDownloadPercentText));
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
            ModelStatusType.Error => FormatModelStatusError(status.ErrorMessage),
            _ => "Not ready"
        };
        OnPropertyChanged(nameof(CanDeleteSelectedModel));
        OnPropertyChanged(nameof(ShowCudaLibraryPathAction));
        OnPropertyChanged(nameof(AccelerationStatusText));
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

    partial void OnLocalModelAccelerationChanged(string value)
    {
        var normalized = AppSettings.NormalizeLocalModelAcceleration(value);

        // Guard: CUDA can't be selected when unavailable; Auto always works and resolves to CPU.
        if (normalized == AppSettings.LocalModelAccelerationNvidiaCuda && !CanUseCuda)
        {
            // Three distinct cases: no GPU; GPU + libs found but not on loader path; libs not installed.
            var message =
                !_commands.HasCudaGpu
                    ? "No NVIDIA GPU/driver detected — CUDA is unavailable on this system. Staying on CPU."
                    : FindCuda12LibraryPath() is not null
                        ? "CUDA 12 runtime libraries are installed but not on TypeWhisper's library path. "
                          + "Click \"Fix CUDA path\", then restart TypeWhisper."
                        : "NVIDIA GPU detected, but the CUDA 12 runtime libraries are not installed "
                          + "(libcudart.so.12 / libcublas.so.12). Install the CUDA 12 runtime, then "
                          + "restart TypeWhisper. Staying on CPU until then.";
            // Revert on the next UI frame: a ComboBox ignores SelectedItem changes inside its
            // own selection-change cycle, leaving the dropdown stuck on "NVIDIA CUDA".
            // Write the message after the revert — the CPU re-entry clears CudaSetupStatus.
            Dispatcher.UIThread.Post(() =>
            {
                LocalModelAcceleration = AppSettings.LocalModelAccelerationCpu;
                OnPropertyChanged(nameof(SelectedAccelerationOption));
                CudaSetupStatus = message;
                StatusText = message;
            });

            OnPropertyChanged(nameof(AccelerationStatusText));
            OnPropertyChanged(nameof(ShowCudaLibraryPathAction));
            return;
        }

        // Clear any stale CUDA-unavailable warning on a successful selection.
        CudaSetupStatus = "";

        if (
            !string.Equals(
                _settings.Current.LocalModelAcceleration,
                normalized,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            _settings.Save(_settings.Current with { LocalModelAcceleration = normalized });
        }

        OnPropertyChanged(nameof(SelectedAccelerationOption));
        OnPropertyChanged(nameof(AccelerationStatusText));
        OnPropertyChanged(nameof(ShowCudaLibraryPathAction));

        // Reload so EnsureModelLoadedAsync re-evaluates; AccelerationStatus surfaces
        // RequiresRestart when the process-pinned runtime no longer matches.
        if (
            _models.ActiveTranscriptionPlugin is not null
            && SelectedModel is { } selected
            && _models.IsDownloaded(selected.ModelId)
        )
        {
            _ = ReloadActiveModelForAccelerationChangeAsync(selected);
        }
    }

    // Moves any already-downloaded models to the chosen folder and makes it the
    // active storage location. Invoked from the section code-behind, which supplies
    // the folder path picked via the platform folder picker.
    public async Task ChangeModelStorageAsync(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || IsMigratingModelStorage)
        {
            return;
        }

        IsMigratingModelStorage = true;
        ModelStorageStatusText = "Moving models to the new location…";
        try
        {
            await _modelStorage.MoveDownloadsAndUsePathAsync(folderPath);
            ModelStorageStatusText = "Model storage location updated.";
        }
        catch (LocalModelStorageUnavailableException ex)
        {
            ModelStorageStatusText = ex.Message;
        }
        catch (Exception ex)
        {
            ModelStorageStatusText = $"Could not change model storage: {ex.Message}";
        }
        finally
        {
            IsMigratingModelStorage = false;
            // SettingsChanged already fires RefreshFromSettings on success, but refresh
            // explicitly so the displayed path is correct even on the no-op/equal path.
            ModelStoragePath = _modelStorage.ResolvedModelStoragePath;
            IsUsingCustomModelStorage =
                AppSettings.NormalizeLocalModelStoragePath(_settings.Current.LocalModelStoragePath)
                is not null;
        }
    }

    [RelayCommand]
    private void ResetModelStorage()
    {
        if (!IsUsingCustomModelStorage || IsMigratingModelStorage)
        {
            return;
        }

        // Leaves already-moved files where they are and points future downloads back
        // at the default app-data location.
        _modelStorage.ResetToDefault();
        ModelStorageStatusText =
            "Future downloads will use the default location. Existing files were left in place.";
    }

    private async Task ReloadActiveModelForAccelerationChangeAsync(DictationModelOption selected)
    {
        try
        {
            await _models.EnsureModelLoadedAsync(selected.ModelId);
        }
        catch (Exception ex)
        {
            StatusText = $"Model reload failed: {FormatModelStatusError(ex.Message)}";
        }
        finally
        {
            OnPropertyChanged(nameof(AccelerationStatusText));
            RefreshModelState();
        }
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
            {
                return;
            }

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
            {
                StatusText = $"Model setup failed: {FormatModelStatusError(ex.Message)}";
            }

            RefreshModelState();
        }
        finally
        {
            if (ReferenceEquals(_modelSelectionCts, cts))
            {
                _modelSelectionCts = null;
            }

            cts.Dispose();
        }
    }

    [RelayCommand]
    private void AddCudaLibraryPathToShellProfile()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
            {
                StatusText = "Could not find your home directory.";
                return;
            }

            var cudaLibraryPath = FindCuda12LibraryPath();
            if (cudaLibraryPath is null)
            {
                StatusText =
                    "CUDA 12 libraries are not installed yet. Install CUDA 12, then run this action again.";
                return;
            }

            var profilePath = ResolveShellProfilePath(home);
            var exportLine = GetCudaLibraryPathExport(profilePath, cudaLibraryPath);
            var existing = File.Exists(profilePath) ? File.ReadAllText(profilePath) : string.Empty;

            Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
            if (
                !existing.Contains(exportLine, StringComparison.Ordinal)
                && !existing.Contains(cudaLibraryPath, StringComparison.Ordinal)
            )
            {
                var prefix =
                    existing.Length > 0 && !existing.EndsWith('\n')
                        ? Environment.NewLine
                        : string.Empty;
                File.AppendAllText(
                    profilePath,
                    $"{prefix}{Environment.NewLine}# TypeWhisper CUDA 12 runtime libraries{Environment.NewLine}{exportLine}{Environment.NewLine}"
                );
            }

            WriteDesktopEnvironmentFile(home, cudaLibraryPath);
            CudaSetupStatus =
                "Saved. This affects future launches only: open a new terminal, or log out and back in for desktop launches, then restart TypeWhisper.";
            StatusText = "CUDA path saved. Restart TypeWhisper from a new environment to use CUDA.";
        }
        catch (Exception ex)
        {
            CudaSetupStatus = $"Could not save CUDA path: {ex.Message}";
            StatusText = $"Could not update shell startup file: {ex.Message}";
        }
    }

    private static string ResolveShellProfilePath(string home)
    {
        var shell = Environment.GetEnvironmentVariable("SHELL") ?? string.Empty;
        if (shell.EndsWith("/zsh", StringComparison.Ordinal))
        {
            return Path.Combine(home, ".zshrc");
        }

        if (shell.EndsWith("/fish", StringComparison.Ordinal))
        {
            return Path.Combine(home, ".config", "fish", "config.fish");
        }

        return Path.Combine(home, ".bashrc");
    }

    private static string GetCudaLibraryPathExport(string profilePath, string cudaLibraryPath)
    {
        return profilePath.EndsWith("config.fish", StringComparison.Ordinal)
            ? $"set -gx LD_LIBRARY_PATH {cudaLibraryPath} $LD_LIBRARY_PATH"
            : $"export LD_LIBRARY_PATH={cudaLibraryPath}:${{LD_LIBRARY_PATH:-}}";
    }

    // ~/.config/environment.d/ is picked up by systemd-environment-d-generator for GUI sessions
    // on Wayland, covering app-menu launches where the shell profile isn't sourced.
    private static string WriteDesktopEnvironmentFile(string home, string cudaLibraryPath)
    {
        var environmentDir = Path.Combine(home, ".config", "environment.d");
        Directory.CreateDirectory(environmentDir);

        var path = Path.Combine(environmentDir, "typewhisper-cuda.conf");
        File.WriteAllText(
            path,
            $"# TypeWhisper CUDA 12 runtime libraries{Environment.NewLine}LD_LIBRARY_PATH={cudaLibraryPath}:${{LD_LIBRARY_PATH:-}}{Environment.NewLine}"
        );
        return path;
    }

    private static string? FindCuda12LibraryPath()
    {
        return SystemCommandAvailabilityService.FindCuda12RuntimeDirectory();
    }

    partial void OnModelStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(ShowCudaLibraryPathAction));
    }

    private static string FormatModelStatusError(string? message)
    {
        if (!IsCudaMissingLibraryError(message))
        {
            return string.IsNullOrWhiteSpace(message) ? "Error" : message;
        }

        var cudaLibraryPath = FindCuda12LibraryPath();
        return cudaLibraryPath is null
            ? "CUDA 12 is not installed yet. Install the CUDA 12 toolkit/runtime, then restart TypeWhisper."
            : "CUDA 12 is installed, but TypeWhisper cannot see it yet. Click Fix CUDA path, then restart TypeWhisper.";
    }

    private static bool IsCudaMissingLibraryError(string? message)
    {
        return !string.IsNullOrWhiteSpace(message)
               && (
                   message.Contains("libcudart.so.12", StringComparison.Ordinal)
                   || message.Contains("libcublas.so.12", StringComparison.Ordinal)
               )
               && message.Contains("cannot open shared object file", StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSelectedDeviceChanged(AudioInputDevice? value)
    {
        if (value is null)
        {
            return;
        }

        _audio.SelectedDeviceIndex = value.Index;
        _settings.Save(
            _settings.Current with
            {
                SelectedMicrophoneDevice = value.Index, SelectedMicrophoneDeviceId = value.PersistentId
            }
        );
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
    {
        _settings.Save(_settings.Current with { AutoPaste = value });
    }

    partial void OnAutoAddDictionaryCorrectionsChanged(bool value)
    {
        _settings.Save(_settings.Current with { AutoAddDictionaryCorrections = value });
    }

    partial void OnLiveTranscriptionEnabledChanged(bool value)
    {
        _settings.Save(_settings.Current with { LiveTranscriptionEnabled = value });
    }

    partial void OnOnlineAsrBatchLiveTranscriptionEnabledChanged(bool value)
    {
        _settings.Save(_settings.Current with { OnlineAsrBatchLiveTranscriptionEnabled = value });
    }

    partial void OnLiveTranscriptionStreamingEnabledChanged(bool value)
    {
        _settings.Save(_settings.Current with { LiveTranscriptionStreamingEnabled = value });
    }

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
            string.Equals(row.ProcessName, processName, StringComparison.OrdinalIgnoreCase)
        );
        if (existing is not null)
        {
            existing.Strategy = NewInsertionStrategy;
            SaveAppInsertionStrategies();
            NewInsertionAppProcess = "";
            StatusText = $"Updated insertion strategy for {processName}.";
            return;
        }

        AppInsertionStrategies.Add(
            new AppInsertionStrategyRow(
                processName,
                NewInsertionStrategy,
                InsertionStrategyOptions,
                SaveAppInsertionStrategies
            )
        );
        SaveAppInsertionStrategies();
        NewInsertionAppProcess = "";
        StatusText = $"Added insertion strategy for {processName}.";
    }

    [RelayCommand]
    private void RemoveAppInsertionStrategy(AppInsertionStrategyRow? row)
    {
        if (row is null)
        {
            return;
        }

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
                StringComparer.OrdinalIgnoreCase
            );

        _settings.Save(_settings.Current with { AppInsertionStrategies = strategies });
    }

    private static string NormalizeProcessName(string? processName)
    {
        return ProcessNameNormalizer.Normalize(processName);
    }

    partial void OnWhisperModeEnabledChanged(bool value)
    {
        _settings.Save(_settings.Current with { WhisperModeEnabled = value });
    }

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
    {
        _settings.Save(_settings.Current with { TranscribeShortQuietClipsAggressively = value });
    }

    partial void OnSilenceAutoStopEnabledChanged(bool value)
    {
        _settings.Save(_settings.Current with { SilenceAutoStopEnabled = value });
    }

    partial void OnSilenceAutoStopSecondsChanged(int value)
    {
        if (value <= 0)
        {
            return;
        }

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
    {
        _settings.Save(
            _settings.Current with { AudioDuckingLevel = (float)Math.Clamp(value, 0d, 0.5d) }
        );
    }

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

public sealed record DictationModelOption(string ModelId, string DisplayName, string EngineName)
{
    public string DisplayLabel => $"{EngineName} / {DisplayName}";
}

public sealed record AccelerationOption(string Value, string DisplayName);

public sealed record SpokenLanguageOption(string Code, string DisplayName);

public sealed record CleanupLevelOption(CleanupLevel Value, string DisplayName);

public sealed record InsertionStrategyOption(TextInsertionStrategy Value, string DisplayName);

public sealed class AppInsertionStrategyRow : ObservableObject
{
    private readonly Action _changed;
    private string _processName;
    private TextInsertionStrategy _strategy;

    public AppInsertionStrategyRow(
        string processName,
        TextInsertionStrategy strategy,
        IReadOnlyList<InsertionStrategyOption> strategyOptions,
        Action changed
    )
    {
        _processName = processName;
        _strategy = strategy;
        StrategyOptions = strategyOptions;
        _changed = changed;
    }

    public string ProcessName
    {
        get => _processName;
        set
        {
            if (SetProperty(ref _processName, value))
            {
                _changed();
            }
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
            {
                return;
            }

            Strategy = selected;
        }
    }
}