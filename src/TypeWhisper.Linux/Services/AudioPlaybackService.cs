using PortAudioSharp;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TypeWhisper.Core;
using PaStream = PortAudioSharp.Stream;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Minimal playback for session-scoped mono PCM16 WAV dictation captures.
/// </summary>
public sealed class AudioPlaybackService : IDisposable
{
    private const int Channels = 1;
    private const uint FramesPerBuffer = 512;

    private static int s_paInitCount;
    private static readonly Lock s_paInitLock = new();

    private readonly Lock _gate = new();
    private int _position;
    private float[] _samples = [];
    private PaStream? _stream;

    public AudioPlaybackService()
    {
        EnsurePortAudioInitialized();
    }

    public string? CurrentFile { get; private set; }
    public bool IsPlaying { get; private set; }

    public void Dispose()
    {
        Stop();
        EnsurePortAudioTerminated();
    }

    // ReSharper disable once UnusedMember.Global — public API (pre-flight playback check); not currently called in-tree.
    public static bool CanPlay(string? audioFileName)
    {
        return ResolveAudioPath(audioFileName) is { } path && File.Exists(path);
    }

    public void Play(string audioFileName)
    {
        if (ResolveAudioPath(audioFileName) is not { } path || !File.Exists(path))
        {
            return;
        }

        bool toggleOff;
        PaStream? previous;
        lock (_gate)
        {
            toggleOff =
                IsPlaying
                && string.Equals(CurrentFile, audioFileName, StringComparison.OrdinalIgnoreCase);
            previous = DetachStreamLocked();
        }

        // Tear down outside _gate: Pa_StopStream blocks on the callback thread,
        // which also takes _gate — stopping under the lock deadlocks the UI.
        DisposeStream(previous);

        if (toggleOff)
        {
            NotifyPlaybackChanged();
            return;
        }

        PaStream? failed = null;
        lock (_gate)
        {
            try
            {
                var (samples, sampleRate) = LoadWav(path);
                _samples = samples;
                _position = 0;

                var deviceIndex = PortAudio.DefaultOutputDevice;
                if (deviceIndex == PortAudio.NoDevice)
                {
                    DetachStreamLocked();
                }
                else
                {
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
                        null,
                        outputParams,
                        sampleRate,
                        FramesPerBuffer,
                        StreamFlags.ClipOff,
                        PlaybackCallback,
                        IntPtr.Zero
                    );

                    _stream.Start();
                    CurrentFile = audioFileName;
                    IsPlaying = true;
                    _ = MonitorPlaybackCompletionAsync();
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[AudioPlaybackService] Play failed: {ex.Message}");
                failed = DetachStreamLocked();
            }
        }

        DisposeStream(failed);
        NotifyPlaybackChanged();
    }

    public void Stop()
    {
        PaStream? stream;
        lock (_gate)
        {
            stream = DetachStreamLocked();
        }

        DisposeStream(stream);
        NotifyPlaybackChanged();
    }

    public event Action? PlaybackStateChanged;

    private StreamCallbackResult PlaybackCallback(
        IntPtr input,
        IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags,
        IntPtr userData
    )
    {
        if (output == IntPtr.Zero || frameCount == 0)
        {
            return StreamCallbackResult.Continue;
        }

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

            PaStream? finished;
            lock (_gate)
            {
                if (_stream is null)
                {
                    return; // Already stopped from Play()/Stop().
                }

                if (_stream.IsActive && !_stream.IsStopped)
                {
                    continue;
                }

                finished = DetachStreamLocked();
            }

            DisposeStream(finished);
            NotifyPlaybackChanged();
            return;
        }
    }

    // Clears state and returns the stream so the caller can dispose it outside _gate.
    // Must be called while holding _gate.
    private PaStream? DetachStreamLocked()
    {
        var stream = _stream;
        _stream = null;
        _samples = [];
        _position = 0;
        CurrentFile = null;
        IsPlaying = false;
        return stream;
    }

    // Pa_StopStream blocks on the audio callback thread — never call while holding _gate.
    private static void DisposeStream(PaStream? stream)
    {
        if (stream is null)
        {
            return;
        }

        try
        {
            stream.Stop();
        }
        catch
        {
            /* best effort */
        }

        try
        {
            stream.Dispose();
        }
        catch
        {
            /* best effort */
        }
    }

    // Resolve relative to the audio root, rejecting ".." escapes as defence-in-depth
    // (paths come from the history service and should never leave the audio directory).
    private static string? ResolveAudioPath(string? audioFileName)
    {
        if (string.IsNullOrWhiteSpace(audioFileName))
        {
            return null;
        }

        string root;
        string candidate;
        try
        {
            root = Path.GetFullPath(TypeWhisperEnvironment.AudioPath);
            candidate = Path.GetFullPath(Path.Join(root, audioFileName));
        }
        catch
        {
            return null;
        }

        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (
            candidate.Equals(root, StringComparison.Ordinal)
            || candidate.StartsWith(rootWithSep, StringComparison.Ordinal)
        )
        {
            return candidate;
        }

        return null;
    }

    private static (float[] Samples, int SampleRate) LoadWav(string path)
    {
        // Minimal chunk-walker: skips unknown chunks (LIST, JUNK, etc.) for broad
        // WAV compatibility, then enforces PCM mono 16-bit (all PortAudio supports here).
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        var riff = new string(reader.ReadChars(4));
        _ = reader.ReadInt32();
        var wave = new string(reader.ReadChars(4));
        if (riff != "RIFF" || wave != "WAVE")
        {
            throw new InvalidDataException("Unsupported WAV container.");
        }

        short audioFormat = 0;
        short channels = 0;
        var sampleRate = 0;
        short bitsPerSample = 0;
        byte[]? data = null;

        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadInt32();

            switch (chunkId)
            {
                case "fmt ":
                    audioFormat = reader.ReadInt16();
                    channels = reader.ReadInt16();
                    sampleRate = reader.ReadInt32();
                    _ = reader.ReadInt32();
                    _ = reader.ReadInt16();
                    bitsPerSample = reader.ReadInt16();

                    var remaining = chunkSize - 16;
                    if (remaining > 0)
                    {
                        reader.ReadBytes(remaining);
                    }

                    break;
                case "data":
                    data = reader.ReadBytes(chunkSize);
                    break;
                default:
                    reader.BaseStream.Seek(chunkSize, SeekOrigin.Current);
                    break;
            }

            // WAV spec: chunks are padded to even boundaries; skip the pad byte if odd.
            if ((chunkSize & 1) == 1 && reader.BaseStream.Position < reader.BaseStream.Length)
            {
                reader.BaseStream.Seek(1, SeekOrigin.Current);
            }
        }

        if (audioFormat != 1 || channels != 1 || bitsPerSample != 16 || data is null)
        {
            throw new InvalidDataException("Only mono PCM16 WAV playback is supported.");
        }

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
        try
        {
            PlaybackStateChanged?.Invoke();
        }
        catch
        {
            /* ignore */
        }
    }

    private static void EnsurePortAudioInitialized()
    {
        // Reference-counted: both services share the process-global PortAudio library
        // and each Initialize() must be balanced by a Terminate().
        lock (s_paInitLock)
        {
            if (s_paInitCount == 0)
            {
                PortAudio.Initialize();
            }

            s_paInitCount++;
        }
    }

    private static void EnsurePortAudioTerminated()
    {
        lock (s_paInitLock)
        {
            if (s_paInitCount <= 0)
            {
                return;
            }

            if (--s_paInitCount == 0)
            {
                PortAudio.Terminate();
            }
        }
    }
}