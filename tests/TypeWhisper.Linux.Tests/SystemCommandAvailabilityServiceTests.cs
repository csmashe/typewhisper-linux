using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SystemCommandAvailabilityServiceTests
{
    [Fact]
    public void LinuxCapabilitySnapshot_CanAutoPasteRequiresClipboardAndPasteTools()
    {
        var snapshot = new LinuxCapabilitySnapshot(
            SessionType: "X11",
            HasClipboardTool: true,
            ClipboardToolName: "xclip",
            HasXdotool: false,
            HasWtype: false,
            HasFfmpeg: true,
            HasSpeechFeedback: false,
            SpeechFeedbackCommand: null,
            HasPactl: false,
            HasPlayerCtl: false,
            HasCanberraGtkPlay: false,
            HasCudaGpu: false,
            HasCudaRuntimeLibraries: false);

        Assert.False(snapshot.CanAutoPaste);
        Assert.Equal("xclip available", snapshot.ClipboardStatus);
        Assert.Equal("Install xdotool to enable automatic paste.", snapshot.PasteStatus);
    }

    [Theory]
    [InlineData(true, true, true, "CUDA available")]
    [InlineData(true, false, false, "NVIDIA GPU detected, but CUDA 12 runtime libraries are missing.")]
    [InlineData(false, false, false, "No NVIDIA GPU/driver detected.")]
    public void LinuxCapabilitySnapshot_ReportsCudaStatus(
        bool hasGpu,
        bool hasRuntime,
        bool expectedCanUseCuda,
        string expectedStatus)
    {
        var snapshot = new LinuxCapabilitySnapshot(
            SessionType: "X11",
            HasClipboardTool: true,
            ClipboardToolName: "xclip",
            HasXdotool: true,
            HasWtype: false,
            HasFfmpeg: true,
            HasSpeechFeedback: true,
            SpeechFeedbackCommand: "espeak-ng",
            HasPactl: true,
            HasPlayerCtl: true,
            HasCanberraGtkPlay: true,
            HasCudaGpu: hasGpu,
            HasCudaRuntimeLibraries: hasRuntime);

        Assert.Equal(expectedCanUseCuda, snapshot.CanUseCuda);
        Assert.Equal(expectedStatus, snapshot.CudaStatus);
    }

    [Theory]
    [InlineData("X11", "Install xdotool to enable automatic paste.")]
    [InlineData("Wayland", "Install wtype (or xdotool for XWayland apps) to enable automatic paste.")]
    public void LinuxCapabilitySnapshot_PasteToolInstallHintIsSessionAware(
        string sessionType,
        string expectedHint)
    {
        var snapshot = new LinuxCapabilitySnapshot(
            SessionType: sessionType,
            HasClipboardTool: false,
            ClipboardToolName: "xclip",
            HasXdotool: false,
            HasWtype: false,
            HasFfmpeg: false,
            HasSpeechFeedback: false,
            SpeechFeedbackCommand: null,
            HasPactl: false,
            HasPlayerCtl: false,
            HasCanberraGtkPlay: false,
            HasCudaGpu: false,
            HasCudaRuntimeLibraries: false);

        Assert.Equal(expectedHint, snapshot.PasteToolInstallHint);
    }

    [Fact]
    public void LinuxCapabilitySnapshot_WaylandWithWtypeReportsAvailable()
    {
        var snapshot = new LinuxCapabilitySnapshot(
            SessionType: "Wayland",
            HasClipboardTool: true,
            ClipboardToolName: "wl-clipboard",
            HasXdotool: false,
            HasWtype: true,
            HasFfmpeg: false,
            HasSpeechFeedback: false,
            SpeechFeedbackCommand: null,
            HasPactl: false,
            HasPlayerCtl: false,
            HasCanberraGtkPlay: false,
            HasCudaGpu: false,
            HasCudaRuntimeLibraries: false);

        Assert.True(snapshot.HasAutomaticPasteTool);
        Assert.Equal("wtype available", snapshot.PasteStatus);
    }

    [Fact]
    public void LinuxCapabilitySnapshot_WaylandXdotoolOnlyReportsXWayland()
    {
        var snapshot = new LinuxCapabilitySnapshot(
            SessionType: "Wayland",
            HasClipboardTool: true,
            ClipboardToolName: "wl-clipboard",
            HasXdotool: true,
            HasWtype: false,
            HasFfmpeg: false,
            HasSpeechFeedback: false,
            SpeechFeedbackCommand: null,
            HasPactl: false,
            HasPlayerCtl: false,
            HasCanberraGtkPlay: false,
            HasCudaGpu: false,
            HasCudaRuntimeLibraries: false);

        Assert.True(snapshot.HasAutomaticPasteTool);
        Assert.Equal("xdotool available (XWayland only)", snapshot.PasteStatus);
    }
}
