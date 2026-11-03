using System.Net.Sockets;
using TypeWhisper.Linux.Services;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SystemCommandAvailabilityServiceTests
{
    [Fact]
    public void Snapshot_XdgWaylandWithoutWaylandDisplay_ReportsWaylandButSelectsXclip()
    {
        var originalWaylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        var originalSessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");

        try
        {
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", "wayland");

            var service = new SystemCommandAvailabilityService(new FakeProcessRunner());
            var snapshot = service.GetSnapshot();

            Assert.Equal("Wayland", snapshot.SessionType);
            Assert.True(service.IsWaylandSession);
            Assert.False(service.IsX11Session);
            Assert.Equal("xclip", snapshot.ClipboardToolName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", originalWaylandDisplay);
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", originalSessionType);
        }
    }

    [Fact]
    public void IsCommandAvailable_RequiresExecutePermissionAndContinuesSearchingPath()
    {
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var tempDirectory = TestPaths.CreateTempDirectory("command-availability");
        var firstDirectory = Path.Join(tempDirectory, "first");
        var secondDirectory = Path.Join(tempDirectory, "second");
        var firstCandidate = Path.Join(firstDirectory, "fake-command");
        var secondCandidate = Path.Join(secondDirectory, "fake-command");

        try
        {
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            File.WriteAllText(firstCandidate, "not executed");
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
            File.SetUnixFileMode(
                firstCandidate,
                UnixFileMode.UserRead | UnixFileMode.UserWrite
            );
#pragma warning restore CA1416
            Environment.SetEnvironmentVariable("PATH", firstDirectory);

            Assert.False(
                SystemCommandAvailabilityService.IsCommandAvailable("fake-command")
            );

#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
            File.SetUnixFileMode(
                firstCandidate,
                UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute
            );
#pragma warning restore CA1416

            Assert.True(
                SystemCommandAvailabilityService.IsCommandAvailable("fake-command")
            );

#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
            File.SetUnixFileMode(
                firstCandidate,
                UnixFileMode.UserRead | UnixFileMode.UserWrite
            );
#pragma warning restore CA1416
            File.WriteAllText(secondCandidate, "also not executed");
#pragma warning disable CA1416 // TypeWhisper.Linux is a Linux-only assembly.
            File.SetUnixFileMode(
                secondCandidate,
                UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.GroupExecute
            );
#pragma warning restore CA1416
            Environment.SetEnvironmentVariable(
                "PATH",
                string.Join(Path.PathSeparator, firstDirectory, secondDirectory)
            );

            Assert.True(
                SystemCommandAvailabilityService.IsCommandAvailable("fake-command")
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            TestPaths.DeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public void ResolveYdotoolSocketPath_DeadDatagramSocketIsUnavailable()
    {
        var tempDirectory = TestPaths.CreateTempDirectory("ydotool-dead");
        var boundSocketPath = Path.Join(tempDirectory, "bound.sock");
        var socketPath = Path.Join(tempDirectory, "ydotool.sock");

        try
        {
            CreateStaleDatagramSocket(boundSocketPath, socketPath);

            Assert.Contains(
                socketPath,
                Directory.EnumerateFileSystemEntries(tempDirectory)
            );

            var resolved = SystemCommandAvailabilityService.ResolveYdotoolSocketPath(
                [socketPath]
            );

            Assert.Null(resolved);
            Assert.Contains(
                socketPath,
                Directory.EnumerateFileSystemEntries(tempDirectory)
            );
        }
        finally
        {
            TestPaths.DeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public void ResolveYdotoolSocketPath_SkipsDeadCandidateAndReturnsLiveDatagramSocket()
    {
        var tempDirectory = TestPaths.CreateTempDirectory("ydotool-live");
        var boundSocketPath = Path.Join(tempDirectory, "bound.sock");
        var deadSocketPath = Path.Join(tempDirectory, "dead.sock");
        var liveSocketPath = Path.Join(tempDirectory, "live.sock");

        try
        {
            CreateStaleDatagramSocket(boundSocketPath, deadSocketPath);

            using var liveSocket = new Socket(
                AddressFamily.Unix,
                SocketType.Dgram,
                ProtocolType.Unspecified
            );
            liveSocket.Bind(new UnixDomainSocketEndPoint(liveSocketPath));

            var resolved = SystemCommandAvailabilityService.ResolveYdotoolSocketPath(
                [deadSocketPath, liveSocketPath]
            );

            Assert.Equal(liveSocketPath, resolved);
        }
        finally
        {
            TestPaths.DeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public void TryPreloadCuda12RuntimeLibraries_PartialLoadRemainsIncompleteAndRetriesMissingLibrary()
    {
        const string directory = "/fake/cuda";
        var cudartPath = Path.Join(directory, "libcudart.so.12");
        var cublasPath = Path.Join(directory, "libcublas.so.12");
        var loadedHandles = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
        var calls = new List<string>();
        var cublasAttempts = 0;

        // ReSharper disable once MoveLocalFunctionAfterJumpStatement -- kept adjacent to the call sites and captured state below.
        (IntPtr Handle, string? Error) LoadLibrary(string path)
        {
            calls.Add(path);
            if (path == cudartPath)
            {
                return (new IntPtr(1), null);
            }

            Assert.Equal(cublasPath, path);
            cublasAttempts++;
            return cublasAttempts == 1
                ? (IntPtr.Zero, "simulated cublas failure")
                : (new IntPtr(2), null);
        }

        var firstResult =
            SystemCommandAvailabilityService.TryPreloadCuda12RuntimeLibrariesFromDirectory(
                directory,
                loadedHandles,
                LoadLibrary,
                out var firstMessage
            );

        Assert.False(firstResult);
        Assert.Equal(
            "Could not load libcublas.so.12 from /fake/cuda: simulated cublas failure",
            firstMessage
        );
        Assert.Single(loadedHandles);
        Assert.Equal(new IntPtr(1), loadedHandles["libcudart.so.12"]);
        Assert.False(loadedHandles.ContainsKey("libcublas.so.12"));

        var secondResult =
            SystemCommandAvailabilityService.TryPreloadCuda12RuntimeLibrariesFromDirectory(
                directory,
                loadedHandles,
                LoadLibrary,
                out var secondMessage
            );

        Assert.True(secondResult);
        Assert.Equal("CUDA 12 runtime libraries were loaded from /fake/cuda.", secondMessage);
        Assert.Equal(2, loadedHandles.Count);
        Assert.Equal(new IntPtr(2), loadedHandles["libcublas.so.12"]);
        Assert.Equal(1, calls.Count(path => path == cudartPath));
        Assert.Equal(2, calls.Count(path => path == cublasPath));
    }

    [Fact]
    public void TryPreloadCuda12RuntimeLibraries_CompleteLoadIsCached()
    {
        const string directory = "/fake/cuda";
        var cudartPath = Path.Join(directory, "libcudart.so.12");
        var cublasPath = Path.Join(directory, "libcublas.so.12");
        var loadedHandles = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
        var calls = new List<string>();

        // ReSharper disable once MoveLocalFunctionAfterJumpStatement -- kept adjacent to the call sites and captured state below.
        (IntPtr Handle, string? Error) LoadLibrary(string path)
        {
            calls.Add(path);
            if (path == cudartPath)
            {
                return (new IntPtr(1), null);
            }

            Assert.Equal(cublasPath, path);
            return (new IntPtr(2), null);
        }

        var firstResult =
            SystemCommandAvailabilityService.TryPreloadCuda12RuntimeLibrariesFromDirectory(
                directory,
                loadedHandles,
                LoadLibrary,
                out var firstMessage
            );

        Assert.True(firstResult);
        Assert.Equal("CUDA 12 runtime libraries were loaded from /fake/cuda.", firstMessage);
        Assert.Equal(new[] { cudartPath, cublasPath }, calls);
        Assert.Equal(new IntPtr(1), loadedHandles["libcudart.so.12"]);
        Assert.Equal(new IntPtr(2), loadedHandles["libcublas.so.12"]);

        var secondResult =
            SystemCommandAvailabilityService.TryPreloadCuda12RuntimeLibrariesFromDirectory(
                directory,
                loadedHandles,
                LoadLibrary,
                out var secondMessage
            );

        Assert.True(secondResult);
        Assert.Equal(
            "CUDA 12 runtime libraries were preloaded from /fake/cuda.",
            secondMessage
        );
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void TryPreloadCuda12RuntimeLibraries_FailedLoadReturnsFalseAndReportsError()
    {
        const string directory = "/fake/cuda";
        var cudartPath = Path.Join(directory, "libcudart.so.12");
        var loadedHandles = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
        var calls = new List<string>();

        // ReSharper disable once MoveLocalFunctionAfterJumpStatement -- kept adjacent to the call sites and captured state below.
        (IntPtr Handle, string? Error) LoadLibrary(string path)
        {
            calls.Add(path);
            return (IntPtr.Zero, "simulated dlopen error");
        }

        var result =
            SystemCommandAvailabilityService.TryPreloadCuda12RuntimeLibrariesFromDirectory(
                directory,
                loadedHandles,
                LoadLibrary,
                out var message
            );

        Assert.False(result);
        Assert.Empty(loadedHandles);
        Assert.Equal(
            "Could not load libcudart.so.12 from /fake/cuda: simulated dlopen error",
            message
        );
        Assert.Equal(new[] { cudartPath }, calls);
    }

    [Fact]
    public void LinuxCapabilitySnapshot_X11WithoutPasteToolReportsInstallStatus()
    {
        var snapshot = new LinuxCapabilitySnapshot(
            "X11",
            true,
            "xclip",
            false,
            false,
            true,
            false,
            null,
            false,
            false,
            false,
            false,
            false
        );

        Assert.Equal("xclip available", snapshot.ClipboardStatus);
        Assert.Equal("Install xdotool to enable automatic paste.", snapshot.PasteStatus);
    }

    [Theory]
    [InlineData(true, true, true, "CUDA available")]
    [InlineData(
        true,
        false,
        false,
        "NVIDIA GPU detected, but CUDA 12 runtime libraries are missing."
    )]
    [InlineData(false, false, false, "No NVIDIA GPU/driver detected.")]
    public void LinuxCapabilitySnapshot_ReportsCudaStatus(
        bool hasGpu,
        bool hasRuntime,
        bool expectedCanUseCuda,
        string expectedStatus
    )
    {
        var snapshot = new LinuxCapabilitySnapshot(
            "X11",
            true,
            "xclip",
            true,
            false,
            true,
            true,
            "espeak-ng",
            true,
            true,
            true,
            hasGpu,
            hasRuntime
        );

        Assert.Equal(expectedCanUseCuda, snapshot.CanUseCuda);
        Assert.Equal(expectedStatus, snapshot.CudaStatus);
    }

    [Theory]
    [InlineData("X11", "Install xdotool to enable automatic paste.")]
    [InlineData("Wayland", "Install wtype or ydotool to enable automatic paste.")]
    public void LinuxCapabilitySnapshot_PasteToolInstallHintIsSessionAware(
        string sessionType,
        string expectedHint
    )
    {
        var snapshot = new LinuxCapabilitySnapshot(
            sessionType,
            false,
            "xclip",
            false,
            false,
            false,
            false,
            null,
            false,
            false,
            false,
            false,
            false
        );

        Assert.Equal(expectedHint, snapshot.PasteToolInstallHint);
    }

    [Fact]
    public void LinuxCapabilitySnapshot_WaylandWithWtypeReportsAvailable()
    {
        var snapshot = new LinuxCapabilitySnapshot(
            "Wayland",
            true,
            "wl-clipboard",
            false,
            true,
            false,
            false,
            null,
            false,
            false,
            false,
            false,
            false
        );

        Assert.Equal("wtype available", snapshot.PasteStatus);
    }

    [Fact]
    public void LinuxCapabilitySnapshot_WaylandXdotoolOnlyReportsXWayland()
    {
        var snapshot = new LinuxCapabilitySnapshot(
            "Wayland",
            true,
            "wl-clipboard",
            true,
            false,
            false,
            false,
            null,
            false,
            false,
            false,
            false,
            false
        );

        Assert.Equal("xdotool available (XWayland only)", snapshot.PasteStatus);
    }

    private static void CreateStaleDatagramSocket(string boundPath, string stalePath)
    {
        using var socket = new Socket(
            AddressFamily.Unix,
            SocketType.Dgram,
            ProtocolType.Unspecified
        );
        socket.Bind(new UnixDomainSocketEndPoint(boundPath));

        // .NET removes the original bound path on dispose. Renaming the live inode
        // first leaves a real, closed datagram endpoint at the test's stale path.
        File.Move(boundPath, stalePath);
    }
}
