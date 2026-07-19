using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SystemCommandAvailabilityServiceTests
{
    [Fact]
    public void TryPreloadCuda12RuntimeLibraries_PartialLoadRemainsIncompleteAndRetriesMissingLibrary()
    {
        const string directory = "/fake/cuda";
        var cudartPath = Path.Join(directory, "libcudart.so.12");
        var cublasPath = Path.Join(directory, "libcublas.so.12");
        var loadedHandles = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
        var calls = new List<string>();
        var cublasAttempts = 0;

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
    public void LinuxCapabilitySnapshot_CanAutoPasteRequiresClipboardAndPasteTools()
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

        Assert.False(snapshot.CanAutoPaste);
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

        Assert.True(snapshot.HasAutomaticPasteTool);
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

        Assert.True(snapshot.HasAutomaticPasteTool);
        Assert.Equal("xdotool available (XWayland only)", snapshot.PasteStatus);
    }
}
