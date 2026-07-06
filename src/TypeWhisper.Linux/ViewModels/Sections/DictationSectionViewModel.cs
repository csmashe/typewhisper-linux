using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.ViewModels.Sections;

// MVVM Toolkit [ObservableProperty] generates the On<Property>Changed(value) partial hooks; the
// value parameter is part of the generated signature and cannot be dropped even when ignored here.
// ReSharper disable UnusedParameterInPartialMethod
public partial class DictationSectionViewModel : ObservableObject
{
    private readonly AudioRecordingService _audio;
    private readonly SystemCommandAvailabilityService _commands;
    private readonly DictationOrchestrator _dictation;
    private readonly ModelManagerService _models;
    private readonly PluginManager _pluginManager;
    private readonly ISettingsService _settings;
    private readonly LocalModelStorageService _modelStorage;

    // Cached snapshot of the selected engine's CUDA-provisioned state; CanUseCuda reads this
    // instead of probing the plugin inline (see CanUseCuda). Recomputed off the UI thread by
    // RefreshSelectedPluginCudaProvisionedAsync; _cudaProbeGeneration discards a probe whose
    // selection has since changed.
    private bool _selectedPluginCudaProvisioned;
    private int _cudaProbeGeneration;

    [ObservableProperty]
    private string _activeModelLabel = Loc.Instance["Dictation.NoModelLoaded"];

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
    private double _audioDuckingLevel = 0.4;

    [ObservableProperty]
    private bool _autoAddDictionaryCorrections;

    [ObservableProperty]
    private bool _targetAppCorrectionLearningEnabled;

    [ObservableProperty]
    private bool _autoPaste;

    [ObservableProperty]
    private CleanupLevel _cleanupLevel = CleanupLevel.None;

    [ObservableProperty]
    private string _cudaSetupStatus = "";

    [ObservableProperty]
    private bool _isDownloadingCudaRuntime;

    [ObservableProperty]
    private bool _isClearingGpuRuntime;

    // Set once the GPU runtime cache has been cleared this session. The deleted libs are
    // re-downloaded only on the next process start (the old ones are held until exit), so
    // this suppresses the Download action until restart (see ShowDownloadCudaRuntimeAction).
    private bool _gpuRuntimeClearedPendingRestart;

    [ObservableProperty]
    private string _engineName = Loc.Instance["Dictation.NoEngineSelected"];

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

    // Set while hydrating from saved settings so OnLocalModelAccelerationChanged doesn't
    // run its CUDA-availability revert guard against a not-yet-loaded engine.
    private bool _suppressAccelerationGuard;

