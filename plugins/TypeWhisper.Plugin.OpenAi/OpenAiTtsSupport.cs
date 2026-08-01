// ReSharper disable MemberCanBePrivate.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.Plugin.OpenAi;

internal static class OpenAiTtsConfiguration
{
    internal const string ModelId = "gpt-4o-mini-tts";
    internal const string DefaultVoiceId = "marin";
    internal const int SampleRate = 24_000;

    internal static IReadOnlyList<PluginVoiceInfo> AvailableVoices { get; } =
    [
        new("alloy", "Alloy"),
        new("ash", "Ash"),
        new("ballad", "Ballad"),
        new("coral", "Coral"),
        new("echo", "Echo"),
        new("fable", "Fable"),
        new("nova", "Nova"),
        new("onyx", "Onyx"),
        new("sage", "Sage"),
        new("shimmer", "Shimmer"),
        new("verse", "Verse"),
        new("marin", "Marin"),
        new("cedar", "Cedar"),
    ];

    internal static Dictionary<string, JsonElement> CreateRequestBody(
        string text,
        string? voice,
        string? instructions)
    {
        var selectedVoice = string.IsNullOrWhiteSpace(voice) ? DefaultVoiceId : voice;
        var body = new Dictionary<string, JsonElement>
        {
            ["model"] = OpenAiJson.Element(ModelId),
            ["input"] = OpenAiJson.Element(text),
            ["voice"] = OpenAiJson.Element(selectedVoice),
            ["response_format"] = OpenAiJson.Element("pcm"),
        };

        if (!string.IsNullOrWhiteSpace(instructions))
            body["instructions"] = OpenAiJson.Element(instructions.Trim());

        return body;
    }
}

/// <summary>
///     Plays raw PCM16 audio returned by the OpenAI speech endpoint through the
///     platform audio player. The Windows <c>System.Media.SoundPlayer</c> engine
///     used upstream is replaced here with the fork's process-based playback
///     pattern (see xAI's <c>XaiPcmTtsPlaybackSession</c>): the PCM is wrapped in
///     a WAV container, written to a temp file, and handed to <c>paplay</c> or
///     <c>aplay</c>.
/// </summary>
internal sealed class OpenAiPcmTtsPlaybackSession : ITtsPlaybackSession, IDisposable
{
    private readonly IPluginProcessSession _session;
    private readonly string _wavFilePath;
    private int _completed;
    private int _stopRequested;

    private OpenAiPcmTtsPlaybackSession(
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
    ///     Whether an audio player (<c>paplay</c> or <c>aplay</c>) is available
    ///     on PATH. Callers should check this before incurring a paid TTS
    ///     request whose audio could otherwise only be discarded.
    /// </summary>
    public static bool IsPlaybackAvailable() => ResolvePlayer() is not null;

    /// <summary>
    ///     Builds a playback session for the given PCM16 audio, or returns the
    ///     inactive sentinel when no audio player is available on PATH.
    /// </summary>
    public static ITtsPlaybackSession Create(
        byte[] pcm16Audio,
        int sampleRate,
        IPluginProcessSupervisor processes
    )
    {
        var player = ResolvePlayer();
        if (player is null)
            return OpenAiInactiveTtsPlaybackSession.Instance;

        return Create(pcm16Audio, sampleRate, processes, player);
    }

    internal static ITtsPlaybackSession Create(
        byte[] pcm16Audio,
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
                $"typewhisper-openai-tts-{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(wavFilePath, BuildWav(pcm16Audio, sampleRate));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"OpenAI TTS playback skipped, could not write temp file: {ex.Message}");
            return OpenAiInactiveTtsPlaybackSession.Instance;
        }

        var started = processes.StartSession(
            new ProcessCommand(player, [wavFilePath]),
            new ProcessSessionOptions()
        );
        // ReSharper disable once InvertIf -- already the guard-clause form; inverting would nest the success path.
        if (started.Session is not { } session)
        {
            Trace.TraceWarning(
                $"OpenAI TTS playback failed to start {player}: {started.StartError}"
            );
            TryDeleteFile(wavFilePath);
            return OpenAiInactiveTtsPlaybackSession.Instance;
        }

        return new OpenAiPcmTtsPlaybackSession(session, wavFilePath);
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
            Trace.TraceWarning($"OpenAI TTS playback stop failed: {ex.Message}");
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
                $"OpenAI TTS playback completion failed: {ex.Message}"
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
            Trace.TraceWarning($"OpenAI TTS temp file cleanup failed: {ex.Message}");
        }
    }
}

internal sealed class OpenAiInactiveTtsPlaybackSession : ITtsPlaybackSession
{
    public static OpenAiInactiveTtsPlaybackSession Instance { get; } = new();

    private OpenAiInactiveTtsPlaybackSession()
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
