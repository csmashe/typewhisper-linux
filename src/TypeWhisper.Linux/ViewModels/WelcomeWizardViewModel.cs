using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
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
public partial class WelcomeWizardViewModel : ObservableObject
{
    private const string PasteSmokeExpectedText = "typewhisper paste test";
    private readonly AudioRecordingService _audio;
    private readonly SystemCommandAvailabilityService _commands;
    private readonly IDictionaryService _dictionary;
    private readonly HotkeyService _hotkey;
    private readonly ModelManagerService _models;
    private readonly PropertyChangedEventHandler _modelStateChangedHandler;
    private readonly PluginManager _pluginManager;
    private readonly EventHandler _pluginStateChangedHandler;
    private readonly ISettingsService _settings;
    private readonly IReadOnlyList<ISetupTask> _setupTasks;
    private readonly TextInsertionService _textInsertion;
    private bool _cleanedUp;

    [ObservableProperty]
    private string _cudaBenchmarkStatus = "Run CUDA check if you plan to use GPU acceleration.";

    [ObservableProperty]
    private string _firstDictationStatus =
        "Record a short phrase to verify the selected model can transcribe audio.";

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
    private string _micTestStatus = "Start the microphone test and speak normally.";

    [ObservableProperty]
    private double _modelDownloadProgress;

    [ObservableProperty]
    private string _modelStatus = "";

    [ObservableProperty]
    private string _pasteSmokeText = "";

    [ObservableProperty]
    private bool _pasteTestPassed;

    [ObservableProperty]
    private string _pasteTestStatus = "Run the paste test to verify text can land in this wizard.";

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
    {
        _models = models;
        _pluginManager = pluginManager;
        _hotkey = hotkey;
        _audio = audio;
        _commands = commands;
        _textInsertion = textInsertion;
        _setupTasks = setupTasks.Where(t => t.AppliesToThisMachine()).ToArray();
        _dictionary = dictionary;
        _settings = settings;

        _pluginStateChangedHandler = (_, _) => Dispatcher.UIThread.Post(RefreshPluginState);
        _modelStateChangedHandler = (_, _) => Dispatcher.UIThread.Post(OnModelStatusChanged);
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

    public int StepCount => 6;
    public bool IsFirstStep => StepIndex == 0;
    public bool IsLastStep => StepIndex == StepCount - 1;
    public string NextLabel => IsLastStep ? "Finish" : "Next";
    public string StepText => $"Step {StepIndex + 1} of {StepCount}";

    // All required, machine-applicable tasks must be satisfied before Finish is allowed.
    public bool AllRequiredReady =>
        SetupItems.Where(r => r.IsRequired).All(r => r.IsSatisfied);

    // Final step is blocked until all required tasks are ready; Skip bypasses this.
    public bool CanAdvance => !IsLastStep || AllRequiredReady;
    public string MicTestButtonText => IsMicTestRunning ? "Stop mic test" : "Start mic test";

    public string FirstDictationButtonText =>
        IsFirstDictationRecording ? "Stop and transcribe" : "Record phrase";

    // Gate on GPU presence, not on CUDA being fully usable — the check diagnoses GPU
    // acceleration even when CUDA libs are missing (RunCudaBenchmarkAsync reports that case).
    // Only machines with no NVIDIA GPU have nothing to check.
    public bool CanRunCudaBenchmark => _commands.GetSnapshot().HasCudaGpu;
    public bool CudaBenchmarkButtonEnabled => CanRunCudaBenchmark && !IsCudaBenchmarkRunning;

    public string ModelDownloadPercentText => $"{ModelDownloadProgress * 100:0}%";

    public async Task<bool> RunPasteSmokeTestAsync()
    {
        PasteTestPassed = false;
        PasteSmokeText = "";
        PasteTestStatus = "Running paste test...";

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
            PasteSmokeText = ex.Message;
            PasteTestPassed = false;
            PasteTestStatus = $"Paste test failed: {ex.Message}";
            return false;
        }

        if (result is InsertionResult.MissingClipboardTool)
        {
            PasteTestStatus =
                "Clipboard helper is missing; install the helper shown in System check.";
            return false;
        }

        if (result is InsertionResult.MissingPasteTool)
        {
            PasteTestStatus =
                $"Automatic paste helper is missing. {_commands.GetSnapshot().PasteToolInstallHint}";
            return false;
        }

        if (result is InsertionResult.CopiedToClipboard)
        {
            PasteTestStatus = "Paste did not complete; test text was left on the clipboard.";
            return false;
        }

        if (result is not InsertionResult.Pasted)
        {
            PasteTestStatus = $"Paste test returned {result}.";
            return false;
        }

        PasteTestStatus = "Paste command sent. Checking the test field...";
        return true;
    }

