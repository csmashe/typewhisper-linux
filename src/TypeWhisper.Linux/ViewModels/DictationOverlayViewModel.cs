using Avalonia.Threading;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Linux.Services;

namespace TypeWhisper.Linux.ViewModels;

public partial class DictationOverlayViewModel : ObservableObject
{
    private readonly AudioRecordingService _audio;
    private readonly ISettingsService _settings;
    private readonly DispatcherTimer _recordingTimer;
    private readonly DispatcherTimer _feedbackTimer;
    private DateTime? _sessionStartedAtUtc;

    [ObservableProperty] private bool _isOverlayVisible;
    [ObservableProperty] private bool _showFeedback;
    [ObservableProperty] private bool _feedbackIsError;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string? _partialText;
    [ObservableProperty] private string? _feedbackText;
    [ObservableProperty] private string? _activeProfileName;
    [ObservableProperty] private string? _activeAppName;
    [ObservableProperty] private double _recordingSeconds;
    [ObservableProperty] private float _audioLevel;

    public DictationOverlayViewModel(DictationOrchestrator dictation, AudioRecordingService audio, ISettingsService settings)
    {
        _audio = audio;
        _settings = settings;

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

        _settings.SettingsChanged += _ => Dispatcher.UIThread.Post(RefreshOverlaySlots);
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
    public bool ShowLeftMeter => ResolveWidget(_settings.Current.OverlayLeftWidget) == OverlaySlotKind.Meter;
    public bool ShowLeftText => ResolveWidget(_settings.Current.OverlayLeftWidget) == OverlaySlotKind.Text;
    public string LeftText => ResolveText(_settings.Current.OverlayLeftWidget);
    public bool ShowRightMeter => ResolveWidget(_settings.Current.OverlayRightWidget) == OverlaySlotKind.Meter;
    public bool ShowRightText => ResolveWidget(_settings.Current.OverlayRightWidget) == OverlaySlotKind.Text;
    public string RightText => ResolveText(_settings.Current.OverlayRightWidget);

    partial void OnIsOverlayVisibleChanged(bool value) => OnPropertyChanged(nameof(HasVisibleContent));

    partial void OnShowFeedbackChanged(bool value)
    {
        OnPropertyChanged(nameof(HasVisibleContent));

        _feedbackTimer.Stop();
        if (value)
            _feedbackTimer.Start();
    }

    partial void OnRecordingSecondsChanged(double value) => OnPropertyChanged(nameof(RecordingTimerText));

    partial void OnAudioLevelChanged(float value)
    {
        OnPropertyChanged(nameof(AudioMeterWidth));
        OnPropertyChanged(nameof(LeftText));
        OnPropertyChanged(nameof(RightText));
    }

    partial void OnFeedbackIsErrorChanged(bool value) => OnPropertyChanged(nameof(FeedbackForeground));

    private void ApplyState(DictationOverlayState state)
    {
        IsOverlayVisible = state.IsOverlayVisible;
        FeedbackIsError = state.FeedbackIsError;
        FeedbackText = state.FeedbackText;
        StatusText = state.StatusText;
        PartialText = state.PartialText;
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

        RefreshOverlaySlots();
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

    private void RefreshOverlaySlots()
    {
        OnPropertyChanged(nameof(ShowLeftMeter));
        OnPropertyChanged(nameof(ShowLeftText));
        OnPropertyChanged(nameof(LeftText));
        OnPropertyChanged(nameof(ShowRightMeter));
        OnPropertyChanged(nameof(ShowRightText));
        OnPropertyChanged(nameof(RightText));
    }

    private OverlaySlotKind ResolveWidget(OverlayWidget widget) => widget switch
    {
        OverlayWidget.None => OverlaySlotKind.None,
        OverlayWidget.Indicator => OverlaySlotKind.Meter,
        OverlayWidget.Waveform => OverlaySlotKind.Meter,
        OverlayWidget.Timer => OverlaySlotKind.Text,
        OverlayWidget.Clock => OverlaySlotKind.Text,
        OverlayWidget.Profile => OverlaySlotKind.Text,
        OverlayWidget.HotkeyMode => OverlaySlotKind.Text,
        OverlayWidget.AppName => OverlaySlotKind.Text,
        _ => OverlaySlotKind.None
    };

    private string ResolveText(OverlayWidget widget) => widget switch
    {
        OverlayWidget.Timer => RecordingTimerText,
        OverlayWidget.Clock => DateTime.Now.ToString("t"),
        OverlayWidget.Profile => ActiveProfileName ?? "",
        OverlayWidget.HotkeyMode => _settings.Current.Mode switch
        {
            RecordingMode.Toggle => "Toggle",
            RecordingMode.PushToTalk => "Push to talk",
            RecordingMode.Hybrid => "Hybrid",
            _ => ""
        },
        OverlayWidget.AppName => ActiveAppName ?? "",
        OverlayWidget.Indicator => "",
        OverlayWidget.Waveform => "",
        OverlayWidget.None => "",
        _ => ""
    };
}

internal enum OverlaySlotKind
{
    None,
    Meter,
    Text
}
