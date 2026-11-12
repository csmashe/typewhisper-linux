using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.ViewModels.Sections;

public partial class DictationSectionViewModel
{
    internal static TimeSpan MicrophoneTestDuration { get; } = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan s_microphoneTestRefreshInterval = TimeSpan.FromMilliseconds(100);

    private MicrophoneTest? _microphoneTest;
    private ITimer? _microphoneTestTimer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MicrophoneTestButtonText))]
    private bool _isMicrophoneTestRunning;

    [ObservableProperty]
    private string _microphoneTestStatus = Loc.Instance["Dictation.MicTestIdle"];

    // What the status line shows, kept so a language switch can redraw it. Null once
    // something outside the test rendering sets the text, which then stays as it is.
    private MicrophoneTestShown? _microphoneTestShown = new MicrophoneTestShown.Idle();
    private bool _renderingMicrophoneTest;

    [ObservableProperty]
    private string _microphoneTestDetail = "";

    [ObservableProperty]
    private string _microphoneTestDevice = "";

    public string MicrophoneTestButtonText =>
        IsMicrophoneTestRunning ? Loc.Instance["Dictation.MicTestStop"] : Loc.Instance["Dictation.MicTestStart"];

    [RelayCommand]
    private void ToggleMicrophoneTest()
    {
        if (_microphoneTest is not null)
        {
            StopMicrophoneTest(MicrophoneTestEnd.StoppedByUser);
            return;
        }

        var test = _audio.TryStartMicrophoneTest(out var failure);
        if (test is null)
        {
            ShowMicrophoneTest(new MicrophoneTestShown.StartFailed(failure switch
            {
                MicrophoneTestStartFailure.Busy => "Dictation.MicTestBusy",
                MicrophoneTestStartFailure.NoDevice => "Dictation.MicTestNoDevice",
                _ => "Dictation.MicTestOpenFailed",
            }));
            return;
        }

        _microphoneTest = test;
        IsMicrophoneTestRunning = true;
        _microphoneTestTimer = _timeProvider.CreateTimer(
            _ => _postToUi(RefreshMicrophoneTest),
            null,
            s_microphoneTestRefreshInterval,
            s_microphoneTestRefreshInterval
        );
        RenderMicrophoneTest(test.Snapshot());
    }

    // Runs on the UI thread. The audio service may already have ended the test
    // (dictation started, default device switched); the snapshot then says why.
    private void RefreshMicrophoneTest()
    {
        if (_microphoneTest is not { } test)
        {
            return;
        }

        var snapshot = test.Snapshot();
        switch (snapshot)
        {
            case { Running: true } when snapshot.Duration >= MicrophoneTestDuration:
                StopMicrophoneTest(MicrophoneTestEnd.TimeLimit);
                return;
            case { Running: false }:
                FinishMicrophoneTest();
                break;
        }

        RenderMicrophoneTest(snapshot);
    }

    private void StopMicrophoneTest(MicrophoneTestEnd reason)
    {
        if (_microphoneTest is not { } test)
        {
            return;
        }

        _audio.StopMicrophoneTest(test, reason);
        FinishMicrophoneTest();
        RenderMicrophoneTest(test.Snapshot());
    }

    private void FinishMicrophoneTest()
    {
        _microphoneTestTimer?.Dispose();
        _microphoneTestTimer = null;
        _microphoneTest = null;
        IsMicrophoneTestRunning = false;
    }

    private void RenderMicrophoneTest(MicrophoneTestSnapshot snapshot) =>
        ShowMicrophoneTest(new MicrophoneTestShown.Result(snapshot));

    private void RelocalizeMicrophoneTest()
    {
        if (_microphoneTestShown is { } shown)
        {
            ShowMicrophoneTest(shown);
        }
    }

    partial void OnMicrophoneTestStatusChanged(string value)
    {
        if (!_renderingMicrophoneTest)
        {
            _microphoneTestShown = null;
        }
    }

    private void ShowMicrophoneTest(MicrophoneTestShown shown)
    {
        _renderingMicrophoneTest = true;
        try
        {
            switch (shown)
            {
                case MicrophoneTestShown.Idle:
                    MicrophoneTestStatus = Loc.Instance["Dictation.MicTestIdle"];
                    break;
                case MicrophoneTestShown.StartFailed(var key):
                    MicrophoneTestStatus = Loc.Instance[key];
                    MicrophoneTestDetail = "";
                    MicrophoneTestDevice = "";
                    break;
                case MicrophoneTestShown.Result(var snapshot):
                    RenderMicrophoneTestResult(snapshot);
                    break;
            }
        }
        finally
        {
            _renderingMicrophoneTest = false;
        }

        _microphoneTestShown = shown;
    }

    private abstract record MicrophoneTestShown
    {
        public sealed record Idle : MicrophoneTestShown;

        public sealed record StartFailed(string Key) : MicrophoneTestShown;

        public sealed record Result(MicrophoneTestSnapshot Snapshot) : MicrophoneTestShown;
    }

    private void RenderMicrophoneTestResult(MicrophoneTestSnapshot snapshot)
    {
        // Following the default usually opens PortAudio's "default" alias, which names
        // nothing the user would recognize; the Automatic entry says what is tested.
        var device = SelectedDevice is { Index: < 0 } automatic
            ? automatic.Name
            : snapshot.DeviceName ?? SelectedDevice?.Name;
        MicrophoneTestDevice = string.IsNullOrWhiteSpace(device)
            ? ""
            : Loc.Instance.GetString("Dictation.MicTestDevice", device);

        var (status, detail) = snapshot.State switch
        {
            MicrophoneTestState.WaitingForAudio => ("Dictation.MicTestWaiting", "Dictation.MicTestWaitingDetail"),
            MicrophoneTestState.NoAudio => ("Dictation.MicTestNoAudio", "Dictation.MicTestNoAudioDetail"),
            MicrophoneTestState.ZeroSamples => ("Dictation.MicTestZeroSamples", "Dictation.MicTestZeroSamplesDetail"),
            MicrophoneTestState.Quiet => ("Dictation.MicTestQuiet", "Dictation.MicTestQuietDetail"),
            _ => ("Dictation.MicTestSignal", "Dictation.MicTestSignalDetail"),
        };

        if (snapshot.Running)
        {
            MicrophoneTestStatus = Loc.Instance[status];
            MicrophoneTestDetail = Loc.Instance[detail];
            return;
        }

        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault -- every end without its own message (time limit, user stop, page close) reports the plain result.
        switch (snapshot.End)
        {
            case MicrophoneTestEnd.DeviceChanged:
                // Naming the old device beside the new selection would read as its result.
                MicrophoneTestDevice = "";
                MicrophoneTestStatus = Loc.Instance["Dictation.MicTestDeviceChanged"];
                MicrophoneTestDetail = Loc.Instance["Dictation.MicTestDeviceChangedDetail"];
                break;
            case MicrophoneTestEnd.DictationStarted:
                MicrophoneTestStatus = Loc.Instance["Dictation.MicTestDictationStarted"];
                MicrophoneTestDetail = "";
                break;
            case MicrophoneTestEnd.PreviewStopped:
                MicrophoneTestStatus = Loc.Instance["Dictation.MicTestClosed"];
                MicrophoneTestDetail = "";
                break;
            default:
                // A test stopped before any audio arrived has no result to report.
                MicrophoneTestStatus = snapshot.State == MicrophoneTestState.WaitingForAudio
                    ? Loc.Instance["Dictation.MicTestStopped"]
                    : Loc.Instance.GetString("Dictation.MicTestStoppedWithResult", Loc.Instance[status]);
                MicrophoneTestDetail = snapshot.State == MicrophoneTestState.WaitingForAudio
                    ? ""
                    : Loc.Instance[detail];
                break;
        }
    }
}
