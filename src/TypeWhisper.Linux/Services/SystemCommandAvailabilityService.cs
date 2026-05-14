using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Services;

public sealed class SystemCommandAvailabilityService
{
    private const int RtldNow = 2;
    private const int RtldGlobal = 0x100;

    private static readonly string[] CudaLibraryPathCandidates =
    [
        "/usr/local/cuda/lib64",
        "/usr/local/cuda/targets/x86_64-linux/lib",
        "/usr/local/cuda-12.9/lib64",
        "/usr/local/cuda-12.9/targets/x86_64-linux/lib",
        "/usr/local/cuda-12.8/lib64",
        "/usr/local/cuda-12.8/targets/x86_64-linux/lib",
        "/usr/local/cuda-12.7/lib64",
        "/usr/local/cuda-12.7/targets/x86_64-linux/lib",
        "/usr/local/cuda-12.6/lib64",
        "/usr/local/cuda-12.6/targets/x86_64-linux/lib",
        "/usr/local/cuda-12.5/lib64",
        "/usr/local/cuda-12.5/targets/x86_64-linux/lib",
        "/usr/local/cuda-12.4/lib64",
        "/usr/local/cuda-12.4/targets/x86_64-linux/lib",
        "/usr/local/cuda-12.3/lib64",
        "/usr/local/cuda-12.3/targets/x86_64-linux/lib",
        "/usr/local/cuda-12.2/lib64",
        "/usr/local/cuda-12.2/targets/x86_64-linux/lib",
        "/usr/local/cuda-12.1/lib64",
        "/usr/local/cuda-12.1/targets/x86_64-linux/lib",
        "/usr/local/cuda-12.0/lib64",
        "/usr/local/cuda-12.0/targets/x86_64-linux/lib",
    ];

    private static readonly object CudaPreloadLock = new();
    private static readonly List<IntPtr> CudaPreloadHandles = [];

    private LinuxCapabilitySnapshot _snapshot;

    public SystemCommandAvailabilityService()
    {
        _snapshot = BuildSnapshot();
    }

    /// <summary>
    /// Fired after <see cref="RefreshSnapshot"/> rebuilds the cached
    /// snapshot. Subscribers (notably the live insertion platform) re-read
    /// their derived state so a one-click ydotool setup takes effect
    /// without an app restart. Handlers must not throw — the refresh
    /// flow can't usefully report a subscriber failure to the user.
    /// </summary>
    public event EventHandler<LinuxCapabilitySnapshot>? SnapshotChanged;

