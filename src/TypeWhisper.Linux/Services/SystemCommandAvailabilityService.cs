using System.IO;
using System.Diagnostics;

namespace TypeWhisper.Linux.Services;

public sealed class SystemCommandAvailabilityService
{
    private LinuxCapabilitySnapshot _snapshot;

    public SystemCommandAvailabilityService()
    {
        _snapshot = BuildSnapshot();
    }

    public bool IsWaylandSession { get { var s = _snapshot; return s.SessionType == "Wayland"; } }
    public bool IsX11Session { get { var s = _snapshot; return s.SessionType == "X11"; } }
    public bool HasXdotool { get { var s = _snapshot; return s.HasAutomaticPasteTool; } }
    public bool HasXclip { get { var s = _snapshot; return s.ClipboardToolName == "xclip" && s.HasClipboardTool; } }
    public bool HasWlClipboard { get { var s = _snapshot; return s.ClipboardToolName == "wl-clipboard" && s.HasClipboardTool; } }
    public bool HasPactl { get { var s = _snapshot; return s.HasPactl; } }
    public bool HasPlayerCtl { get { var s = _snapshot; return s.HasPlayerCtl; } }
    public bool HasCanberraGtkPlay { get { var s = _snapshot; return s.HasCanberraGtkPlay; } }
    public bool HasFfmpeg { get { var s = _snapshot; return s.HasFfmpeg; } }
    public bool HasSpeechFeedback { get { var s = _snapshot; return s.HasSpeechFeedback; } }
    public bool HasCudaGpu { get { var s = _snapshot; return s.HasCudaGpu; } }
    public bool HasCudaRuntimeLibraries { get { var s = _snapshot; return s.HasCudaRuntimeLibraries; } }
    public string? SpeechFeedbackCommand { get { var s = _snapshot; return s.SpeechFeedbackCommand; } }

    public LinuxCapabilitySnapshot GetSnapshot()
    {
        var s = _snapshot;
        return s;
    }

    public LinuxCapabilitySnapshot RefreshSnapshot()
    {
        var snapshot = BuildSnapshot();
        Interlocked.Exchange(ref _snapshot, snapshot);
        return snapshot;
    }

    public async Task<CudaBenchmarkResult> RunCudaBenchmarkAsync(CancellationToken cancellationToken = default)
    {
        if (!HasCudaGpu)
            return new CudaBenchmarkResult(false, "No NVIDIA GPU/driver detected.", null);

        if (!HasCudaRuntimeLibraries)
            return new CudaBenchmarkResult(false, "NVIDIA GPU detected, but CUDA 12 runtime libraries are missing.", null);

        if (!IsCommandAvailable("nvidia-smi"))
            return new CudaBenchmarkResult(true, "CUDA runtime libraries are present. nvidia-smi was not found for timing.", null);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var process = Process.Start(new ProcessStartInfo(
                "nvidia-smi",
                "--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
                return new CudaBenchmarkResult(false, "Could not start nvidia-smi.", null);

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var waitTask = process.WaitForExitAsync(cancellationToken);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            var completed = await Task.WhenAny(waitTask, timeoutTask);
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);

            if (!ReferenceEquals(completed, waitTask) && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return new CudaBenchmarkResult(false, "nvidia-smi did not respond within 3 seconds.", stopwatch.Elapsed);
            }

            await waitTask;

            stopwatch.Stop();
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            if (process.ExitCode != 0)
                return new CudaBenchmarkResult(false, string.IsNullOrWhiteSpace(error) ? "nvidia-smi failed." : error, stopwatch.Elapsed);

            var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            var message = string.IsNullOrWhiteSpace(firstLine)
                ? $"CUDA responded in {stopwatch.ElapsedMilliseconds} ms."
                : $"CUDA responded in {stopwatch.ElapsedMilliseconds} ms: {firstLine}.";
            return new CudaBenchmarkResult(true, message, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return new CudaBenchmarkResult(false, "CUDA benchmark was canceled.", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            return new CudaBenchmarkResult(false, $"CUDA benchmark failed: {ex.Message}", stopwatch.Elapsed);
        }
    }

    private LinuxCapabilitySnapshot BuildSnapshot()
    {
        var isWayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 };
        var isX11 = Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 };
        var hasXclip = IsCommandAvailable("xclip");
        var hasWlClipboard = IsCommandAvailable("wl-copy") && IsCommandAvailable("wl-paste");
        var speechCommand = ResolveSpeechFeedbackCommand();

        var hasPactl = IsCommandAvailable("pactl");
        var hasPlayerCtl = IsCommandAvailable("playerctl");
        var hasCanberraGtkPlay = IsCommandAvailable("canberra-gtk-play");

        return new LinuxCapabilitySnapshot(
            SessionType: isWayland ? "Wayland" : isX11 ? "X11" : "Unknown",
            HasClipboardTool: isWayland ? hasWlClipboard : hasXclip,
            ClipboardToolName: isWayland ? "wl-clipboard" : "xclip",
            HasAutomaticPasteTool: IsCommandAvailable("xdotool"),
            HasFfmpeg: IsCommandAvailable("ffmpeg"),
            HasSpeechFeedback: speechCommand is not null,
            SpeechFeedbackCommand: speechCommand,
            HasPactl: hasPactl,
            HasPlayerCtl: hasPlayerCtl,
            HasCanberraGtkPlay: hasCanberraGtkPlay,
            HasCudaGpu: IsCommandAvailable("nvidia-smi") || File.Exists("/dev/nvidiactl"),
            HasCudaRuntimeLibraries: IsLibraryAvailable("libcudart.so.12")
                                     && IsLibraryAvailable("libcublas.so.12"));
    }

