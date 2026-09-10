using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Linux.Services;

/// <summary>Stores dictation captures and prunes expired or unreferenced recordings.</summary>
public sealed class SessionAudioFileService
{
    private const string DictationFilePattern = "dictation-*.wav";
    private readonly string _audioDirectory;
    private readonly IHistoryService? _history;

    public SessionAudioFileService(string audioDirectory, IHistoryService? history = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioDirectory);
        _audioDirectory = Path.GetFullPath(audioDirectory);
        _history = history;
    }

    public string SaveDictationCapture(byte[] wav)
    {
        Directory.CreateDirectory(_audioDirectory);
        var fileName = $"dictation-{Guid.NewGuid():N}.wav";
        var path = Path.Join(_audioDirectory, fileName);
        File.WriteAllBytes(path, wav);
        return path;
    }

    public string? GetAudioPath(string? audioFileName)
    {
        if (string.IsNullOrWhiteSpace(audioFileName) || Path.GetFileName(audioFileName) != audioFileName)
        {
            return null;
        }

        var path = Path.Join(_audioDirectory, audioFileName);
        try
        {
            return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 ? path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool HasAudio(string? audioFileName)
    {
        return GetAudioPath(audioFileName) is not null;
    }

    /// <summary>
    ///     Deletes captures older than <paramref name="days" />, plus any capture no history record
    ///     still points at. <c>-1</c> is the "keep nothing" sentinel and deletes every capture; any
    ///     other value is clamped to 1..365. Orphans are only pruned when the history could be read,
    ///     so an unreadable history never looks like an empty one.
    /// </summary>
    public void ApplyRetention(int days)
    {
        try
        {
            if (!Directory.Exists(_audioDirectory))
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 365));
            var canPruneOrphans = false;
            var referenced = new HashSet<string?>(StringComparer.Ordinal);
            if (_history is not null)
            {
                canPruneOrphans = _history.TryGetRecords(out var records);
                foreach (var record in records)
                    referenced.Add(record.AudioFileName);
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
                    if (days == -1 || File.GetLastWriteTimeUtc(file) < cutoff
                        || (canPruneOrphans && !referenced.Contains(Path.GetFileName(file))))
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
