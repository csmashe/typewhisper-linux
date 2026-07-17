using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationInsertionOrderGateTests
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void WaitForTurnAsync_CompletesImmediately_ForOldestReservation()
    {
        var gate = new DictationInsertionOrderGate();
        gate.Reserve(1);

        var wait = gate.WaitForTurnAsync(1, CancellationToken.None);

        Assert.True(wait.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitForTurnAsync_BlocksSuccessor_UntilPredecessorReleases()
    {
        var gate = new DictationInsertionOrderGate();
        gate.Reserve(1);
        gate.Reserve(2);

        var successorWait = gate.WaitForTurnAsync(2, CancellationToken.None);

        Assert.False(successorWait.IsCompleted);

        gate.Release(1);

        await successorWait.WaitAsync(s_testTimeout);
        Assert.True(successorWait.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitForTurnAsync_ReleasesThreeDeepQueue_InSessionOrder()
    {
        var gate = new DictationInsertionOrderGate();
        gate.Reserve(1);
        gate.Reserve(2);
        gate.Reserve(3);

        var secondWait = gate.WaitForTurnAsync(2, CancellationToken.None);
        var thirdWait = gate.WaitForTurnAsync(3, CancellationToken.None);

        Assert.False(secondWait.IsCompleted);
        Assert.False(thirdWait.IsCompleted);

        gate.Release(1);

        await secondWait.WaitAsync(s_testTimeout);
        Assert.True(secondWait.IsCompletedSuccessfully);
        Assert.False(thirdWait.IsCompleted);

        gate.Release(2);

        await thirdWait.WaitAsync(s_testTimeout);
        Assert.True(thirdWait.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Release_OfNonHeadSession_RemovesItsSlotSoSuccessorsAdvance()
    {
        var gate = new DictationInsertionOrderGate();
        gate.Reserve(1);
        gate.Reserve(2);
        gate.Reserve(3);

        var thirdWait = gate.WaitForTurnAsync(3, CancellationToken.None);

        Assert.False(thirdWait.IsCompleted);

        gate.Release(2);

        Assert.False(thirdWait.IsCompleted);

        gate.Release(1);

        await thirdWait.WaitAsync(s_testTimeout);
        Assert.True(thirdWait.IsCompletedSuccessfully);
    }

    [Fact]
    public void Release_IsIdempotent()
    {
        var gate = new DictationInsertionOrderGate();
        gate.Reserve(1);

        gate.Release(1);
        gate.Release(1);
        gate.Reserve(2);

        var wait = gate.WaitForTurnAsync(2, CancellationToken.None);

        Assert.True(wait.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitForTurnAsync_Cancels_WhenCancellationIsRequested()
    {
        var gate = new DictationInsertionOrderGate();
        gate.Reserve(1);
        gate.Reserve(2);
        using var cts = new CancellationTokenSource();

        var successorWait = gate.WaitForTurnAsync(2, cts.Token);

        Assert.False(successorWait.IsCompleted);

        await cts.CancelAsync();

        // ReSharper disable once MethodSupportsCancellation -- WaitAsync here is only a hang-guard; passing cts.Token would satisfy the OperationCanceledException assertion via the guard's own cancellation instead of the wait-under-test.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            successorWait.WaitAsync(s_testTimeout)
        );
        Assert.True(successorWait.IsCanceled);
        Assert.False(successorWait.IsFaulted);
    }

    [Fact]
    public async Task WaitForTurnAsync_FailsOpen_WhenMaximumWaitExpires()
    {
        var gate = new DictationInsertionOrderGate(TimeSpan.FromMilliseconds(50));
        gate.Reserve(1);
        gate.Reserve(2);

        var successorWait = gate.WaitForTurnAsync(2, CancellationToken.None);

        Assert.False(successorWait.IsCompleted);

        await successorWait.WaitAsync(s_testTimeout);
        Assert.True(successorWait.IsCompletedSuccessfully);
    }

    [Fact]
    public void WaitForTurnAsync_CompletesImmediately_WhenNothingIsReserved()
    {
        var gate = new DictationInsertionOrderGate();

        var wait = gate.WaitForTurnAsync(42, CancellationToken.None);

        Assert.True(wait.IsCompletedSuccessfully);
    }
}
