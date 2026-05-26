using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;

namespace TypeWhisper.Linux.ViewModels;

public partial class DictationOverlayViewModel : ObservableObject
{
    // 5 samples of audio-level history feed the waveform dots; chosen to match
    // the bubble's footprint and give a perceptible rolling motion at the
    // ~10 Hz cadence of LevelChanged.
    private const int WaveformSampleCount = 5;

    private readonly AudioRecordingService _audio;
    private readonly DispatcherTimer _feedbackTimer;
    private readonly DispatcherTimer _recordingTimer;
    private readonly ISettingsService _settings;
    private readonly float[] _waveformLevels = new float[WaveformSampleCount];

    [ObservableProperty]
    private string? _activeAppName;

    [ObservableProperty]
    private string? _activeProfileName;

    [ObservableProperty]
    private float _audioLevel;

    [ObservableProperty]
    private bool _feedbackIsError;

    [ObservableProperty]
    private string? _feedbackText;

    [ObservableProperty]
    private bool _isOverlayVisible;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private string? _partialText;

    [ObservableProperty]
    private double _recordingSeconds;

    private DateTime? _sessionStartedAtUtc;

    [ObservableProperty]
    private bool _showFeedback;

    [ObservableProperty]
    private string _statusText = "Ready";

    public DictationOverlayViewModel(
        DictationOrchestrator dictation,
        TransformSelectionService transformSelection,
        AudioRecordingService audio,
        ISettingsService settings,
        IDetectionFailureTracker failureTracker
    )
    {
        _audio = audio;
        _settings = settings;

        _recordingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _recordingTimer.Tick += (_, _) => RefreshRecordingSeconds();

        // Auto-hide feedback after a user-configurable delay
        // (AppSettings.PreviewBubbleAutoHideMilliseconds). Interval is set per
        // arm via ArmFeedbackAutoHideTimer so live setting changes apply on
        // the next feedback event. New events call RestartFeedbackTimer() to
        // re-arm even when ShowFeedback is already true (a plain re-assignment
        // skips OnShowFeedbackChanged due to value equality).
        _feedbackTimer = new DispatcherTimer();
        _feedbackTimer.Tick += (_, _) =>
        {
            _feedbackTimer.Stop();
            ShowFeedback = false;
            FeedbackText = null;
            OnPropertyChanged(nameof(HasVisibleContent));
        };

        dictation.OverlayStateChanged += (_, state) =>
            Dispatcher.UIThread.Post(() => ApplyState(state));

        transformSelection.OverlayStateChanged += (_, state) =>
            Dispatcher.UIThread.Post(() => ApplyState(state));

        // Raw RMS is typically well below 0.1 for speech, so amplify ×8 to drive a
        // visible meter — same scaling the recorder and wizard VMs apply.
        _audio.LevelChanged += (_, level) =>
            Dispatcher.UIThread.Post(() => AudioLevel = Math.Clamp(level * 8, 0f, 1f));

        _settings.SettingsChanged += _ => Dispatcher.UIThread.Post(RefreshOverlaySlots);

        failureTracker.OnFailure += (_, e) =>
        {
            if (e.ShouldShowPersistentBanner)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                FeedbackText = e.Reason;
                FeedbackIsError = true;
                ShowFeedback = true;
                RestartFeedbackTimer();
            });
        };
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

    // Single pulsing dot: 10px at silence, grows to 18px at peak level.
    public double IndicatorSize => 10 + PerceptualLevel(AudioLevel) * 8;

    // Each waveform bar's height reflects one slot of the rolling buffer.
    // Idle slots collapse to 4px so the row reads as five-dots-at-rest;
    // loud slots climb to 18px so the wave is clearly moving up and down.
    public double WaveformBar0Height => BarHeight(_waveformLevels[0]);
    public double WaveformBar1Height => BarHeight(_waveformLevels[1]);
    public double WaveformBar2Height => BarHeight(_waveformLevels[2]);
    public double WaveformBar3Height => BarHeight(_waveformLevels[3]);
    public double WaveformBar4Height => BarHeight(_waveformLevels[4]);

    public string FeedbackForeground => FeedbackIsError ? "#FF8888" : "#66E3A2";

    public bool ShowLeftIndicator =>
        _settings.Current.OverlayLeftWidget == OverlayWidget.Indicator;

    public bool ShowLeftWaveform =>
        _settings.Current.OverlayLeftWidget == OverlayWidget.Waveform;

    public bool ShowLeftText => IsTextWidget(_settings.Current.OverlayLeftWidget);

    public string LeftText => ResolveText(_settings.Current.OverlayLeftWidget);

    public bool ShowRightIndicator =>
        _settings.Current.OverlayRightWidget == OverlayWidget.Indicator;

    public bool ShowRightWaveform =>
        _settings.Current.OverlayRightWidget == OverlayWidget.Waveform;

    public bool ShowRightText => IsTextWidget(_settings.Current.OverlayRightWidget);

    public string RightText => ResolveText(_settings.Current.OverlayRightWidget);

    private static double BarHeight(float level) =>
        4 + PerceptualLevel(level) * 14;

    // sqrt curve pulls quiet/medium levels closer to the top of the range so
    // the meter reads as responsive without requiring shouting.
    private static double PerceptualLevel(float level) =>
        Math.Sqrt(Math.Clamp(level, 0f, 1f));

    partial void OnIsOverlayVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(HasVisibleContent));
    }

    partial void OnShowFeedbackChanged(bool value)
    {
        OnPropertyChanged(nameof(HasVisibleContent));

        _feedbackTimer.Stop();
        if (value)
        {
            ArmFeedbackAutoHideTimer();
        }
    }

    private void RestartFeedbackTimer()
    {
        _feedbackTimer.Stop();
        ArmFeedbackAutoHideTimer();
    }

    private void ArmFeedbackAutoHideTimer()
    {
        var milliseconds = AppSettings.NormalizePreviewBubbleAutoHideMilliseconds(
            _settings.Current.PreviewBubbleAutoHideMilliseconds);

        if (milliseconds <= 0)
        {
            _feedbackTimer.Stop();
            ShowFeedback = false;
            FeedbackText = null;
            OnPropertyChanged(nameof(HasVisibleContent));
            return;
        }

        _feedbackTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _feedbackTimer.Start();
    }

    partial void OnRecordingSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(RecordingTimerText));
    }

    partial void OnAudioLevelChanged(float value)
    {
        for (var i = 0; i < WaveformSampleCount - 1; i++)
        {
            _waveformLevels[i] = _waveformLevels[i + 1];
        }
        _waveformLevels[WaveformSampleCount - 1] = value;

        OnPropertyChanged(nameof(IndicatorSize));
        OnPropertyChanged(nameof(WaveformBar0Height));
        OnPropertyChanged(nameof(WaveformBar1Height));
        OnPropertyChanged(nameof(WaveformBar2Height));
        OnPropertyChanged(nameof(WaveformBar3Height));
        OnPropertyChanged(nameof(WaveformBar4Height));
        OnPropertyChanged(nameof(LeftText));
        OnPropertyChanged(nameof(RightText));
    }

    partial void OnFeedbackIsErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(FeedbackForeground));
    }

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
            Array.Clear(_waveformLevels);
            OnPropertyChanged(nameof(WaveformBar0Height));
            OnPropertyChanged(nameof(WaveformBar1Height));
            OnPropertyChanged(nameof(WaveformBar2Height));
            OnPropertyChanged(nameof(WaveformBar3Height));
            OnPropertyChanged(nameof(WaveformBar4Height));
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
        OnPropertyChanged(nameof(ShowLeftIndicator));
        OnPropertyChanged(nameof(ShowLeftWaveform));
        OnPropertyChanged(nameof(ShowLeftText));
        OnPropertyChanged(nameof(LeftText));
        OnPropertyChanged(nameof(ShowRightIndicator));
        OnPropertyChanged(nameof(ShowRightWaveform));
        OnPropertyChanged(nameof(ShowRightText));
        OnPropertyChanged(nameof(RightText));
    }

    private static bool IsTextWidget(OverlayWidget widget) =>
        widget
            is OverlayWidget.Timer
                or OverlayWidget.Clock
                or OverlayWidget.Profile
                or OverlayWidget.HotkeyMode
                or OverlayWidget.AppName;

    private string ResolveText(OverlayWidget widget)
    {
        return widget switch
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
}