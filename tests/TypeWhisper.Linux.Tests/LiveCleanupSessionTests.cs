using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class LiveCleanupSessionTests
{
    [Fact]
    public async Task RevisedDraft_UsesTimeSpentWaitingForPreviousRequestAsDebounce()
    {
        var clock = new OffsetTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new LiveCleanupSession(async (input, token) =>
        {
            if (input == "first")
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
            return input.ToUpperInvariant();
        }, (_, output) => published.TrySetResult(output), TimeSpan.FromMilliseconds(20), clock);
        session.Submit("first");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var timersBeforeRevision = clock.TimerCount;
        session.Submit("revised");
        clock.Advance(TimeSpan.FromSeconds(3));
        release.SetResult();

        Assert.Equal("REVISED", await published.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(timersBeforeRevision, clock.TimerCount);
        Assert.Equal("REVISED", await session.ReuseAsync("revised", default));
    }

    [Fact]
    public async Task FinalInputMismatch_CancelsObsoleteRequestAfterStop()
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new LiveCleanupSession(async (_, token) =>
        {
            entered.SetResult(token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        }, (_, _) => { }, TimeSpan.Zero);
        session.Submit("old hypothesis");
        var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        session.StopScheduling();

        Assert.Null(await session.ReuseAsync("corrected final text", default));
        Assert.True(token.IsCancellationRequested);
    }

    private sealed class OffsetTimeProvider : TimeProvider
    {
        private long _offset;
        private int _timerCount;
        public int TimerCount => Volatile.Read(ref _timerCount);
        public override long TimestampFrequency => System.TimestampFrequency;
        public override long GetTimestamp() => System.GetTimestamp() + Volatile.Read(ref _offset);
        public void Advance(TimeSpan duration) =>
            Interlocked.Add(ref _offset, (long)(duration.TotalSeconds * TimestampFrequency));
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _timerCount);
            return System.CreateTimer(callback, state, dueTime, period);
        }
    }

    [Fact]
    public async Task RevisedDraft_CoalescesPendingWorkAndRejectsStaleOutput()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var latest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputs = new List<string>();
        var outputs = new List<string>();
        using var session = new LiveCleanupSession(async (input, token) =>
        {
            inputs.Add(input);
            if (inputs.Count == 1) { entered.SetResult(); return await release.Task.WaitAsync(token); }
            latest.SetResult();
            return input.ToUpperInvariant();
        }, (_, output) => outputs.Add(output), TimeSpan.Zero);
        session.Submit("send on Monday");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        session.Submit("send on Monday actually Tuesday");
        session.Submit("send on Tuesday morning");
        release.SetResult("Send on Monday.");
        await latest.Task.WaitAsync(TimeSpan.FromSeconds(2));
        session.StopScheduling();
        Assert.Equal("SEND ON TUESDAY MORNING", await session.ReuseAsync("send on Tuesday morning", default));
        Assert.Equal(new[] { "send on Monday", "send on Tuesday morning" }, inputs);
        Assert.DoesNotContain("Send on Monday.", outputs);
        Assert.Null(await session.ReuseAsync("send on Tuesday evening", default));
    }

    [Fact]
    public async Task Stop_RejectsNewDraftsButReusesMatchingInFlightWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new LiveCleanupSession(async (_, token) =>
        {
            entered.SetResult();
            return await release.Task.WaitAsync(token);
        }, (_, _) => { }, TimeSpan.Zero);
        session.Submit("draft");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        session.StopScheduling();
        session.Submit("ignored");
        release.SetResult("Draft.");
        Assert.Equal("Draft.", await session.ReuseAsync("draft", default));
        Assert.Null(await session.ReuseAsync("ignored", default));
    }

    [Fact]
    public async Task FailedDraft_IsNotCached_AndDisposeCancelsWork()
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new LiveCleanupSession(async (_, token) =>
        {
            entered.SetResult(token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        }, (_, _) => { }, TimeSpan.Zero);
        session.Submit("draft");
        var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        session.Dispose();
        Assert.True(token.IsCancellationRequested);
        Assert.Null(await session.ReuseAsync("draft", default));
    }
}
