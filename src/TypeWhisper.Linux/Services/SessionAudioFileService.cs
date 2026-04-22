using TypeWhisper.Core;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// Manages dictation capture WAV files that are only valid for the current app session.
/// Files are deleted on startup and shutdown so persisted history retains text, not audio.
/// </summary>
public sealed class SessionAudioFileService
{
    private const string DictationFilePattern = "dictation-*.wav";

    public string SaveDictationCapture(byte[] wav)
    {
        Directory.CreateDirectory(TypeWhisperEnvironment.AudioPath);
        var fileName = $"dictation-{Guid.NewGuid():N}.wav";
        var path = Path.Combine(TypeWhisperEnvironment.AudioPath, fileName);
        File.WriteAllBytes(path, wav);
        return path;
    }

    public string? GetAudioPath(string? audioFileName)
    {
        if (string.IsNullOrWhiteSpace(audioFileName))
            return null;

        var path = Path.Combine(TypeWhisperEnvironment.AudioPath, audioFileName);
        return File.Exists(path) ? path : null;
    }

    public bool HasAudio(string? audioFileName) => GetAudioPath(audioFileName) is not null;

    public void DeleteSessionCaptures()
    {
        try
        {
            if (!Directory.Exists(TypeWhisperEnvironment.AudioPath))
                return;

            foreach (var file in Directory.GetFiles(TypeWhisperEnvironment.AudioPath, DictationFilePattern))
            {
                try { File.Delete(file); }
                catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
    }
}