    public bool IsWaylandSession { get { var s = _snapshot; return s.SessionType == "Wayland"; } }
    public bool IsX11Session { get { var s = _snapshot; return s.SessionType == "X11"; } }
    public bool HasXdotool { get { var s = _snapshot; return s.HasXdotool; } }
    public bool HasWtype { get { var s = _snapshot; return s.HasWtype; } }
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
        try
        {
            SnapshotChanged?.Invoke(this, snapshot);
        }
        catch
        {
            // A misbehaving subscriber must not turn a successful refresh
            // (e.g. ydotool just got set up) into a failure surfaced to
            // the user. Swallow and continue.
        }
        return snapshot;
    }

    /// <summary>
    /// Test seam: replaces the cached snapshot with a supplied one and
    /// raises <see cref="SnapshotChanged"/>. Lets the chain-rebuild
    /// integration be exercised without relying on whatever ydotool /
    /// wtype binaries happen to live on the test host.
    /// </summary>
    internal void RaiseSnapshotChangedForTests(LinuxCapabilitySnapshot snapshot)
    {
        Interlocked.Exchange(ref _snapshot, snapshot);
        try
        {
            SnapshotChanged?.Invoke(this, snapshot);
        }
        catch
        {
            // Match RefreshSnapshot's swallow-on-subscriber-throw behavior.
        }
    }

    public static string? FindCuda12RuntimeDirectory()
    {
        foreach (var path in CudaLibraryPathCandidates)
        {
            try
            {
                if (File.Exists(Path.Combine(path, "libcudart.so.12"))
                    && File.Exists(Path.Combine(path, "libcublas.so.12")))
                    return path;
            }
            catch
            {
                // Ignore inaccessible paths.
            }
        }

        return null;
    }

    public static bool TryPreloadCuda12RuntimeLibraries(out string message)
    {
        if (IsLibraryAvailable("libcudart.so.12") && IsLibraryAvailable("libcublas.so.12"))
        {
            message = "CUDA 12 runtime libraries are already visible.";
            return true;
        }

        var directory = FindCuda12RuntimeDirectory();
        if (directory is null)
        {
            message = "CUDA 12 runtime libraries are not installed.";
            return false;
        }

        lock (CudaPreloadLock)
        {
            if (CudaPreloadHandles.Count > 0)
            {
                message = $"CUDA 12 runtime libraries were preloaded from {directory}.";
                return true;
            }

            foreach (var library in new[] { "libcudart.so.12", "libcublas.so.12" })
            {
                var path = Path.Combine(directory, library);
                var handle = dlopen(path, RtldNow | RtldGlobal);
                if (handle == IntPtr.Zero)
                {
                    var error = Marshal.PtrToStringAnsi(dlerror());
                    message = $"Could not load {library} from {directory}: {error ?? "unknown error"}";
                    return false;
                }

                CudaPreloadHandles.Add(handle);
            }
        }

        message = $"CUDA 12 runtime libraries were loaded from {directory}.";
        return true;
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
        var hasYdotool = IsCommandAvailable("ydotool");
        var ydotoolSocket = ResolveYdotoolSocketPath();

        return new LinuxCapabilitySnapshot(
            SessionType: isWayland ? "Wayland" : isX11 ? "X11" : "Unknown",
            HasClipboardTool: isWayland ? hasWlClipboard : hasXclip,
            ClipboardToolName: isWayland ? "wl-clipboard" : "xclip",
            HasXdotool: IsCommandAvailable("xdotool"),
            HasWtype: IsCommandAvailable("wtype"),
            HasFfmpeg: IsCommandAvailable("ffmpeg"),
            HasSpeechFeedback: speechCommand is not null,
            SpeechFeedbackCommand: speechCommand,
            HasPactl: hasPactl,
            HasPlayerCtl: hasPlayerCtl,
            HasCanberraGtkPlay: hasCanberraGtkPlay,
            HasCudaGpu: IsCommandAvailable("nvidia-smi") || File.Exists("/dev/nvidiactl"),
            HasCudaRuntimeLibraries: (IsLibraryAvailable("libcudart.so.12")
                                      && IsLibraryAvailable("libcublas.so.12"))
                                     || FindCuda12RuntimeDirectory() is not null,
            Compositor: DesktopDetector.DetectId(),
            HasYdotool: hasYdotool,
            HasYdotoolSocket: ydotoolSocket is not null,
            YdotoolSocketPath: ydotoolSocket);
    }

    /// <summary>
    /// Resolve the ydotoold socket using the same priority list voxtype
    /// settled on. Returns null if no candidate path exists or is
    /// readable. We do not stat-check permissions here — the daemon is
    /// the authoritative answer; we only need to know whether *some*
    /// candidate is reachable so the snapshot can advertise availability.
    /// </summary>
    internal static string? ResolveYdotoolSocketPath()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("YDOTOOL_SOCKET"),
        };

        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(runtimeDir))
            candidates.Add(Path.Combine(runtimeDir, ".ydotool_socket"));

        candidates.Add("/tmp/.ydotool_socket");

        var uid = TryReadUserId();
        if (uid is not null)
            candidates.Add($"/run/user/{uid}/.ydotool_socket");

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            try
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Inaccessible socket path — skip it.
            }
        }

        return null;
    }

    private static string? TryReadUserId()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("id", "-u")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            if (!p.WaitForExit(500))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch
        {
            return null;
        }
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

        if (FindInEnvironmentLibraryPath(libraryName))
            return true;

        foreach (var directory in new[]
                 {
                     "/usr/lib64",
                     "/lib64",
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

    private static bool FindInEnvironmentLibraryPath(string libraryName)
    {
        var value = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var directory in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory, libraryName)))
                    return true;
            }
            catch
            {
                // Ignore invalid entries.
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

            // Drain both pipes concurrently so a noisy stderr can't block ldconfig
            // while it's still streaming the (large) library list to stdout.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(1000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore failures while cleaning up a timed-out probe.
                }

                return false;
            }

            var output = outputTask.GetAwaiter().GetResult();
            errorTask.GetAwaiter().GetResult();
            return output.Contains(libraryName, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("libdl.so.2", CharSet = CharSet.Ansi)]
    private static extern IntPtr dlopen(string fileName, int flags);

    [DllImport("libdl.so.2")]
    private static extern IntPtr dlerror();
}

