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

    private readonly List<float[]> _sampleChunks = [];
    private readonly Lock _sampleLock = new();
    private float _currentRmsLevel;
    private int _disposed;
    private int _isPreviewing;
    private int _isRecording;
    private long _lastLevelPostedTicksUtc;

    // Device enumeration seam. Production uses the PortAudio-backed enumerator;
    // tests inject a fake so the follow-default selection policy and the
    // migration-deferral state machine can be exercised without real hardware
    // or a native PortAudio table. Instance-level (not static) so a test service
    // is fully isolated; GetInputDevices() (the static view helper) uses the
    // process-wide PortAudio enumerator.
    private readonly IAudioDeviceEnumerator _deviceEnumerator;

    // The stable id of the device the live/last-created capture stream is bound to.
    // Migration compares this against the freshly-resolved OS default to decide
    // whether a default change requires a swap.
    private string? _activeDeviceId;

    // Set when a default-device migration was requested while a recording was in
    // flight; the swap is deferred (never tear down the live buffer) and applied
    // on the next CheckForDefaultDeviceChange() once recording has stopped. Mirrors
    // upstream's _preferredDeviceMigrationPending.
    private bool _preferredDeviceMigrationPending;

    private readonly Lock _migrationLock = new();

    // Per-frame tap fired from the PortAudio realtime thread when copySamples is true.
    // Must be allocation-free and non-blocking; sink borrows processedBuffer (no copy).
    // A throw detaches the sink via CAS so the same exception can't kill every frame.
    private Action<float[]>? _liveFrameSink;
    private int _sampleCount;
    private PaStream? _stream;

    // Serializes every capture-stream lifecycle transition (open / stop+dispose /
    // migrate) AND the PortAudio Terminate()+Initialize() device-table refresh, so a
    // watcher-thread migration can never race a UI-thread StartRecording/StartPreview/
    // StopRecording that is opening or disposing the native stream. Held only around
    // the brief transition — never for the duration of a recording — so the
    // never-interrupt-a-live-recording guarantee is preserved (migration still defers
    // while IsRecording). Ordering: acquire _streamLock as the outermost lock; nested
    // acquisition of s_paInitLock (inside RefreshPortAudioDeviceTable) is fine because
    // the reverse order never occurs.
    private readonly Lock _streamLock = new();
    private readonly IErrorLogService? _errorLog;

    // Reactive trigger for CheckForDefaultDeviceChange: detects OS default capture
    // changes at runtime (pactl subscribe) and, debounced, calls back here. Optional
    // so the buffer/selection paths unit-test without it; when null (or when pactl is
    // absent) the service degrades to lazy re-resolve at the next recording start.
    // Started/stopped as FollowSystemDefault toggles; see StartOrStopDeviceWatcher.
    private readonly IDefaultDeviceChangeWatcher? _deviceWatcher;
    private int _watcherStarted;

    internal int CaptureSampleRate { get; private set; } = SampleRate;

    // PortAudio is initialized lazily via EnsurePortAudioInitialized, so
    // constructing this service doesn't load the native library and the
    // buffer-processing path can be unit-tested without portaudio.

    // errorLog is optional so the buffer-processing path can still be unit-tested
    // with a bare `new AudioRecordingService()`; DI supplies the real instance.
    // deviceEnumerator is optional so production/DI gets the PortAudio-backed
    // enumerator by default while tests can inject a fake device table.
    // deviceWatcher is optional so tests exercise the migration state machine
    // without a real pactl process; DI supplies the pactl-backed watcher.
    public AudioRecordingService(
        IErrorLogService? errorLog = null,
        IAudioDeviceEnumerator? deviceEnumerator = null,
        IDefaultDeviceChangeWatcher? deviceWatcher = null
    )
    {
        _errorLog = errorLog;
        _deviceEnumerator = deviceEnumerator ?? PortAudioDeviceEnumerator.Shared;
        _deviceWatcher = deviceWatcher;
    }

    public bool IsRecording => Volatile.Read(ref _isRecording) == 1;
    private bool IsPreviewing => Volatile.Read(ref _isPreviewing) == 1;
    public float CurrentRmsLevel => Volatile.Read(ref _currentRmsLevel);
    public bool HasSpeechEnergy => CurrentRmsLevel >= SpeechEnergyThreshold;

    public int? SelectedDeviceIndex { get; set; }

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
        get => _followSystemDefault;
        set
        {
            _followSystemDefault = value;
            StartOrStopDeviceWatcher();
        }
    }

    private bool _followSystemDefault;

    public bool WhisperModeEnabled { get; set; }

    internal Action<float[]>? LiveFrameSink
    {
        get => _liveFrameSink;
        set => _liveFrameSink = value;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        Volatile.Write(ref _isPreviewing, 0);
        Volatile.Write(ref _isRecording, 0);

        // Stop the reactive default-device watcher first so no debounced callback can
        // race a partially-disposed service (CheckForDefaultDeviceChange also guards on
        // _disposed, but killing the child process here is the clean primary path).
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

        StopAndDisposeInputStream();
        UpdateLevel(0f);

        TerminatePortAudioIfInitialized();
    }

    public static IReadOnlyList<AudioInputDevice> GetInputDevices()
    {
        return PortAudioDeviceEnumerator.Shared.GetDevices();
    }

    public void StartRecording()
    {
        if (IsRecording || Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        lock (_sampleLock)
        {
            _sampleChunks.Clear();
            _sampleCount = 0;
            // Do NOT reset _captureSampleRate: EnsureInputStreamStarted may reuse a
            // preview stream, and the negotiated rate is only assigned inside
            // CreateInputStream. Resetting early would tag samples at the wrong rate.
        }

        // Open the stream AND flip into the recording state atomically under _streamLock
        // so a watcher-thread migration can never observe an open stream that is not yet
        // marked as recording and dispose it out from under the imminent capture.
        lock (_streamLock)
        {
            try
            {
                if (!EnsureInputStreamStarted())
                {
                    _errorLog?.AddEntry(
                        "Recording could not start: no usable microphone was found. "
                        + "Check that an input device is connected and selected in Recorder settings.",
                        ErrorCategory.Recording
                    );
                    return;
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

            Trace.WriteLine(
                $"[AudioRecordingService] Recording started: captureSampleRate={CaptureSampleRate} Hz, target={SampleRate} Hz."
            );

            Volatile.Write(ref _isRecording, 1);
        }
    }

    public byte[] StopRecording()
    {
        if (!IsRecording)
        {
            return [];
        }

        // Flip out of the recording state AND dispose the stream atomically under
        // _streamLock so a watcher-thread migration can't observe IsRecording==false
        // mid-teardown and dispose the same live capture stream concurrently.
        lock (_streamLock)
        {
            Volatile.Write(ref _isRecording, 0);

            if (!IsPreviewing)
            {
                StopAndDisposeInputStream();
            }
        }

        var wav = BuildWavFromRecordedAudio();

        // A default-device change may have been deferred while this recording was
        // in flight (see CheckForDefaultDeviceChange). The live buffer has now been
        // finalized above, so it is safe to migrate. Re-check to complete the swap.
        bool pending;
        lock (_migrationLock)
        {
            pending = _preferredDeviceMigrationPending;
        }

        if (pending)
        {
            CheckForDefaultDeviceChange();
        }

        return wav;
    }

    public async Task<byte[]> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRecording)
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

        return StopRecording();
    }

    public byte[]? GetCurrentBuffer()
    {
        if (!IsRecording)
        {
            return null;
        }

        lock (_sampleLock)
        {
            if (_sampleCount == 0)
            {
                return null;
            }
        }

        return BuildWavFromRecordedAudio();
    }

    public bool StartPreview()
    {
        if (Volatile.Read(ref _disposed) == 1 || IsRecording || IsPreviewing)
        {
            return false;
        }

        // Open + flip into preview atomically under _streamLock (same rationale as
        // StartRecording) so a migration can't dispose the freshly opened stream.
        lock (_streamLock)
        {
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
                if (!IsRecording)
                {
                    StopAndDisposeInputStream();
                }

                return false;
            }
        }
    }

    public void StopPreview()
    {
        if (!IsPreviewing)
        {
            return;
        }

        lock (_streamLock)
        {
            Volatile.Write(ref _isPreviewing, 0);
            if (!IsRecording)
            {
                StopAndDisposeInputStream();
            }
        }

        UpdateLevel(0f);
    }

    /// <summary>
    ///     Resolve the microphone the service should capture from, given a saved
    ///     selection. Resolution order:
    ///     <list type="number">
    ///         <item>
    ///             The "follow system default" sentinel — always resolves to the
    ///             current OS default (or first device if the default is unknown),
    ///             so a user who once pinned a device can opt back into auto-follow.
    ///         </item>
    ///         <item>An explicit device matched by stable id.</item>
    ///         <item>An explicit device matched by legacy index (id churn fallback).</item>
    ///         <item>
    ///             Automatic (nothing configured): the system default endpoint first,
    ///             then the first available device.
    ///         </item>
    ///     </list>
    /// </summary>
    public AudioInputDevice? ResolveConfiguredDevice(int? preferredIndex, string? preferredDeviceId)
    {
        var devices = _deviceEnumerator.GetDevices();

        // Follow-default sentinel: ignore any pinned index/id and take the current default.
        if (IsFollowSystemDefault(preferredDeviceId))
        {
            return ResolveSystemDefault(devices);
        }

        if (!string.IsNullOrWhiteSpace(preferredDeviceId))
        {
            var byId = devices.FirstOrDefault(d => d.PersistentId == preferredDeviceId);
            if (byId is not null)
            {
                return byId;
            }
        }

        if (preferredIndex.HasValue)
        {
            var byIndex = devices.FirstOrDefault(d => d.Index == preferredIndex.Value);
            if (byIndex is not null)
            {
                return byIndex;
            }
        }

        return ResolveSystemDefault(devices);
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
        new(
            -1,
            displayName,
            0,
            false,
            AppSettings.FollowSystemDefaultMicrophoneId
        );

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

    // Linear-interpolation resampler: adequate quality for speech (well below
    // Nyquist for any capture rate) without a native resampling library.
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

    internal StreamCallbackResult ProcessAudioBufferForTest(float[] frame, bool copySamples)
    {
        var handle = GCHandle.Alloc(frame, GCHandleType.Pinned);
        try
        {
            return ProcessAudioBuffer(
                handle.AddrOfPinnedObject(),
                (uint)frame.Length,
                copySamples
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
        return ProcessAudioBuffer(input, frameCount, IsRecording);
    }

    private StreamCallbackResult ProcessAudioBuffer(IntPtr input, uint frameCount, bool copySamples)
    {
        if (input == IntPtr.Zero || frameCount == 0)
        {
            return StreamCallbackResult.Continue;
        }

        var buffer = new float[frameCount];
        Marshal.Copy(input, buffer, 0, (int)frameCount);

        var processedBuffer = ApplyWhisperModeGain(buffer, copySamples && WhisperModeEnabled);
        UpdateLevel(ComputeRmsLevel(processedBuffer));

        if (!copySamples)
        {
            return StreamCallbackResult.Continue;
        }

        lock (_sampleLock)
        {
            _sampleChunks.Add(processedBuffer);
            _sampleCount += processedBuffer.Length;
        }

        var sink = _liveFrameSink;
        if (sink is null)
        {
            return StreamCallbackResult.Continue;
        }

        try
        {
            sink(processedBuffer);
        }
        catch (Exception ex)
        {
            // Deliberate catch-all: crashing the PortAudio realtime thread
            // is worse. CAS detach avoids clobbering a newer sink installed
            // by a concurrent stop/start.
            Trace.WriteLine(
                $"[AudioRecordingService] LiveFrameSink threw, detaching: {ex.Message}"
            );
            Interlocked.CompareExchange(ref _liveFrameSink, null, sink);
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
        // In follow-default mode always re-resolve the current OS default from the
        // enumerator and remember its stable id as the preferred device so a later
        // default change can be detected. NOTE: the enumerator reads PortAudio's CACHED
        // table (it only ensures init, it does not cycle the library), so callers that
        // need the freshest default must refresh the table first — EnsureInputStreamStarted
        // and CheckForDefaultDeviceChange both call RefreshPortAudioDeviceTable ahead of
        // this. Also honors an explicit pin by index.
        if (FollowSystemDefault)
        {
            var devices = _deviceEnumerator.GetDevices();
            var preferred = ResolveSystemDefault(devices);
            if (preferred is not null)
            {
                _activeDeviceId = preferred.PersistentId;
                return preferred.Index;
            }
        }
        else if (SelectedDeviceIndex.HasValue)
        {
            _activeDeviceId = TryGetStableDeviceId(SelectedDeviceIndex.Value);
        }

        var deviceIndex = SelectedDeviceIndex ?? PortAudio.DefaultInputDevice;
        if (deviceIndex != PortAudio.NoDevice)
        {
            _activeDeviceId ??= TryGetStableDeviceId(deviceIndex);
            return deviceIndex;
        }

        Trace.WriteLine("[AudioRecordingService] No default input device.");
        return null;
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

    private bool EnsureInputStreamStarted()
    {
        // _streamLock serializes the native open against a concurrent watcher-thread
        // device-table refresh / migration so PortAudio is never re-initialized while
        // Pa_OpenStream/Pa_StartStream is running on this thread.
        lock (_streamLock)
        {
            if (_stream is not null)
            {
                return true;
            }

            EnsurePortAudioInitialized();

            // In follow-default mode, cycle PortAudio's cached device table BEFORE
            // resolving the device so recording starts on the CURRENT OS default rather
            // than whatever default was captured at the last Pa_Initialize. Without this
            // a default change that happened while the app was idle (no watcher event, or
            // pactl unavailable) would leave a new recording bound to the STALE default.
            // Safe here: we hold _streamLock and _stream is null (checked above) and
            // IsRecording is still false (StartRecording/StartPreview flip it only AFTER
            // this returns), so RefreshPortAudioDeviceTable does not skip and never
            // re-inits under a live stream. In pinned mode the table is left untouched.
            if (FollowSystemDefault)
            {
                RefreshPortAudioDeviceTable();
            }

            var deviceIndex = ResolveSelectedDeviceIndex();
            if (deviceIndex is null)
            {
                return false;
            }

            // _captureSampleRate is committed only after Start() succeeds — the
            // PaStream constructor accepts rates that the device rejects at start time.
            _stream = CreateInputStream(deviceIndex.Value, InputAudioCallback);
            return true;
        }
    }

    private void StopAndDisposeInputStream()
    {
        lock (_streamLock)
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
            _stream = null;
        }
    }

    internal static string GetStableDeviceId(string deviceName, int maxInputChannels)
    {
        return $"{deviceName}|{maxInputChannels}";
    }

    // Init wrapper for PortAudioDeviceEnumerator (which lives outside this class but
    // must share the ref-counted global init). Keeps the s_paInitLock/s_paInitCount
    // discipline in one place.
    internal static void EnsurePortAudioInitializedForEnumerator()
    {
        EnsurePortAudioInitialized();
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
            hostApiSpecificStreamInfo = IntPtr.Zero
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

    private byte[] BuildWavFromRecordedAudio()
    {
        lock (_sampleLock)
        {
            var samples = new float[_sampleCount];
            var offset = 0;
            foreach (var chunk in _sampleChunks)
            {
                Array.Copy(chunk, 0, samples, offset, chunk.Length);
                offset += chunk.Length;
            }

            var outputSamples = ResampleToSampleRate(samples, CaptureSampleRate, SampleRate);
            Trace.WriteLine(
                $"[AudioRecordingService] Finalized WAV: capturedSamples={samples.Length} @ {CaptureSampleRate} Hz "
                + $"({samples.Length / (double)CaptureSampleRate:F2}s real-time), "
                + $"outputSamples={outputSamples.Length} @ {SampleRate} Hz "
                + $"({outputSamples.Length / (double)SampleRate:F2}s tagged)."
            );
            return FloatSamplesToWav(outputSamples, SampleRate);
        }
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

    /// <summary>
    ///     Re-resolve the current OS default input device and, in follow-default
    ///     mode, migrate the capture to it when it has changed. IN-FLIGHT-SAFE: the
    ///     live capture stream is NEVER torn down while a recording is in progress —
    ///     the migration is deferred (mirroring upstream's
    ///     <c>_preferredDeviceMigrationPending</c>) and re-applied from
    ///     <see cref="StopRecording" /> once the buffer has been finalized.
    /// </summary>
    /// <remarks>
    ///     Migration decisions are made on the stable <c>PersistentId</c>
    ///     ("name|channels"), not the PortAudio index, because PipeWire/PulseAudio
    ///     reorder and re-index devices freely; only the name-derived id is stable
    ///     across a reconnect.
    ///     <para>
    ///         This is the unit-testable decision entry point. At runtime it is TRIGGERED
    ///         by the injected <see cref="IDefaultDeviceChangeWatcher" /> (a debounced
    ///         `pactl subscribe` change on the server/source), which calls it from a
    ///         background thread. It degrades safely when no watcher is present (or pactl
    ///         is absent): nothing calls it, so behavior falls back to lazy re-resolve.
    ///     </para>
    ///     <para>
    ///         THREAD-SAFETY: the whole refresh→resolve→migrate sequence runs under
    ///         <c>_streamLock</c> so it can never race a UI-thread start/stop that is
    ///         opening or disposing the native stream, and it bails (deferring) while a
    ///         recording is in flight so a live capture is never interrupted.
    ///     </para>
    /// </remarks>
    public void CheckForDefaultDeviceChange()
    {
        if (Volatile.Read(ref _disposed) == 1 || !FollowSystemDefault)
        {
            return;
        }

        // Hold _streamLock across the whole refresh→resolve→migrate sequence so a
        // concurrent UI-thread StartRecording/StartPreview/StopRecording cannot open or
        // dispose the native stream while this watcher-thread callback re-inits PortAudio
        // and swaps the device. The lock is released well before any recording runs (the
        // deferral path below bails while IsRecording), so a live recording is never
        // blocked or interrupted. Re-check the guards under the lock in case the state
        // changed between the outer early-out and acquiring it.
        lock (_streamLock)
        {
            if (Volatile.Read(ref _disposed) == 1 || !FollowSystemDefault)
            {
                return;
            }

            // Refresh PortAudio's cached device table so the enumerator reports the NEW
            // default. No-ops (returns false) while a stream is live / recording (checked
            // under the lock) — in which case the table below is STALE and still reports
            // the OLD default. We must not treat that stale reading as authoritative when
            // deciding to clear a pending migration.
            var deviceTableFresh = RefreshPortAudioDeviceTable();

            AudioInputDevice? preferred;
            try
            {
                preferred = ResolveSystemDefault(_deviceEnumerator.GetDevices());
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
                // No devices right now (all unplugged); keep whatever we have and retry
                // on the next check rather than tearing down a possibly-live stream.
                return;
            }

            lock (_migrationLock)
            {
                // Already on the preferred device — nothing to migrate.
                if (
                    string.Equals(_activeDeviceId, preferred.PersistentId, StringComparison.Ordinal)
                )
                {
                    // Only clear a pending defer when this reading is TRUSTWORTHY. If the
                    // device-table refresh was skipped (a stream is live / recording), the
                    // enumerator still reports the OLD default, which of course equals the
                    // device we are recording on — so "already on default" here is an
                    // artifact of the stale table, NOT proof the pending migration is moot.
                    // Clearing it would silently drop a real deferred migration that
                    // StopRecording is relying on replaying. Leave it set; the next check
                    // (after recording stops, table refreshable) re-resolves the true
                    // default and completes or clears the migration correctly.
                    if (deviceTableFresh)
                    {
                        _preferredDeviceMigrationPending = false;
                    }

                    return;
                }

                // Never tear down an in-flight recording to migrate. Defer and let
                // StopRecording re-invoke this method once the buffer is finalized.
                // Checked under _streamLock so it can't race a StartRecording that is
                // mid-transition (about to flip _isRecording / assign _stream).
                if (IsRecording)
                {
                    _preferredDeviceMigrationPending = true;
                    Trace.WriteLine(
                        "[AudioRecordingService] Default device changed while recording; migration deferred."
                    );
                    return;
                }

                _preferredDeviceMigrationPending = false;
            }

            MigrateActiveCaptureToDevice(preferred);
        }
    }

    // Swap the live capture stream (preview only; recording is guaranteed stopped by
    // the caller) to the preferred device. Safe to no-op if no stream is open — the
    // next EnsureInputStreamStarted picks up the new default via ResolveSelectedDeviceIndex.
    private void MigrateActiveCaptureToDevice(AudioInputDevice preferred)
    {
        var wasPreviewing = IsPreviewing;
        SelectedDeviceIndex = preferred.Index;
        _activeDeviceId = preferred.PersistentId;

        if (_stream is null)
        {
            // No open stream (idle): the new default is applied lazily on the next
            // StartRecording/StartPreview, so there is nothing to do now.
            return;
        }

        // Tear down and reopen only when NOT recording (caller guarantees this).
        StopAndDisposeInputStream();

        if (!wasPreviewing)
        {
            return;
        }

        try
        {
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

    // Refresh PortAudio's device table (which is snapshotted at Pa_Initialize and
    // does NOT observe OS default changes until re-initialized) by cycling
    // Terminate()+Initialize() under the global init lock. CRITICAL: this is a
    // no-op while a stream is live — re-initializing PortAudio would invalidate the
    // native stream handle and could crash the realtime callback. Callers must have
    // already ensured no recording is in flight; here we additionally bail if any
    // stream (e.g. a preview) is still open, deferring the refresh implicitly.
    //
    // Returns TRUE when the caller can trust the device table to reflect the current OS
    // default afterward — i.e. the table was cycled, OR PortAudio was not yet initialized
    // (in which case the next enumeration initializes it and reads a fresh table). Returns
    // FALSE ONLY when the refresh was SKIPPED because a stream is live / recording: the
    // table is then STALE, so callers (see CheckForDefaultDeviceChange) must not treat a
    // "still on the same default" reading as authoritative and must not clear a pending
    // migration off it.
    private bool RefreshPortAudioDeviceTable()
    {
        // Never re-init the native library out from under a live stream. Skipped =>
        // table is stale.
        if (_stream is not null || IsRecording)
        {
            return false;
        }

        lock (s_paInitLock)
        {
            // Re-check under the lock: a concurrent StartRecording/StartPreview may
            // have opened a stream between the outer check and acquiring the lock.
            if (_stream is not null || IsRecording)
            {
                return false;
            }

            if (s_paInitCount <= 0)
            {
                // Not initialized yet; the next EnsurePortAudioInitialized (called by the
                // enumerator) will read a fresh table anyway, so there is nothing cached
                // to refresh and the resulting reading is NOT stale.
                return true;
            }

            try
            {
                PortAudio.Terminate();

                // From here PortAudio is terminated: s_paInitCount must NOT stay positive
                // unless a matching Initialize() succeeds, otherwise the next
                // EnsurePortAudioInitialized() would see a positive count and skip the
                // re-init, leaving the library terminated-but-"initialized" (unusable).
                s_paInitCount = 0;

                PortAudio.Initialize();
                s_paInitCount = 1;
            }
            catch (Exception ex)
            {
                // Best-effort refresh: if the cycle fails, fall back to the stale table
                // and try to restore a usable init state. s_paInitCount now reflects
                // actual PortAudio state: 0 if Terminate() ran but Initialize() has not
                // yet succeeded (so a later EnsurePortAudioInitialized() recovers), or
                // still 1 if Terminate() itself threw before changing state.
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
                    // Leave s_paInitCount as set above (0 after a successful Terminate);
                    // a later EnsurePortAudioInitialized() then retries the init.
                }
            }
        }

        // Reached only when the refresh was attempted (not skipped for a live stream):
        // Terminate()+Initialize() cycled the table, so the reading is fresh. Even on the
        // best-effort failure path the library was re-initialized against the current OS
        // state, so the table is not stale in the sense that matters for migration.
        return true;
    }

    // ======================= RUNTIME DEFAULT-DEVICE WATCHER =======================
    // The reactive trigger for CheckForDefaultDeviceChange is now wired: the injected
    // IDefaultDeviceChangeWatcher (production: PactlDefaultDeviceWatcher, running
    // `pactl subscribe`) detects OS default capture-device changes at runtime,
    // debounces a burst into one re-resolve, and calls CheckForDefaultDeviceChange
    // from a background thread (never the PortAudio realtime thread).
    //
    //   - The watcher is started only while FollowSystemDefault is active and stopped
    //     when it is turned off (see the FollowSystemDefault setter / this method), so
    //     no child process runs when the user has pinned a specific microphone.
    //   - GRACEFUL FALLBACK: if pactl is unavailable (or _deviceWatcher is null in a
    //     test), the watcher never starts and behavior degrades to the existing lazy
    //     re-resolve at the next StartRecording/StartPreview. Starting is best-effort
    //     and never throws.
    //   - The watcher NEVER tears down a live _stream: it only calls
    //     CheckForDefaultDeviceChange, which already defers migration while recording
    //     (and RefreshPortAudioDeviceTable no-ops while a stream is open), so the
    //     mid-recording safety is preserved.
    //
    // Start/stop is idempotent via _watcherStarted so repeated FollowSystemDefault
    // assignments (App bootstrap + ViewModel selection) don't spawn duplicate processes.
    private void StartOrStopDeviceWatcher()
    {
        if (_deviceWatcher is null || Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        if (_followSystemDefault)
        {
            // Idempotent start: only the first transition into follow mode launches it.
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
                // Never let a watcher launch failure break follow-default mode; the
                // lazy re-resolve path still applies.
                Volatile.Write(ref _watcherStarted, 0);
                Trace.WriteLine(
                    $"[AudioRecordingService] Default-device watcher start failed: {ex.Message}"
                );
            }
        }
        else
        {
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
    }
    // =============================================================================

    // ---- Test seams -------------------------------------------------------
    // The migration state machine (CheckForDefaultDeviceChange) is unit-tested
    // through the injected IAudioDeviceEnumerator with NO real PortAudio stream:
    // _stream stays null (so MigrateActiveCaptureToDevice only updates the target
    // index/id) and RefreshPortAudioDeviceTable no-ops (PortAudio uninitialized).
    // These seams let a test seed the "currently active" device and toggle the
    // in-flight-recording flag without opening a native stream.

    internal string? ActiveDeviceIdForTest => _activeDeviceId;

    internal bool MigrationPendingForTest
    {
        get
        {
            lock (_migrationLock)
            {
                return _preferredDeviceMigrationPending;
            }
        }
    }

    // Seed the device the (notional) capture is currently bound to, as if a
    // stream had been opened on it. Test-only.
    internal void SetActiveDeviceIdForTest(string? deviceId, int? deviceIndex)
    {
        _activeDeviceId = deviceId;
        SelectedDeviceIndex = deviceIndex;
    }

    // Simulate an in-flight recording without a native stream, so the deferral
    // path in CheckForDefaultDeviceChange can be exercised. Test-only.
    internal void SetRecordingForTest(bool recording)
    {
        Volatile.Write(ref _isRecording, recording ? 1 : 0);
    }

    // Seed the deferred-migration flag directly, so the "pending survives a stale/
    // skipped device-table refresh" path in CheckForDefaultDeviceChange can be
    // exercised without reconstructing a full defer→stale-recheck sequence. Test-only.
    internal void SetMigrationPendingForTest(bool pending)
    {
        lock (_migrationLock)
        {
            _preferredDeviceMigrationPending = pending;
        }
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
///     Device-enumeration seam for <see cref="AudioRecordingService" />. Abstracts
///     the PortAudio device table so the follow-default selection policy and the
///     migration-deferral state machine can be unit-tested without real hardware.
///     <see cref="GetDevices" /> must report which device is the current OS default
///     (via <see cref="AudioInputDevice.IsDefault" />).
/// </summary>
public interface IAudioDeviceEnumerator
{
    IReadOnlyList<AudioInputDevice> GetDevices();
}

/// <summary>
///     Production enumerator backed by PortAudio's cached device table.
///     <para>
///         IMPORTANT: PortAudio snapshots the device list at <c>Pa_Initialize</c> and
///         does NOT observe OS default changes until it is re-initialized. This
///         enumerator therefore only reflects a changed default AFTER
///         <see cref="AudioRecordingService" /> has cycled the native library
///         (see <c>RefreshPortAudioDeviceTable</c>) — which it does at the start of
///         <see cref="AudioRecordingService.CheckForDefaultDeviceChange" /> and only
///         when no stream is live.
///     </para>
/// </summary>
public sealed class PortAudioDeviceEnumerator : IAudioDeviceEnumerator
{
    public static PortAudioDeviceEnumerator Shared { get; } = new();

    public IReadOnlyList<AudioInputDevice> GetDevices()
    {
        AudioRecordingService.EnsurePortAudioInitializedForEnumerator();
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
                            AudioRecordingService.GetStableDeviceId(info.name, info.maxInputChannels)
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
}