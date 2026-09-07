using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>
///     Coverage for <see cref="DictationOrchestrator.ClassifyThrownInsertionFailure" />
///     (audit §2 M6): a thrown insertion/action exception must classify to a recordable
///     InsertionResult (Failed/ActionFailed) so Recents/History still record the
///     dictation instead of silently dropping it.
/// </summary>
public sealed class DictationOrchestratorInsertionFailureRecordingTests
{
    [Fact]
    public void ClassifyThrownInsertionFailure_PlainInsertion_ReturnsFailed()
    {
        Assert.Equal(
            InsertionResult.Failed,
            DictationOrchestrator.ClassifyThrownInsertionFailure(viaActionPlugin: false)
        );
    }

    [Fact]
    public void ClassifyThrownInsertionFailure_ActionPlugin_ReturnsActionFailed()
    {
        Assert.Equal(
            InsertionResult.ActionFailed,
            DictationOrchestrator.ClassifyThrownInsertionFailure(viaActionPlugin: true)
        );
    }
}
