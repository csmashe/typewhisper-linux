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
            HasAutomaticPasteTool: false,
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
            HasAutomaticPasteTool: true,
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
}
