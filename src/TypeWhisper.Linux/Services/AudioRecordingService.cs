using System.Diagnostics;
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
    public const float SpeechEnergyThreshold = 0.01f;

    private static int _paInitCount;
    private static readonly object _paInitLock = new();

    private readonly List<float> _samples = [];
    private readonly object _sampleLock = new();
    private PaStream? _stream;
    private bool _isRecording;
    private bool _isPreviewing;
    private int? _selectedDeviceIndex;
    private float _currentRmsLevel;
    private bool _disposed;

    public bool IsRecording => _isRecording;
    public bool IsPreviewing => _isPreviewing;
    public float CurrentRmsLevel => _currentRmsLevel;
    public bool HasSpeechEnergy => _currentRmsLevel >= SpeechEnergyThreshold;
    public int? SelectedDeviceIndex
    {
        get => _selectedDeviceIndex;
        set
        {
            if (_selectedDeviceIndex == value)
                return;

            _selectedDeviceIndex = value;
        }
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
        if (_isRecording || _disposed) return;

        lock (_sampleLock) { _samples.Clear(); }

        if (!EnsureInputStreamStarted())
            return;

        _isRecording = true;
    }

    public byte[] StopRecording()
    {
        if (!_isRecording) return [];
        _isRecording = false;

        if (!_isPreviewing)
            StopAndDisposeInputStream();

        float[] floats;
        lock (_sampleLock) { floats = [.. _samples]; }

        return FloatSamplesToWav(floats, SampleRate);
    }

    public byte[]? GetCurrentBuffer()
    {
        if (!_isRecording)
            return null;

        float[] floats;
        lock (_sampleLock)
        {
            if (_samples.Count == 0)
                return null;

            floats = [.. _samples];
        }

        return FloatSamplesToWav(floats, SampleRate);
    }

    public bool StartPreview()
    {
        if (_disposed || _isRecording || _isPreviewing)
            return false;

        try
        {
            if (!EnsureInputStreamStarted())
                return false;

            _isPreviewing = true;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioRecordingService] Failed to start preview: {ex.Message}");
            _isPreviewing = false;
            if (!_isRecording)
                StopAndDisposeInputStream();
            return false;
        }
    }

    public void StopPreview()
    {
        if (!_isPreviewing)
            return;

        _isPreviewing = false;
        if (!_isRecording)
            StopAndDisposeInputStream();
        UpdateLevel(0f);
    }

    private StreamCallbackResult InputAudioCallback(
        IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags, IntPtr userData)
        => ProcessAudioBuffer(input, frameCount, copySamples: _isRecording);

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
            lock (_sampleLock) { _samples.AddRange(processedBuffer); }
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
        _currentRmsLevel = level;
        try { LevelChanged?.Invoke(this, _currentRmsLevel); } catch { /* ignore */ }
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
            Debug.WriteLine("[AudioRecordingService] No default input device.");
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

    private static PaStream CreateInputStream(int deviceIndex, PortAudioSharp.Stream.Callback callback)
    {
        var inputInfo = PortAudio.GetDeviceInfo(deviceIndex);
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
            sampleRate: SampleRate,
            framesPerBuffer: FramesPerBuffer,
            streamFlags: StreamFlags.ClipOff,
            callback: callback,
            userData: IntPtr.Zero);
    }

    private static byte[] FloatSamplesToWav(float[] samples, int sampleRate)
    {
        const short bitsPerSample = 16;
        const short channels = 1;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = channels * bitsPerSample / 8;
        var dataSize = samples.Length * 2;

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

        foreach (var s in samples)
        {
            var clamped = Math.Max(-1f, Math.Min(1f, s));
            w.Write((short)(clamped * short.MaxValue));
        }

        return ms.ToArray();
    }

    private static void EnsurePortAudioInitialized()
    {
        lock (_paInitLock)
        {
            if (_paInitCount == 0)
                PortAudio.Initialize();
            _paInitCount++;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isPreviewing = false;
        _isRecording = false;
        StopAndDisposeInputStream();

        lock (_paInitLock)
        {
            _paInitCount--;
            if (_paInitCount == 0)
                try { PortAudio.Terminate(); } catch { }
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
