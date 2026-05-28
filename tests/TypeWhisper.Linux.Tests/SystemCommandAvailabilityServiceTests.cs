using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SystemCommandAvailabilityServiceTests
{
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
    [InlineData("Wayland", "Install wtype (or ydotool / xdotool) to enable automatic paste.")]
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