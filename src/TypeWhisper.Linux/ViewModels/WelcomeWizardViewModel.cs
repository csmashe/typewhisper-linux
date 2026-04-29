using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
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
    private readonly SystemCommandAvailabilityService _commands;
    private readonly TextInsertionService _textInsertion;
    private readonly ISettingsService _settings;
    private const string PasteSmokeExpectedText = "typewhisper paste test";
    private bool _cleanedUp;

    public ObservableCollection<WizardModelRow> AvailableModels { get; } = [];
    public ObservableCollection<PluginRow> ExtensionPlugins { get; } = [];
    public ObservableCollection<WelcomeDiagnosticRow> Diagnostics { get; } = [];

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
        ISettingsService settings)
    {
        _models = models;
        _pluginManager = pluginManager;
        _hotkey = hotkey;
        _audio = audio;
        _commands = commands;
        _textInsertion = textInsertion;
        _settings = settings;

        _pluginManager.PluginStateChanged += (_, _) => Dispatcher.UIThread.Post(RefreshPluginState);
        _models.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(RefreshModelState);
        _audio.LevelChanged += OnAudioLevelChanged;

        LoadModels();
        LoadExtensions();
        LoadMics();
        RefreshDiagnostics();

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

        if (value == 3)
            RefreshDiagnostics();
    }

    partial void OnIsMicTestRunningChanged(bool value) =>
        OnPropertyChanged(nameof(MicTestButtonText));

    partial void OnIsFirstDictationRecordingChanged(bool value) =>
        OnPropertyChanged(nameof(FirstDictationButtonText));

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

        if (StepIndex == 3)
            RefreshDiagnostics();

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
            "Install xdotool to paste automatically after transcription."));
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

        var result = await _textInsertion.InsertTextAsync(
            PasteSmokeExpectedText,
            autoPaste: true,
            strategy: TextInsertionStrategy.ClipboardPaste);

        if (result is InsertionResult.MissingClipboardTool)
        {
            PasteTestStatus = "Clipboard helper is missing; install the helper shown in System check.";
            return false;
        }

        if (result is InsertionResult.MissingPasteTool)
        {
            PasteTestStatus = "Automatic paste helper is missing; install xdotool.";
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

            _audio.StartRecording();
            if (!_audio.IsRecording)
            {
                FirstDictationStatus = "Could not start recording.";
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
            if (SelectedModel is { } selected)
                await _models.EnsureModelLoadedAsync(selected.ModelId);

            var plugin = _models.ActiveTranscriptionPlugin;
            if (plugin is null)
            {
                FirstDictationStatus = "No transcription model is loaded.";
                return;
            }

            FirstDictationStatus = $"Transcribing with {plugin.ProviderDisplayName}...";
            var result = await plugin.TranscribeAsync(wav, null, false, null, CancellationToken.None);
            FirstDictationText = result.Text?.Trim() ?? "";
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
        _audio.LevelChanged -= OnAudioLevelChanged;

        if (IsMicTestRunning)
            _audio.StopPreview();

        if (IsFirstDictationRecording)
            _ = _audio.StopRecordingAsync();

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
