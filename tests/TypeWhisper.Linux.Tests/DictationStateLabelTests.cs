using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
/// Tests for <see cref="DictationOrchestrator.MapOverlayStatusToStateLabel"/>,
/// the pure projection from overlay StatusText to the documented
/// <c>typewhisper status</c> state labels. The <c>recording</c> branch is
/// sourced from the live audio recorder, not StatusText, so it is verified
/// manually rather than here.
/// </summary>
public sealed class DictationStateLabelTests
{
    [Fact]
    public void NullStatus_MapsToIdle()
        => Assert.Equal("idle", DictationOrchestrator.MapOverlayStatusToStateLabel(null));

    [Theory]
    [InlineData("Processing…")]
    [InlineData("Transcribing via Whisper…")]
    public void TranscriptionStatuses_MapToTranscribing(string status)
        => Assert.Equal("transcribing", DictationOrchestrator.MapOverlayStatusToStateLabel(status));

    [Fact]
    public void InsertingStatus_MapsToInjecting()
        => Assert.Equal("injecting", DictationOrchestrator.MapOverlayStatusToStateLabel("Inserting…"));

    [Theory]
    [InlineData("Typed 12 char(s).")]
    [InlineData("Pasted…")]
    [InlineData("Copied to clipboard…")]
    [InlineData("Insertion failed: boom")]
    [InlineData("Ready")]
    public void CompletionAndTerminalStatuses_MapToIdle(string status)
        => Assert.Equal("idle", DictationOrchestrator.MapOverlayStatusToStateLabel(status));
}
