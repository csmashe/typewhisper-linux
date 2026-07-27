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
    private readonly string _audioDirectory;

    public SessionAudioFileService(string audioDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioDirectory);
        _audioDirectory = Path.GetFullPath(audioDirectory);
    }

    public string SaveDictationCapture(byte[] wav)
    {
        Directory.CreateDirectory(_audioDirectory);
        var fileName = $"dictation-{Guid.NewGuid():N}.wav";
        var path = Path.Join(_audioDirectory, fileName);
        File.WriteAllBytes(path, wav);
        return path;
    }

    private string? GetAudioPath(string? audioFileName)
    {
        if (string.IsNullOrWhiteSpace(audioFileName))
        {
            return null;
        }

        var path = Path.Join(_audioDirectory, audioFileName);
        return File.Exists(path) ? path : null;
    }

    public bool HasAudio(string? audioFileName)
    {
        return GetAudioPath(audioFileName) is not null;
    }

    public void DeleteSessionCaptures()
    {
        try
        {
            if (!Directory.Exists(_audioDirectory))
            {
                return;
            }

            foreach (
                var file in Directory.GetFiles(
                    _audioDirectory,
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
