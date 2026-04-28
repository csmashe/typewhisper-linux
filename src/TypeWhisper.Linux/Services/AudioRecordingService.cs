using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;
using PortAudioSharp;
using PaStream = PortAudioSharp.Stream;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// Minimal Linux audio capture via PortAudioSharp2. Records 16kHz mono PCM16
/// suitable for whisper.cpp / SherpaOnnx input. The richer feature set of
/// the Windows AudioRecordingService (AGC, VAD, preview streams, live level
/// meter, device polling) is deferred — this is only enough to drive the
/// voice→text→paste pipeline on Linux.
/// </summary>
public sealed class AudioRecordingService : IDisposable
{
    private const int SampleRate = 16000;
    private const int Channels = 1;
    private const uint FramesPerBuffer = 512;
    private const float AgcTargetRms = 0.1f;
    private const float AgcMaxGain = 20f;
    private const float AgcMinGain = 1f;
    private static readonly TimeSpan StopDrainDuration = TimeSpan.FromMilliseconds(120);
    public const float SpeechEnergyThreshold = 0.01f;

    private static int _paInitCount;
    private static readonly object _paInitLock = new();

    private readonly List<float[]> _sampleChunks = [];
    private readonly object _sampleLock = new();
    private PaStream? _stream;
    private int _sampleCount;
    private int _captureSampleRate = SampleRate;
    private int _isRecording;
    private int _isPreviewing;
    private int? _selectedDeviceIndex;
    private float _currentRmsLevel;
    private long _lastLevelPostedTicksUtc;
    private int _disposed;

    public bool IsRecording => Volatile.Read(ref _isRecording) == 1;
    public bool IsPreviewing => Volatile.Read(ref _isPreviewing) == 1;
    public float CurrentRmsLevel => Volatile.Read(ref _currentRmsLevel);
    public bool HasSpeechEnergy => CurrentRmsLevel >= SpeechEnergyThreshold;
    public int? SelectedDeviceIndex
    {
        get => _selectedDeviceIndex;
        set => _selectedDeviceIndex = value;
    }
    public bool WhisperModeEnabled { get; set; }

    public event EventHandler<float>? LevelChanged;