    private static string? ResolveSpeechFeedbackCommand()
    {
        if (IsCommandAvailable("espeak-ng")) return "espeak-ng";
        if (IsCommandAvailable("espeak")) return "espeak";
        if (IsCommandAvailable("spd-say")) return "spd-say";
        return null;
    }

    public static bool IsCommandAvailable(string commandName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
            return false;

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, commandName);
                if (File.Exists(candidate))
                    return true;
            }
            catch
            {
                // Ignore invalid PATH entries.
            }
        }

        return false;
    }

    private static bool IsLibraryAvailable(string libraryName)
    {
        if (FindInLdCache(libraryName))
            return true;

        foreach (var directory in new[]
                 {
                     "/usr/local/cuda/lib64",
                     "/usr/local/cuda-13.0/lib64",
                     "/usr/lib/x86_64-linux-gnu",
                     "/lib/x86_64-linux-gnu"
                 })
        {
            try
            {
                if (File.Exists(Path.Combine(directory, libraryName)))
                    return true;
            }
            catch
            {
                // Ignore inaccessible library directories.
            }
        }

        return false;
    }

    private static bool FindInLdCache(string libraryName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ldconfig", "-p")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
                return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1000);
            return output.Contains(libraryName, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}

public sealed record CudaBenchmarkResult(bool Success, string Message, TimeSpan? Elapsed);

public sealed record LinuxCapabilitySnapshot(
    string SessionType,
    bool HasClipboardTool,
    string ClipboardToolName,
    bool HasAutomaticPasteTool,
    bool HasFfmpeg,
    bool HasSpeechFeedback,
    string? SpeechFeedbackCommand,
    bool HasPactl,
    bool HasPlayerCtl,
    bool HasCanberraGtkPlay,
    bool HasCudaGpu,
    bool HasCudaRuntimeLibraries)
{
    public bool CanAutoPaste => HasClipboardTool && HasAutomaticPasteTool;
    public bool CanUseCuda => HasCudaGpu && HasCudaRuntimeLibraries;
    public string ClipboardStatus => HasClipboardTool
        ? $"{ClipboardToolName} available"
        : $"Install {ClipboardToolName} to enable clipboard insertion.";
    public string PasteStatus => HasAutomaticPasteTool
        ? "xdotool available"
        : "Install xdotool to enable automatic paste.";
    public string CudaStatus => CanUseCuda
        ? "CUDA available"
        : HasCudaGpu
            ? "NVIDIA GPU detected, but CUDA 12 runtime libraries are missing."
            : "No NVIDIA GPU/driver detected.";
}
