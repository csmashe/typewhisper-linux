using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.Linux.Services;

public sealed partial class SystemCommandAvailabilityService
{
    private const int RtldNow = 2;
    private const int RtldGlobal = 0x100;
    private static readonly TimeSpan s_ydotoolSocketConnectTimeout =
        TimeSpan.FromMilliseconds(250);

    private static readonly string[] s_cudaLibraryPathCandidates =
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
    private static readonly string[] s_requiredCuda12RuntimeLibraries =
    [
        "libcudart.so.12",
        "libcublas.so.12",
    ];

    private static readonly Lock s_cudaPreloadLock = new();
    private static readonly Dictionary<string, IntPtr> s_cudaPreloadHandles = new(
        StringComparer.Ordinal
    );

    private readonly IProcessRunner _processRunner;
    private LinuxCapabilitySnapshot _snapshot;

    public SystemCommandAvailabilityService(IProcessRunner? processRunner = null)
    {
        _processRunner = processRunner ?? new ProcessRunner();
        _snapshot = BuildSnapshot();
    }

    public bool IsWaylandSession
    {
        get
        {
            var s = _snapshot;
            return s.SessionType == "Wayland";
        }
    }

    // ReSharper disable once UnusedMember.Global  public capability-flag property mirroring LinuxCapabilitySnapshot; not currently called in-tree (callers read the snapshot record directly)
    public bool IsX11Session
    {
        get
        {
            var s = _snapshot;
            return s.SessionType == "X11";
        }
    }

    // ReSharper disable once UnusedMember.Global  public capability-flag property mirroring LinuxCapabilitySnapshot; not currently called in-tree (callers read the snapshot record directly)
    public bool HasXdotool
    {
        get
        {
            var s = _snapshot;
            return s.HasXdotool;
        }
    }

    // ReSharper disable once UnusedMember.Global  public capability-flag property mirroring LinuxCapabilitySnapshot; not currently called in-tree (callers read the snapshot record directly)
    public bool HasWtype
    {
        get
        {
            var s = _snapshot;
            return s.HasWtype;
        }
    }

    // ReSharper disable once UnusedMember.Global  public capability-flag property mirroring LinuxCapabilitySnapshot; not currently called in-tree (callers read the snapshot record directly)
    public bool HasXclip
    {
        get
        {
            var s = _snapshot;
            return s is { ClipboardToolName: "xclip", HasClipboardTool: true };
        }
    }

    // ReSharper disable once UnusedMember.Global  public capability-flag property mirroring LinuxCapabilitySnapshot; not currently called in-tree (callers read the snapshot record directly)
    public bool HasWlClipboard
    {
        get
        {
            var s = _snapshot;
            return s is { ClipboardToolName: "wl-clipboard", HasClipboardTool: true };
        }
    }

    public bool HasPactl
    {
        get
        {
            var s = _snapshot;
            return s.HasPactl;
        }
    }

    public bool HasPlayerCtl
    {
        get
        {
            var s = _snapshot;
            return s.HasPlayerCtl;
        }
    }

    public bool HasAudioPlayer
    {
        get
        {
            var s = _snapshot;
            return s.HasAudioPlayer;
        }
    }

    public bool HasFfmpeg
    {
        get
        {
            var s = _snapshot;
            return s.HasFfmpeg;
        }
    }

    // ReSharper disable once UnusedMember.Global  public capability-flag property mirroring LinuxCapabilitySnapshot; not currently called in-tree (callers read the snapshot record directly)
    public bool HasSpeechFeedback
    {
        get
        {
            var s = _snapshot;
            return s.HasSpeechFeedback;
        }
    }

    public bool HasCudaGpu
    {
        get
        {
            var s = _snapshot;
            return s.HasCudaGpu;
        }
    }

    public bool HasCudaRuntimeLibraries
    {
        get
        {
            var s = _snapshot;
            return s.HasCudaRuntimeLibraries;
        }
    }

    public string? SpeechFeedbackCommand
    {
        get
        {
            var s = _snapshot;
            return s.SpeechFeedbackCommand;
        }
    }

    public LinuxCapabilitySnapshot GetSnapshot()
    {
        var s = _snapshot;
        return s;
    }

