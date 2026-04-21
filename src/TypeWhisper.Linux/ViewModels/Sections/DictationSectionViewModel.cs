using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Linux.Services;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class DictationSectionViewModel : ObservableObject
{
    private readonly DictationOrchestrator _dictation;
    private readonly ModelManagerService _models;

    [ObservableProperty] private string _statusText = "Press your hotkey or click Toggle to start recording.";
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string? _lastCapturePath;
    [ObservableProperty] private string? _lastTranscription;
    [ObservableProperty] private string _activeModelLabel = "No model loaded";

    public DictationSectionViewModel(DictationOrchestrator dictation, ModelManagerService models)
    {
        _dictation = dictation;
        _models = models;

        _dictation.RecordingStateChanged += (_, recording) =>
            Dispatcher.UIThread.Post(() =>
            {
                IsRecording = recording;
                StatusText = recording
                    ? "Recording… press the hotkey again to stop."
                    : "Stopped. Processing…";
            });

        _dictation.RecordingCaptured += (_, path) =>
            Dispatcher.UIThread.Post(() => LastCapturePath = path);

        _dictation.TranscriptionCompleted += (_, text) =>
            Dispatcher.UIThread.Post(() => LastTranscription = text);

        _dictation.StatusMessage += (_, msg) =>
            Dispatcher.UIThread.Post(() => StatusText = msg);

        _models.PropertyChanged += (_, _) =>
            Dispatcher.UIThread.Post(RefreshActiveModel);

        RefreshActiveModel();
    }

    private void RefreshActiveModel()
    {
        var active = _models.ActiveModelId;
        ActiveModelLabel = string.IsNullOrEmpty(active) ? "No model loaded" : $"Active: {active}";
    }

    [RelayCommand]
    private async Task Toggle() => await _dictation.ToggleAsync();
}
