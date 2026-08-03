using System.Diagnostics;

namespace TypeWhisper.Linux.Services;

public sealed class AudioFileService
{
    private static readonly HashSet<string> s_supportedExtensions = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ".wav",
        ".mp3",
        ".m4a",
        ".aac",
        ".ogg",
        ".flac",
        ".mp4",
        ".mkv",
        ".avi",
        ".mov",
        ".webm",
    };

    private readonly SystemCommandAvailabilityService _commands;

    public AudioFileService(SystemCommandAvailabilityService commands)
    {
        _commands = commands;
    }

    public bool IsImporterAvailable => _commands.HasFfmpeg;

    public static bool IsSupported(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return s_supportedExtensions.Contains(ext);
    }

    public async Task<byte[]> LoadAudioAsWavAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File not found.", filePath);
        }

        if (!IsSupported(filePath))
        {
            throw new NotSupportedException("Unsupported format.");
        }

        if (!_commands.HasFfmpeg)
        {
            throw new InvalidOperationException("ffmpeg is not installed on this system.");
        }

        // Transcode to mono 16 kHz PCM WAV on stdout: -vn drops any video
        // stream, -ac 1 and -ar 16000 normalize to the format whisper.cpp /
        // SherpaOnnx expects, and pipe:1 avoids a temp file on disk.
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(
            "ffmpeg",
            $"-v error -i \"{filePath}\" -vn -ac 1 -ar 16000 -f wav pipe:1"
        )
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };

        process.Start();

        await using var output = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await copyTask;
        await process.WaitForExitAsync(cancellationToken);
        var stderr = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr)
                    ? "ffmpeg failed to load the file."
                    : stderr.Trim()
            );
        }

        return output.ToArray();
    }
}