    // ReSharper disable once UnusedMethodReturnValue.Global -- returns the rebuilt snapshot for callers that want it; the current caller ignores it.
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
            // Swallow: a misbehaving subscriber must not surface as a refresh failure.
        }

        return snapshot;
    }

    public static string? FindCuda12RuntimeDirectory()
    {
        foreach (var path in s_cudaLibraryPathCandidates)
        {
            try
            {
                if (
                    File.Exists(Path.Join(path, "libcudart.so.12"))
                    && File.Exists(Path.Join(path, "libcublas.so.12"))
                )
                {
                    return path;
                }
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
        var processRunner = new ProcessRunner();
        if (AreCuda12LibrariesVisible(processRunner))
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

        // RTLD_GLOBAL pins the CUDA libs into the process's global symbol table
        // so native whisper/sherpa libs find them even without LD_LIBRARY_PATH.
        lock (s_cudaPreloadLock)
        {
            return TryPreloadCuda12RuntimeLibrariesFromDirectory(
                directory,
                s_cudaPreloadHandles,
                LoadCuda12RuntimeLibrary,
                out message
            );
        }
    }

    // Callers sharing loadedHandles must synchronize access around this operation.
    internal static bool TryPreloadCuda12RuntimeLibrariesFromDirectory(
        string directory,
        IDictionary<string, IntPtr> loadedHandles,
        Func<string, (IntPtr Handle, string? Error)> loadLibrary,
        out string message
    )
    {
        if (
            s_requiredCuda12RuntimeLibraries.All(library =>
                loadedHandles.TryGetValue(library, out var handle) && handle != IntPtr.Zero
            )
        )
        {
            message = $"CUDA 12 runtime libraries were preloaded from {directory}.";
            return true;
        }

        foreach (var library in s_requiredCuda12RuntimeLibraries)
        {
            if (
                loadedHandles.TryGetValue(library, out var loadedHandle)
                && loadedHandle != IntPtr.Zero
            )
            {
                continue;
            }

            var (handle, error) = loadLibrary(Path.Join(directory, library));
            if (handle == IntPtr.Zero)
            {
                message =
                    $"Could not load {library} from {directory}: {error ?? "unknown error"}";
                return false;
            }

            loadedHandles[library] = handle;
        }

        message = $"CUDA 12 runtime libraries were loaded from {directory}.";
        return true;
    }

    private static (IntPtr Handle, string? Error) LoadCuda12RuntimeLibrary(string path)
    {
        var handle = dlopen(path, RtldNow | RtldGlobal);
        return handle == IntPtr.Zero
            ? (handle, Marshal.PtrToStringAnsi(dlerror()))
            : (handle, null);
    }

    public async Task<CudaBenchmarkResult> RunCudaBenchmarkAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (!HasCudaGpu)
        {
            return new CudaBenchmarkResult(false, "No NVIDIA GPU/driver detected.", null);
        }

        if (!HasCudaRuntimeLibraries)
        {
            return new CudaBenchmarkResult(
                false,
                "NVIDIA GPU detected, but CUDA 12 runtime libraries are missing.",
                null
            );
        }

        if (!IsCommandAvailable("nvidia-smi"))
        {
            return new CudaBenchmarkResult(
                true,
                "CUDA runtime libraries are present. nvidia-smi was not found for timing.",
                null
            );
        }

        var stopwatch = Stopwatch.StartNew();
        Process? process = null;
        try
        {
            var result = await _processRunner.RunOneShotAsync(
                new ProcessCommand(
                    "nvidia-smi",
                    [
                        "--query-gpu=name,memory.total,driver_version",
                        "--format=csv,noheader,nounits",
                    ]
                ),
                new ProcessOneShotOptions(Timeout: TimeSpan.FromSeconds(3)),
                cancellationToken
            );
            // ReSharper disable once ConvertIfStatementToSwitchStatement -- chain continues
            // into ExitCode checks below; a switch would only cover part of it.
            if (result.Status == ProcessRunStatus.StartFailed)
            {
                return new CudaBenchmarkResult(false, "Could not start nvidia-smi.", null);
            }

            if (result.Status == ProcessRunStatus.TimedOut)
            {
                return new CudaBenchmarkResult(
                    false,
                    "nvidia-smi did not respond within 3 seconds.",
                    stopwatch.Elapsed
                );
            }

            stopwatch.Stop();
            var output = result.StandardOutputText.Trim();
            var error = result.StandardErrorText.Trim();
            if (result.ExitCode != 0)
            {
                return new CudaBenchmarkResult(
                    false,
                    string.IsNullOrWhiteSpace(error) ? "nvidia-smi failed." : error,
                    stopwatch.Elapsed
                );
            }

            var firstLine = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            var message = string.IsNullOrWhiteSpace(firstLine)
                ? $"CUDA responded in {stopwatch.ElapsedMilliseconds} ms."
                : $"CUDA responded in {stopwatch.ElapsedMilliseconds} ms: {firstLine}.";
            return new CudaBenchmarkResult(true, message, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return new CudaBenchmarkResult(
                false,
                "CUDA benchmark was canceled.",
                stopwatch.Elapsed
            );
        }
        catch (Exception ex)
        {
            return new CudaBenchmarkResult(
                false,
                $"CUDA benchmark failed: {ex.Message}",
                stopwatch.Elapsed
            );
        }
        finally
        {
            if (process is not null)
            {
                // Disposing a Process does not stop the child, so every early exit —
                // cancellation, the 3 s timeout, an I/O failure — would orphan nvidia-smi.
                TryKillProcessTree(process);
                process.Dispose();
            }
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
            /* best effort */
        }
    }

    public static bool IsCommandAvailable(string commandName)
    {
        return ExecutablePathResolver.Find(commandName) is not null;
    }

    /// <summary>
    ///     Test seam: replaces the cached snapshot and raises <see cref="SnapshotChanged" />
    ///     without relying on ydotool/wtype binaries present on the test host.
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

    /// <summary>
    ///     Finds the ydotool socket path using the standard priority list.
    ///     Returns null if no candidate accepts a bounded datagram connection.
    /// </summary>
    internal static string? ResolveYdotoolSocketPath()
    {
        return ResolveYdotoolSocketPath(new ProcessRunner());
    }

    private static string? ResolveYdotoolSocketPath(IProcessRunner processRunner)
    {
        var candidates = new List<string?> { Environment.GetEnvironmentVariable("YDOTOOL_SOCKET") };

        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(runtimeDir))
        {
            candidates.Add(Path.Join(runtimeDir, ".ydotool_socket"));
        }

        candidates.Add("/tmp/.ydotool_socket");

        var uid = TryReadUserId(processRunner);
        if (uid is not null)
        {
            candidates.Add($"/run/user/{uid}/.ydotool_socket");
        }

        return ResolveYdotoolSocketPath(candidates);
    }

    internal static string? ResolveYdotoolSocketPath(IEnumerable<string?> candidates)
    {
        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator -- the explicit whitespace guard keeps socket-path resolution linear; the partial LINQ form only hoists this one guard while the try/catch + early-return stay in the body
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                using var socket = new Socket(
                    AddressFamily.Unix,
                    SocketType.Dgram,
                    ProtocolType.Unspecified
                );
                using var timeout = new CancellationTokenSource(
                    s_ydotoolSocketConnectTimeout
                );
                socket
                    .ConnectAsync(new UnixDomainSocketEndPoint(candidate), timeout.Token)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                return candidate;
            }
            catch
            {
                // Missing, stale, inaccessible, or non-datagram endpoint — skip it.
            }
        }

        return null;
    }

    /// <summary>
    ///     Fired after <see cref="RefreshSnapshot" /> rebuilds the snapshot so
    ///     subscribers (e.g. the insertion platform) can pick up new tools without
    ///     an app restart. Handlers must not throw.
    /// </summary>
    public event EventHandler<LinuxCapabilitySnapshot>? SnapshotChanged;

    private LinuxCapabilitySnapshot BuildSnapshot()
    {
        var isWayland = WaylandSessionDetector.IsWaylandSession();
        var isX11 = Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 };
        var hasXclip = IsCommandAvailable("xclip");
        var hasWlClipboard = IsCommandAvailable("wl-copy") && IsCommandAvailable("wl-paste");
        var speechCommand = ResolveSpeechFeedbackCommand();

        var hasPactl = IsCommandAvailable("pactl");
        var hasPlayerCtl = IsCommandAvailable("playerctl");
        // Plays bundled WAVs via any PCM player rather than libcanberra/XDG events —
        // works regardless of the desktop sound theme or "System Sounds" toggle.
        var hasAudioPlayer = PcmPlayerResolver.Resolve() is not null;
        var hasYdotool = IsCommandAvailable("ydotool");
        var ydotoolSocket = ResolveYdotoolSocketPath(_processRunner);

        return new LinuxCapabilitySnapshot(
            isWayland ? "Wayland"
            : isX11 ? "X11"
            : "Unknown",
            isWayland ? hasWlClipboard : hasXclip,
            isWayland ? "wl-clipboard" : "xclip",
            IsCommandAvailable("xdotool"),
            IsCommandAvailable("wtype"),
            IsCommandAvailable("ffmpeg"),
            speechCommand is not null,
            speechCommand,
            hasPactl,
            hasPlayerCtl,
            hasAudioPlayer,
            IsCommandAvailable("nvidia-smi") || File.Exists("/dev/nvidiactl"),
            AreCuda12LibrariesVisible(_processRunner)
            || FindCuda12RuntimeDirectory() is not null,
            DesktopDetector.DetectId(),
            hasYdotool,
            ydotoolSocket is not null,
            ydotoolSocket
        );
    }

    private static string? TryReadUserId(IProcessRunner processRunner)
    {
        try
        {
            var result = processRunner.RunProbe(
                new ProcessCommand("id", ["-u"]),
                new ProcessOneShotOptions(
                    Timeout: TimeSpan.FromMilliseconds(500),
                    StandardError: ProcessCaptureMode.Discard
                )
            );
            if (!result.Succeeded)
            {
                return null;
            }

            var output = result.StandardOutputText.Trim();
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveSpeechFeedbackCommand()
    {
        if (IsCommandAvailable("espeak-ng"))
        {
            return "espeak-ng";
        }

        if (IsCommandAvailable("espeak"))
        {
            return "espeak";
        }

        return IsCommandAvailable("spd-say") ? "spd-say" : null;
    }

    // One `ldconfig -p` for the pair: probing each library separately spawned the process
    // twice on every snapshot build, and startup pays for that.
    private static bool AreCuda12LibrariesVisible(IProcessRunner processRunner)
    {
        var ldCache = ReadLdCache(processRunner);
        return IsLibraryAvailable("libcudart.so.12", ldCache)
               && IsLibraryAvailable("libcublas.so.12", ldCache);
    }

    private static bool IsLibraryAvailable(string libraryName, string ldCache)
    {
        if (ldCache.Contains(libraryName, StringComparison.Ordinal))
        {
            return true;
        }

        if (FindInEnvironmentLibraryPath(libraryName))
        {
            return true;
        }

        foreach (
            var directory in new[] { "/usr/lib64", "/lib64", "/usr/lib/x86_64-linux-gnu", "/lib/x86_64-linux-gnu" }
        )
        {
            try
            {
                if (File.Exists(Path.Join(directory, libraryName)))
                {
                    return true;
                }
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
        {
            return false;
        }

        foreach (
            var directory in value.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            try
            {
                if (File.Exists(Path.Join(directory, libraryName)))
                {
                    return true;
                }
            }
            catch
            {
                // Ignore invalid entries.
            }
        }

        return false;
    }

    /// <summary>The <c>ldconfig -p</c> listing, or empty when it can't be read.</summary>
    private static string ReadLdCache(IProcessRunner processRunner)
    {
        try
        {
            var result = processRunner.RunProbe(
                new ProcessCommand("ldconfig", ["-p"]),
                new ProcessOneShotOptions(
                    Timeout: TimeSpan.FromSeconds(1),
                    StandardError: ProcessCaptureMode.Discard
                )
            );
            return result.Succeeded ? result.StandardOutputText : "";
        }
        catch
        {
            return "";
        }
    }

    // Linux marshals "ANSI" strings as UTF-8, so StringMarshalling.Utf8 matches the prior CharSet.Ansi/LPStr behavior for ASCII library paths.
    // ReSharper disable once InconsistentNaming -- native libdl function name; LibraryImport EntryPoint defaults to the method name.
    [LibraryImport("libdl.so.2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr dlopen(string fileName, int flags);

    [LibraryImport("libdl.so.2")]
    private static partial IntPtr dlerror();
}

// Success/Elapsed are carried in the benchmark result record's data shape (not currently read).
// ReSharper disable NotAccessedPositionalProperty.Global
public sealed record CudaBenchmarkResult(bool Success, string Message, TimeSpan? Elapsed);
// ReSharper restore NotAccessedPositionalProperty.Global

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
    bool HasAudioPlayer,
    bool HasCudaGpu,
    bool HasCudaRuntimeLibraries,
    string Compositor = "unknown",
    bool HasYdotool = false,
    bool HasYdotoolSocket = false,
    string? YdotoolSocketPath = null
)
{
    public bool CanUseCuda => HasCudaGpu && HasCudaRuntimeLibraries;

    /// <summary>
    ///     True when the compositor is unlikely to implement wtype's
    ///     <c>zwp_virtual_keyboard_v1</c> (GNOME Mutter and KDE KWin both omit it),
    ///     so the insertion chain demotes wtype below ydotool.
    /// </summary>
    public bool CompositorRejectsWtype =>
        SessionType == "Wayland" && Compositor is "gnome" or "kde";

    public bool HasYdotoolAvailable => HasYdotool && HasYdotoolSocket;

    public string ClipboardStatus =>
        HasClipboardTool
            ? Localization.Loc.Instance.GetString("TextInsertion.ClipboardAvailable", ClipboardToolName)
            : Localization.Loc.Instance.GetString("TextInsertion.ClipboardInstallHint", ClipboardToolName);

    public string PasteStatus =>
        SessionType == "Wayland"
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

    public string PasteToolInstallHint =>
        SessionType == "Wayland"
            ? CompositorRejectsWtype
                ? "Set up ydotool to enable automatic paste on GNOME / KDE Wayland."
                : "Install wtype or ydotool to enable automatic paste."
            : "Install xdotool to enable automatic paste.";

    public string CudaStatus =>
        CanUseCuda ? Localization.Loc.Instance["Dictation.CudaStatusAvailable"]
        : HasCudaGpu ? Localization.Loc.Instance["Dictation.CudaStatusRuntimeMissing"]
        : Localization.Loc.Instance["Dictation.CudaStatusNoGpu"];
}
