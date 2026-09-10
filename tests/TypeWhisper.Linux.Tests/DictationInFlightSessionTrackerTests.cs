using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationInFlightSessionTrackerTests
{
    [Fact]
    public void Contains_ReturnsFalse_ForUnknownSession()
    {
        var tracker = new DictationInFlightSessionTracker();

        Assert.False(tracker.Contains(1));
    }

    [Fact]
    public void Begin_MarksSessionAsInFlight()
    {
        var tracker = new DictationInFlightSessionTracker();

        tracker.Begin(1);

        Assert.True(tracker.Contains(1));
    }

    [Fact]
    public void End_ClearsSession()
    {
        var tracker = new DictationInFlightSessionTracker();
        tracker.Begin(1);

        tracker.End(1);

        Assert.False(tracker.Contains(1));
    }

    [Fact]
    public void End_IsIdempotent_ForUntrackedSession()
    {
        var tracker = new DictationInFlightSessionTracker();

        tracker.End(1);

        Assert.False(tracker.Contains(1));
    }

    [Fact]
    public async Task RunAsync_ClearsSession_OnSuccessfulCompletion()
    {
        var tracker = new DictationInFlightSessionTracker();
        tracker.Begin(1);

        await tracker.RunAsync(1, () => Task.CompletedTask);

        Assert.False(tracker.Contains(1));
    }

    [Fact]
    public async Task RunAsync_ClearsSession_WhenAnEarlyStepThrows_NotJustTheLastStep()
    {
        var tracker = new DictationInFlightSessionTracker();
        tracker.Begin(1);
        var reachedFinalStep = false;

        await Assert.ThrowsAsync<IOException>(() =>
            tracker.RunAsync(1, async () =>
            {
                await Task.Yield();
                ThrowCaptureSaveFailure();
                reachedFinalStep = true;
            }));

        Assert.False(reachedFinalStep);
        Assert.False(tracker.Contains(1));
    }

    [Fact]
    public async Task RunAsync_PropagatesException_ToCaller()
    {
        var tracker = new DictationInFlightSessionTracker();
        tracker.Begin(1);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tracker.RunAsync(1, () => throw new InvalidOperationException("pipeline failed")));

        Assert.Equal("pipeline failed", exception.Message);
    }

    [Fact]
    public async Task RunAsync_ClearsSession_WhenPipelineIsCanceled()
    {
        var tracker = new DictationInFlightSessionTracker();
        tracker.Begin(1);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            tracker.RunAsync(1, () => throw new OperationCanceledException()));

        Assert.False(tracker.Contains(1));
    }

    [Fact]
    public async Task RunAsync_TracksConcurrentSessionsIndependently()
    {
        var tracker = new DictationInFlightSessionTracker();
        tracker.Begin(1);
        tracker.Begin(2);

        var failedPipeline = Assert.ThrowsAsync<IOException>(() =>
            tracker.RunAsync(1, () => throw new IOException("disk full")));
        var successfulPipeline = tracker.RunAsync(2, () => Task.CompletedTask);

        await Task.WhenAll(failedPipeline, successfulPipeline);

        Assert.False(tracker.Contains(1));
        Assert.False(tracker.Contains(2));
    }

    private static void ThrowCaptureSaveFailure()
    {
        throw new IOException("disk full");
    }
}
