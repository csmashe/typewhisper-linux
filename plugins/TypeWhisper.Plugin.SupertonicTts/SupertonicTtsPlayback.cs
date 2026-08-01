using System.Buffers.Binary;
using System.Diagnostics;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Processes;

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
    private readonly IPluginProcessSession _session;
    private readonly string _wavFilePath;
    private int _completed;
    private int _stopRequested;

    private SupertonicTtsPlaybackSession(
        IPluginProcessSession session,
        string wavFilePath
    )
    {
        _session = session;
        _wavFilePath = wavFilePath;
        _ = ObserveCompletionAsync();
    }

    public bool IsActive =>
        Volatile.Read(ref _completed) == 0 && _session.IsRunning;

    public event EventHandler? Completed;

    /// <summary>
    ///     Builds a playback session for the given float PCM audio, or returns
    ///     the inactive sentinel when no audio player is available on PATH.
    /// </summary>
    public static ITtsPlaybackSession Create(
        float[] samples,
        int sampleRate,
        IPluginProcessSupervisor processes
    )
    {
        var player = ResolvePlayer();
        if (player is null)
            return SupertonicInactiveTtsPlaybackSession.Instance;

        return Create(samples, sampleRate, processes, player);
    }

    internal static ITtsPlaybackSession Create(
        float[] samples,
        int sampleRate,
        IPluginProcessSupervisor processes,
        string player
    )
    {
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

        var started = processes.StartSession(
            new ProcessCommand(player, [wavFilePath]),
            new ProcessSessionOptions()
        );
        // ReSharper disable once InvertIf -- already the guard-clause form; inverting would nest the success path.
        if (started.Session is not { } session)
        {
            Trace.TraceWarning(
                $"Supertonic TTS playback failed to start {player}: {started.StartError}"
            );
            TryDeleteFile(wavFilePath);
            return SupertonicInactiveTtsPlaybackSession.Instance;
        }

        return new SupertonicTtsPlaybackSession(session, wavFilePath);
    }

    public void Stop()
    {
        if (
            Volatile.Read(ref _completed) != 0
            || Interlocked.Exchange(ref _stopRequested, 1) != 0
        )
            return;

        try
        {
            _session.Terminate();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Supertonic TTS playback stop failed: {ex.Message}");
        }
    }

    public void Dispose() => Stop();

    private async Task ObserveCompletionAsync()
    {
        try
        {
            await _session.Completion.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                $"Supertonic TTS playback completion failed: {ex.Message}"
            );
        }
        finally
        {
            Finish();
        }
    }

    private void Finish()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

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