public sealed record CudaBenchmarkResult(bool Success, string Message, TimeSpan? Elapsed);

public sealed record LinuxCapabilitySnapshot(
    string SessionType,
    bool HasClipboardTool,
    string ClipboardToolName,
    bool HasXdotool,
    bool HasWtype,
    bool HasFfmpeg,
    bool HasSpeechFeedback,
    string? SpeechFeedbackCommand,
    bool HasPactl,
    bool HasPlayerCtl,
    bool HasCanberraGtkPlay,
    bool HasCudaGpu,
    bool HasCudaRuntimeLibraries,
    string Compositor = "unknown",
    bool HasYdotool = false,
    bool HasYdotoolSocket = false,
    string? YdotoolSocketPath = null)
{
    public bool HasAutomaticPasteTool => SessionType == "Wayland"
        ? HasWtype || HasXdotool || (HasYdotool && HasYdotoolSocket)
        : HasXdotool;
    public bool CanAutoPaste => HasClipboardTool && HasAutomaticPasteTool;
    public bool CanUseCuda => HasCudaGpu && HasCudaRuntimeLibraries;
    /// <summary>
    /// True when wtype's virtual-keyboard protocol is unlikely to be
    /// implemented by the running compositor. GNOME Mutter and KDE
    /// Plasma's KWin both omit <c>zwp_virtual_keyboard_v1</c>, which
    /// makes wtype fail with "Compositor does not support…" — we want
    /// the chain to demote wtype below ydotool in that case.
    /// </summary>
    public bool CompositorRejectsWtype =>
        SessionType == "Wayland" && Compositor is "gnome" or "kde";
    public bool HasYdotoolAvailable => HasYdotool && HasYdotoolSocket;
    public string ClipboardStatus => HasClipboardTool
        ? $"{ClipboardToolName} available"
        : $"Install {ClipboardToolName} to enable clipboard insertion.";
    public string PasteStatus => SessionType == "Wayland"
        ? HasYdotoolAvailable
            ? "ydotool available"
            : HasWtype && !CompositorRejectsWtype
                ? "wtype available"
                : HasXdotool
                    ? "xdotool available (XWayland only)"
                    : PasteToolInstallHint
        : HasXdotool
            ? "xdotool available"
            : PasteToolInstallHint;
    public string PasteToolInstallHint => SessionType == "Wayland"
        ? CompositorRejectsWtype
            ? "Set up ydotool to enable automatic paste on GNOME / KDE Wayland."
            : "Install wtype (or ydotool / xdotool) to enable automatic paste."
        : "Install xdotool to enable automatic paste.";
    public string CudaStatus => CanUseCuda
        ? "CUDA available"
        : HasCudaGpu
            ? "NVIDIA GPU detected, but CUDA 12 runtime libraries are missing."
            : "No NVIDIA GPU/driver detected.";
}
