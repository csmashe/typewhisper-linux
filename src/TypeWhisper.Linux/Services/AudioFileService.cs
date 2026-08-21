using TypeWhisper.PluginSDK.Processes;

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
    private readonly IProcessRunner _processRunner;

    public AudioFileService(
        SystemCommandAvailabilityService commands,
        IProcessRunner? processRunner = null
    )
    {
        _commands = commands;
        _processRunner = processRunner ?? new ProcessRunner();
    }

    public bool IsImporterAvailable => _commands.HasFfmpeg;

    public static bool IsSupported(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return s_supportedExtensions.Contains(ext);
    }

    public async Task<bool> IsSupportedAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        if (IsSupported(filePath))
        {
            return true;
        }

        var result = await _processRunner.RunOneShotAsync(
            new ProcessCommand(
                "ffmpeg",
                [
                    "-nostdin",
                    "-v",
                    "error",
                    "-i",
                    filePath,
                    "-map",
                    "0:a:0",
                    "-frames:a",
                    "1",
                    "-f",
                    "null",
                    "-",
                ]
            ),
            // The timeout bounds the probe against inputs File.Exists admits (FIFOs,
            // device nodes) that -frames:a cannot cover: it limits decode output, not demux.
            new ProcessOneShotOptions(
                StandardOutput: ProcessCaptureMode.Discard,
                StandardError: ProcessCaptureMode.Discard,
                Timeout: TimeSpan.FromSeconds(10)
            ),
            cancellationToken
        ).ConfigureAwait(false);

        if (result.Status == ProcessRunStatus.StartFailed)
        {
            throw new InvalidOperationException(
                result.StartError ?? "ffmpeg failed to start."
            );
        }

        if (result.Status == ProcessRunStatus.TimedOut)
        {
            throw new TimeoutException("ffmpeg content probe timed out.");
        }

        if (result.ExitCode is null)
        {
            throw new InvalidOperationException(
                "ffmpeg content probe completed without an exit code."
            );
        }

        return result.ExitCode == 0;
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

        if (!_commands.HasFfmpeg)
        {
            throw new InvalidOperationException("ffmpeg is not installed on this system.");
        }

        // Transcode to mono 16 kHz PCM WAV on stdout: -vn drops any video
        // stream, -ac 1 and -ar 16000 normalize to the format whisper.cpp /
        // SherpaOnnx expects, and pipe:1 avoids a temp file on disk.
        var result = await _processRunner.RunOneShotAsync(
            new ProcessCommand(
                "ffmpeg",
                [
                    "-nostdin",
                    "-v",
                    "error",
                    "-i",
                    filePath,
                    "-vn",
                    "-ac",
                    "1",
                    "-ar",
                    "16000",
                    "-f",
                    "wav",
                    "pipe:1",
                ]
            ),
            new ProcessOneShotOptions(
                StandardOutput: ProcessCaptureMode.Binary,
                StandardError: ProcessCaptureMode.Utf8Text
            ),
            cancellationToken
        ).ConfigureAwait(false);

        if (result.Status == ProcessRunStatus.StartFailed)
        {
            throw new InvalidOperationException(
                result.StartError ?? "ffmpeg failed to start."
            );
        }

        // ReSharper disable once InvertIf -- guard clause; inverting would nest the success path.
        if (!result.Succeeded)
        {
            var stderr = result.StandardErrorText;
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr)
                    ? "ffmpeg failed to load the file."
                    : stderr.Trim()
            );
        }

        return result.StandardOutput;
    }
}
