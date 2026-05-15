using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Insertion;
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
    private readonly SystemCommandAvailabilityService _commands;
    private readonly TextInsertionService _textInsertion;
    private readonly YdotoolSetupHelper _ydotoolSetup;
    private readonly ISettingsService _settings;
    private readonly EventHandler _pluginStateChangedHandler;
    private readonly PropertyChangedEventHandler _modelStateChangedHandler;
    private const string PasteSmokeExpectedText = "typewhisper paste test";
    private bool _cleanedUp;

    public ObservableCollection<WizardModelRow> AvailableModels { get; } = [];
    public ObservableCollection<PluginRow> ExtensionPlugins { get; } = [];
    public ObservableCollection<WelcomeDiagnosticRow> Diagnostics { get; } = [];
    public ObservableCollection<WelcomeStepDot> StepDots { get; } = [];

    [ObservableProperty] private int _stepIndex;
    [ObservableProperty] private WizardModelRow? _selectedModel;
    [ObservableProperty] private string _modelStatus = "";
    [ObservableProperty] private string _hotkeyText = "";
    [ObservableProperty] private string _hotkeyStatus = "";
    [ObservableProperty] private AudioInputDevice? _selectedMic;
    [ObservableProperty] private string _diagnosticsSummary = "";
    [ObservableProperty] private bool _isMicTestRunning;
    [ObservableProperty] private double _micLevel;
    [ObservableProperty] private string _micTestStatus = "Start the microphone test and speak normally.";
    [ObservableProperty] private string _pasteSmokeText = "";
    [ObservableProperty] private string _pasteTestStatus = "Run the paste test to verify text can land in this wizard.";
    [ObservableProperty] private bool _pasteTestPassed;
    [ObservableProperty] private bool _isFirstDictationRecording;
    [ObservableProperty] private string _firstDictationStatus = "Record a short phrase to verify the selected model can transcribe audio.";
    [ObservableProperty] private string _firstDictationText = "";
    [ObservableProperty] private bool _isCudaBenchmarkRunning;
    [ObservableProperty] private string _cudaBenchmarkStatus = "Run CUDA check if you plan to use GPU acceleration.";
    [ObservableProperty] private bool _isYdotoolSetupRunning;
    [ObservableProperty] private string _ydotoolSetupStatus = "";
    [ObservableProperty] private bool _showYdotoolSetupSection;
    public ObservableCollection<AudioInputDevice> Mics { get; } = [];

    public int StepCount => 6;
    public bool IsFirstStep => StepIndex == 0;
    public bool IsLastStep => StepIndex == StepCount - 1;
    public string NextLabel => IsLastStep ? "Finish" : "Next";
    public string StepText => $"Step {StepIndex + 1} of {StepCount}";
    public string MicTestButtonText => IsMicTestRunning ? "Stop mic test" : "Start mic test";
    public string FirstDictationButtonText => IsFirstDictationRecording ? "Stop and transcribe" : "Record phrase";
    public bool CanRunCudaBenchmark => _commands.GetSnapshot().CanUseCuda;
    public bool CudaBenchmarkButtonEnabled => CanRunCudaBenchmark && !IsCudaBenchmarkRunning;

    // Events consumed by the view to close itself.
    public event EventHandler? RequestClose;

    public WelcomeWizardViewModel(
        ModelManagerService models,
        PluginManager pluginManager,
        HotkeyService hotkey,
        AudioRecordingService audio,
        SystemCommandAvailabilityService commands,
        TextInsertionService textInsertion,
        YdotoolSetupHelper ydotoolSetup,
        ISettingsService settings)
    {
        _models = models;
        _pluginManager = pluginManager;
        _hotkey = hotkey;
        _audio = audio;
        _commands = commands;
        _textInsertion = textInsertion;
        _ydotoolSetup = ydotoolSetup;
        _settings = settings;

        _pluginStateChangedHandler = (_, _) => Dispatcher.UIThread.Post(RefreshPluginState);
        _modelStateChangedHandler = (_, _) => Dispatcher.UIThread.Post(RefreshModelState);
        _pluginManager.PluginStateChanged += _pluginStateChangedHandler;
        _models.PropertyChanged += _modelStateChangedHandler;
        _audio.LevelChanged += OnAudioLevelChanged;

        LoadModels();
        LoadExtensions();
        LoadMics();
        RefreshDiagnostics();
        RefreshStepDots();

        HotkeyText = _hotkey.CurrentHotkeyString;
    }

    private void LoadModels()
    {
        var previousSelectedId = SelectedModel?.ModelId ?? _settings.Current.SelectedModelId;

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
        SelectedModel = AvailableModels.FirstOrDefault(m => m.ModelId == previousSelectedId)
            ?? AvailableModels.FirstOrDefault(m => m.IsRecommended)
            ?? AvailableModels.FirstOrDefault();
    }

    private void LoadExtensions()
    {
        ExtensionPlugins.Clear();
        foreach (var p in _pluginManager.AllPlugins)
        {
            ExtensionPlugins.Add(new PluginRow(
                owner: null,
                id: p.Manifest.Id,
                name: p.Manifest.Name,
                version: p.Manifest.Version,
                author: p.Manifest.Author ?? "",
                description: p.Manifest.Description ?? "",
                category: p.Manifest.Category,
                isLocal: p.Manifest.IsLocal,
                hasExpandableSettings: false,
                isEnabled: _pluginManager.IsEnabled(p.Manifest.Id)));
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
                existing.IsEnabled = isEnabled;
        }

        LoadModels();
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
            {
                AvailableModels[i] = existing with { IsDownloaded = downloaded };
                if (SelectedModel?.ModelId == existing.ModelId)
                    SelectedModel = AvailableModels[i];
            }
        }
    }

    partial void OnStepIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsFirstStep));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(NextLabel));
        OnPropertyChanged(nameof(StepText));
        RefreshStepDots();

        if (value == 3)
            RefreshDiagnostics();

        // Final step: if we're on Wayland and ydotool needs setup, run it
        // automatically. The user already saw the System Check page, so
        // arriving here implies they're ready to finalize — surprising
        // them with the pkexec prompt is fine because it's the explicit
        // last action of the wizard. If ydotool is already configured
        // or the binary isn't installed, the section stays hidden.
        if (value == StepCount - 1)
            _ = RunYdotoolSetupIfNeededAsync();
    }

    partial void OnIsMicTestRunningChanged(bool value) =>
        OnPropertyChanged(nameof(MicTestButtonText));

    partial void OnIsFirstDictationRecordingChanged(bool value) =>
        OnPropertyChanged(nameof(FirstDictationButtonText));

    /// <summary>
    /// Runs the ydotool setup helper from inside the wizard's final step
    /// when (a) we're on Wayland, (b) the ydotool binary is installed,
    /// and (c) the integration isn't already fully configured. The
    /// helper itself prompts pkexec for the one udev-rule install.
    /// Idempotent: a fully-configured install becomes a no-op and the
    /// section stays hidden.
    /// </summary>
    private async Task RunYdotoolSetupIfNeededAsync()
    {
        var snapshot = _commands.GetSnapshot();
        if (snapshot.SessionType != "Wayland")
        {
            ShowYdotoolSetupSection = false;
            return;
        }

        var status = _ydotoolSetup.IsCurrentlyConfigured();
        if (status.IsFullyConfigured)
        {
            ShowYdotoolSetupSection = false;
            return;
        }

        if (!status.BinaryInstalled)
        {
            ShowYdotoolSetupSection = true;
            YdotoolSetupStatus =
                "Automatic paste needs ydotool. Install it through your package manager, "
                + "then open the Text insertion section to finish the setup.";
            return;
        }

        ShowYdotoolSetupSection = true;
        IsYdotoolSetupRunning = true;
        YdotoolSetupStatus = "Setting up automatic paste… (you may be asked for your admin password).";

        try
        {
            var result = await _ydotoolSetup.SetUpAsync(CancellationToken.None).ConfigureAwait(true);
            YdotoolSetupStatus = result.Success
                ? $"{result.Message} You can now dictate into any Wayland window."
                : $"{result.Message} {result.Detail} You can retry from the Text insertion section.";
        }
        catch (Exception ex)
        {
            YdotoolSetupStatus = $"Setup failed: {ex.Message}. Open the Text insertion section to retry.";
        }
        finally
        {
            IsYdotoolSetupRunning = false;
            _commands.RefreshSnapshot();
            RefreshDiagnostics();
        }
    }

    partial void OnIsCudaBenchmarkRunningChanged(bool value) =>
        OnPropertyChanged(nameof(CudaBenchmarkButtonEnabled));

    partial void OnHotkeyTextChanged(string value)
    {
        HotkeyStatus = "";
    }

    [RelayCommand]
    private void Back()
    {
        if (StepIndex > 0) StepIndex--;
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        // Step 0: pick model — download/load before advancing
        if (StepIndex == 0)
        {
            if (SelectedModel is not { } row)
            {
                ModelStatus = "No transcription models are available. Enable a transcription plugin and try again.";
                return;
            }

            ModelStatus = _models.IsDownloaded(row.ModelId)
                ? $"Loading {row.DisplayName}..."
                : $"Downloading {row.DisplayName}...";

            try
            {
                await _models.DownloadAndLoadModelAsync(row.ModelId);
                _settings.Save(_settings.Current with { SelectedModelId = row.ModelId });
                ModelStatus = $"{row.DisplayName} is ready.";
                RefreshModelState();
            }
            catch (Exception ex)
            {
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

    private void RefreshDiagnostics()
    {
        var snapshot = _commands.GetSnapshot();
        var selectedModelReady = SelectedModel is { } selected
            && _models.GetStatus(selected.ModelId).Type == ModelStatusType.Ready;
        var microphoneReady = SelectedMic is not null;
        var hotkeyReady = !string.IsNullOrWhiteSpace(_hotkey.CurrentHotkeyString);

        Diagnostics.Clear();
        Diagnostics.Add(new WelcomeDiagnosticRow(
            "Session",
            snapshot.SessionType,
            snapshot.SessionType != "Unknown",
            "TypeWhisper can still run, but desktop automation may be limited."));
        Diagnostics.Add(new WelcomeDiagnosticRow(
            "Clipboard",
            snapshot.ClipboardStatus,
            snapshot.HasClipboardTool,
            $"Install {snapshot.ClipboardToolName} for clipboard fallback."));
        Diagnostics.Add(new WelcomeDiagnosticRow(
            "Automatic paste",
            snapshot.PasteStatus,
            snapshot.HasAutomaticPasteTool,
            snapshot.PasteToolInstallHint));
        Diagnostics.Add(new WelcomeDiagnosticRow(
            "Audio conversion",
            snapshot.HasFfmpeg ? "ffmpeg available" : "ffmpeg not found",
            snapshot.HasFfmpeg,
            "Install ffmpeg for broader file transcription support."));
        Diagnostics.Add(new WelcomeDiagnosticRow(
            "Microphone",
            microphoneReady ? SelectedMic!.Name : "No input device selected",
            microphoneReady,
            "Select a microphone before your first dictation."));
        Diagnostics.Add(new WelcomeDiagnosticRow(
            "Shortcut",
            hotkeyReady ? _hotkey.CurrentHotkeyString : "No dictation shortcut set",
            hotkeyReady,
            "Return to the microphone and shortcut step and set a valid shortcut."));
        Diagnostics.Add(new WelcomeDiagnosticRow(
            "Model",
            selectedModelReady
                ? $"{SelectedModel!.DisplayName} ready"
                : SelectedModel is null
                    ? "No model selected"
                    : $"{SelectedModel.DisplayName} not loaded",
            selectedModelReady,
            "Return to the model step and load a transcription model."));
        Diagnostics.Add(new WelcomeDiagnosticRow(
            "CUDA",
            snapshot.CudaStatus,
            snapshot.CanUseCuda || !snapshot.HasCudaGpu,
            "Use CPU or install CUDA 12 runtime libraries before selecting CUDA."));

        var blockingIssues = Diagnostics.Count(row => !row.IsReady && row.Title is "Clipboard" or "Automatic paste" or "Microphone" or "Shortcut" or "Model");
        DiagnosticsSummary = blockingIssues == 0
            ? "Ready for first dictation."
            : $"{blockingIssues} setup item(s) need attention before the smoothest first dictation.";
        OnPropertyChanged(nameof(CanRunCudaBenchmark));
        OnPropertyChanged(nameof(CudaBenchmarkButtonEnabled));
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
            _audio.SelectedDeviceIndex = SelectedMic.Index;

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
                autoPaste: true,
                strategy: TextInsertionStrategy.ClipboardPaste);
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
            PasteTestStatus = "Clipboard helper is missing; install the helper shown in System check.";
            return false;
        }

        if (result is InsertionResult.MissingPasteTool)
        {
            PasteTestStatus = $"Automatic paste helper is missing. {_commands.GetSnapshot().PasteToolInstallHint}";
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
        PasteTestPassed = PasteSmokeText.Contains(PasteSmokeExpectedText, StringComparison.OrdinalIgnoreCase);
        PasteTestStatus = PasteTestPassed
            ? "Paste test passed."
            : "Paste test did not find the expected text in the field.";
    }

    [RelayCommand]
    private async Task ToggleFirstDictationAsync()
    {
        if (!IsFirstDictationRecording)
        {
            if (IsMicTestRunning)
                ToggleMicTest();

            FirstDictationText = "";
            FirstDictationStatus = "Recording. Say a short phrase, then stop.";
            if (SelectedMic is not null)
                _audio.SelectedDeviceIndex = SelectedMic.Index;

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
                var result = await plugin.TranscribeAsync(wav, null, false, null, CancellationToken.None);
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
            return;

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

    public void Cleanup()
    {
        if (_cleanedUp)
            return;

        _cleanedUp = true;
        _pluginManager.PluginStateChanged -= _pluginStateChangedHandler;
        _models.PropertyChanged -= _modelStateChangedHandler;
        _audio.LevelChanged -= OnAudioLevelChanged;

        if (IsMicTestRunning)
            _audio.StopPreview();

        if (IsFirstDictationRecording)
            FireAndLog(() => _audio.StopRecordingAsync(), "welcome wizard stop recording");

        IsMicTestRunning = false;
        IsFirstDictationRecording = false;
        MicLevel = 0;
    }

    private void OnAudioLevelChanged(object? sender, float level)
    {
        if (!IsMicTestRunning && !IsFirstDictationRecording)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            MicLevel = Math.Clamp(level * 8, 0, 1);
            if (IsMicTestRunning && MicLevel > 0.05)
                MicTestStatus = "Microphone input detected.";
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
            t => Trace.WriteLine($"[WelcomeWizard] {label} faulted: {t.Exception?.GetBaseException().Message}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void RefreshStepDots()
    {
        while (StepDots.Count < StepCount)
            StepDots.Add(new WelcomeStepDot(StepDots.Count));

        while (StepDots.Count > StepCount)
            StepDots.RemoveAt(StepDots.Count - 1);

        foreach (var dot in StepDots)
            dot.IsActive = dot.Index == StepIndex;
    }
}

public sealed partial class WelcomeStepDot(int index) : ObservableObject
{
    public int Index { get; } = index;
    [ObservableProperty] private bool _isActive;
}

public sealed record WizardModelRow(
    string ModelId,
    string DisplayName,
    string SizeDescription,
    bool IsDownloaded,
    bool IsRecommended);

public sealed record WelcomeDiagnosticRow(
    string Title,
    string Status,
    bool IsReady,
    string Hint);
