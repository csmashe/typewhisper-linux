using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.Services.Setup;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Linux.ViewModels;

/// <summary>
///     Onboarding wizard. Steps:
///     1. Pick a transcription model (with recommended default).
///     2. Show available extension plugins and their enable state.
///     3. Confirm hotkey + microphone.
///     4. Setup checklist — a machine-driven list of <see cref="ISetupTask" />s
///     (clipboard, automatic paste, global-hotkey registration, active-window
///     detection, …). Each task self-gates on the detected desktop/session,
///     so the same wizard fully configures GNOME/Wayland, KDE, Hyprland, etc.
///     without hard-coding any one of them. Required tasks gate Finish.
///     5. First-dictation check.
///     6. Done — sets HasCompletedOnboarding.
/// </summary>
// MVVM Toolkit [ObservableProperty] generates the On<Property>Changed(value) partial hooks; the
// value parameter is part of the generated signature and cannot be dropped even when ignored here.
// ReSharper disable UnusedParameterInPartialMethod
public partial class WelcomeWizardViewModel : ObservableObject
{
    private const string PasteSmokeExpectedText = "typewhisper paste test";
    private readonly AudioRecordingService _audio;
    private readonly IReadOnlyList<AudioInputDevice>? _availableMics;
    private readonly SystemCommandAvailabilityService _commands;
    private readonly IDictionaryService _dictionary;
    private readonly HotkeyService _hotkey;
    private readonly CancellationTokenSource _lifetimeCts;
    private readonly CancellationToken _lifetimeToken;
    private readonly ModelManagerService _models;
    private readonly PropertyChangedEventHandler _modelStateChangedHandler;
    private readonly PluginManager _pluginManager;
    private readonly EventHandler _pluginStateChangedHandler;
    private readonly ISettingsService _settings;
    private readonly IReadOnlyList<ISetupTask> _setupTasks;
    private readonly TextInsertionService _textInsertion;
    private int _cleanedUp;
    private AudioRecordingService.AudioCaptureSession? _firstDictationCaptureSession;

    [ObservableProperty]
    private string _cudaBenchmarkStatus = Loc.Instance["Wizard.CudaBenchmarkIdle"];

    [ObservableProperty]
    private string _firstDictationStatus = Loc.Instance["Wizard.FirstDictationIdle"];

    [ObservableProperty]
    private string _firstDictationText = "";

    [ObservableProperty]
    private string _hotkeyStatus = "";

    [ObservableProperty]
    private string _hotkeyText = "";

    [ObservableProperty]
    private bool _isCudaBenchmarkRunning;

    [ObservableProperty]
    private bool _isFirstDictationRecording;

    [ObservableProperty]
    private bool _isMicTestRunning;

    [ObservableProperty]
    private bool _isModelDownloading;

    [ObservableProperty]
    private double _micLevel;

    [ObservableProperty]
    private string _micTestStatus = Loc.Instance["Wizard.MicTestIdle"];

    [ObservableProperty]
    private double _modelDownloadProgress;

    [ObservableProperty]
    private string _modelStatus = "";

    [ObservableProperty]
    private string _pasteSmokeText = "";

    [ObservableProperty]
    private bool _pasteTestPassed;

    [ObservableProperty]
    private string _pasteTestStatus = Loc.Instance["Wizard.PasteTestIdle"];

    [ObservableProperty]
    private string _selectedIndustryPresetId = "general";

    [ObservableProperty]
    private AudioInputDevice? _selectedMic;

    [ObservableProperty]
    private WizardModelRow? _selectedModel;

    [ObservableProperty]
    private string _setupSummary = "";

    [ObservableProperty]
    private bool _showReloginNotice;

    [ObservableProperty]
    private int _stepIndex;

    public WelcomeWizardViewModel(
        ModelManagerService models,
        PluginManager pluginManager,
        HotkeyService hotkey,
        AudioRecordingService audio,
        SystemCommandAvailabilityService commands,
        TextInsertionService textInsertion,
        IEnumerable<ISetupTask> setupTasks,
        IDictionaryService dictionary,
        ISettingsService settings
    )
        : this(
            models,
            pluginManager,
            hotkey,
            audio,
            commands,
            textInsertion,
            setupTasks,
            dictionary,
            settings,
            availableMics: null
        )
    {
    }

