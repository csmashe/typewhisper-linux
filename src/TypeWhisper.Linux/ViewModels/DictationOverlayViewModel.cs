using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.ViewModels;

// MVVM Toolkit [ObservableProperty] generates the On<Property>Changed(value) partial hooks; the
// value parameter is part of the generated signature and cannot be dropped even when ignored here.
// ReSharper disable UnusedParameterInPartialMethod
public partial class DictationOverlayViewModel : ObservableObject
{
    // 5 samples of audio-level history feed the waveform dots; chosen to match
    // the bubble's footprint and give a perceptible rolling motion at the
    // ~10 Hz cadence of LevelChanged.
    private const int WaveformSampleCount = 5;

    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _feedbackTimer;
    private readonly Action<Action> _postToUiThread;
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
    private string? _llmResponseText;

    [ObservableProperty]
    private string? _partialText;

    [ObservableProperty]
    private double _recordingSeconds;

    private DateTime? _sessionStartedAtUtc;

    [ObservableProperty]
    private bool _showFeedback;

    [ObservableProperty]
    private string _statusText = Loc.Instance["Overlay.Ready"];

    public DictationOverlayViewModel(
        DictationOrchestrator dictation,
        TransformSelectionService transformSelection,
        AudioRecordingService audio,
        ISettingsService settings,
        IDetectionFailureTracker failureTracker
    )
        : this(settings, static action => Dispatcher.UIThread.Post(action))
    {
        dictation.OverlayStateChanged += (_, state) =>
            _postToUiThread(() => ApplyState(state));

        transformSelection.OverlayStateChanged += (_, state) =>
            _postToUiThread(() => ApplyState(state));

        SubscribeToAudioLevels(audio);

        failureTracker.OnFailure += (_, e) =>
        {
            if (e.ShouldShowPersistentBanner)
            {
                return;
            }

            _postToUiThread(() =>
            {
                FeedbackText = e.Reason;
                FeedbackIsError = true;
                ShowFeedback = true;
                RestartFeedbackTimer();
            });
        };
    }

    // Test seam: production posts non-audio service events to Avalonia's UI thread; tests can
    // run that path synchronously and optionally exercise the shared direct audio subscription.
    internal DictationOverlayViewModel(
        ISettingsService settings,
        Action<Action> postToUiThread,
        AudioRecordingService? audio = null
    )
    {
        _settings = settings;
        _postToUiThread = postToUiThread;
        if (audio is not null)
        {
            SubscribeToAudioLevels(audio);
        }

        _recordingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _recordingTimer.Tick += (_, _) => RecordingTimerTick();

        // LeftText/RightText render DateTime.Now with minute resolution. Polling once per second
        // keeps a minute rollover's visible delay below one second without re-arming for wall-clock
        // alignment; this timer is stopped whenever no clock slot is actually visible.
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => ClockTimerTick();

        // Interval is set per arm so live setting changes take effect on the
        // next event. RestartFeedbackTimer re-arms even when ShowFeedback is
        // already true — plain re-assignment is skipped by value equality.
        _feedbackTimer = new DispatcherTimer();
        _feedbackTimer.Tick += (_, _) =>
        {
            _feedbackTimer.Stop();
            ShowFeedback = false;
            FeedbackText = null;
            OnPropertyChanged(nameof(HasVisibleContent));
        };

        _settings.SettingsChanged += _ => _postToUiThread(RefreshOverlaySlots);
    }

    private void SubscribeToAudioLevels(AudioRecordingService audio)
    {
        audio.LevelChanged += (_, level) =>
        {
            if (IsRecording)
            {
                // Raw RMS is typically well below 0.1 for speech, so amplify ×8 to drive a
                // visible meter — same scaling the recorder and wizard VMs apply.
                AudioLevel = Math.Clamp(level * 8, 0f, 1f);
            }
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

    // Each bar reflects one slot of the rolling buffer: 4px at silence, 18px at peak.
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

    private static double BarHeight(float level)
    {
        return 4 + PerceptualLevel(level) * 14;
    }

    // sqrt curve pulls quiet/medium levels closer to the top of the range so
    // the meter reads as responsive without requiring shouting.
    private static double PerceptualLevel(float level)
    {
        return Math.Sqrt(Math.Clamp(level, 0f, 1f));
    }

    partial void OnIsOverlayVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(HasVisibleContent));
        UpdateClockTimer();
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
        LlmResponseText = state.LlmResponseText;
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

    internal void RecordingTimerTick()
    {
        RefreshRecordingSeconds();
        NotifyTextSlots(OverlayWidget.Timer);
    }

    internal void ClockTimerTick()
    {
        NotifyTextSlots(OverlayWidget.Clock);
    }

    internal bool IsClockTimerRunning => _clockTimer.IsEnabled;

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
        UpdateClockTimer();
    }

    private void NotifyTextSlots(OverlayWidget widget)
    {
        if (_settings.Current.OverlayLeftWidget == widget)
        {
            OnPropertyChanged(nameof(LeftText));
        }

        if (_settings.Current.OverlayRightWidget == widget)
        {
            OnPropertyChanged(nameof(RightText));
        }
    }

    private void UpdateClockTimer()
    {
        var shouldRun = IsOverlayVisible
                        && (_settings.Current.OverlayLeftWidget == OverlayWidget.Clock
                            || _settings.Current.OverlayRightWidget == OverlayWidget.Clock);

        if (shouldRun)
        {
            _clockTimer.Start();
        }
        else
        {
            _clockTimer.Stop();
        }
    }

    private static bool IsTextWidget(OverlayWidget widget)
    {
        return widget
            is OverlayWidget.Timer
            or OverlayWidget.Clock
            or OverlayWidget.Profile
            or OverlayWidget.HotkeyMode
            or OverlayWidget.AppName;
    }

    private string ResolveText(OverlayWidget widget)
    {
        return widget switch
        {
            OverlayWidget.Timer => RecordingTimerText,
            OverlayWidget.Clock => DateTime.Now.ToString("t"),
            OverlayWidget.Profile => ActiveProfileName ?? "",
            OverlayWidget.HotkeyMode => _settings.Current.Mode switch
            {
                RecordingMode.Toggle => Loc.Instance["Common.ModeToggle"],
                RecordingMode.PushToTalk => Loc.Instance["Common.ModePushToTalk"],
                RecordingMode.Hybrid => Loc.Instance["Common.ModeHybrid"],
                _ => "",
            },
            OverlayWidget.AppName => ActiveAppName ?? "",
            // Indicator, Waveform and None render no text; handled by the default arm.
            _ => "",
        };
    }
}
