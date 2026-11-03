namespace TypeWhisper.Linux.Services;

public enum MicrophoneTestState
{
    WaitingForAudio,
    NoAudio,
    ZeroSamples,
    Quiet,
    Signal,
}

public enum MicrophoneTestEnd
{
    TimeLimit,
    StoppedByUser,
    PageClosed,
    DeviceChanged,
    DictationStarted,
    PreviewStopped,
}

public sealed record MicrophoneTestSnapshot(
    string? DeviceName,
    bool Running,
    MicrophoneTestState State,
    MicrophoneTestEnd? End,
    TimeSpan Duration,
    float Level
);

/// <summary>
///     Classifies the preview frames seen during a settings microphone test. Never keeps
///     samples: only counts, the latest level and whether speech-level input was heard.
/// </summary>
public sealed class MicrophoneTest
{
    // PortAudio delivers a 512-frame buffer every ~32 ms; a full second without one
    // means the device stopped delivering (unplugged, suspended or stalled).
    private static readonly TimeSpan s_noAudioTimeout = TimeSpan.FromSeconds(1);

    private readonly Lock _lock = new();
    private readonly TimeProvider _clock;
    private readonly long _started;
    private readonly float _speechThreshold;
    private long? _lastBuffer;
    private long? _stopped;
    private MicrophoneTestEnd? _end;
    private bool _lastBufferZero;
    private bool _heardSignal;
    private float _level;

    internal MicrophoneTest(string? deviceName, float speechThreshold, TimeProvider? clock = null)
    {
        DeviceName = deviceName;
        _speechThreshold = speechThreshold;
        _clock = clock ?? TimeProvider.System;
        _started = _clock.GetTimestamp();
    }

    public string? DeviceName { get; }

    // Called on the PortAudio realtime thread; the lock is held only for a few assignments.
    internal void Observe(ReadOnlySpan<float> samples, float rms)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        // Both signs of zero are digital silence; any nonzero sample, however small, is
        // real input (a live microphone always has some noise floor).
        var allZero = !samples.ContainsAnyExcept(0f);

        lock (_lock)
        {
            if (_stopped.HasValue)
            {
                return;
            }

            _lastBuffer = _clock.GetTimestamp();
            _lastBufferZero = allZero;
            _level = float.IsFinite(rms) ? Math.Clamp(rms, 0f, 1f) : 0f;
            _heardSignal |= _level >= _speechThreshold;
        }
    }

    /// <summary>Ends the test; the first reason wins and later frames are ignored.</summary>
    internal bool Stop(MicrophoneTestEnd reason)
    {
        lock (_lock)
        {
            if (_stopped.HasValue)
            {
                return false;
            }

            _stopped = _clock.GetTimestamp();
            _end = reason;
            return true;
        }
    }

    public MicrophoneTestSnapshot Snapshot()
    {
        lock (_lock)
        {
            var now = _stopped ?? _clock.GetTimestamp();
            var age = _clock.GetElapsedTime(_lastBuffer ?? _started, now);
            var state = age >= s_noAudioTimeout ? MicrophoneTestState.NoAudio
                : !_lastBuffer.HasValue ? MicrophoneTestState.WaitingForAudio
                : _lastBufferZero ? MicrophoneTestState.ZeroSamples
                : _heardSignal ? MicrophoneTestState.Signal
                : MicrophoneTestState.Quiet;
            return new MicrophoneTestSnapshot(
                DeviceName,
                !_stopped.HasValue,
                state,
                _end,
                _clock.GetElapsedTime(_started, now),
                state == MicrophoneTestState.NoAudio ? 0f : _level
            );
        }
    }
}