    public IReadOnlyList<AudioInputDevice> GetInputDevices()
    {
        EnsurePortAudioInitialized();
        var result = new List<AudioInputDevice>();
        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            try
            {
                var info = PortAudio.GetDeviceInfo(i);
                if (info.maxInputChannels > 0)
                {
                    result.Add(new AudioInputDevice(
                        i,
                        info.name,
                        info.maxInputChannels,
                        i == PortAudio.DefaultInputDevice,
                        GetStableDeviceId(info.name, info.maxInputChannels)));
                }
            }
            catch { /* ignore broken devices */ }
        }
        return result;
    }

    public AudioRecordingService()
    {
        EnsurePortAudioInitialized();
    }

    public void StartRecording()
    {
        if (IsRecording || Volatile.Read(ref _disposed) == 1) return;

        lock (_sampleLock)
        {
            _sampleChunks.Clear();
            _sampleCount = 0;
            _captureSampleRate = SampleRate;
        }

        if (!EnsureInputStreamStarted())
            return;

        Volatile.Write(ref _isRecording, 1);
    }

    public byte[] StopRecording()
    {
        if (!IsRecording) return [];
        Volatile.Write(ref _isRecording, 0);

        if (!IsPreviewing)
            StopAndDisposeInputStream();

        return BuildWavFromRecordedAudio();
    }

    public async Task<byte[]> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRecording) return [];

        try
        {
            await Task.Delay(StopDrainDuration, cancellationToken);
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
            return null;

        lock (_sampleLock)
        {
            if (_sampleCount == 0)
                return null;
        }

        return BuildWavFromRecordedAudio();
    }

    public bool StartPreview()
    {
        if (Volatile.Read(ref _disposed) == 1 || IsRecording || IsPreviewing)
            return false;

        try
        {
            if (!EnsureInputStreamStarted())
                return false;

            Volatile.Write(ref _isPreviewing, 1);
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AudioRecordingService] Failed to start preview: {ex.Message}");
            Volatile.Write(ref _isPreviewing, 0);
            if (!IsRecording)
                StopAndDisposeInputStream();
            return false;
        }
    }

    public void StopPreview()
    {
        if (!IsPreviewing)
            return;

        Volatile.Write(ref _isPreviewing, 0);
        if (!IsRecording)
            StopAndDisposeInputStream();
        UpdateLevel(0f);
    }

    private StreamCallbackResult InputAudioCallback(
        IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags, IntPtr userData)
        => ProcessAudioBuffer(input, frameCount, copySamples: IsRecording);

    private StreamCallbackResult ProcessAudioBuffer(
        IntPtr input,
        uint frameCount,
        bool copySamples)
    {
        if (input == IntPtr.Zero || frameCount == 0)
            return StreamCallbackResult.Continue;

        var buffer = new float[frameCount];
        System.Runtime.InteropServices.Marshal.Copy(input, buffer, 0, (int)frameCount);

        var processedBuffer = ApplyWhisperModeGain(buffer, copySamples && WhisperModeEnabled);
        UpdateLevel(ComputeRmsLevel(processedBuffer));

        if (copySamples)
        {
            lock (_sampleLock)
            {
                _sampleChunks.Add(processedBuffer);
                _sampleCount += processedBuffer.Length;
            }
        }

        return StreamCallbackResult.Continue;
    }

    internal static float[] ApplyWhisperModeGain(float[] samples, bool whisperModeEnabled)
    {
        if (!whisperModeEnabled || samples.Length == 0)
            return samples;

        var rms = ComputeRmsLevel(samples);
        if (rms <= 0.0001f)
            return samples;

        var gain = Math.Clamp(AgcTargetRms / rms, AgcMinGain, AgcMaxGain);
        if (gain <= 1f)
            return samples;

        var adjusted = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
            adjusted[i] = Math.Clamp(samples[i] * gain, -1f, 1f);

        return adjusted;
    }

    internal static float ComputeRmsLevel(float[] samples)
    {
        if (samples.Length == 0)
            return 0f;

        double sumSquares = 0;
        for (var i = 0; i < samples.Length; i++)
            sumSquares += samples[i] * samples[i];

        return (float)Math.Sqrt(sumSquares / samples.Length);
    }

    private void UpdateLevel(float level)
    {
        Volatile.Write(ref _currentRmsLevel, level);

        var nowTicks = DateTime.UtcNow.Ticks;
        if (level > 0f && !ShouldPostLevelUpdate(nowTicks))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            try { LevelChanged?.Invoke(this, level); } catch { /* ignore */ }
        });
    }

    private bool ShouldPostLevelUpdate(long nowTicks)
    {
        var minIntervalTicks = TimeSpan.FromMilliseconds(66).Ticks;

        while (true)
        {
            var lastTicks = Interlocked.Read(ref _lastLevelPostedTicksUtc);
            if (nowTicks - lastTicks < minIntervalTicks)
                return false;

            if (Interlocked.CompareExchange(ref _lastLevelPostedTicksUtc, nowTicks, lastTicks) == lastTicks)
                return true;
        }
    }

    public AudioInputDevice? ResolveConfiguredDevice(int? preferredIndex, string? preferredDeviceId)
    {
        var devices = GetInputDevices();

        if (!string.IsNullOrWhiteSpace(preferredDeviceId))
        {
            var byId = devices.FirstOrDefault(d => d.PersistentId == preferredDeviceId);
            if (byId is not null)
                return byId;
        }

        if (preferredIndex.HasValue)
        {
            var byIndex = devices.FirstOrDefault(d => d.Index == preferredIndex.Value);
            if (byIndex is not null)
                return byIndex;
        }

        return devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault();
    }

    private int? ResolveSelectedDeviceIndex()
    {
        var deviceIndex = SelectedDeviceIndex ?? PortAudio.DefaultInputDevice;
        if (deviceIndex == PortAudio.NoDevice)
        {
            Trace.WriteLine("[AudioRecordingService] No default input device.");
            return null;
        }

        return deviceIndex;
    }

    private bool EnsureInputStreamStarted()
    {
        if (_stream is not null)
            return true;

        var deviceIndex = ResolveSelectedDeviceIndex();
        if (deviceIndex is null)
            return false;

        _stream = CreateInputStream(deviceIndex.Value, InputAudioCallback);
        _stream.Start();
        return true;
    }

    private void StopAndDisposeInputStream()
    {
        try { _stream?.Stop(); } catch { /* best effort */ }
        _stream?.Dispose();
        _stream = null;
    }

    private static string GetStableDeviceId(string deviceName, int maxInputChannels) =>
        $"{deviceName}|{maxInputChannels}";

    private PaStream CreateInputStream(int deviceIndex, PortAudioSharp.Stream.Callback callback)
    {
        var inputInfo = PortAudio.GetDeviceInfo(deviceIndex);
        var candidateRates = CandidateSampleRates(inputInfo.defaultSampleRate);
        Exception? lastError = null;

        foreach (var sampleRate in candidateRates)
        {
            try
            {
                var stream = CreateInputStream(deviceIndex, inputInfo, sampleRate, callback);
                _captureSampleRate = sampleRate;
                if (sampleRate != SampleRate)
                    Trace.WriteLine($"[AudioRecordingService] Capturing at {sampleRate} Hz and resampling to {SampleRate} Hz.");
                return stream;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Trace.WriteLine($"[AudioRecordingService] Failed to open input stream at {sampleRate} Hz: {ex.Message}");
            }
        }

        throw lastError ?? new InvalidOperationException("No compatible input sample rate was accepted by PortAudio.");
    }

    private static PaStream CreateInputStream(
        int deviceIndex,
        dynamic inputInfo,
        int sampleRate,
        PortAudioSharp.Stream.Callback callback)
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
            inParams: inputParams,
            outParams: null,
            sampleRate: sampleRate,
            framesPerBuffer: FramesPerBuffer,
            streamFlags: StreamFlags.ClipOff,
            callback: callback,
            userData: IntPtr.Zero);
    }

    private static IReadOnlyList<int> CandidateSampleRates(double defaultSampleRate)
    {
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
                rates.Add(rate);
        }
    }

    private static byte[] FloatSamplesToWav(float[] samples, int sampleRate)
    {
        return WriteWav(sampleRate, samples.Length, writer =>
        {
            foreach (var sample in samples)
                writer.Write(ToPcm16(sample));
        });
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

            var outputSamples = ResampleToSampleRate(samples, _captureSampleRate, SampleRate);
            return FloatSamplesToWav(outputSamples, SampleRate);
        }
    }

    internal static float[] ResampleToSampleRate(float[] samples, int sourceSampleRate, int targetSampleRate)
    {
        if (samples.Length == 0 || sourceSampleRate <= 0 || sourceSampleRate == targetSampleRate)
            return samples;

        var outputLength = Math.Max(1, (int)Math.Round(samples.Length * (double)targetSampleRate / sourceSampleRate));
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

    private static byte[] WriteWav(int sampleRate, int sampleCount, Action<BinaryWriter> writeSamples)
    {
        const short bitsPerSample = 16;
        const short channels = 1;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = channels * bitsPerSample / 8;
        var dataSize = sampleCount * 2;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + dataSize);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);               // fmt chunk size
        w.Write((short)1);         // PCM
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

    private static short ToPcm16(float sample)
    {
        var clamped = Math.Clamp(sample, -1f, 1f);
        return (short)(clamped * short.MaxValue);
    }

    // Idempotent: only initializes (and bumps the count to 1) on first call.
    // GetInputDevices calls this too; without idempotence we'd leak the count
    // every enumeration and Dispose would never terminate PortAudio.
    private static void EnsurePortAudioInitialized()
    {
        lock (_paInitLock)
        {
            if (_paInitCount == 0)
            {
                PortAudio.Initialize();
                _paInitCount = 1;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        Volatile.Write(ref _isPreviewing, 0);
        Volatile.Write(ref _isRecording, 0);
        StopAndDisposeInputStream();
        UpdateLevel(0f);

        lock (_paInitLock)
        {
            if (_paInitCount > 0)
            {
                _paInitCount = 0;
                try { PortAudio.Terminate(); } catch { }
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
    string PersistentId);