    internal WelcomeWizardViewModel(
        ModelManagerService models,
        PluginManager pluginManager,
        HotkeyService hotkey,
        AudioRecordingService audio,
        SystemCommandAvailabilityService commands,
        TextInsertionService textInsertion,
        IEnumerable<ISetupTask> setupTasks,
        IDictionaryService dictionary,
        ISettingsService settings,
        IReadOnlyList<AudioInputDevice>? availableMics
    )
    {
        _lifetimeCts = new CancellationTokenSource();
        _lifetimeToken = _lifetimeCts.Token;
        _models = models;
        _pluginManager = pluginManager;
        _hotkey = hotkey;
        _audio = audio;
        _availableMics = availableMics;
        _commands = commands;
        _textInsertion = textInsertion;
        _setupTasks = setupTasks.Where(t => t.AppliesToThisMachine()).ToArray();
        _dictionary = dictionary;
        _settings = settings;

        _pluginStateChangedHandler = (_, _) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsAbandoned)
                {
                    RefreshPluginState();
                }
            });
        _modelStateChangedHandler = (_, _) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsAbandoned)
                {
                    OnModelStatusChanged();
                }
            });
        _pluginManager.PluginStateChanged += _pluginStateChangedHandler;
        _models.PropertyChanged += _modelStateChangedHandler;
        _audio.LevelChanged += OnAudioLevelChanged;

        LoadIndustryPresets();
        LoadModels();
        LoadExtensions();
        LoadMics();
        RefreshStepDots();

        HotkeyText = _hotkey.CurrentHotkeyString;
    }

    public ObservableCollection<WizardModelRow> AvailableModels { get; } = [];
    public ObservableCollection<PluginRow> ExtensionPlugins { get; } = [];
    public ObservableCollection<SetupTaskRow> SetupItems { get; } = [];
    public ObservableCollection<WelcomeStepDot> StepDots { get; } = [];
    public ObservableCollection<AudioInputDevice> Mics { get; } = [];
    public ObservableCollection<IndustryPreset> IndustryPresets { get; } = [];

    private static int StepCount => 6;
    public bool IsFirstStep => StepIndex == 0;
    public bool IsLastStep => StepIndex == StepCount - 1;
    public string NextLabel => IsLastStep ? Loc.Instance["Common.Finish"] : Loc.Instance["Common.Next"];
    public string StepText => Loc.Instance.GetString("Wizard.StepText", StepIndex + 1, StepCount);

    // All required, machine-applicable tasks must be satisfied before Finish is allowed.
    public bool AllRequiredReady =>
        SetupItems.Where(r => r.IsRequired).All(r => r.IsSatisfied);

    // Final step is blocked until all required tasks are ready; Skip bypasses this.
    public bool CanAdvance => !IsLastStep || AllRequiredReady;
    public string MicTestButtonText =>
        IsMicTestRunning ? Loc.Instance["Wizard.StopMicTest"] : Loc.Instance["Wizard.StartMicTest"];

    public string FirstDictationButtonText =>
        IsFirstDictationRecording
            ? Loc.Instance["Wizard.StopAndTranscribe"]
            : Loc.Instance["Wizard.RecordPhrase"];

    // Gate on GPU presence, not on CUDA being fully usable — the check diagnoses GPU
    // acceleration even when CUDA libs are missing (RunCudaBenchmarkAsync reports that case).
    // Only machines with no NVIDIA GPU have nothing to check.
    public bool CanRunCudaBenchmark => _commands.GetSnapshot().HasCudaGpu;
    public bool CudaBenchmarkButtonEnabled => CanRunCudaBenchmark && !IsCudaBenchmarkRunning;

    public string ModelDownloadPercentText => $"{ModelDownloadProgress * 100:0}%";

    public async Task<bool> RunPasteSmokeTestAsync()
    {
        if (IsAbandoned)
        {
            return false;
        }

        PasteTestPassed = false;
        PasteSmokeText = "";
        PasteTestStatus = Loc.Instance["Wizard.PasteTestRunning"];

        InsertionResult result;
        try
        {
            result = await _textInsertion.InsertTextAsync(
                PasteSmokeExpectedText,
                strategy: TextInsertionStrategy.ClipboardPaste
            );
        }
        catch (Exception ex)
        {
            if (IsAbandoned)
            {
                return false;
            }

            PasteSmokeText = ex.Message;
            PasteTestPassed = false;
            PasteTestStatus = Loc.Instance.GetString("Wizard.PasteTestFailed", ex.Message);
            return false;
        }

        if (IsAbandoned)
        {
            return false;
        }

        // Remaining values (Pasted, Typed, NoText, …) are handled by the check below.
        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault -- only the actionable cases are handled; remaining enum values are deliberate no-ops.
        switch (result)
        {
            case InsertionResult.MissingClipboardTool:
                PasteTestStatus = Loc.Instance["Wizard.PasteTestMissingClipboard"];
                return false;
            case InsertionResult.MissingPasteTool:
                PasteTestStatus = Loc.Instance.GetString(
                    "Wizard.PasteTestMissingPaste",
                    _commands.GetSnapshot().PasteToolInstallHint
                );
                return false;
            case InsertionResult.CopiedToClipboard:
                PasteTestStatus = Loc.Instance["Wizard.PasteTestCopiedOnly"];
                return false;
        }

        if (result is not InsertionResult.Pasted)
        {
            PasteTestStatus = Loc.Instance.GetString("Wizard.PasteTestUnexpected", result);
            return false;
        }

        PasteTestStatus = Loc.Instance["Wizard.PasteTestSent"];
        return true;
    }

    public void CompletePasteSmokeTest(string? actualText)
    {
        if (IsAbandoned)
        {
            return;
        }

        PasteSmokeText = actualText ?? "";
        PasteTestPassed = PasteSmokeText.Contains(
            PasteSmokeExpectedText,
            StringComparison.OrdinalIgnoreCase
        );
        PasteTestStatus = PasteTestPassed
            ? Loc.Instance["Wizard.PasteTestPassed"]
            : Loc.Instance["Wizard.PasteTestNotFound"];
    }

    // Guards against Avalonia firing Closed more than once on certain backends.
    public void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleanedUp, 1) != 0)
        {
            return;
        }

        _pluginManager.PluginStateChanged -= _pluginStateChangedHandler;
        _models.PropertyChanged -= _modelStateChangedHandler;
        _audio.LevelChanged -= OnAudioLevelChanged;
        try
        {
            _lifetimeCts.Cancel();
        }
        catch (AggregateException)
        {
            // A cancellation callback must not block the rest of close cleanup.
        }
        finally
        {
            _lifetimeCts.Dispose();
        }

        if (IsMicTestRunning)
        {
            _audio.StopPreview();
        }

        var firstDictationCaptureSession = _firstDictationCaptureSession;
        _firstDictationCaptureSession = null;
        if (firstDictationCaptureSession is not null)
        {
            FireAndLog(
                // ReSharper disable once MethodSupportsCancellation -- teardown path; the CTS is already cancelled and disposed, so forwarding it would just fault the stop.
                () => _audio.StopRecordingAsync(firstDictationCaptureSession),
                "welcome wizard stop recording"
            );
        }

        IsMicTestRunning = false;
        IsFirstDictationRecording = false;
        MicLevel = 0;
    }

    public event EventHandler? RequestClose;

    private void LoadIndustryPresets()
    {
        IndustryPresets.Clear();
        foreach (var preset in IndustryPreset.All)
        {
            IndustryPresets.Add(preset);
        }

        var saved = _settings.Current.SelectedIndustryPresetId;
        SelectedIndustryPresetId = IndustryPresets.Any(p =>
            string.Equals(p.Id, saved, StringComparison.OrdinalIgnoreCase)
        )
            ? saved
            : "general";
    }

    private void LoadModels()
    {
        var previousSelectedId = SelectedModel?.ModelId ?? _settings.Current.SelectedModelId;

        AvailableModels.Clear();
        foreach (var engine in _pluginManager.TranscriptionEngines)
        {
            foreach (var model in engine.TranscriptionModels)
            {
                var modelId = ModelManagerService.GetPluginModelId(engine.GetTranscriptionSelectionId(), model.Id);
                var downloaded = engine.SupportsModelDownload
                    ? engine.IsModelDownloaded(model.Id)
                    : engine.IsConfigured;
                AvailableModels.Add(
                    new WizardModelRow(
                        modelId,
                        $"{engine.ProviderDisplayName} — {model.DisplayName}",
                        model.SizeDescription ?? "",
                        downloaded,
                        model.IsRecommended
                    )
                );
            }
        }

        SelectedModel =
            AvailableModels.FirstOrDefault(m => m.ModelId == previousSelectedId)
            ?? AvailableModels.FirstOrDefault(m => m.IsRecommended)
            ?? AvailableModels.FirstOrDefault();
    }

    private void LoadExtensions()
    {
        ExtensionPlugins.Clear();
        foreach (var p in _pluginManager.AllPlugins)
        {
            ExtensionPlugins.Add(
                new PluginRow(
                    null,
                    p.Manifest.Id,
                    p.Manifest.Name,
                    p.Manifest.Version,
                    p.Manifest.Description ?? "",
                    p.Metadata,
                    false,
                    _pluginManager.IsEnabled(p.Manifest.Id)
                )
            );
        }
    }

    private void LoadMics()
    {
        Mics.Clear();
        foreach (var d in _availableMics ?? AudioRecordingService.GetInputDevices())
        {
            Mics.Add(d);
        }

        SelectedMic = _audio.ResolveConfiguredDevice(
            _settings.Current.SelectedMicrophoneDevice,
            _settings.Current.SelectedMicrophoneDeviceId
        );
    }

    private void RefreshPluginState()
    {
        if (IsAbandoned)
        {
            return;
        }

        foreach (var existing in ExtensionPlugins)
        {
            var isEnabled = _pluginManager.IsEnabled(existing.Id);
            if (isEnabled != existing.IsEnabled)
            {
                existing.IsEnabled = isEnabled;
            }
        }

        LoadModels();
    }

    // Posted on every ModelManagerService status change (including download-progress ticks).
    // Update the progress bar first: RefreshModelState does heavier per-model file probing
    // that can throw mid-download, and a throw there must not swallow the progress update.
    // While downloading, skip RefreshModelState — whisper.cpp ticks are unthrottled and
    // running the heavy probe on each one would saturate the UI thread.
    private void OnModelStatusChanged()
    {
        if (IsAbandoned)
        {
            return;
        }

        UpdateDownloadProgress();

        if (!IsModelDownloading)
        {
            RefreshModelState();
        }
    }

    private void RefreshModelState()
    {
        for (var i = 0; i < AvailableModels.Count; i++)
        {
            var existing = AvailableModels[i];
            var (pluginId, rawModelId) = ModelManagerService.ParsePluginModelId(existing.ModelId);
            var engine = _pluginManager.TranscriptionEngines.FirstOrDefault(e =>
                e.GetTranscriptionSelectionId() == pluginId
            );
            if (engine is null)
            {
                continue;
            }

            var downloaded = engine.SupportsModelDownload
                ? engine.IsModelDownloaded(rawModelId)
                : engine.IsConfigured;
            if (downloaded == existing.IsDownloaded)
            {
                continue;
            }

            AvailableModels[i] = existing with { IsDownloaded = downloaded };
            if (SelectedModel?.ModelId == existing.ModelId)
            {
                SelectedModel = AvailableModels[i];
            }
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

    partial void OnSelectedModelChanged(WizardModelRow? value)
    {
        UpdateDownloadProgress();
    }

    partial void OnStepIndexChanged(int value)
    {
        if (IsAbandoned)
        {
            return;
        }

        OnPropertyChanged(nameof(IsFirstStep));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(NextLabel));
        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(CanAdvance));
        RefreshStepDots();

        // Re-evaluate the checklist on steps 3 and 5 so the Finish gate stays current
        // (the user may have fixed things between steps).
        if (value is 3 or 5)
        {
            _ = RefreshSetupAsync();
        }
    }

    partial void OnIsMicTestRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(MicTestButtonText));
    }

    partial void OnIsFirstDictationRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(FirstDictationButtonText));
    }

    partial void OnIsCudaBenchmarkRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CudaBenchmarkButtonEnabled));
    }

    partial void OnHotkeyTextChanged(string value)
    {
        HotkeyStatus = "";
    }

    [RelayCommand]
    private void Back()
    {
        if (IsAbandoned)
        {
            return;
        }

        if (StepIndex > 0)
        {
            StepIndex--;
        }
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        if (IsAbandoned)
        {
            return;
        }

        // Step 0: pick model — download/load before advancing
        if (StepIndex == 0)
        {
            if (SelectedModel is not { } row)
            {
                ModelStatus = Loc.Instance["Wizard.NoModelsAvailable"];
                return;
            }

            var needsDownload = !_models.IsDownloaded(row.ModelId);
            ModelStatus = needsDownload
                ? Loc.Instance.GetString("Wizard.Downloading", row.DisplayName)
                : Loc.Instance.GetString("Wizard.Loading", row.DisplayName);

            // Show the progress bar immediately for a real download so it's
            // visible from 0% — the status-change handler then drives it live.
            if (needsDownload)
            {
                ModelDownloadProgress = 0;
                IsModelDownloading = true;
            }

            try
            {
                await _models.DownloadAndLoadModelAsync(row.ModelId, _lifetimeToken);
                if (IsAbandoned)
                {
                    return;
                }

                _settings.Save(_settings.Current with { SelectedModelId = row.ModelId });
                ModelStatus = Loc.Instance.GetString("Wizard.ModelReady", row.DisplayName);
                IsModelDownloading = false;
                RefreshModelState();
            }
            catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (IsAbandoned)
                {
                    return;
                }

                IsModelDownloading = false;
                ModelStatus = Loc.Instance.GetString("Wizard.ModelFailed", ex.Message);
                return;
            }
        }

        // Step 2: save hotkey + mic
        if (StepIndex == 2)
        {
            if (!_hotkey.TrySetHotkeyFromString(HotkeyText))
            {
                HotkeyStatus = Loc.Instance.GetString("Wizard.HotkeyParseFailed", HotkeyText);
                return;
            }

            _settings.Save(_settings.Current with { ToggleHotkey = _hotkey.CurrentHotkeyString });
            HotkeyText = _hotkey.CurrentHotkeyString;
            HotkeyStatus = Loc.Instance.GetString("Wizard.HotkeySet", _hotkey.CurrentHotkeyString);

            if (SelectedMic is not null)
            {
                _audio.SelectedDeviceIndex = SelectedMic.Index;
                _settings.Save(
                    _settings.Current with
                    {
                        SelectedMicrophoneDevice = SelectedMic.Index,
                        SelectedMicrophoneDeviceId = SelectedMic.PersistentId,
                    }
                );
            }
        }

        if (IsLastStep)
        {
            // Button is disabled in this state too, but guard here so a stray
            // invocation can't bypass it. Skip is the explicit escape hatch.
            if (!AllRequiredReady)
            {
                return;
            }

            FinishOnboardingWithIndustryPreset();
            RequestClose?.Invoke(this, EventArgs.Empty);
            return;
        }

        StepIndex++;
    }

    [RelayCommand]
    private void Skip()
    {
        if (IsAbandoned)
        {
            return;
        }

        FinishOnboardingWithIndustryPreset();
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private void FinishOnboardingWithIndustryPreset()
    {
        _dictionary.ApplyIndustryPreset(SelectedIndustryPresetId);
        _settings.Save(
            _settings.Current with
            {
                HasCompletedOnboarding = true,
                SelectedIndustryPresetId = SelectedIndustryPresetId,
                EnabledPackIds = IndustryPreset.MergeIntoEnabledPackIds(
                    _settings.Current.EnabledPackIds,
                    SelectedIndustryPresetId
                ),
            }
        );
    }

    [RelayCommand]
    private async Task TogglePluginEnabledAsync(PluginRow row)
    {
        if (IsAbandoned)
        {
            return;
        }

        if (row.IsEnabled)
        {
            await _pluginManager.DisablePluginAsync(row.Id);
        }
        else
        {
            await _pluginManager.EnablePluginAsync(row.Id);
        }
    }

    /// <summary>
    ///     Re-evaluates all applicable setup tasks and updates <see cref="SetupItems" />.
    ///     Runs off the UI thread (tasks may spawn gdbus/gsettings); rows mid-action are skipped.
    /// </summary>
    private async Task RefreshSetupAsync()
    {
        if (IsAbandoned)
        {
            return;
        }

        if (SetupItems.Count == 0)
        {
            foreach (var task in _setupTasks)
            {
                SetupItems.Add(new SetupTaskRow(task));
            }
        }

        foreach (var row in SetupItems)
        {
            if (row.IsBusy)
            {
                continue;
            }

            SetupTaskState state;
            try
            {
                state = await Task.Run(
                        () => row.Source.EvaluateAsync(_lifetimeToken),
                        _lifetimeToken
                    )
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (IsAbandoned)
                {
                    return;
                }

                state = new SetupTaskState(
                    SetupTaskStatusKind.Failed,
                    Loc.Instance.GetString("Wizard.SetupCheckFailed", ex.Message)
                );
            }

            if (IsAbandoned)
            {
                return;
            }

            row.Apply(state);
        }

        if (IsAbandoned)
        {
            return;
        }

        RefreshSetupGating();
    }

    private void RefreshSetupGating()
    {
        // Show a re-login notice when the hotkey task added the user to the input group
        // (group membership only activates after re-login).
        var hotkeyRow = SetupItems.FirstOrDefault(r => r.Id == "global-hotkey");
        ShowReloginNotice =
            hotkeyRow is not null
            && hotkeyRow.Summary.Contains("log out", StringComparison.OrdinalIgnoreCase);

        var outstanding = SetupItems.Where(r => r is { IsRequired: true, IsSatisfied: false }).ToList();
        SetupSummary = SetupItems.All(r => r.IsSatisfied)
            ? Loc.Instance["Wizard.SetupAllSet"]
            : outstanding.Count == 0
                ? Loc.Instance["Wizard.SetupRequiredReady"]
                : Loc.Instance.GetString(
                    "Wizard.SetupOutstanding",
                    outstanding.Count,
                    string.Join(", ", outstanding.Select(r => r.Title))
                );

        OnPropertyChanged(nameof(AllRequiredReady));
        OnPropertyChanged(nameof(CanAdvance));
        OnPropertyChanged(nameof(CanRunCudaBenchmark));
        OnPropertyChanged(nameof(CudaBenchmarkButtonEnabled));
    }

    [RelayCommand]
    private async Task RunSetupActionAsync(SetupTaskRow? row)
    {
        if (IsAbandoned || row is null || row.IsBusy)
        {
            return;
        }

        row.BeginAction();
        RefreshSetupGating();

        SetupActionOutcome outcome;
        try
        {
            outcome = await Task.Run(
                    () => row.Source.RunActionAsync(_lifetimeToken),
                    _lifetimeToken
                )
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            if (IsAbandoned)
            {
                return;
            }

            outcome = new SetupActionOutcome(
                false,
                Loc.Instance.GetString("Wizard.SetupActionFailed", ex.Message)
            );
        }

        if (IsAbandoned)
        {
            return;
        }

        row.EndAction(outcome);

        // Re-evaluate all tasks: one install can satisfy several (e.g. a shared package).
        await RefreshSetupAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RecheckSetupAsync()
    {
        if (IsAbandoned)
        {
            return;
        }

        await RefreshSetupAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void ToggleMicTest()
    {
        if (IsAbandoned)
        {
            return;
        }

        if (IsMicTestRunning)
        {
            _audio.StopPreview();
            IsMicTestRunning = false;
            MicLevel = 0;
            MicTestStatus = Loc.Instance["Wizard.MicTestStopped"];
            return;
        }

        if (SelectedMic is not null)
        {
            _audio.SelectedDeviceIndex = SelectedMic.Index;
        }

        if (_audio.StartPreview())
        {
            IsMicTestRunning = true;
            MicTestStatus = Loc.Instance["Wizard.MicTestListening"];
        }
        else
        {
            IsMicTestRunning = false;
            MicLevel = 0;
            MicTestStatus = Loc.Instance["Wizard.MicTestStartFailed"];
        }
    }

    [RelayCommand]
    private async Task ToggleFirstDictationAsync()
    {
        if (IsAbandoned)
        {
            return;
        }

        if (!IsFirstDictationRecording)
        {
            if (IsMicTestRunning)
            {
                ToggleMicTest();
            }

            FirstDictationText = "";
            FirstDictationStatus = Loc.Instance["Wizard.FirstDictationRecording"];
            if (SelectedMic is not null)
            {
                _audio.SelectedDeviceIndex = SelectedMic.Index;
            }

            _firstDictationCaptureSession = null;
            try
            {
                _firstDictationCaptureSession = _audio.TryStartRecording(
                    _settings.Current.WhisperModeEnabled
                );
            }
            catch (Exception ex)
            {
                FirstDictationStatus = Loc.Instance.GetString(
                    "Wizard.FirstDictationStartFailed",
                    ex.Message
                );
                IsFirstDictationRecording = false;
                return;
            }

            if (_firstDictationCaptureSession is null)
            {
                FirstDictationStatus = Loc.Instance["Wizard.FirstDictationStartFailedGeneric"];
                IsFirstDictationRecording = false;
                return;
            }

            IsFirstDictationRecording = true;
            return;
        }

        IsFirstDictationRecording = false;
        FirstDictationStatus = Loc.Instance["Wizard.FirstDictationStopping"];
        var captureSession = _firstDictationCaptureSession;
        _firstDictationCaptureSession = null;
        byte[] wav;
        try
        {
            wav = captureSession is null
                ? []
                // ReSharper disable once MethodSupportsCancellation -- must run to completion to return the captured audio; intentionally non-cancellable.
                : await _audio.StopRecordingAsync(captureSession);
        }
        catch (Exception ex)
        {
            if (IsAbandoned)
            {
                return;
            }

            FirstDictationStatus = Loc.Instance.GetString("Wizard.RecordingFailed", ex.Message);
            return;
        }

        if (IsAbandoned)
        {
            return;
        }

        if (wav.Length == 0)
        {
            FirstDictationStatus = Loc.Instance["Wizard.NoAudioCaptured"];
            return;
        }

        try
        {
            ModelManagerService.TranscriptionLease lease;
            try
            {
                lease = await _models.AcquireTranscriptionAsync(
                    SelectedModel?.ModelId,
                    cancellationToken: _lifetimeToken
                );
            }
            catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                if (IsAbandoned)
                {
                    return;
                }

                FirstDictationStatus = Loc.Instance["Wizard.ModelLoadFailed"];
                return;
            }

            string transcript;
            await using (lease)
            {
                if (IsAbandoned)
                {
                    return;
                }

                var plugin = lease.Plugin;
                FirstDictationStatus = Loc.Instance.GetString(
                    "Wizard.Transcribing",
                    plugin.ProviderDisplayName
                );
                var result = await plugin.TranscribeAsync(
                    wav,
                    null,
                    false,
                    null,
                    _lifetimeToken
                );
                if (IsAbandoned)
                {
                    return;
                }

                // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract -- Text comes from an external ITranscriptionEnginePlugin; its non-null annotation may not hold, keep the defensive ?.
                transcript = result.Text?.Trim() ?? "";
            }

            if (IsAbandoned)
            {
                return;
            }

            FirstDictationText = transcript;
            FirstDictationStatus = string.IsNullOrWhiteSpace(FirstDictationText)
                ? Loc.Instance["Wizard.NoTextReturned"]
                : Loc.Instance["Wizard.FirstDictationPassed"];
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            // Cancellation during teardown is expected; swallow it.
        }
        catch (Exception ex)
        {
            if (IsAbandoned)
            {
                return;
            }

            FirstDictationStatus = Loc.Instance.GetString("Wizard.TranscriptionFailed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RunCudaBenchmarkAsync()
    {
        if (IsAbandoned || IsCudaBenchmarkRunning)
        {
            return;
        }

        if (!CanRunCudaBenchmark)
        {
            CudaBenchmarkStatus = _commands.GetSnapshot().CudaStatus;
            return;
        }

        IsCudaBenchmarkRunning = true;
        CudaBenchmarkStatus = Loc.Instance["Wizard.CudaChecking"];
        try
        {
            var result = await _commands.RunCudaBenchmarkAsync(_lifetimeToken);
            if (IsAbandoned)
            {
                return;
            }

            CudaBenchmarkStatus = result.Message;
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            // Cancellation during teardown is expected; swallow it.
        }
        finally
        {
            if (!IsAbandoned)
            {
                IsCudaBenchmarkRunning = false;
            }
        }
    }

    private void OnAudioLevelChanged(object? sender, float level)
    {
        if (IsAbandoned || (!IsMicTestRunning && !IsFirstDictationRecording))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (IsAbandoned)
            {
                return;
            }

            // Raw RMS is typically well below 0.1 for normal speech; ×8 maps it to 0–1 for the meter.
            MicLevel = Math.Clamp(level * 8, 0, 1);
            if (IsMicTestRunning && MicLevel > 0.05)
            {
                MicTestStatus = Loc.Instance["Wizard.MicInputDetected"];
            }
        });
    }

    private static void FireAndLog(Func<Task> start, string label)
    {
        Task task;
        try
        {
            task = start();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[WelcomeWizard] {label} threw synchronously: {ex.Message}");
            return;
        }

        task.ContinueWith(
            t =>
                Trace.WriteLine(
                    $"[WelcomeWizard] {label} faulted: {t.Exception?.GetBaseException().Message}"
                ),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private bool IsAbandoned =>
        Volatile.Read(ref _cleanedUp) != 0 || _lifetimeToken.IsCancellationRequested;

    private void RefreshStepDots()
    {
        while (StepDots.Count < StepCount)
        {
            StepDots.Add(new WelcomeStepDot(StepDots.Count));
        }

        while (StepDots.Count > StepCount)
        {
            StepDots.RemoveAt(StepDots.Count - 1);
        }

        foreach (var dot in StepDots)
        {
            dot.IsActive = dot.Index == StepIndex;
        }
    }
}

public sealed partial class WelcomeStepDot(int index) : ObservableObject
{
    [ObservableProperty]
    private bool _isActive;

    public int Index { get; } = index;
}

public sealed record WizardModelRow(
    string ModelId,
    string DisplayName,
    string SizeDescription,
    bool IsDownloaded,
    bool IsRecommended
);

/// <summary>
///     View-model wrapper around one <see cref="ISetupTask" /> for the setup checklist.
///     <see cref="ActionMessage" /> is kept separately from the evaluated state so it survives
///     the re-evaluation that runs after each action.
/// </summary>
public sealed partial class SetupTaskRow : ObservableObject
{
    [ObservableProperty]
    private string? _actionLabel;

    [ObservableProperty]
    private string? _actionMessage;

    [ObservableProperty]
    private string? _copyCommand;

    [ObservableProperty]
    private string? _detail;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private SetupTaskStatusKind _kind = SetupTaskStatusKind.Working;

    [ObservableProperty]
    private string _summary = Loc.Instance["Wizard.SetupChecking"];

    public SetupTaskRow(ISetupTask source)
    {
        Source = source;
        Title = source.Title;
        IsRequired = source.Severity == SetupTaskSeverity.Required;
    }

    public ISetupTask Source { get; }
    public string Id => Source.Id;
    public string Title { get; }
    public bool IsRequired { get; }
    public string RequirementLabel =>
        IsRequired ? Loc.Instance["Wizard.Required"] : Loc.Instance["Wizard.Recommended"];

    public bool IsSatisfied => Kind == SetupTaskStatusKind.Satisfied;
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public bool HasCopyCommand => !string.IsNullOrWhiteSpace(CopyCommand);
    public bool HasActionMessage => !string.IsNullOrWhiteSpace(ActionMessage);
    public bool CanRunAction => !IsBusy && !string.IsNullOrWhiteSpace(ActionLabel);

    public string StatusTone => Kind switch
    {
        SetupTaskStatusKind.Satisfied => "ok",
        SetupTaskStatusKind.Failed => "error",
        SetupTaskStatusKind.Working => "busy",
        _ => "missing",
    };

    public string StatusGlyph => Kind switch
    {
        SetupTaskStatusKind.Satisfied => "✓",
        SetupTaskStatusKind.Failed => "!",
        SetupTaskStatusKind.Working => "…",
        _ => "•",
    };

    public void Apply(SetupTaskState state)
    {
        Kind = state.Kind;
        Summary = state.Summary;
        Detail = state.Detail;
        ActionLabel = state.ActionLabel;
        CopyCommand = state.CopyCommand;
        NotifyDerived();
    }

    public void BeginAction()
    {
        IsBusy = true;
        Kind = SetupTaskStatusKind.Working;
        ActionMessage = Loc.Instance["Wizard.SetupWorking"];
        NotifyDerived();
    }

    public void EndAction(SetupActionOutcome outcome)
    {
        IsBusy = false;
        ActionMessage = string.IsNullOrWhiteSpace(outcome.Detail)
            ? outcome.Message
            : $"{outcome.Message} {outcome.Detail}";
        NotifyDerived();
    }

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(IsSatisfied));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(HasCopyCommand));
        OnPropertyChanged(nameof(HasActionMessage));
        OnPropertyChanged(nameof(CanRunAction));
        OnPropertyChanged(nameof(StatusTone));
        OnPropertyChanged(nameof(StatusGlyph));
    }
}
