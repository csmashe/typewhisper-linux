using System.Diagnostics;
using System.Text.Json.Serialization;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Linux.Services;

public enum RecorderState
{
    Ready,
    Recording,
    Paused,
    Saving,
}

// Distinguishes "another owner holds the recorder" from a capture device fault, so
// callers can absorb the conflict without swallowing real start failures.
public sealed class RecorderBusyException(string message) : InvalidOperationException(message);

public sealed record RecorderStopResult(string SessionId, string FilePath, byte[] Wav, TimeSpan Duration);
public sealed record RecorderSavedEventArgs(RecorderStopResult Result, object? Initiator);
public sealed record RecorderSessionSnapshot(
    string Id,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OutputFile,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error,
    // ReSharper disable once NotAccessedPositionalProperty.Global -- serialized into the API session response.
    double DurationSeconds
);

public sealed class RecorderService : IDisposable
{
    public static readonly TimeSpan MaximumActiveDuration = TimeSpan.FromHours(1);
    private readonly AudioRecordingService _audio;
    private readonly ISettingsService _settings;
    private readonly string _recordingDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _snapshotLock = new();
    private readonly List<byte[]> _segments = [];
    private readonly Dictionary<string, RecorderSessionSnapshot> _sessions = new(StringComparer.Ordinal);
    private readonly Queue<string> _sessionOrder = new();
    private AudioRecordingService.CaptureReservation? _reservation;
    private AudioRecordingService.AudioCaptureSession? _capture;
    private RecorderState _state;
    private string? _activeSessionId;
    private int _samples;
    private long _segmentStarted;
    private bool _whisperMode;
    private bool _disposed;

    public RecorderService(AudioRecordingService audio, ISettingsService settings)
        : this(audio, settings, TypeWhisperEnvironment.AudioPath)
    {
    }

    internal RecorderService(
        AudioRecordingService audio,
        ISettingsService settings,
        string recordingDirectory,
        TimeProvider? timeProvider = null
    )
    {
        _audio = audio;
        _settings = settings;
        _recordingDirectory = recordingDirectory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public RecorderState State
    {
        get
        {
            lock (_snapshotLock)
            {
                return _state;
            }
        }
    }

    public string? ActiveSessionId
    {
        get
        {
            lock (_snapshotLock)
            {
                return _activeSessionId;
            }
        }
    }

    public bool IsSessionActive => State is RecorderState.Recording or RecorderState.Paused;

    public TimeSpan ActiveDuration
    {
        get
        {
            lock (_snapshotLock)
            {
                return DurationLocked();
            }
        }
    }

    public event Action? Changed;
    public event Action<RecorderSavedEventArgs>? RecordingSaved;
    public event EventHandler<float>? LevelChanged
    {
        add => _audio.LevelChanged += value;
        remove => _audio.LevelChanged -= value;
    }

    public async Task<bool> StartAsync()
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            throw new RecorderBusyException("A recorder transition is in progress.");
        }

