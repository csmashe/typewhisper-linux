using Avalonia.Threading;
using PortAudioSharp;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using PaStream = PortAudioSharp.Stream;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Linux audio capture via PortAudioSharp2: 16kHz mono PCM16 for
///     whisper.cpp / SherpaOnnx. Richer Windows features (VAD, device polling)
///     are deferred — this covers the voice→text→paste pipeline.
/// </summary>
public sealed class AudioRecordingService : IDisposable
{
    internal sealed class AudioCaptureSession
    {
        internal AudioCaptureSession(long diagnosticId)
        {
            DiagnosticId = diagnosticId;
        }

        internal long DiagnosticId { get; }

        public override string ToString() => $"AudioCaptureSession({DiagnosticId})";
    }

    private sealed record LiveFrameSubscription(
        AudioCaptureSession Session,
        Action<float[]> Sink
    );

    private sealed record RecordedAudioSnapshot(
        float[][] Chunks,
        int SampleCount,
        int CaptureSampleRate
    );

    private const int SampleRate = 16000;
    private const int Channels = 1;
    private const uint FramesPerBuffer = 512;
    private const float AgcTargetRms = 0.1f;
    private const float AgcMaxGain = 20f;
    private const float AgcMinGain = 1f;
    private const float SpeechEnergyThreshold = 0.01f;
    private static readonly TimeSpan s_stopDrainDuration = TimeSpan.FromMilliseconds(120);

    private static int s_paInitCount;
    private static readonly Lock s_paInitLock = new();
    private static string? s_nativeAudioUnavailable;

    private readonly Lock _captureLock = new();
    private readonly Func<int> _defaultInputDeviceIndexProvider;
    private readonly Action _ensurePortAudioInitialized;
    private readonly IErrorLogService? _errorLog;
    private readonly Func<IReadOnlyList<AudioInputDevice>> _inputDeviceListProvider;

    // Reactive trigger for CheckForDefaultDeviceChange: detects OS default capture
    // changes at runtime (pactl subscribe) and, debounced, calls back here. Optional
    // so the buffer/selection paths unit-test without it; when null (or when pactl is
    // absent) the service degrades to lazy re-resolve at the next recording start.
    // Started/stopped as FollowSystemDefault toggles; see StartOrStopDeviceWatcher.
    private readonly IDefaultDeviceChangeWatcher? _deviceWatcher;
    private int _watcherStarted;

    // The stable id of the device the live/last-opened capture stream is bound to.
    // Migration compares this against the freshly-resolved OS default to decide
    // whether a default change requires a swap. Guarded by _captureLock.
    private string? _activeDeviceId;

    // Set when a default-device migration was requested while a recording was in
    // flight; the swap is deferred (never tear down the live buffer) and applied
    // from StopRecording once the WAV has been materialized. Guarded by _captureLock.
    private bool _preferredDeviceMigrationPending;
    private bool _followSystemDefault;
    private readonly Action<int> _openInputStream;
    private readonly List<float[]> _sampleChunks = [];
    private readonly Lock _sampleLock = new();
    private readonly Action _stopAndDisposeInputStreamCore;
    private readonly bool _terminatePortAudioOnDispose;
    private readonly Action<bool>? _wavMaterializationObserver;
    private AudioCaptureSession? _activeCaptureSession;
    private long _captureSessionGeneration;
    private float _currentRmsLevel;
    private int _disposed;
    private int _isPreviewing;
    private int _isRecording;
    private long _lastLevelPostedTicksUtc;

    // Per-frame tap fired from the PortAudio realtime thread during an owned capture.
    // Must be allocation-free and non-blocking; sink borrows processedBuffer (no copy).
    // A throw detaches the sink via CAS so the same exception can't kill every frame.
    private LiveFrameSubscription? _liveFrameSink;
    private int? _openStreamDeviceIndex;
    private int _sampleCount;
    private int? _selectedDeviceIndex;
    private PaStream? _stream;
    private int _whisperModeEnabled;
    internal int CaptureSampleRate { get; private set; } = SampleRate;

    // PortAudio is initialized lazily via EnsurePortAudioInitialized, so
    // constructing this service doesn't load the native library and the
    // buffer-processing path can be unit-tested without portaudio.

    // errorLog is optional so the buffer-processing path can still be unit-tested
    // with a bare `new AudioRecordingService()`; DI supplies the real instance.
    // deviceEnumerator is optional so production/DI reads the PortAudio device table
    // while tests can inject a fake one; deviceWatcher is optional so the migration
    // state machine is exercised without a real pactl process.
    public AudioRecordingService(
        IErrorLogService? errorLog = null,
        IAudioDeviceEnumerator? deviceEnumerator = null,
        IDefaultDeviceChangeWatcher? deviceWatcher = null
    )
    {
        _errorLog = errorLog;
        _deviceWatcher = deviceWatcher;
        _defaultInputDeviceIndexProvider = static () => PortAudio.DefaultInputDevice;
        _ensurePortAudioInitialized = EnsurePortAudioInitialized;
        _inputDeviceListProvider =
            deviceEnumerator is null ? GetInputDevices : deviceEnumerator.GetDevices;
        _openInputStream = OpenInputStream;
        _stopAndDisposeInputStreamCore = StopAndDisposeInputStreamCore;
        _terminatePortAudioOnDispose = true;
        _wavMaterializationObserver = null;
    }

    // Test seam: exercises the production device-selection and ownership state machines
    // while replacing only PortAudio initialization and stream operations.
    internal AudioRecordingService(
        Action<int> openInputStream,
        Func<int> defaultInputDeviceIndexProvider,
        Action stopAndDisposeInputStream,
        IErrorLogService? errorLog = null,
        Action<bool>? wavMaterializationObserver = null
    )
        : this(
            static () => [],
            openInputStream,
            defaultInputDeviceIndexProvider,
            stopAndDisposeInputStream,
            errorLog,
            wavMaterializationObserver
        )
    {
    }