    public void CompletePasteSmokeTest(string? actualText)
    {
        PasteSmokeText = actualText ?? "";
        PasteTestPassed = PasteSmokeText.Contains(
            PasteSmokeExpectedText,
            StringComparison.OrdinalIgnoreCase
        );
        PasteTestStatus = PasteTestPassed
            ? "Paste test passed."
            : "Paste test did not find the expected text in the field.";
    }

    // Guards against Avalonia firing Closed more than once on certain backends.
    public void Cleanup()
    {
        if (_cleanedUp)
        {
            return;
        }

        _cleanedUp = true;
        _pluginManager.PluginStateChanged -= _pluginStateChangedHandler;
        _models.PropertyChanged -= _modelStateChangedHandler;
        _audio.LevelChanged -= OnAudioLevelChanged;

        if (IsMicTestRunning)
        {
            _audio.StopPreview();
        }

        if (IsFirstDictationRecording)
        {
            FireAndLog(() => _audio.StopRecordingAsync(), "welcome wizard stop recording");
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
                    p.Manifest.Category,
                    p.Manifest.IsLocal,
                    false,
                    _pluginManager.IsEnabled(p.Manifest.Id)
                )
            );
        }
    }

    private void LoadMics()
    {
        Mics.Clear();
        foreach (var d in _audio.GetInputDevices())
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
        for (var i = 0; i < ExtensionPlugins.Count; i++)
        {
            var existing = ExtensionPlugins[i];
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
            if (downloaded != existing.IsDownloaded)
            {
                AvailableModels[i] = existing with { IsDownloaded = downloaded };
                if (SelectedModel?.ModelId == existing.ModelId)
                {
                    SelectedModel = AvailableModels[i];
                }
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
        if (StepIndex > 0)
        {
            StepIndex--;
        }
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        // Step 0: pick model — download/load before advancing
        if (StepIndex == 0)
        {
            if (SelectedModel is not { } row)
            {
                ModelStatus =
                    "No transcription models are available. Enable a transcription plugin and try again.";
                return;
            }

            var needsDownload = !_models.IsDownloaded(row.ModelId);
            ModelStatus = needsDownload
                ? $"Downloading {row.DisplayName}..."
                : $"Loading {row.DisplayName}...";

            // Show the progress bar immediately for a real download so it's
            // visible from 0% — the status-change handler then drives it live.
            if (needsDownload)
            {
                ModelDownloadProgress = 0;
                IsModelDownloading = true;
            }

            try
            {
                await _models.DownloadAndLoadModelAsync(row.ModelId);
                _settings.Save(_settings.Current with { SelectedModelId = row.ModelId });
                ModelStatus = $"{row.DisplayName} is ready.";
                IsModelDownloading = false;
                RefreshModelState();
            }
            catch (Exception ex)
            {
                IsModelDownloading = false;
                ModelStatus = $"Failed: {ex.Message}";
                return;
            }
        }

        // Step 2: save hotkey + mic
        if (StepIndex == 2)
        {
            if (!_hotkey.TrySetHotkeyFromString(HotkeyText))
            {
                HotkeyStatus = $"Could not parse '{HotkeyText}'. Try Ctrl+Shift+Space or Alt+F9.";
                return;
            }

            _settings.Save(_settings.Current with { ToggleHotkey = _hotkey.CurrentHotkeyString });
            HotkeyText = _hotkey.CurrentHotkeyString;
            HotkeyStatus = $"Hotkey set to {_hotkey.CurrentHotkeyString}.";

            if (SelectedMic is not null)
            {
                _audio.SelectedDeviceIndex = SelectedMic.Index;
                _settings.Save(
                    _settings.Current with
                    {
                        SelectedMicrophoneDevice = SelectedMic.Index,
                        SelectedMicrophoneDeviceId = SelectedMic.PersistentId
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
                )
            }
        );
    }

    [RelayCommand]
    private async Task TogglePluginEnabledAsync(PluginRow row)
    {
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
                state = await Task.Run(() => row.Source.EvaluateAsync(CancellationToken.None))
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                state = new SetupTaskState(
                    SetupTaskStatusKind.Failed,
                    $"Could not check this item: {ex.Message}"
                );
            }

            row.Apply(state);
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

        var outstanding = SetupItems.Where(r => r.IsRequired && !r.IsSatisfied).ToList();
        SetupSummary = SetupItems.All(r => r.IsSatisfied)
            ? "Everything's set — you're ready to dictate."
            : outstanding.Count == 0
                ? "All required items are ready. The remaining items are optional."
                : $"{outstanding.Count} required item(s) still need attention: "
                  + $"{string.Join(", ", outstanding.Select(r => r.Title))}.";

        OnPropertyChanged(nameof(AllRequiredReady));
        OnPropertyChanged(nameof(CanAdvance));
        OnPropertyChanged(nameof(CanRunCudaBenchmark));
        OnPropertyChanged(nameof(CudaBenchmarkButtonEnabled));
    }

    [RelayCommand]
    private async Task RunSetupActionAsync(SetupTaskRow? row)
    {
        if (row is null || row.IsBusy)
        {
            return;
        }

        row.BeginAction();
        RefreshSetupGating();

        SetupActionOutcome outcome;
        try
        {
            outcome = await Task.Run(() => row.Source.RunActionAsync(CancellationToken.None))
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            outcome = new SetupActionOutcome(false, $"Action failed: {ex.Message}");
        }

        row.EndAction(outcome);

        // Re-evaluate all tasks: one install can satisfy several (e.g. a shared package).
        await RefreshSetupAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RecheckSetupAsync()
    {
        await RefreshSetupAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void ToggleMicTest()
    {
        if (IsMicTestRunning)
        {
            _audio.StopPreview();
            IsMicTestRunning = false;
            MicLevel = 0;
            MicTestStatus = "Microphone test stopped.";
            return;
        }

        if (SelectedMic is not null)
        {
            _audio.SelectedDeviceIndex = SelectedMic.Index;
        }

        if (_audio.StartPreview())
        {
            IsMicTestRunning = true;
            MicTestStatus = "Listening. Speak normally and watch the level meter.";
        }
        else
        {
            IsMicTestRunning = false;
            MicLevel = 0;
            MicTestStatus = "Could not start microphone input.";
        }
    }

    [RelayCommand]
    private async Task ToggleFirstDictationAsync()
    {
        if (!IsFirstDictationRecording)
        {
            if (IsMicTestRunning)
            {
                ToggleMicTest();
            }

            FirstDictationText = "";
            FirstDictationStatus = "Recording. Say a short phrase, then stop.";
            if (SelectedMic is not null)
            {
                _audio.SelectedDeviceIndex = SelectedMic.Index;
            }

            try
            {
                _audio.StartRecording();
            }
            catch (Exception ex)
            {
                FirstDictationStatus = $"Could not start recording: {ex.Message}";
                IsFirstDictationRecording = false;
                return;
            }

            if (!_audio.IsRecording)
            {
                FirstDictationStatus = "Could not start recording.";
                IsFirstDictationRecording = false;
                return;
            }

            IsFirstDictationRecording = true;
            return;
        }

        IsFirstDictationRecording = false;
        FirstDictationStatus = "Stopping recording...";
        byte[] wav;
        try
        {
            wav = await _audio.StopRecordingAsync();
        }
        catch (Exception ex)
        {
            FirstDictationStatus = $"Recording failed: {ex.Message}";
            return;
        }

        if (wav.Length == 0)
        {
            FirstDictationStatus = "No audio was captured.";
            return;
        }

        try
        {
            ModelManagerService.TranscriptionLease lease;
            try
            {
                lease = await _models.AcquireTranscriptionAsync(SelectedModel?.ModelId);
            }
            catch (InvalidOperationException)
            {
                FirstDictationStatus = "Could not load the selected transcription model.";
                return;
            }

            string transcript;
            await using (lease)
            {
                var plugin = lease.Plugin;
                FirstDictationStatus = $"Transcribing with {plugin.ProviderDisplayName}...";
                var result = await plugin.TranscribeAsync(
                    wav,
                    null,
                    false,
                    null,
                    CancellationToken.None
                );
                transcript = result.Text?.Trim() ?? "";
            }

            FirstDictationText = transcript;
            FirstDictationStatus = string.IsNullOrWhiteSpace(FirstDictationText)
                ? "The model returned no text."
                : "First dictation test passed.";
        }
        catch (Exception ex)
        {
            FirstDictationStatus = $"Transcription failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RunCudaBenchmarkAsync()
    {
        if (IsCudaBenchmarkRunning)
        {
            return;
        }

        if (!CanRunCudaBenchmark)
        {
            CudaBenchmarkStatus = _commands.GetSnapshot().CudaStatus;
            return;
        }

        IsCudaBenchmarkRunning = true;
        CudaBenchmarkStatus = "Checking CUDA...";
        try
        {
            var result = await _commands.RunCudaBenchmarkAsync();
            CudaBenchmarkStatus = result.Message;
        }
        finally
        {
            IsCudaBenchmarkRunning = false;
        }
    }

    private void OnAudioLevelChanged(object? sender, float level)
    {
        if (!IsMicTestRunning && !IsFirstDictationRecording)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            // Raw RMS is typically well below 0.1 for normal speech; ×8 maps it to 0–1 for the meter.
            MicLevel = Math.Clamp(level * 8, 0, 1);
            if (IsMicTestRunning && MicLevel > 0.05)
            {
                MicTestStatus = "Microphone input detected.";
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
    private string _summary = "Checking…";

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
    public string RequirementLabel => IsRequired ? "Required" : "Recommended";

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
        _ => "missing"
    };

    public string StatusGlyph => Kind switch
    {
        SetupTaskStatusKind.Satisfied => "✓",
        SetupTaskStatusKind.Failed => "!",
        SetupTaskStatusKind.Working => "…",
        _ => "•"
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
        ActionMessage = "Working… (you may be prompted for your admin password).";
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