        try
        {
            RequireState(RecorderState.Ready);
            var reservation = _audio.TryReserveCapture();
            if (reservation is null)
            {
                return false;
            }

            try
            {
                _whisperMode = _settings.Current.WhisperModeEnabled;
                var capture = _audio.TryStartRecording(_whisperMode, reservation);
                if (capture is null)
                {
                    reservation.Dispose();
                    return false;
                }

                lock (_snapshotLock)
                {
                    _reservation = reservation;
                    _capture = capture;
                    _segments.Clear();
                    _samples = 0;
                    _segmentStarted = _timeProvider.GetTimestamp();
                    if (_sessions.Count == 100)
                    {
                        _sessions.Remove(_sessionOrder.Dequeue());
                    }

                    _activeSessionId = Guid.NewGuid().ToString();
                    _sessionOrder.Enqueue(_activeSessionId);
                    _state = RecorderState.Recording;
                    UpdateSessionLocked("recording");
                }
            }
            catch
            {
                reservation.Dispose();
                throw;
            }

            NotifyChanged();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PauseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireState(RecorderState.Recording);
            await FinishSegmentAsync(RecorderState.Paused, "paused").ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResumeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireState(RecorderState.Paused);
            if (_samples >= RecorderWavSegments.MaximumSamples)
            {
                throw new InvalidOperationException("The recording limit was reached.");
            }

            var capture = _audio.TryStartRecording(_whisperMode, _reservation)
                ?? throw new InvalidOperationException("Could not resume recording.");
            lock (_snapshotLock)
            {
                _capture = capture;
                _segmentStarted = _timeProvider.GetTimestamp();
            }

            SetState(RecorderState.Recording, "recording");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RecorderStopResult?> StopAsync(object? initiator = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsSessionActive)
            {
                throw new RecorderBusyException("No recorder session is active.");
            }

            RecorderStopResult result;
            try
            {
                // AudioRecordingService releases the capture before it can throw, so a
                // failed finalization must fall into the cleanup below, not stay Recording.
                if (State == RecorderState.Recording)
                {
                    await FinishSegmentAsync(RecorderState.Saving, "finalizing").ConfigureAwait(false);
                }
                else
                {
                    SetState(RecorderState.Saving, "finalizing");
                }

                var wav = await Task.Run(() => RecorderWavSegments.Combine(_segments)).ConfigureAwait(false);
                if (wav.Length == 0)
                {
                    CompleteSession("failed", error: "No audio captured");
                    return null;
                }

                var filePath = await Task.Run(() =>
                    RecorderFileNamer.CommitRecording(_recordingDirectory, DateTime.Now, wav)
                ).ConfigureAwait(false);
                result = new RecorderStopResult(ActiveSessionId!, filePath, wav, ActiveDuration);
            }
            catch
            {
                CompleteSession("failed", error: "Could not save recording");
                throw;
            }

            CompleteSession("completed", result.FilePath);
            if (RecordingSaved is not { } saved)
            {
                return result;
            }

            foreach (var handler in saved.GetInvocationList().Cast<Action<RecorderSavedEventArgs>>())
            {
                try
                {
                    handler(new RecorderSavedEventArgs(result, initiator));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Trace.WriteLine($"[Recorder] Save observer failed: {ex}");
                }
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool TryGetSession(string id, out RecorderSessionSnapshot snapshot)
    {
        lock (_snapshotLock)
        {
            if (Guid.TryParse(id, out var guid) && _sessions.TryGetValue(guid.ToString(), out var found))
            {
                snapshot = found.Id == _activeSessionId
                    ? found with { DurationSeconds = DurationLocked().TotalSeconds }
                    : found;
                return true;
            }

            snapshot = null!;
            return false;
        }
    }

    public void Dispose()
    {
        lock (_snapshotLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _reservation?.Dispose();
            _reservation = null;
        }

        _gate.Dispose();
    }

    private async Task FinishSegmentAsync(RecorderState nextState, string status)
    {
        var wav = await _audio.StopRecordingAsync(_capture!, CancellationToken.None).ConfigureAwait(false);
        lock (_snapshotLock)
        {
            if (wav.Length != 0)
            {
                var segment = RecorderWavSegments.Clamp(wav, RecorderWavSegments.MaximumSamples - _samples);
                _segments.Add(segment);
                _samples += RecorderWavSegments.SampleCount(segment);
            }

            _capture = null;
            _state = nextState;
            UpdateSessionLocked(status);
        }

        NotifyChanged();
    }

    private TimeSpan DurationLocked() =>
        TimeSpan.FromSeconds((double)_samples / RecorderWavSegments.SampleRate)
        + (_state == RecorderState.Recording
            ? _timeProvider.GetElapsedTime(_segmentStarted)
            : TimeSpan.Zero);

    private void RequireState(RecorderState expected)
    {
        lock (_snapshotLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != expected)
            {
                throw new RecorderBusyException("Invalid recorder state.");
            }
        }
    }

    private void SetState(RecorderState state, string status)
    {
        lock (_snapshotLock)
        {
            _state = state;
            UpdateSessionLocked(status);
        }

        NotifyChanged();
    }

    private void CompleteSession(string status, string? outputFile = null, string? error = null)
    {
        lock (_snapshotLock)
        {
            _reservation?.Dispose();
            _reservation = null;
            _capture = null;
            _segments.Clear();
            _state = RecorderState.Ready;
            UpdateSessionLocked(status, outputFile, error);
        }

        NotifyChanged();
    }

    private void UpdateSessionLocked(string status, string? outputFile = null, string? error = null)
    {
        _sessions[_activeSessionId!] = new RecorderSessionSnapshot(
            _activeSessionId!, status, outputFile, error, DurationLocked().TotalSeconds
        );
    }

    private void NotifyChanged()
    {
        if (Changed is not { } changed)
        {
            return;
        }
        foreach (var handler in changed.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Trace.WriteLine($"[Recorder] State observer failed: {ex}");
            }
        }
    }
}
