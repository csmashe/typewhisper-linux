using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Tests for <see cref="DictationOrchestrator.MapOverlayPhaseToStateLabel" />,
///     the pure projection from the overlay phase to the documented
///     <c>typewhisper status</c> state labels. The <c>recording</c> branch is
///     sourced from the live audio recorder, not the overlay, so it is verified
///     manually rather than here.
/// </summary>
public sealed class DictationStateLabelTests
{
    [Theory]
    [InlineData(DictationOverlayPhase.Idle, "idle")]
    [InlineData(DictationOverlayPhase.Transcribing, "transcribing")]
    [InlineData(DictationOverlayPhase.Injecting, "injecting")]
    public void Phase_MapsToDocumentedStateLabel(DictationOverlayPhase phase, string expected)
    {
        Assert.Equal(expected, DictationOrchestrator.MapOverlayPhaseToStateLabel(phase));
    }

    [Fact]
    public void DefaultOverlayState_IsIdle()
    {
        Assert.Equal(DictationOverlayPhase.Idle, DictationOverlayState.Hidden.Phase);
        Assert.Equal("idle", DictationOrchestrator.MapOverlayPhaseToStateLabel(new DictationOverlayState().Phase));
    }
}
