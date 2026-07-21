using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.SupertonicTts;

/// <summary>
///     Plays the float PCM audio produced by the Supertonic ONNX synthesizer
///     through the platform audio player. The Windows
///     <c>System.Media.SoundPlayer</c> engine used upstream is replaced here
///     with the fork's process-based playback pattern (see
///     <c>LinuxSystemTtsProvider</c> / the xAI plugin): the samples are wrapped
///     in a WAV container, written to a temp file, and handed to <c>paplay</c>
///     or <c>aplay</c>.
/// </summary>
internal sealed class SupertonicTtsPlaybackSession : ITtsPlaybackSession, IDisposable
{
    private readonly Process _process;
    private readonly string _wavFilePath;
    private int _completed;

    private SupertonicTtsPlaybackSession(Process process, string wavFilePath)
    {
        _process = process;
        _wavFilePath = wavFilePath;
        _process.EnableRaisingEvents = true;
        _process.Exited += OnExited;

        if (_process.HasExited)
            Finish();
    }

    public bool IsActive => Volatile.Read(ref _completed) == 0 && !_process.HasExited;

    public event EventHandler? Completed;

    /// <summary>
    ///     Builds a playback session for the given float PCM audio, or returns
    ///     the inactive sentinel when no audio player is available on PATH.
    /// </summary>
    public static ITtsPlaybackSession Create(float[] samples, int sampleRate)
    {
        var player = ResolvePlayer();
        if (player is null)
            return SupertonicInactiveTtsPlaybackSession.Instance;

        string wavFilePath;
        try
        {
            wavFilePath = Path.Join(
                Path.GetTempPath(),
                $"typewhisper-supertonic-tts-{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(wavFilePath, BuildWav(samples, sampleRate));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Supertonic TTS playback skipped, could not write temp file: {ex.Message}");
            return SupertonicInactiveTtsPlaybackSession.Instance;
        }

        var startInfo = new ProcessStartInfo(player)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(wavFilePath);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning($"Supertonic TTS playback failed to start {player}: {ex.Message}");
            process = null;
        }

        // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
        if (process is null)
        {
            TryDeleteFile(wavFilePath);
            return SupertonicInactiveTtsPlaybackSession.Instance;
        }

        return new SupertonicTtsPlaybackSession(process, wavFilePath);
    }

    public void Stop()
    {
        if (Volatile.Read(ref _completed) != 0)
            return;

        try
        {
            if (!_process.HasExited)
                _process.Kill(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            Trace.TraceWarning($"Supertonic TTS playback stop failed: {ex.Message}");
        }

        Finish();
    }

    public void Dispose() => Stop();

    private void OnExited(object? sender, EventArgs e) => Finish();

    private void Finish()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        _process.Exited -= OnExited;
        _process.Dispose();
        TryDeleteFile(_wavFilePath);
        Completed?.Invoke(this, EventArgs.Empty);
    }

    private static byte[] BuildWav(float[] samples, int sampleRate)
    {
        var dataLength = samples.Length * 2;
        var buffer = new byte[44 + dataLength];
        "RIFF"u8.CopyTo(buffer);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), 36 + dataLength);
        "WAVE"u8.CopyTo(buffer.AsSpan(8));
        "fmt "u8.CopyTo(buffer.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(28), sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(34), 16);
        "data"u8.CopyTo(buffer.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(40), dataLength);

        var destination = buffer.AsSpan(44);
        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Max(-1.0f, Math.Min(1.0f, samples[i]));
            var sample = (short)Math.Round(clamped * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(i * 2, 2), sample);
        }

        return buffer;
    }

    private static string? ResolvePlayer()
    {
        if (CommandExists("paplay"))
            return "paplay";

        // ReSharper disable once ConvertIfStatementToReturnStatement -- subjective style; kept as an explicit if.
        if (CommandExists("aplay"))
            return "aplay";

        return null;
    }

    private static bool CommandExists(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return path.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(dir => File.Exists(Path.Join(dir, name)));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Supertonic TTS temp file cleanup failed: {ex.Message}");
        }
    }
}

internal sealed class SupertonicInactiveTtsPlaybackSession : ITtsPlaybackSession
{
    public static SupertonicInactiveTtsPlaybackSession Instance { get; } = new();

    private SupertonicInactiveTtsPlaybackSession()
    {
    }

    public bool IsActive => false;

    public event EventHandler? Completed
    {
        add { value?.Invoke(this, EventArgs.Empty); }
        remove { }
    }

    public void Stop()
    {
    }
}
