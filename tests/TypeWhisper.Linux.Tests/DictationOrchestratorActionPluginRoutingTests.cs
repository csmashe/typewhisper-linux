using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Coverage for <see cref="DictationOrchestrator.IsActionPluginTargetUnavailable" />
///     (audit §2 M2): an explicitly configured but unresolved target plugin must fail
///     with a routing error, not silently fall back to plain text insertion.
/// </summary>
public sealed class DictationOrchestratorActionPluginRoutingTests
{
    [Fact]
    public void ReturnsFalse_WhenNoTargetConfigured()
    {
        Assert.False(
            DictationOrchestrator.IsActionPluginTargetUnavailable(null, actionPluginResolved: false)
        );
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnsFalse_WhenTargetIsBlank(string targetActionPluginId)
    {
        Assert.False(
            DictationOrchestrator.IsActionPluginTargetUnavailable(
                targetActionPluginId,
                actionPluginResolved: false
            )
        );
    }

    [Fact]
    public void ReturnsFalse_WhenTargetConfiguredAndResolved()
    {
        Assert.False(
            DictationOrchestrator.IsActionPluginTargetUnavailable(
                "com.typewhisper.linear",
                actionPluginResolved: true
            )
        );
    }

    [Fact]
    public void ReturnsTrue_WhenTargetConfiguredButNotResolved()
    {
        Assert.True(
            DictationOrchestrator.IsActionPluginTargetUnavailable(
                "com.typewhisper.linear",
                actionPluginResolved: false
            )
        );
    }
}
