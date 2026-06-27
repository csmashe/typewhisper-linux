using TypeWhisper.Core;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Manages per-session dictation capture WAV files. Captures are
///     scoped to one app session — they are deleted at startup (to clear
///     any orphans from a previous crash) and at shutdown, so persisted
///     history retains the transcribed text but not the raw audio.
/// </summary>
public sealed class SessionAudioFileService
{
    private const string DictationFilePattern = "dictation-*.wav";

    // kept instance: injected as a DI/test seam by callers
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "kept instance: injected as a DI/test seam")]
    // ReSharper disable once MemberCanBeMadeStatic.Global
    public string SaveDictationCapture(byte[] wav)
    {
        Directory.CreateDirectory(TypeWhisperEnvironment.AudioPath);
        var fileName = $"dictation-{Guid.NewGuid():N}.wav";
        var path = Path.Join(TypeWhisperEnvironment.AudioPath, fileName);
        File.WriteAllBytes(path, wav);
        return path;
    }

    private static string? GetAudioPath(string? audioFileName)
    {
        if (string.IsNullOrWhiteSpace(audioFileName))
        {
            return null;
        }

        var path = Path.Join(TypeWhisperEnvironment.AudioPath, audioFileName);
        return File.Exists(path) ? path : null;
    }

    // kept instance: member of a DI/test seam type
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "kept instance: injected as a DI/test seam")]
    // ReSharper disable once MemberCanBeMadeStatic.Global
    public bool HasAudio(string? audioFileName)
    {
        return GetAudioPath(audioFileName) is not null;
    }

    public static void DeleteSessionCaptures()
    {
        try
        {
            if (!Directory.Exists(TypeWhisperEnvironment.AudioPath))
            {
                return;
            }

            foreach (
                var file in Directory.GetFiles(
                    TypeWhisperEnvironment.AudioPath,
                    DictationFilePattern
                )
            )
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    /* best effort */
                }
            }
        }
        catch
        {
            /* best effort */
        }
    }
}