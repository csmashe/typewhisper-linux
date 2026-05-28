using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Xai;

internal static class XaiTtsConfiguration
{
    internal const string DefaultVoiceId = "eve";
    internal const int SampleRate = 24_000;

    internal static IReadOnlyList<PluginVoiceInfo> FallbackVoices { get; } =
    [
        new("eve", "Eve"),
        new("ara", "Ara"),
        new("leo", "Leo"),
        new("rex", "Rex"),
        new("sal", "Sal"),
    ];

    internal static Dictionary<string, JsonElement> CreateRequestBody(
        string text,
        string? voice,
        string? language,
        bool lowLatency,
        bool textNormalization)
    {
        var selectedVoice = string.IsNullOrWhiteSpace(voice) ? DefaultVoiceId : voice.Trim();
        var selectedLanguage = string.IsNullOrWhiteSpace(language) ? "auto" : language.Trim();

        return new Dictionary<string, JsonElement>
        {
            ["text"] = XaiJson.Element(text),
            ["voice_id"] = XaiJson.Element(selectedVoice),
            ["language"] = XaiJson.Element(selectedLanguage),
            ["output_format"] = XaiJson.Element(new
            {
                codec = "pcm",
                sample_rate = SampleRate,
            }),
            ["optimize_streaming_latency"] = XaiJson.Element(lowLatency ? 1 : 0),
            ["text_normalization"] = XaiJson.Element(textNormalization),
        };
    }
}

/// <summary>
///     Plays raw PCM16 audio returned by the xAI TTS endpoint through the
///     platform audio player. The Windows <c>System.Media.SoundPlayer</c> engine
///     used upstream is replaced here with the fork's process-based playback
///     pattern (see <c>LinuxSystemTtsProvider</c>): the PCM is wrapped in a WAV
///     container, written to a temp file, and handed to <c>paplay</c> or
///     <c>aplay</c>.
/// </summary>
internal sealed class XaiPcmTtsPlaybackSession : ITtsPlaybackSession, IDisposable
{
    private readonly Process _process;
    private readonly string _wavFilePath;
    private int _completed;

    private XaiPcmTtsPlaybackSession(Process process, string wavFilePath)
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
    ///     Whether an audio player (<c>paplay</c> or <c>aplay</c>) is available
    ///     on PATH. Callers should check this before incurring a paid TTS
    ///     request whose audio could otherwise only be discarded.
    /// </summary>
    public static bool IsPlaybackAvailable() => ResolvePlayer() is not null;

    /// <summary>
    ///     Builds a playback session for the given PCM16 audio, or returns the
    ///     inactive sentinel when no audio player is available on PATH.
    /// </summary>
    public static ITtsPlaybackSession Create(byte[] pcm16Audio, int sampleRate)
    {
        var player = ResolvePlayer();
        if (player is null)
            return XaiInactiveTtsPlaybackSession.Instance;

        string wavFilePath;
        try
        {
            wavFilePath = Path.Combine(
                Path.GetTempPath(),
                $"typewhisper-xai-tts-{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(wavFilePath, BuildWav(pcm16Audio, sampleRate));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"xAI TTS playback skipped, could not write temp file: {ex.Message}");
            return XaiInactiveTtsPlaybackSession.Instance;
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
            Trace.TraceWarning($"xAI TTS playback failed to start {player}: {ex.Message}");
            process = null;
        }

        if (process is null)
        {
            TryDeleteFile(wavFilePath);
            return XaiInactiveTtsPlaybackSession.Instance;
        }

        return new XaiPcmTtsPlaybackSession(process, wavFilePath);
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
            Trace.TraceWarning($"xAI TTS playback stop failed: {ex.Message}");
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

    private static byte[] BuildWav(byte[] pcm16Audio, int sampleRate)
    {
        var dataLength = pcm16Audio.Length;
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
        pcm16Audio.CopyTo(buffer.AsSpan(44));
        return buffer;
    }

    private static string? ResolvePlayer()
    {
        if (CommandExists("paplay"))
            return "paplay";

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
            .Any(dir => File.Exists(Path.Combine(dir, name)));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"xAI TTS temp file cleanup failed: {ex.Message}");
        }
    }
}

internal sealed class XaiInactiveTtsPlaybackSession : ITtsPlaybackSession
{
    public static XaiInactiveTtsPlaybackSession Instance { get; } = new();

    private XaiInactiveTtsPlaybackSession()
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
