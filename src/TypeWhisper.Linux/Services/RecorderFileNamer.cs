using TypeWhisper.Core.Services;

namespace TypeWhisper.Linux.Services;

internal static class RecorderFileNamer
{
    private const int MaxPathAttempts = 1000;

    public static string CommitRecording(string directory, DateTime timestamp, byte[] wav)
    {
        var baseStem = $"recording-{timestamp:yyyy-MM-dd-HHmmss}";

        for (var attempt = 0; attempt < MaxPathAttempts; attempt++)
        {
            var stem = attempt == 0 ? baseStem : $"{baseStem} ({attempt})";
            var wavPath = Path.Join(directory, stem + ".wav");
            var transcriptPath = Path.Join(directory, stem + ".txt");
            if (PathIsOccupied(wavPath) || PathIsOccupied(transcriptPath))
            {
                continue;
            }

            try
            {
                AtomicFileWrite.WriteAllBytesCreateNew(wavPath, wav);
                return wavPath;
            }
            catch (IOException) when (PathIsOccupied(wavPath))
            {
                // Another actor claimed the WAV candidate after the fast-path check.
            }
        }

        throw new IOException(
            $"Could not create a unique recorder WAV for '{baseStem}.wav' "
            + $"after {MaxPathAttempts} attempts."
        );
    }

    private static bool PathIsOccupied(string path)
    {
        return File.Exists(path) || Directory.Exists(path);
    }
}