    [ObservableProperty]
    private string _modelStatusText = Loc.Instance["Dictation.StatusNotReady"];

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
    private string _statusText = Loc.Instance["Dictation.StatusPressHotkey"];

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
                    ? Loc.Instance["Dictation.StatusRecording"]
                    : Loc.Instance["Dictation.StatusStopped"];

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
        new(AppSettings.LocalModelAccelerationAuto, Loc.Instance["Dictation.AccelerationAuto"]),
        new(AppSettings.LocalModelAccelerationCpu, Loc.Instance["Dictation.AccelerationCpu"]),
        new(AppSettings.LocalModelAccelerationNvidiaCuda, Loc.Instance["Dictation.AccelerationNvidiaCuda"])
    ];

    public ObservableCollection<SpokenLanguageOption> LanguageChoices { get; } =
    [
        new("auto", Loc.Instance["Dictation.LanguageAutoDetect"]),
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
        new(CleanupLevel.None, Loc.Instance["Dictation.CleanupNone"]),
        new(CleanupLevel.Light, Loc.Instance["Dictation.CleanupLight"]),
        new(CleanupLevel.Medium, Loc.Instance["Dictation.CleanupMedium"]),
        new(CleanupLevel.High, Loc.Instance["Dictation.CleanupHigh"])
    ];

    public ObservableCollection<InsertionStrategyOption> InsertionStrategyOptions { get; } =
    [
        new(TextInsertionStrategy.Auto, Loc.Instance["Dictation.AccelerationAuto"]),
        new(TextInsertionStrategy.ClipboardPaste, Loc.Instance["Dictation.StrategyClipboardPaste"]),
        new(TextInsertionStrategy.DirectTyping, Loc.Instance["Dictation.StrategyDirectTyping"]),
        new(TextInsertionStrategy.CopyOnly, Loc.Instance["Dictation.StrategyCopyOnly"])
    ];

    public ObservableCollection<AppInsertionStrategyRow> AppInsertionStrategies { get; } = [];

    public bool CanUseAudioDucking => _commands.HasPactl;
    public bool ShowAudioDuckingUnavailableReason => !CanUseAudioDucking;

    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public string AudioDuckingUnavailableReason =>
        Loc.Instance["Dictation.PactlUnavailable"];

    public bool CanUseMediaPause => _commands.HasPlayerCtl;
    public bool ShowMediaPauseUnavailableReason => !CanUseMediaPause;

    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public string MediaPauseUnavailableReason =>
        Loc.Instance["Dictation.PlayerctlUnavailable"];

    public bool CanUseSoundFeedback => _commands.HasAudioPlayer;
    public bool ShowSoundFeedbackUnavailableReason => !CanUseSoundFeedback;

    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public string SoundFeedbackUnavailableReason =>
        Loc.Instance["Dictation.AudioPlayerUnavailable"];

    public bool CanDeleteSelectedModel =>
        SelectedModel is { } selected && _models.CanDeleteModel(selected.ModelId);

    // The loader-path fix only applies when the system CUDA libraries exist on disk but
    // aren't currently loadable. Once they are loadable (HasCudaRuntimeLibraries) there is
    // nothing to fix — a self-provisioning engine that still lacks its own GPU build is then
    // handled by ShowDownloadCudaRuntimeAction instead. The !CanUseCuda gate also hides it
    // once an engine is fully provisioned from its own cache (CUDA already usable).
    public bool ShowCudaLibraryPathAction =>
        _commands.HasCudaGpu
        && !CanUseCuda
        && !_commands.HasCudaRuntimeLibraries
        && FindCuda12LibraryPath() is not null;

    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public string CudaLibraryPathActionText => Loc.Instance["Dictation.FixCudaPath"];

    // CUDA is usable only when the GPU/driver is present AND the runtime the SELECTED engine
    // needs is fully available. A self-provisioning engine needs its own GPU native build
    // (the sherpa-onnx GPU ORT / whisper.cpp CUDA build) on top of the CUDA math libraries —
    // even a complete host CUDA toolkit never ships that engine build — so the system-library
    // flag alone is not enough: we require the engine's full provisioned state. That keeps the
    // "Download CUDA runtime" action available to prefetch the engine build instead of hiding
    // it and deferring a large download to the lazy model-load path. Engines that rely on a
    // host-provided runtime are usable as soon as the system CUDA libraries are present.
    //
    // The provisioned arm reads a cached flag rather than the plugin's IsCudaRuntimeProvisioned:
    // the probe can shell out to `ldconfig -p` (~1s) once per missing CUDA library on a
    // driver-only host, and CanUseCuda is read by several bindings and re-evaluated on every
    // ModelStatusText change — calling it inline would freeze the UI thread.
    // RefreshSelectedPluginCudaProvisionedAsync recomputes the flag off-thread.
    public bool CanUseCuda =>
        _commands.HasCudaGpu
        && (
            SelectedModelPlugin?.ProvisionsCudaRuntimeOnDemand == true
                ? _selectedPluginCudaProvisioned
                : _commands.HasCudaRuntimeLibraries
        );

    // The engine that owns the model selected in the Dictation UI — the one a CUDA download
    // must target. Distinct from ActiveTranscriptionPlugin (the loaded engine), which is null
    // before any model loads (e.g. at startup) and can lag a freshly selected model.
    private ITranscriptionEnginePlugin? SelectedModelPlugin =>
        _models.GetTranscriptionPlugin(SelectedModel?.ModelId);

    // Offer the in-app download when there's a GPU but CUDA isn't usable yet and the
    // selected engine can fetch its own runtime — i.e. a driver-only host, or a partial
    // install where only some libraries are present (the engine downloads just the gaps).
    // Suppressed while a download is in flight, and when the libs exist on disk but only
    // need a loader-path fix (that's the ShowCudaLibraryPathAction button's job instead).
    // Also suppressed once the GPU runtime has been cleared this session: clearing
    // requires a restart to take effect (the old native libs are pinned until exit), so
    // re-offering a multi-GB download in the same poisoned process would contradict the
    // "restart to re-download" guidance. A fresh launch starts the flag false.
    public bool ShowDownloadCudaRuntimeAction =>
        _commands.HasCudaGpu
        && !CanUseCuda
        && !IsDownloadingCudaRuntime
        && !ShowCudaLibraryPathAction
        && !_gpuRuntimeClearedPendingRestart
        && SelectedModelPlugin?.ProvisionsCudaRuntimeOnDemand == true;

    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public string DownloadCudaRuntimeText => Loc.Instance["Dictation.DownloadCudaRuntime"];

    // Offer "Clear GPU runtime" exactly in the corrupt-but-present case: a provisioning
    // engine is selected and reports provisioned (the files exist on disk, so Download is
    // hidden), yet a cached lib may be corrupt — deleting the cache so the next restart
    // re-downloads is the only in-app repair. Mutually exclusive with the Download button
    // (which shows only when NOT provisioned) and hidden while either action is running.
    public bool ShowClearGpuRuntimeAction =>
        SelectedModelPlugin?.ProvisionsCudaRuntimeOnDemand == true
        && _selectedPluginCudaProvisioned
        && !IsClearingGpuRuntime
        && !IsDownloadingCudaRuntime;

    // ReSharper disable once MemberCanBeMadeStatic.Global
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML binding surface; ViewModel properties must be instance members for compiled bindings")]
    public string ClearGpuRuntimeText => Loc.Instance["Dictation.ClearGpuRuntime"];

    public string AccelerationStatusText
    {
        get
        {
            var status = _models.ActiveTranscriptionPlugin?.AccelerationStatus;
            if (status is null)
            {
                return LocalModelAcceleration switch
                {
                    AppSettings.LocalModelAccelerationCpu => Loc.Instance["Dictation.AccelCpuActive"],
                    AppSettings.LocalModelAccelerationNvidiaCuda when !CanUseCuda =>
                        FindCuda12LibraryPath() is null
                            ? Loc.Instance["Dictation.AccelCudaNotInstalled"]
                            : Loc.Instance["Dictation.AccelCudaNotVisible"],
                    AppSettings.LocalModelAccelerationNvidiaCuda =>
                        Loc.Instance["Dictation.AccelCudaReady"],
                    _ => Loc.Instance["Dictation.AccelAutoStatus"]
                };
            }

            var text = status.DisplayText;
            if (!string.IsNullOrWhiteSpace(status.Detail))
            {
                text += " — " + status.Detail;
            }

            if (status.RequiresRestart)
            {
                text += " " + Loc.Instance["Dictation.RestartToApply"];
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
            MicrophoneStatus = Loc.Instance["Dictation.PreviewStartFailed"];
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

        if (_modelSelectionCts is not null)
        {
            await _modelSelectionCts.CancelAsync();
        }

        StatusText = Loc.Instance.GetString("Dictation.DeletingModel", selected.DisplayLabel);

        try
        {
            await _models.DeleteModelAsync(selected.ModelId);
            if (_settings.Current.SelectedModelId == selected.ModelId)
            {
                _settings.Save(_settings.Current with { SelectedModelId = null });
            }

            SelectedModel = null;
            StatusText = Loc.Instance.GetString("Dictation.ModelDeleted", selected.DisplayLabel);
        }
        catch (Exception ex)
        {
            StatusText = Loc.Instance.GetString("Dictation.ModelDeleteFailed", ex.Message);
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
        foreach (var d in AudioRecordingService.GetInputDevices())
        {
            Devices.Add(d);
        }

        SelectedDevice = _audio.ResolveConfiguredDevice(
            _settings.Current.SelectedMicrophoneDevice,
            _settings.Current.SelectedMicrophoneDeviceId
        );

        MicrophoneStatus =
            Devices.Count == 0
                ? Loc.Instance["Dictation.NoInputDevices"]
                : Loc.Instance.GetString("Dictation.InputDevicesAvailable", Devices.Count);
    }

    private void RefreshModels()
    {
        var previousSelectedId = SelectedModel?.ModelId ?? _settings.Current.SelectedModelId;

        ModelOptions.Clear();
        foreach (var engine in _pluginManager.TranscriptionEngines)
        {
            foreach (var model in engine.TranscriptionModels)
            {
                var fullModelId = ModelManagerService.GetPluginModelId(
                    engine.GetTranscriptionSelectionId(),
                    model.Id);
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
        // Hydrate the saved acceleration WITHOUT running the change guard: at startup no
        // model is loaded yet, so ActiveTranscriptionPlugin is null and CanUseCuda would
        // read false even when the runtime is fully provisioned — the guard would then
        // revert a saved "nvidia-cuda" back to CPU and persist it, losing the user's
        // choice. The model-load path provisions/falls back and AccelerationStatus
        // reports the truth; we just reflect the saved value here.
        _suppressAccelerationGuard = true;
        LocalModelAcceleration = AppSettings.NormalizeLocalModelAcceleration(
            settings.LocalModelAcceleration
        );
        _suppressAccelerationGuard = false;
        ModelStoragePath = _modelStorage.ResolvedModelStoragePath;
        IsUsingCustomModelStorage =
            AppSettings.NormalizeLocalModelStoragePath(settings.LocalModelStoragePath) is not null;
        AutoPaste = settings.AutoPaste;
        AutoAddDictionaryCorrections = settings.AutoAddDictionaryCorrections;
        TargetAppCorrectionLearningEnabled = settings.TargetAppCorrectionLearningEnabled;
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
        ActiveModelLabel = string.IsNullOrEmpty(active)
            ? Loc.Instance["Dictation.NoModelLoaded"]
            : Loc.Instance.GetString("Dictation.ActiveModel", active);

        var selected = SelectedModel;
        if (selected is null)
        {
            EngineName = Loc.Instance["Dictation.NoEngineSelected"];
            ModelStatusText = Loc.Instance["Dictation.StatusNotSelected"];
            ModelReady = false;
            OnPropertyChanged(nameof(CanDeleteSelectedModel));
            return;
        }

        EngineName = selected.EngineName;
        var status = _models.GetStatus(selected.ModelId);
        ModelReady = status.Type == ModelStatusType.Ready;
        ModelStatusText = status.Type switch
        {
            ModelStatusType.Ready => Loc.Instance["Dictation.StatusReady"],
            ModelStatusType.Loading => Loc.Instance["Dictation.StatusLoading"],
            ModelStatusType.Downloading => Loc.Instance.GetString(
                "Dictation.StatusDownloading",
                status.Progress.ToString("P0")
            ),
            ModelStatusType.Error => FormatModelStatusError(status.ErrorMessage),
            _ => Loc.Instance["Dictation.StatusNotReady"]
        };
        OnPropertyChanged(nameof(CanDeleteSelectedModel));
        OnPropertyChanged(nameof(CanUseCuda));
        OnPropertyChanged(nameof(ShowCudaLibraryPathAction));
        OnPropertyChanged(nameof(ShowDownloadCudaRuntimeAction));
        OnPropertyChanged(nameof(AccelerationStatusText));
        _ = RefreshSelectedPluginCudaProvisionedAsync();
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
        // During settings hydration just reflect the saved value — no revert, no persist,
        // no reload (see RefreshFromSettings). The guard below is only for live user edits.
        if (_suppressAccelerationGuard)
        {
            OnPropertyChanged(nameof(SelectedAccelerationOption));
            OnPropertyChanged(nameof(AccelerationStatusText));
            return;
        }

        var normalized = AppSettings.NormalizeLocalModelAcceleration(value);

        // Guard: CUDA can't be selected when unavailable; Auto always works and resolves to CPU.
        if (normalized == AppSettings.LocalModelAccelerationNvidiaCuda && !CanUseCuda)
        {
            // Three distinct cases: no GPU; GPU + libs found but not on loader path; libs not installed.
            var message =
                !_commands.HasCudaGpu
                    ? Loc.Instance["Dictation.CudaNoGpu"]
                    : FindCuda12LibraryPath() is not null
                        ? Loc.Instance["Dictation.CudaNotOnPath"]
                        : Loc.Instance["Dictation.CudaRuntimeMissing"];
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
        ModelStorageStatusText = Loc.Instance["Dictation.ModelStorageMoving"];
        try
        {
            await _modelStorage.MoveDownloadsAndUsePathAsync(folderPath);
            ModelStorageStatusText = Loc.Instance["Dictation.ModelStorageUpdated"];
        }
        catch (LocalModelStorageUnavailableException ex)
        {
            ModelStorageStatusText = ex.Reason switch
            {
                LocalModelStorageUnavailableReason.DoesNotExist =>
                    Loc.Instance.GetString("Dictation.ModelStorageDoesNotExist", ex.Path),
                LocalModelStorageUnavailableReason.NotWritable =>
                    Loc.Instance.GetString("Dictation.ModelStorageNotWritable", ex.Path),
                LocalModelStorageUnavailableReason.NestedUnderCurrentFolder =>
                    Loc.Instance.GetString(
                        "Dictation.ModelStorageNestedUnderCurrent", ex.Path, ex.CurrentPath ?? string.Empty),
                _ => Loc.Instance.GetString("Dictation.ModelStorageChangeFailed", ex.Message)
            };
        }
        catch (Exception ex)
        {
            ModelStorageStatusText = Loc.Instance.GetString("Dictation.ModelStorageChangeFailed", ex.Message);
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
        ModelStorageStatusText = Loc.Instance["Dictation.ModelStorageResetStatus"];
    }

    private async Task ReloadActiveModelForAccelerationChangeAsync(DictationModelOption selected)
    {
        try
        {
            await _models.EnsureModelLoadedAsync(selected.ModelId);
        }
        catch (Exception ex)
        {
            StatusText = Loc.Instance.GetString(
                "Dictation.ModelReloadFailed",
                FormatModelStatusError(ex.Message)
            );
        }
        finally
        {
            OnPropertyChanged(nameof(AccelerationStatusText));
            RefreshModelState();
        }
    }

    private async Task DownloadAndLoadSelectedModelAsync(DictationModelOption selected)
    {
        if (_modelSelectionCts is not null)
        {
            await _modelSelectionCts.CancelAsync();
        }

        _modelSelectionCts?.Dispose();
        var cts = _modelSelectionCts = new CancellationTokenSource();

        try
        {
            StatusText = _models.IsDownloaded(selected.ModelId)
                ? Loc.Instance.GetString("Dictation.LoadingModel", selected.DisplayLabel)
                : Loc.Instance.GetString("Dictation.DownloadingModel", selected.DisplayLabel);

            await _models.DownloadAndLoadModelAsync(selected.ModelId, cts.Token);

            if (cts.IsCancellationRequested || SelectedModel?.ModelId != selected.ModelId)
            {
                return;
            }

            StatusText = Loc.Instance.GetString("Dictation.ModelIsReady", selected.DisplayLabel);
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
                StatusText = Loc.Instance.GetString(
                    "Dictation.ModelSetupFailed",
                    FormatModelStatusError(ex.Message)
                );
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
                StatusText = Loc.Instance["Dictation.NoHomeDirectory"];
                return;
            }

            var cudaLibraryPath = FindCuda12LibraryPath();
            if (cudaLibraryPath is null)
            {
                StatusText = Loc.Instance["Dictation.CudaLibsMissingRetry"];
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
            CudaSetupStatus = Loc.Instance["Dictation.CudaPathSavedDetail"];
            StatusText = Loc.Instance["Dictation.CudaPathSaved"];
        }
        catch (Exception ex)
        {
            CudaSetupStatus = Loc.Instance.GetString("Dictation.CudaPathSaveFailed", ex.Message);
            StatusText = Loc.Instance.GetString("Dictation.ShellProfileUpdateFailed", ex.Message);
        }
    }

    // Driver-only host (or partial install): download just the CUDA libraries the selected
    // engine is missing, show progress, then ask the user to restart so a clean process
    // loads the GPU build. The engine itself skips any library already on the system.
    [RelayCommand]
    private async Task DownloadCudaRuntimeAsync()
    {
        var plugin = SelectedModelPlugin;
        if (plugin is not { ProvisionsCudaRuntimeOnDemand: true } || IsDownloadingCudaRuntime)
        {
            return;
        }

        IsDownloadingCudaRuntime = true;
        OnPropertyChanged(nameof(ShowDownloadCudaRuntimeAction));
        StatusText = Loc.Instance["Dictation.CudaDownloadingShort"];
        CudaSetupStatus = Loc.Instance.GetString("Dictation.CudaDownloading", "0%");

        // Marshal progress onto the UI thread — the provisioner reports from a background
        // task, and CudaSetupStatus binds straight into the view.
        var progress = new Progress<double>(p =>
            CudaSetupStatus = Loc.Instance.GetString(
                "Dictation.CudaDownloading",
                Math.Clamp(p, 0, 1).ToString("P0")
            )
        );

        try
        {
            await plugin.EnsureCudaRuntimeReadyAsync(progress, CancellationToken.None);

            // Refresh the cached provisioned flag (cheap now — the cache is populated, so the
            // probe is file-existence checks, no `ldconfig`) so CanUseCuda reads true before
            // we select CUDA below.
            await RefreshSelectedPluginCudaProvisionedAsync();

            // Auto-select CUDA so the single restart we prompt for lands directly on the
            // GPU — the user downloaded the runtime in order to use it, so making them
            // also toggle the dropdown (and restart a second time) is needless friction.
            // CanUseCuda is now true (the selected engine just provisioned the cache), so
            // this persists the preference instead of the guard reverting it to CPU. Set
            // the status text AFTER, since the acceleration-change handler clears it.
            LocalModelAcceleration = AppSettings.LocalModelAccelerationNvidiaCuda;
            CudaSetupStatus = Loc.Instance["Dictation.CudaDownloaded"];
            StatusText = Loc.Instance["Dictation.CudaDownloadedShort"];
        }
        catch (Exception ex)
        {
            CudaSetupStatus = Loc.Instance.GetString("Dictation.CudaDownloadFailed", ex.Message);
            StatusText = Loc.Instance["Dictation.CudaDownloadFailedShort"];
        }
        finally
        {
            IsDownloadingCudaRuntime = false;
            OnPropertyChanged(nameof(CanUseCuda));
            OnPropertyChanged(nameof(ShowDownloadCudaRuntimeAction));
            OnPropertyChanged(nameof(ShowClearGpuRuntimeAction));
            OnPropertyChanged(nameof(ShowCudaLibraryPathAction));
            OnPropertyChanged(nameof(AccelerationStatusText));
        }
    }

    // Recovery for a corrupt cached GPU runtime: delete every provisioning engine's
    // cached CUDA runtime (the shared math libs + each engine's own GPU build) so the
    // next process start re-provisions from scratch. The libs already dlopen'd this
    // session are held until exit, so the success message prompts the user to restart.
    [RelayCommand]
    private async Task ClearGpuRuntimeAsync()
    {
        var plugin = SelectedModelPlugin;
        if (plugin is not { ProvisionsCudaRuntimeOnDemand: true } || IsClearingGpuRuntime)
        {
            return;
        }

        IsClearingGpuRuntime = true;
        OnPropertyChanged(nameof(ShowClearGpuRuntimeAction));
        CudaSetupStatus = Loc.Instance["Dictation.ClearingGpuRuntime"];

        try
        {
            // Run the (potentially multi-GB) recursive deletes off the UI thread: the
            // service's awaits all complete synchronously on an uncontended lock, so the
            // plugins' synchronous ClearCache()/Directory.Delete would otherwise run on the
            // dispatcher and freeze the UI (the "Clearing…" status wouldn't even paint).
            await Task.Run(() => _models.ClearCudaRuntimeCacheAsync());
            // Re-provisioning happens on the next process start, so don't re-offer the
            // in-session download (it would just re-fetch into a process that can't use it).
            _gpuRuntimeClearedPendingRestart = true;
            CudaSetupStatus = Loc.Instance["Dictation.GpuRuntimeCleared"];
            StatusText = Loc.Instance["Dictation.GpuRuntimeCleared"];
        }
        catch (Exception ex)
        {
            CudaSetupStatus = Loc.Instance.GetString("Dictation.GpuRuntimeClearFailed", ex.Message);
        }
        finally
        {
            IsClearingGpuRuntime = false;
            // The cache is now gone, so the engine reports unprovisioned: this flips
            // _selectedPluginCudaProvisioned false, hiding the Clear button and re-showing
            // Download (the off-thread probe is now cheap file-existence checks).
            await RefreshSelectedPluginCudaProvisionedAsync();
            OnPropertyChanged(nameof(ShowClearGpuRuntimeAction));
            OnPropertyChanged(nameof(ShowDownloadCudaRuntimeAction));
            OnPropertyChanged(nameof(CanUseCuda));
            OnPropertyChanged(nameof(AccelerationStatusText));
        }
    }

    private static string ResolveShellProfilePath(string home)
    {
        var shell = Environment.GetEnvironmentVariable("SHELL") ?? string.Empty;
        if (shell.EndsWith("/zsh", StringComparison.Ordinal))
        {
            return Path.Join(home, ".zshrc");
        }

        return shell.EndsWith("/fish", StringComparison.Ordinal)
            ? Path.Join(home, ".config", "fish", "config.fish")
            : Path.Join(home, ".bashrc");
    }

    private static string GetCudaLibraryPathExport(string profilePath, string cudaLibraryPath)
    {
        return profilePath.EndsWith("config.fish", StringComparison.Ordinal)
            ? $"set -gx LD_LIBRARY_PATH {cudaLibraryPath} $LD_LIBRARY_PATH"
            : $"export LD_LIBRARY_PATH={cudaLibraryPath}:${{LD_LIBRARY_PATH:-}}";
    }

    // ~/.config/environment.d/ is picked up by systemd-environment-d-generator for GUI sessions
    // on Wayland, covering app-menu launches where the shell profile isn't sourced.
    // ReSharper disable once UnusedMethodReturnValue.Local -- returns the written path for callers that want it; the current caller invokes it for its file-writing side effect.
    private static string WriteDesktopEnvironmentFile(string home, string cudaLibraryPath)
    {
        var environmentDir = Path.Join(home, ".config", "environment.d");
        Directory.CreateDirectory(environmentDir);

        var path = Path.Join(environmentDir, "typewhisper-cuda.conf");
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
        OnPropertyChanged(nameof(CanUseCuda));
        OnPropertyChanged(nameof(ShowCudaLibraryPathAction));
        OnPropertyChanged(nameof(ShowDownloadCudaRuntimeAction));
        OnPropertyChanged(nameof(ShowClearGpuRuntimeAction));
        _ = RefreshSelectedPluginCudaProvisionedAsync();
    }

    // Recompute the cached CanUseCuda provisioned flag off the UI thread — the selected
    // engine's IsCudaRuntimeProvisioned can shell out to `ldconfig -p` (~1s) per missing CUDA
    // library, so it must never run inline on a binding (see CanUseCuda). A generation guard
    // drops the result if the selection changed while the probe was in flight.
    private async Task RefreshSelectedPluginCudaProvisionedAsync()
    {
        var generation = ++_cudaProbeGeneration;
        var provisioned = false;
        if (SelectedModelPlugin is { ProvisionsCudaRuntimeOnDemand: true } plugin)
        {
            provisioned = await Task
                .Run(() => plugin.IsCudaRuntimeProvisioned)
                .ConfigureAwait(true);
        }

        if (generation != _cudaProbeGeneration)
        {
            return;
        }

        SetSelectedPluginCudaProvisioned(provisioned);
    }

    private void SetSelectedPluginCudaProvisioned(bool value)
    {
        if (_selectedPluginCudaProvisioned == value)
        {
            return;
        }

        _selectedPluginCudaProvisioned = value;
        OnPropertyChanged(nameof(CanUseCuda));
        OnPropertyChanged(nameof(ShowCudaLibraryPathAction));
        OnPropertyChanged(nameof(ShowDownloadCudaRuntimeAction));
        OnPropertyChanged(nameof(ShowClearGpuRuntimeAction));
        OnPropertyChanged(nameof(AccelerationStatusText));
    }

    private static string FormatModelStatusError(string? message)
    {
        if (!IsCudaMissingLibraryError(message))
        {
            return string.IsNullOrWhiteSpace(message) ? Loc.Instance["Dictation.StatusError"] : message;
        }

        var cudaLibraryPath = FindCuda12LibraryPath();
        return cudaLibraryPath is null
            ? Loc.Instance["Dictation.CudaNotInstalledRestart"]
            : Loc.Instance["Dictation.CudaNotVisibleRestart"];
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

    partial void OnTargetAppCorrectionLearningEnabledChanged(bool value)
    {
        _settings.Save(_settings.Current with { TargetAppCorrectionLearningEnabled = value });
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
            StatusText = Loc.Instance["Dictation.EnterProcessName"];
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
            StatusText = Loc.Instance.GetString("Dictation.UpdatedStrategy", processName);
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
        StatusText = Loc.Instance.GetString("Dictation.AddedStrategy", processName);
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
        StatusText = Loc.Instance.GetString("Dictation.RemovedStrategy", row.ProcessName);
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

    // Lower and upper bounds for how quiet ducking may make other audio,
    // expressed as the surviving volume fraction. 0.1 => reduce by 90%
    // (near-silent), 0.8 => reduce by 20% (gentle). Keeps the slider out of
    // the sub-audible cliff while still allowing a strong duck.
    private const double MinDuckingLevel = 0.1;
    private const double MaxDuckingLevel = 0.8;

    /// <summary>
    ///     Slider-facing value: how much to reduce other audio, as a percent
    ///     (higher = quieter). Backed by <see cref="AudioDuckingLevel" />, which
    ///     stores the surviving volume fraction the ducking service multiplies by.
    /// </summary>
    public double AudioDuckingReductionPercent
    {
        get => Math.Round((1d - AudioDuckingLevel) * 100d);
        set => AudioDuckingLevel = Math.Clamp(1d - value / 100d, MinDuckingLevel, MaxDuckingLevel);
    }

    partial void OnAudioDuckingLevelChanged(double value)
    {
        _settings.Save(
            _settings.Current with
            {
                AudioDuckingLevel = (float)Math.Clamp(value, MinDuckingLevel, MaxDuckingLevel)
            }
        );
        OnPropertyChanged(nameof(AudioDuckingReductionPercent));
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
            if (!SetProperty(ref _strategy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedStrategyOption));
            _changed();
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