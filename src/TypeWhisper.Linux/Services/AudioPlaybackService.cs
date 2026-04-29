using System.Diagnostics;
using System.Runtime.InteropServices;
using PortAudioSharp;
using TypeWhisper.Core;
using PaStream = PortAudioSharp.Stream;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// Minimal playback for session-scoped mono PCM16 WAV dictation captures.
/// </summary>
public sealed class AudioPlaybackService : IDisposable
{
    private const int Channels = 1;
    private const uint FramesPerBuffer = 512;

    private static int _paInitCount;
    private static readonly object _paInitLock = new();

    private readonly object _gate = new();
    private PaStream? _stream;
    private float[] _samples = [];
    private int _position;

    public string? CurrentFile { get; private set; }
    public bool IsPlaying { get; private set; }

    public event Action? PlaybackStateChanged;

    public AudioPlaybackService()
    {
        EnsurePortAudioInitialized();
    }

    public bool CanPlay(string? audioFileName) =>
        ResolveAudioPath(audioFileName) is { } path && File.Exists(path);

    public void Play(string audioFileName)
    {
        if (ResolveAudioPath(audioFileName) is not { } path || !File.Exists(path))
            return;

        lock (_gate)
        {
            if (IsPlaying && string.Equals(CurrentFile, audioFileName, StringComparison.OrdinalIgnoreCase))
            {
                StopCore();
                NotifyPlaybackChanged();
                return;
            }

            StopCore();

            try
            {
                var (samples, sampleRate) = LoadWav(path);
                _samples = samples;
                _position = 0;

                var deviceIndex = PortAudio.DefaultOutputDevice;
                if (deviceIndex == PortAudio.NoDevice)
                {
                    StopCore();
                    return;
                }

                var outputInfo = PortAudio.GetDeviceInfo(deviceIndex);
                var outputParams = new StreamParameters
                {
                    device = deviceIndex,
                    channelCount = Channels,
                    sampleFormat = SampleFormat.Float32,
                    suggestedLatency = outputInfo.defaultLowOutputLatency,
                    hostApiSpecificStreamInfo = IntPtr.Zero,
                };

                _stream = new PaStream(
                    inParams: null,
                    outParams: outputParams,
                    sampleRate: sampleRate,
                    framesPerBuffer: FramesPerBuffer,
                    streamFlags: StreamFlags.ClipOff,
                    callback: PlaybackCallback,
                    userData: IntPtr.Zero);

                _stream.Start();
                CurrentFile = audioFileName;
                IsPlaying = true;
                _ = MonitorPlaybackCompletionAsync();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[AudioPlaybackService] Play failed: {ex.Message}");
                StopCore();
                return;
            }
        }

        NotifyPlaybackChanged();
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
        }

        NotifyPlaybackChanged();
    }

    private StreamCallbackResult PlaybackCallback(
        IntPtr input,
        IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags,
        IntPtr userData)
    {
        if (output == IntPtr.Zero || frameCount == 0)
            return StreamCallbackResult.Continue;

        var requested = (int)frameCount;
        var buffer = new float[requested];
        var copied = 0;

        lock (_gate)
        {
            if (_position < _samples.Length)
            {
                copied = Math.Min(requested, _samples.Length - _position);
                Array.Copy(_samples, _position, buffer, 0, copied);
                _position += copied;
            }
        }

        Marshal.Copy(buffer, 0, output, requested);
        return copied < requested ? StreamCallbackResult.Complete : StreamCallbackResult.Continue;
    }

    private async Task MonitorPlaybackCompletionAsync()
    {
        while (true)
        {
            await Task.Delay(100).ConfigureAwait(false);

            bool done;
            lock (_gate)
            {
                done = _stream is null || !_stream.IsActive || _stream.IsStopped;
                if (!done)
                    continue;

                StopCore();
            }

            NotifyPlaybackChanged();
            return;
        }
    }

    private void StopCore()
    {
        try { _stream?.Stop(); } catch { /* best effort */ }
        _stream?.Dispose();
        _stream = null;
        _samples = [];
        _position = 0;
        CurrentFile = null;
        IsPlaying = false;
    }

    // Resolve audioFileName relative to the audio root, rejecting anything that escapes via "..".
    private static string? ResolveAudioPath(string? audioFileName)
    {
        if (string.IsNullOrWhiteSpace(audioFileName))
            return null;

        string root;
        string candidate;
        try
        {
            root = Path.GetFullPath(TypeWhisperEnvironment.AudioPath);
            candidate = Path.GetFullPath(Path.Combine(root, audioFileName));
        }
        catch
        {
            return null;
        }

        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (candidate.Equals(root, StringComparison.Ordinal)
            || candidate.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            return candidate;
        }

        return null;
    }

    private static (float[] Samples, int SampleRate) LoadWav(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        var riff = new string(reader.ReadChars(4));
        _ = reader.ReadInt32();
        var wave = new string(reader.ReadChars(4));
        if (riff != "RIFF" || wave != "WAVE")
            throw new InvalidDataException("Unsupported WAV container.");

        short audioFormat = 0;
        short channels = 0;
        int sampleRate = 0;
        short bitsPerSample = 0;
        byte[]? data = null;

        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadInt32();

            if (chunkId == "fmt ")
            {
                audioFormat = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                _ = reader.ReadInt32();
                _ = reader.ReadInt16();
                bitsPerSample = reader.ReadInt16();

                var remaining = chunkSize - 16;
                if (remaining > 0)
                    reader.ReadBytes(remaining);
            }
            else if (chunkId == "data")
            {
                data = reader.ReadBytes(chunkSize);
            }
            else
            {
                reader.BaseStream.Seek(chunkSize, SeekOrigin.Current);
            }

            if ((chunkSize & 1) == 1 && reader.BaseStream.Position < reader.BaseStream.Length)
                reader.BaseStream.Seek(1, SeekOrigin.Current);
        }

        if (audioFormat != 1 || channels != 1 || bitsPerSample != 16 || data is null)
            throw new InvalidDataException("Only mono PCM16 WAV playback is supported.");

        var samples = new float[data.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = BitConverter.ToInt16(data, i * 2);
            samples[i] = sample / 32768f;
        }

        return (samples, sampleRate);
    }

    private void NotifyPlaybackChanged()
    {
        try { PlaybackStateChanged?.Invoke(); } catch { /* ignore */ }
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

    private static void EnsurePortAudioTerminated()
    {
        lock (_paInitLock)
        {
            if (_paInitCount <= 0)
                return;

            if (--_paInitCount == 0)
                PortAudio.Terminate();
        }
    }

    public void Dispose()
    {
        Stop();
        EnsurePortAudioTerminated();
    }
}
