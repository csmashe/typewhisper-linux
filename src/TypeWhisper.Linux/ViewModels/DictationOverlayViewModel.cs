using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Linux.Services;

namespace TypeWhisper.Linux.ViewModels;

public partial class DictationOverlayViewModel : ObservableObject
{
    private readonly AudioRecordingService _audio;
    private readonly DispatcherTimer _recordingTimer;
    private readonly DispatcherTimer _feedbackTimer;
    private DateTime? _sessionStartedAtUtc;

    [ObservableProperty] private bool _isOverlayVisible;
    [ObservableProperty] private bool _showFeedback;
    [ObservableProperty] private bool _feedbackIsError;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string? _feedbackText;
    [ObservableProperty] private string? _activeProfileName;
    [ObservableProperty] private string? _activeAppName;
    [ObservableProperty] private double _recordingSeconds;
    [ObservableProperty] private float _audioLevel;

    public DictationOverlayViewModel(DictationOrchestrator dictation, AudioRecordingService audio)
    {
        _audio = audio;

        _recordingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _recordingTimer.Tick += (_, _) => RefreshRecordingSeconds();

        _feedbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _feedbackTimer.Tick += (_, _) =>
        {
            _feedbackTimer.Stop();
            ShowFeedback = false;
            FeedbackText = null;
            OnPropertyChanged(nameof(HasVisibleContent));
        };

        dictation.OverlayStateChanged += (_, state) =>
            Dispatcher.UIThread.Post(() => ApplyState(state));

        _audio.LevelChanged += (_, level) =>
            Dispatcher.UIThread.Post(() => AudioLevel = level);
    }

    public bool HasVisibleContent => IsOverlayVisible || ShowFeedback;

    public string RecordingTimerText
    {
        get
        {
            var totalSeconds = Math.Max(0, (int)Math.Floor(RecordingSeconds));
            var minutes = totalSeconds / 60;
            var seconds = totalSeconds % 60;
            return $"{minutes}:{seconds:00}";
        }
    }

    public double AudioMeterWidth => 18 + Math.Clamp(AudioLevel, 0f, 1f) * 54;
    public string FeedbackForeground => FeedbackIsError ? "#FF8888" : "#66E3A2";

    partial void OnIsOverlayVisibleChanged(bool value) => OnPropertyChanged(nameof(HasVisibleContent));

    partial void OnShowFeedbackChanged(bool value)
    {
        OnPropertyChanged(nameof(HasVisibleContent));

        _feedbackTimer.Stop();
        if (value)
            _feedbackTimer.Start();
    }

    partial void OnRecordingSecondsChanged(double value) => OnPropertyChanged(nameof(RecordingTimerText));

    partial void OnAudioLevelChanged(float value) => OnPropertyChanged(nameof(AudioMeterWidth));

    partial void OnFeedbackIsErrorChanged(bool value) => OnPropertyChanged(nameof(FeedbackForeground));

    private void ApplyState(DictationOverlayState state)
    {
        IsOverlayVisible = state.IsOverlayVisible;
        FeedbackIsError = state.FeedbackIsError;
        FeedbackText = state.FeedbackText;
        StatusText = state.StatusText;
        ActiveProfileName = state.ActiveProfileName;
        ActiveAppName = state.ActiveAppName;
        _sessionStartedAtUtc = state.SessionStartedAtUtc;
        IsRecording = state.IsRecording;
        ShowFeedback = state.ShowFeedback;

        if (IsRecording && _sessionStartedAtUtc is not null)
        {
            RefreshRecordingSeconds();
            _recordingTimer.Start();
        }
        else
        {
            _recordingTimer.Stop();
            RecordingSeconds = 0;
            AudioLevel = 0f;
        }
    }

    private void RefreshRecordingSeconds()
    {
        if (_sessionStartedAtUtc is not { } startedAt)
        {
            RecordingSeconds = 0;
            return;
        }

        RecordingSeconds = Math.Max(0, (DateTime.UtcNow - startedAt).TotalSeconds);
    }
}