    // Test seam for configured-device resolution. The provider supplies descriptors only;
    // matching and fallback decisions remain in ResolveConfiguredDevice.
    internal AudioRecordingService(
        Func<IReadOnlyList<AudioInputDevice>> inputDeviceListProvider,
        Action<int> openInputStream,
        Func<int> defaultInputDeviceIndexProvider,
        Action stopAndDisposeInputStream,
        IErrorLogService? errorLog = null,
        Action<bool>? wavMaterializationObserver = null
    )
    {
        _errorLog = errorLog;
        _defaultInputDeviceIndexProvider = defaultInputDeviceIndexProvider;
        _ensurePortAudioInitialized = static () => { };
        _inputDeviceListProvider = inputDeviceListProvider;
        _openInputStream = openInputStream;
        _stopAndDisposeInputStreamCore = stopAndDisposeInputStream;
        _terminatePortAudioOnDispose = false;
        _wavMaterializationObserver = wavMaterializationObserver;
    }

    public bool IsRecording => Volatile.Read(ref _isRecording) == 1;
    private bool IsPreviewing => Volatile.Read(ref _isPreviewing) == 1;
    public float CurrentRmsLevel => Volatile.Read(ref _currentRmsLevel);
    public bool HasSpeechEnergy => CurrentRmsLevel >= SpeechEnergyThreshold;

    public int? SelectedDeviceIndex
    {
        get
        {
            lock (_captureLock)
            {
                return _selectedDeviceIndex;
            }
        }
        set
        {
            lock (_captureLock)
            {
                _selectedDeviceIndex = value;
            }
        }
    }

    /// <summary>
    ///     When true the service captures from the current OS default input device
    ///     and migrates to a new default when it changes (deferred while recording).
    ///     When false the pinned <see cref="SelectedDeviceIndex" /> is used verbatim.
    ///     App/ViewModel set this from the persisted "follow system default" sentinel.
    ///     <para>
    ///         Toggling this also starts/stops the runtime default-device watcher so the
    ///         reactive migration trigger is only live while it is meaningful. The
    ///         watcher gracefully no-ops when unavailable (e.g. pactl missing).
    ///     </para>
    /// </summary>
    public bool FollowSystemDefault
    {
        get
        {
            lock (_captureLock)
            {
                return _followSystemDefault;
            }
        }
        set
        {
            lock (_captureLock)
            {
                _followSystemDefault = value;
            }

            StartOrStopDeviceWatcher(value);
        }
    }

    public void Dispose()
    {
        // Stop the reactive watcher before taking the capture lock so no debounced
        // callback can be mid-CheckForDefaultDeviceChange against a disposing service.
        try
        {
            _deviceWatcher?.Stop();
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[AudioRecordingService] Default-device watcher stop failed on dispose: {ex.Message}"
            );
        }

        lock (_captureLock)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            Volatile.Write(ref _disposed, 1);
            Volatile.Write(ref _activeCaptureSession, null);
            Volatile.Write(ref _liveFrameSink, null);
            Volatile.Write(ref _isPreviewing, 0);
            Volatile.Write(ref _isRecording, 0);
            StopAndDisposeInputStream();
        }

        UpdateLevel(0f);

        if (_terminatePortAudioOnDispose)
        {
            TerminatePortAudioIfInitialized();
        }
    }

    /// <summary>
    ///     Why the native audio stack could not be loaded, or null while it is fine.
    ///     Set the first time <see cref="GetInputDevices" /> fails to initialize PortAudio.
    /// </summary>
    public static string? NativeAudioUnavailableReason => Volatile.Read(ref s_nativeAudioUnavailable);

    public static IReadOnlyList<AudioInputDevice> GetInputDevices()
    {
        // PortAudio is a native library resolved on first use, so this throws when the
        // audio stack is missing — no libportaudio, or its libjack/libasound dependencies
        // absent. Enumeration is a query, and its callers run from constructors and UI
        // commands where an escaping exception unwinds straight out of the app; hand back
        // an empty table instead. Capture still fails loudly through the recording paths.
        try
        {
            EnsurePortAudioInitialized();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Volatile.Write(ref s_nativeAudioUnavailable, ex.Message);
            Trace.WriteLine($"[AudioRecordingService] PortAudio unavailable: {ex.Message}");
            return [];
        }

        // Any reason recorded by an earlier failure is now stale; leaving it set would make
        // the UI report audio as unavailable for the rest of the session.
        Volatile.Write(ref s_nativeAudioUnavailable, null);

        var result = new List<AudioInputDevice>();
        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            try
            {
                var info = PortAudio.GetDeviceInfo(i);
                if (info.maxInputChannels > 0)
                {
                    result.Add(
                        new AudioInputDevice(
                            i,
                            info.name,
                            info.maxInputChannels,
                            i == PortAudio.DefaultInputDevice,
                            GetStableDeviceId(info.name, info.maxInputChannels)
                        )
                    );
                }
            }
            catch
            {
                /* ignore broken devices */
            }
        }

        return result;
    }

    internal AudioCaptureSession? TryStartRecording(bool whisperModeEnabled)
    {
        lock (_captureLock)
        {
            if (_activeCaptureSession is not null || Volatile.Read(ref _disposed) == 1)
            {
                return null;
            }

            try
            {
                if (!EnsureInputStreamStarted())
                {
                    _errorLog?.AddEntry(
                        "Recording could not start: no usable microphone was found. "
                        + "Check that an input device is connected and selected in Recorder settings.",
                        ErrorCategory.Recording
                    );
                    return null;
                }
            }
            catch (Exception ex)
            {
                // Surface a stuck-at-silent dictation: the user pressed the hotkey but no
                // input stream could be opened (device busy, all sample rates rejected, …).
                _errorLog?.AddEntry(
                    $"Recording could not start: the microphone could not be opened ({ex.Message}).",
                    ErrorCategory.Recording
                );
                throw;
            }

            lock (_sampleLock)
            {
                _sampleChunks.Clear();
                _sampleCount = 0;
                // Do NOT reset CaptureSampleRate: the input seam may reuse a preview
                // stream, whose negotiated rate was assigned when that stream opened.
            }

            Volatile.Write(ref _whisperModeEnabled, whisperModeEnabled ? 1 : 0);
            Volatile.Write(ref _liveFrameSink, null);
            var session = new AudioCaptureSession(++_captureSessionGeneration);
            Volatile.Write(ref _activeCaptureSession, session);
            Volatile.Write(ref _isRecording, 1);

            Trace.WriteLine(
                $"[AudioRecordingService] Recording started: session={session.DiagnosticId}, "
                + $"captureSampleRate={CaptureSampleRate} Hz, target={SampleRate} Hz."
            );
            return session;
        }
    }

    internal bool IsRecordingOwnedBy(AudioCaptureSession? session)
    {
        lock (_captureLock)
        {
            return session is not null && ReferenceEquals(_activeCaptureSession, session);
        }
    }

    internal bool TrySetWhisperMode(AudioCaptureSession session, bool enabled)
    {
        lock (_captureLock)
        {
            if (!ReferenceEquals(_activeCaptureSession, session))
            {
                return false;
            }

            Volatile.Write(ref _whisperModeEnabled, enabled ? 1 : 0);
            return true;
        }
    }

    internal bool TrySetLiveFrameSink(AudioCaptureSession session, Action<float[]>? sink)
    {
        lock (_captureLock)
        {
            if (!ReferenceEquals(_activeCaptureSession, session))
            {
                return false;
            }

            Volatile.Write(
                ref _liveFrameSink,
                sink is null ? null : new LiveFrameSubscription(session, sink)
            );
            return true;
        }
    }

    internal byte[] StopRecording(AudioCaptureSession session)
    {
        byte[] wav;
        bool migrationPending;
        lock (_captureLock)
        {
            if (!ReferenceEquals(_activeCaptureSession, session))
            {
                return [];
            }

            Volatile.Write(ref _activeCaptureSession, null);
            Volatile.Write(ref _liveFrameSink, null);
            Volatile.Write(ref _isRecording, 0);

            if (!IsPreviewing)
            {
                StopAndDisposeInputStream();
            }

            // Keep the capture lock through materialization. A new owner cannot
            // clear or reuse the sample list until this WAV is complete.
            wav = BuildWavFromRecordedAudio(SnapshotRecordedAudio());
            migrationPending = _preferredDeviceMigrationPending;
        }

        // A default-device change may have been deferred while this recording was in
        // flight (see CheckForDefaultDeviceChange). The buffer is now finalized and the
        // lock released, so it is safe to complete the swap.
        if (migrationPending)
        {
            CheckForDefaultDeviceChange();
        }

        return wav;
    }

    internal async Task<byte[]> StopRecordingAsync(
        AudioCaptureSession session,
        CancellationToken cancellationToken = default
    )
    {
        if (!IsRecordingOwnedBy(session))
        {
            return [];
        }

        try
        {
            await Task.Delay(s_stopDrainDuration, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Still stop and return the samples captured so far.
        }

        // StopRecording validates again so a stale delayed stop cannot affect a
        // newer capture that started while this method was draining.
        return StopRecording(session);
    }

    internal byte[]? GetCurrentBuffer(AudioCaptureSession session)
    {
        lock (_captureLock)
        {
            if (!ReferenceEquals(_activeCaptureSession, session))
            {
                return null;
            }

            var snapshot = SnapshotRecordedAudio();
            return snapshot.SampleCount == 0 ? null : BuildWavFromRecordedAudio(snapshot);
        }
    }

    public bool StartPreview()
    {
        lock (_captureLock)
        {
            if (
                Volatile.Read(ref _disposed) == 1
                || _activeCaptureSession is not null
                || IsPreviewing
            )
            {
                return false;
            }

            try
            {
                if (!EnsureInputStreamStarted())
                {
                    return false;
                }

                Volatile.Write(ref _isPreviewing, 1);
                return true;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[AudioRecordingService] Failed to start preview: {ex.Message}");
                _errorLog?.AddEntry(
                    $"Microphone preview could not start: {ex.Message}",
                    ErrorCategory.Recording
                );
                Volatile.Write(ref _isPreviewing, 0);
                if (_activeCaptureSession is null)
                {
                    StopAndDisposeInputStream();
                }

                return false;
            }
        }
    }

    public void StopPreview()
    {
        lock (_captureLock)
        {
            if (!IsPreviewing)
            {
                return;
            }

            Volatile.Write(ref _isPreviewing, 0);
            if (_activeCaptureSession is null)
            {
                StopAndDisposeInputStream();
            }
        }

        UpdateLevel(0f);
    }

    public AudioInputDevice? ResolveConfiguredDevice(int? preferredIndex, string? preferredDeviceId)
    {
        var devices = _inputDeviceListProvider();

        // Follow-default sentinel: ignore any pinned index/id and take the current
        // default, so a user who once pinned a device can opt back into auto-follow.
        if (IsFollowSystemDefault(preferredDeviceId))
        {
            return ResolveSystemDefault(devices);
        }

        // ReSharper disable once InvertIf -- keeps the three resolution cases as a flat
        // ladder; inverting pulls the last case's return into the middle branch.
        if (!string.IsNullOrWhiteSpace(preferredDeviceId))
        {
            var matches = devices
                .Where(d => string.Equals(d.PersistentId, preferredDeviceId, StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        // A pinned index that survived the id lookup above is a device that no longer exists;
        // fall back to the system default only when nothing was pinned at all.
        return preferredIndex.HasValue ? null : ResolveSystemDefault(devices);
    }

    internal static bool IsFollowSystemDefault(string? deviceId) =>
        string.Equals(
            deviceId,
            AppSettings.FollowSystemDefaultMicrophoneId,
            StringComparison.Ordinal
        );

    /// <summary>
    ///     Synthetic "Automatic (follow system default)" entry for the device
    ///     dropdown. Its <see cref="AudioInputDevice.PersistentId" /> is the
    ///     follow-default sentinel and its <see cref="AudioInputDevice.Index" /> is
    ///     -1 (never a real PortAudio index). Selecting it persists the sentinel;
    ///     capture then follows the current OS default.
    /// </summary>
    public static AudioInputDevice CreateFollowSystemDefaultOption(string displayName) =>
        new(-1, displayName, 0, false, AppSettings.FollowSystemDefaultMicrophoneId);

    private static AudioInputDevice? ResolveSystemDefault(IReadOnlyList<AudioInputDevice> devices)
    {
        return devices.FirstOrDefault(d => d.IsDefault) ?? (devices.Count > 0 ? devices[0] : null);
    }

    // Per-chunk AGC for "whisper mode": boosts quiet speech for noise-gated
    // models. Gain capped at 20× to avoid amplifying silence; samples clamped
    // to [-1, 1] to prevent clipping.
    internal static float[] ApplyWhisperModeGain(float[] samples, bool whisperModeEnabled)
    {
        if (!whisperModeEnabled || samples.Length == 0)
        {
            return samples;
        }

        var rms = ComputeRmsLevel(samples);
        if (rms <= 0.0001f)
        {
            return samples;
        }

        var gain = Math.Clamp(AgcTargetRms / rms, AgcMinGain, AgcMaxGain);
        if (gain <= 1f)
        {
            return samples;
        }

        var adjusted = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            adjusted[i] = Math.Clamp(samples[i] * gain, -1f, 1f);
        }

        return adjusted;
    }

    internal static float ComputeRmsLevel(float[] samples)
    {
        if (samples.Length == 0)
        {
            return 0f;
        }

        double sumSquares = 0;
        // ReSharper disable once LoopCanBeConvertedToQuery -- hot RMS path; the explicit loop avoids LINQ iterator/delegate overhead per audio frame.
        // ReSharper disable once ForCanBeConvertedToForeach -- hot RMS path; keep the explicit indexed loop deliberately, consistent with the query suppression above.
        for (var i = 0; i < samples.Length; i++)
        {
            sumSquares += samples[i] * samples[i];
        }

        return (float)Math.Sqrt(sumSquares / samples.Length);
    }

    // Downsampling applies a symmetric Blackman-windowed sinc low-pass with a
    // 0.40-to-0.50 target-rate transition band before retaining the existing
    // linear interpolation and sample alignment. Upsampling uses interpolation alone.
    internal static float[] ResampleToSampleRate(
        float[] samples,
        int sourceSampleRate,
        int targetSampleRate
    )
    {
        if (samples.Length == 0 || sourceSampleRate <= 0 || sourceSampleRate == targetSampleRate)
        {
            return samples;
        }

        var outputLength = Math.Max(
            1,
            (int)Math.Round(samples.Length * (double)targetSampleRate / sourceSampleRate)
        );
        var output = new float[outputLength];
        var ratio = (double)sourceSampleRate / targetSampleRate;

        if (targetSampleRate > 0 && sourceSampleRate > targetSampleRate)
        {
            var filterRadius = (int)Math.Ceiling(24 * ratio);
            var coefficientCount = filterRadius + 1;
            const int maxStackAllocatedCoefficientCount = 256;
            // ReSharper disable once SuggestVarOrType_Elsewhere -- the explicit Span<double> is the shared target type that unifies the stackalloc and heap arms.
            Span<double> coefficients = coefficientCount <= maxStackAllocatedCoefficientCount
                ? stackalloc double[coefficientCount]
                : new double[coefficientCount];
            CreateDownsamplingFilter(
                coefficients,
                filterRadius,
                sourceSampleRate,
                targetSampleRate
            );

            for (var i = 0; i < output.Length; i++)
            {
                var sourceIndex = i * ratio;
                var leftIndex = (int)Math.Floor(sourceIndex);
                var rightIndex = Math.Min(leftIndex + 1, samples.Length - 1);
                var fraction = (float)(sourceIndex - leftIndex);
                var leftSample = EvaluateFirAtIndex(samples, leftIndex, coefficients);

                if (rightIndex != leftIndex && fraction != 0f)
                {
                    var rightSample = EvaluateFirAtIndex(samples, rightIndex, coefficients);
                    leftSample += (rightSample - leftSample) * fraction;
                }

                output[i] = (float)leftSample;
            }

            return output;
        }

        for (var i = 0; i < output.Length; i++)
        {
            var sourceIndex = i * ratio;
            var leftIndex = (int)Math.Floor(sourceIndex);
            var rightIndex = Math.Min(leftIndex + 1, samples.Length - 1);
            var fraction = (float)(sourceIndex - leftIndex);

            output[i] = samples[leftIndex] + (samples[rightIndex] - samples[leftIndex]) * fraction;
        }

        return output;
    }

    private static void CreateDownsamplingFilter(
        Span<double> coefficients,
        int filterRadius,
        int sourceSampleRate,
        int targetSampleRate
    )
    {
        var normalizedCutoff = 0.45 * targetSampleRate / sourceSampleRate;
        double coefficientSum = 0;

        for (var offset = 0; offset <= filterRadius; offset++)
        {
            var sincArgument = 2 * normalizedCutoff * offset;
            var sinc = offset == 0
                ? 1
                : Math.Sin(Math.PI * sincArgument) / (Math.PI * sincArgument);
            var ideal = 2 * normalizedCutoff * sinc;
            var window = 0.42
                + 0.50 * Math.Cos(Math.PI * offset / filterRadius)
                + 0.08 * Math.Cos(2 * Math.PI * offset / filterRadius);
            var coefficient = ideal * window;
            coefficients[offset] = coefficient;
            coefficientSum += offset == 0 ? coefficient : 2 * coefficient;
        }

        for (var offset = 0; offset < coefficients.Length; offset++)
        {
            coefficients[offset] /= coefficientSum;
        }
    }

    private static double EvaluateFirAtIndex(
        float[] samples,
        int index,
        ReadOnlySpan<double> coefficients
    )
    {
        var result = coefficients[0] * samples[index];
        var finalIndex = samples.Length - 1;

        for (var offset = 1; offset < coefficients.Length; offset++)
        {
            var leftIndex = Math.Max(index - offset, 0);
            var rightIndex = Math.Min(index + offset, finalIndex);
            result += coefficients[offset] * (samples[leftIndex] + samples[rightIndex]);
        }

        return result;
    }

    internal StreamCallbackResult ProcessAudioBufferForTest(float[] frame)
    {
        var handle = GCHandle.Alloc(frame, GCHandleType.Pinned);
        try
        {
            return ProcessAudioBuffer(
                handle.AddrOfPinnedObject(),
                (uint)frame.Length,
                Volatile.Read(ref _activeCaptureSession)
            );
        }
        finally
        {
            handle.Free();
        }
    }

    internal static short ToPcm16(float sample)
    {
        var clamped = Math.Clamp(sample, -1f, 1f);
        return (short)(clamped * short.MaxValue);
    }

    public event EventHandler<float>? LevelChanged;

    private StreamCallbackResult InputAudioCallback(
        IntPtr input,
        IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags,
        IntPtr userData
    )
    {
        return ProcessAudioBuffer(
            input,
            frameCount,
            Volatile.Read(ref _activeCaptureSession)
        );
    }

    private StreamCallbackResult ProcessAudioBuffer(
        IntPtr input,
        uint frameCount,
        AudioCaptureSession? captureSession
    )
    {
        if (input == IntPtr.Zero || frameCount == 0)
        {
            return StreamCallbackResult.Continue;
        }

        var buffer = new float[frameCount];
        Marshal.Copy(input, buffer, 0, (int)frameCount);

        var processedBuffer = ApplyWhisperModeGain(
            buffer,
            captureSession is not null && Volatile.Read(ref _whisperModeEnabled) == 1
        );
        UpdateLevel(ComputeRmsLevel(processedBuffer));

        if (captureSession is null)
        {
            return StreamCallbackResult.Continue;
        }

        lock (_sampleLock)
        {
            // Re-check the token here: a callback from a stopped preview-backed
            // recording can still land after a later owner reset the buffer.
            if (!ReferenceEquals(Volatile.Read(ref _activeCaptureSession), captureSession))
            {
                return StreamCallbackResult.Continue;
            }

            _sampleChunks.Add(processedBuffer);
            _sampleCount += processedBuffer.Length;
        }

        var subscription = Volatile.Read(ref _liveFrameSink);
        if (
            subscription is null
            || !ReferenceEquals(subscription.Session, captureSession)
        )
        {
            return StreamCallbackResult.Continue;
        }

        try
        {
            subscription.Sink(processedBuffer);
        }
        catch (Exception ex)
        {
            // Deliberate catch-all: crashing the PortAudio realtime thread
            // is worse. CAS detach avoids clobbering a newer sink installed
            // by a concurrent stop/start.
            Trace.WriteLine(
                $"[AudioRecordingService] LiveFrameSink threw, detaching: {ex.Message}"
            );
            Interlocked.CompareExchange(ref _liveFrameSink, null, subscription);
        }

        return StreamCallbackResult.Continue;
    }

    private void UpdateLevel(float level)
    {
        Volatile.Write(ref _currentRmsLevel, level);

        var nowTicks = DateTime.UtcNow.Ticks;
        if (level > 0f && !ShouldPostLevelUpdate(nowTicks))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                LevelChanged?.Invoke(this, level);
            }
            catch
            {
                /* ignore */
            }
        });
    }

    private bool ShouldPostLevelUpdate(long nowTicks)
    {
        // Rate-limit UI posts to ~15 Hz (66 ms): the PortAudio callback fires
        // at ~30 Hz and flooding the dispatcher causes visible input lag.
        // CAS loop ensures only one concurrent caller wins.
        var minIntervalTicks = TimeSpan.FromMilliseconds(66).Ticks;

        while (true)
        {
            var lastTicks = Interlocked.Read(ref _lastLevelPostedTicksUtc);
            if (nowTicks - lastTicks < minIntervalTicks)
            {
                return false;
            }

            if (
                Interlocked.CompareExchange(ref _lastLevelPostedTicksUtc, nowTicks, lastTicks)
                == lastTicks
            )
            {
                return true;
            }
        }
    }

    private int? ResolveSelectedDeviceIndex()
    {
        // Called with _captureLock held. Read the selected index once so
        // resolve/compare/rebuild sees one consistent device snapshot.

        // In follow-default mode re-resolve the current OS default from the device
        // list and remember its stable id, so a later default change is detectable.
        // NOTE: the provider reads PortAudio's CACHED table, so callers needing the
        // freshest default refresh it first (EnsureInputStreamStarted and
        // CheckForDefaultDeviceChange both call RefreshPortAudioDeviceTable ahead of this).
        if (_followSystemDefault)
        {
            var preferred = ResolveSystemDefault(_inputDeviceListProvider());
            if (preferred is not null)
            {
                _activeDeviceId = preferred.PersistentId;
                return preferred.Index;
            }
        }

        var deviceIndex = _selectedDeviceIndex ?? _defaultInputDeviceIndexProvider();
        if (deviceIndex != PortAudio.NoDevice)
        {
            _activeDeviceId ??= TryGetStableDeviceId(deviceIndex);
            return deviceIndex;
        }

        Trace.WriteLine("[AudioRecordingService] No default input device.");
        return null;
    }

    private bool EnsureInputStreamStarted()
    {
        _ensurePortAudioInitialized();

        // In follow-default mode, cycle PortAudio's cached device table BEFORE resolving
        // so recording starts on the CURRENT OS default rather than whatever default was
        // captured at the last Pa_Initialize. Without this a default change that happened
        // while the app was idle (no watcher event, or pactl unavailable) would leave a new
        // recording bound to the STALE default. Safe here: _captureLock is held and the
        // refresh no-ops while any stream is open or a recording is live.
        if (_followSystemDefault)
        {
            RefreshPortAudioDeviceTable();
        }

        // Resolve before considering reuse: a preview stream is reusable only
        // when it was opened for this exact requested/default device index.
        var deviceIndex = ResolveSelectedDeviceIndex();
        if (deviceIndex is not null && _openStreamDeviceIndex == deviceIndex)
        {
            return true;
        }

        var replacingPreviewStream = IsPreviewing && _openStreamDeviceIndex is not null;
        try
        {
            StopAndDisposeInputStream();

            if (deviceIndex is null)
            {
                if (replacingPreviewStream)
                {
                    Volatile.Write(ref _isPreviewing, 0);
                }

                return false;
            }

            // CaptureSampleRate is committed only after Start() succeeds; publish
            // the owning device only after the opener returns successfully.
            _openInputStream(deviceIndex.Value);
            _openStreamDeviceIndex = deviceIndex.Value;
            return true;
        }
        catch
        {
            // The old preview no longer owns a live stream. Do not let a failed
            // replacement leave the service logically previewing disposed input.
            if (replacingPreviewStream)
            {
                Volatile.Write(ref _isPreviewing, 0);
            }

            throw;
        }
    }

    private void StopAndDisposeInputStream()
    {
        if (_openStreamDeviceIndex is null && _stream is null)
        {
            return;
        }

        try
        {
            _stopAndDisposeInputStreamCore();
        }
        finally
        {
            // Keep the physical stream and its concrete device owner synchronized,
            // even when best-effort native teardown throws.
            _stream = null;
            _openStreamDeviceIndex = null;
        }
    }

    private void StopAndDisposeInputStreamCore()
    {
        try
        {
            _stream?.Stop();
        }
        catch
        {
            /* best effort */
        }

        _stream?.Dispose();
    }

    private void OpenInputStream(int deviceIndex)
    {
        // The constructor can accept rates that the device rejects at Start(),
        // so assign the physical stream only after CreateInputStream has started it.
        _stream = CreateInputStream(deviceIndex, InputAudioCallback);
    }

    private static string GetStableDeviceId(string deviceName, int maxInputChannels)
    {
        return $"{deviceName}|{maxInputChannels}";
    }

    private static string? TryGetStableDeviceId(int deviceIndex)
    {
        try
        {
            if (deviceIndex < 0 || deviceIndex >= PortAudio.DeviceCount)
            {
                return null;
            }

            var info = PortAudio.GetDeviceInfo(deviceIndex);
            return GetStableDeviceId(info.name, info.maxInputChannels);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Re-resolve the current OS default input device and, in follow-default mode,
    ///     migrate the capture to it when it has changed. IN-FLIGHT-SAFE: the live capture
    ///     stream is NEVER torn down while a recording is in progress — the migration is
    ///     deferred and replayed from <see cref="StopRecording" /> once the WAV has been
    ///     materialized.
    /// </summary>
    /// <remarks>
    ///     Decisions are made on the stable <c>PersistentId</c> ("name|channels"), not the
    ///     PortAudio index, because PipeWire/PulseAudio reorder and re-index devices freely.
    ///     At runtime this is triggered from a background thread by the injected
    ///     <see cref="IDefaultDeviceChangeWatcher" />; with no watcher (or no pactl) nothing
    ///     calls it and behavior falls back to lazy re-resolve at the next capture start.
    ///     The whole refresh→resolve→migrate sequence runs under <c>_captureLock</c>, so it
    ///     can never race a start/stop that is opening or disposing the stream.
    /// </remarks>
    public void CheckForDefaultDeviceChange()
    {
        lock (_captureLock)
        {
            if (Volatile.Read(ref _disposed) == 1 || !_followSystemDefault)
            {
                return;
            }

            // Refresh PortAudio's cached table so the provider reports the NEW default.
            // Returns false while a stream is live / recording — the reading is then STALE
            // and must not be treated as authoritative when clearing a pending migration.
            var deviceTableFresh = RefreshPortAudioDeviceTable();

            AudioInputDevice? preferred;
            try
            {
                preferred = ResolveSystemDefault(_inputDeviceListProvider());
            }
            catch (Exception ex)
            {
                Trace.WriteLine(
                    $"[AudioRecordingService] Default-device re-resolve failed, keeping current: {ex.Message}"
                );
                return;
            }

            if (preferred is null)
            {
                // No devices right now (all unplugged); keep what we have and retry on the
                // next check rather than tearing down a possibly-live stream.
                return;
            }

            if (string.Equals(_activeDeviceId, preferred.PersistentId, StringComparison.Ordinal))
            {
                // Only clear a pending defer when this reading is TRUSTWORTHY. If the refresh
                // was skipped, the table still reports the OLD default — which of course equals
                // the device we are recording on — so "already on default" is an artifact of the
                // stale table, not proof the pending migration is moot. Clearing it would drop a
                // real deferred migration that StopRecording is relying on replaying.
                if (deviceTableFresh)
                {
                    _preferredDeviceMigrationPending = false;
                }

                return;
            }

            // Never tear down an in-flight recording to migrate. Defer and let StopRecording
            // re-invoke this once the buffer is finalized.
            if (Volatile.Read(ref _isRecording) == 1 || _activeCaptureSession is not null)
            {
                _preferredDeviceMigrationPending = true;
                Trace.WriteLine(
                    "[AudioRecordingService] Default device changed while recording; migration deferred."
                );
                return;
            }

            _preferredDeviceMigrationPending = false;
            MigrateActiveCaptureToDevice(preferred);
        }
    }

    // Swap the capture to the preferred device. Called with _captureLock held and with a
    // recording guaranteed not to be in flight, so only an idle service or a live preview
    // is affected. A no-op when no stream is open — the next EnsureInputStreamStarted picks
    // up the new default via ResolveSelectedDeviceIndex.
    private void MigrateActiveCaptureToDevice(AudioInputDevice preferred)
    {
        var wasPreviewing = IsPreviewing;
        _selectedDeviceIndex = preferred.Index;
        _activeDeviceId = preferred.PersistentId;

        if (_openStreamDeviceIndex is null && _stream is null)
        {
            // Idle: the new default is applied lazily on the next capture start.
            return;
        }

        StopAndDisposeInputStream();

        if (!wasPreviewing)
        {
            return;
        }

        try
        {
            // ReSharper disable once InvertIf -- compact "if reopen failed, handle it" guard.
            if (!EnsureInputStreamStarted())
            {
                Trace.WriteLine(
                    "[AudioRecordingService] Preview stream could not reopen on the new default device."
                );
                Volatile.Write(ref _isPreviewing, 0);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[AudioRecordingService] Failed to reopen preview on migrated device: {ex.Message}"
            );
            Volatile.Write(ref _isPreviewing, 0);
            StopAndDisposeInputStream();
        }
    }

    // Refresh PortAudio's device table (snapshotted at Pa_Initialize; it does NOT observe
    // OS default changes until re-initialized) by cycling Terminate()+Initialize(). CRITICAL:
    // a no-op while any stream is live — re-initializing would invalidate the native handle
    // and could crash the realtime callback.
    //
    // Returns TRUE when the caller can trust the table afterward (cycled, or PortAudio was
    // never initialized so the next enumeration reads a fresh table). Returns FALSE ONLY when
    // the refresh was SKIPPED for a live stream, leaving the table stale.
    private bool RefreshPortAudioDeviceTable()
    {
        // Called with _captureLock held, so the stream state cannot change underneath.
        if (_stream is not null || _openStreamDeviceIndex is not null || IsRecording)
        {
            return false;
        }

        lock (s_paInitLock)
        {
            // Delegated to a static helper so the process-global init counter is only ever
            // written by the static lifetime methods, matching EnsurePortAudioInitialized.
            CyclePortAudioDeviceTableLocked();
        }

        return true;
    }

    // Terminate()+Initialize() cycle that refreshes PortAudio's device-table snapshot.
    // CONTRACT: the caller MUST hold s_paInitLock and have verified no stream is live.
    private static void CyclePortAudioDeviceTableLocked()
    {
        if (s_paInitCount <= 0)
        {
            // Not initialized yet; the next EnsurePortAudioInitialized reads a fresh table
            // anyway, so there is nothing cached to refresh and the reading is NOT stale.
            return;
        }

        try
        {
            PortAudio.Terminate();

            // From here PortAudio is terminated: the count must not stay positive unless a
            // matching Initialize() succeeds, or the next EnsurePortAudioInitialized would
            // skip the re-init and leave the library terminated-but-"initialized".
            s_paInitCount = 0;

            PortAudio.Initialize();
            s_paInitCount = 1;
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[AudioRecordingService] PortAudio device-table refresh failed: {ex.Message}"
            );
            try
            {
                PortAudio.Initialize();
                s_paInitCount = 1;
            }
            catch
            {
                // Leave the count as set above (0 after a successful Terminate) so a later
                // EnsurePortAudioInitialized retries the init.
            }
        }
    }

    // Start/stop is idempotent via _watcherStarted so repeated FollowSystemDefault
    // assignments (App bootstrap + ViewModel selection) don't spawn duplicate processes.
    // Never throws: a watcher that cannot start degrades to lazy re-resolve.
    private void StartOrStopDeviceWatcher(bool follow)
    {
        if (_deviceWatcher is null || Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        if (follow)
        {
            if (Interlocked.Exchange(ref _watcherStarted, 1) == 1)
            {
                return;
            }

            try
            {
                _deviceWatcher.Start(CheckForDefaultDeviceChange);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _watcherStarted, 0);
                Trace.WriteLine(
                    $"[AudioRecordingService] Default-device watcher start failed: {ex.Message}"
                );
            }

            return;
        }

        if (Interlocked.Exchange(ref _watcherStarted, 0) == 0)
        {
            return;
        }

        try
        {
            _deviceWatcher.Stop();
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[AudioRecordingService] Default-device watcher stop failed: {ex.Message}"
            );
        }
    }

    // ---- Test seams for the migration state machine -----------------------
    // Exercised through the injected device enumerator with no real PortAudio stream:
    // _stream stays null (so MigrateActiveCaptureToDevice only updates the target
    // index/id) and RefreshPortAudioDeviceTable no-ops (PortAudio uninitialized).

    internal string? ActiveDeviceIdForTest
    {
        get
        {
            lock (_captureLock)
            {
                return _activeDeviceId;
            }
        }
    }

    internal bool MigrationPendingForTest
    {
        get
        {
            lock (_captureLock)
            {
                return _preferredDeviceMigrationPending;
            }
        }
    }

    // Seed the device the (notional) capture is bound to, as if a stream had been opened.
    internal void SetActiveDeviceIdForTest(string? deviceId, int? deviceIndex)
    {
        lock (_captureLock)
        {
            _activeDeviceId = deviceId;
            _selectedDeviceIndex = deviceIndex;
        }
    }

    // Simulate an in-flight recording without a native stream, so the deferral path
    // in CheckForDefaultDeviceChange can be exercised.
    internal void SetRecordingForTest(bool recording)
    {
        Volatile.Write(ref _isRecording, recording ? 1 : 0);
    }

    // Seed the deferred-migration flag so the "pending survives a stale/skipped device-table
    // refresh" path can be exercised without reconstructing a full defer→recheck sequence.
    internal void SetMigrationPendingForTest(bool pending)
    {
        lock (_captureLock)
        {
            _preferredDeviceMigrationPending = pending;
        }
    }

    private PaStream CreateInputStream(int deviceIndex, PaStream.Callback callback)
    {
        var inputInfo = PortAudio.GetDeviceInfo(deviceIndex);
        var candidateRates = CandidateSampleRates(inputInfo.defaultSampleRate);
        Exception? lastError = null;

        foreach (var sampleRate in candidateRates)
        {
            PaStream? stream = null;
            try
            {
                stream = CreateInputStream(deviceIndex, inputInfo, sampleRate, callback);
                stream.Start();
                CaptureSampleRate = sampleRate;
                Trace.WriteLine(
                    $"[AudioRecordingService] Opened input stream: device={deviceIndex} ('{inputInfo.name}'), "
                    + $"negotiatedRate={sampleRate} Hz, deviceDefaultRate={inputInfo.defaultSampleRate} Hz, "
                    + $"resampleToTarget={(sampleRate != SampleRate ? "yes" : "no")}."
                );

                return stream;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Trace.WriteLine(
                    $"[AudioRecordingService] Failed to open input stream at {sampleRate} Hz: {ex.Message}"
                );
                try { stream?.Dispose(); }
                catch
                {
                    /* best effort */
                }
            }
        }

        throw lastError
              ?? new InvalidOperationException(
                  "No compatible input sample rate was accepted by PortAudio."
              );
    }

    private static PaStream CreateInputStream(
        int deviceIndex,
        DeviceInfo inputInfo,
        int sampleRate,
        PaStream.Callback callback
    )
    {
        var inputParams = new StreamParameters
        {
            device = deviceIndex,
            channelCount = Channels,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = inputInfo.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        return new PaStream(
            inputParams,
            null,
            sampleRate,
            FramesPerBuffer,
            StreamFlags.ClipOff,
            callback,
            IntPtr.Zero
        );
    }

    private static List<int> CandidateSampleRates(double defaultSampleRate)
    {
        // Try the device's native rate first to avoid PortAudio internal resampling;
        // fall through common rates in descending order. Captured audio is always
        // resampled to 16 kHz in software before transcription.
        var rates = new List<int>();
        AddRate((int)Math.Round(defaultSampleRate));
        AddRate(48000);
        AddRate(44100);
        AddRate(32000);
        AddRate(24000);
        AddRate(SampleRate);
        return rates;

        void AddRate(int rate)
        {
            if (rate > 0 && !rates.Contains(rate))
            {
                rates.Add(rate);
            }
        }
    }

    private static byte[] FloatSamplesToWav(float[] samples, int sampleRate)
    {
        return WriteWav(
            sampleRate,
            samples.Length,
            writer =>
            {
                foreach (var sample in samples)
                {
                    writer.Write(ToPcm16(sample));
                }
            }
        );
    }

    private RecordedAudioSnapshot SnapshotRecordedAudio()
    {
        lock (_sampleLock)
        {
            return new RecordedAudioSnapshot(
                _sampleChunks.ToArray(),
                _sampleCount,
                CaptureSampleRate
            );
        }
    }

    private byte[] BuildWavFromRecordedAudio(RecordedAudioSnapshot snapshot)
    {
        _wavMaterializationObserver?.Invoke(_sampleLock.IsHeldByCurrentThread);

        var samples = new float[snapshot.SampleCount];
        var offset = 0;
        foreach (var chunk in snapshot.Chunks)
        {
            Array.Copy(chunk, 0, samples, offset, chunk.Length);
            offset += chunk.Length;
        }

        var outputSamples = ResampleToSampleRate(
            samples,
            snapshot.CaptureSampleRate,
            SampleRate
        );
        Trace.WriteLine(
            $"[AudioRecordingService] Finalized WAV: capturedSamples={samples.Length} @ {snapshot.CaptureSampleRate} Hz "
            + $"({samples.Length / (double)snapshot.CaptureSampleRate:F2}s real-time), "
            + $"outputSamples={outputSamples.Length} @ {SampleRate} Hz "
            + $"({outputSamples.Length / (double)SampleRate:F2}s tagged)."
        );
        return FloatSamplesToWav(outputSamples, SampleRate);
    }

    private static byte[] WriteWav(
        int sampleRate,
        int sampleCount,
        Action<BinaryWriter> writeSamples
    )
    {
        const short bitsPerSample = 16;
        const short channels = 1;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        const int blockAlign = channels * bitsPerSample / 8;
        var dataSize = sampleCount * 2;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + dataSize);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16); // fmt chunk size
        w.Write((short)1); // PCM
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write(bitsPerSample);
        w.Write("data"u8);
        w.Write(dataSize);

        writeSamples(w);

        return ms.ToArray();
    }

    // Idempotent: initializes on first call only. GetInputDevices also calls
    // this; without idempotence the count would leak and Dispose would never
    // terminate PortAudio.
    private static void EnsurePortAudioInitialized()
    {
        lock (s_paInitLock)
        {
            if (s_paInitCount != 0)
            {
                return;
            }

            PortAudio.Initialize();
            s_paInitCount = 1;
        }
    }

    // Teardown counterpart to EnsurePortAudioInitialized. Kept static so the
    // process-global init counter is only ever mutated by the static lifetime
    // helpers, never written directly from an instance's Dispose.
    private static void TerminatePortAudioIfInitialized()
    {
        lock (s_paInitLock)
        {
            if (s_paInitCount <= 0)
            {
                return;
            }

            s_paInitCount = 0;
            try
            {
                PortAudio.Terminate();
            }
            catch
            {
                // PortAudio teardown is best-effort; ignore if it was never fully initialized.
            }
        }
    }
}

/// <summary>Minimal descriptor for an audio input device the user can pick from.</summary>
public sealed record AudioInputDevice(
    int Index,
    string Name,
    int MaxInputChannels,
    bool IsDefault,
    string PersistentId
);

/// <summary>
///     Device-enumeration seam for <see cref="AudioRecordingService" />. Abstracts the
///     PortAudio device table so the follow-default selection policy and the
///     migration-deferral state machine can be unit-tested without real hardware.
///     <see cref="GetDevices" /> must report which device is the current OS default
///     (via <see cref="AudioInputDevice.IsDefault" />).
///     <para>
///         PortAudio snapshots its device list at <c>Pa_Initialize</c> and does NOT observe
///         OS default changes until re-initialized, so a production implementation only
///         reflects a changed default after <see cref="AudioRecordingService" /> has cycled
///         the native library — which it does at the start of
///         <see cref="AudioRecordingService.CheckForDefaultDeviceChange" /> and only when no
///         stream is live.
///     </para>
/// </summary>
public interface IAudioDeviceEnumerator
{
    IReadOnlyList<AudioInputDevice> GetDevices();
}
