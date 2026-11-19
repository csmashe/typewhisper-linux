using Xunit;

namespace TypeWhisper.Linux.Tests;

internal static class BlockedThread
{
    /// <summary>
    ///     Runs <paramref name="work" /> on a pool thread and returns once that thread blocks,
    ///     for example on a lock another test thread holds. Fails if the work finishes first.
    /// </summary>
    public static async Task<Task<T>> StartAndWaitUntilBlocked<T>(Func<T> work, TimeSpan timeout)
    {
        var worker = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Task.Run(() =>
        {
            worker.SetResult(Thread.CurrentThread);
            return work();
        });
        var thread = await worker.Task.WaitAsync(timeout);
        Assert.True(
            SpinWait.SpinUntil(() => task.IsCompleted || IsWaiting(thread), timeout),
            "The work neither blocked nor finished."
        );
        Assert.False(task.IsCompleted, "The work finished instead of blocking.");
        return task;
    }

    public static async Task<Task> StartAndWaitUntilBlocked(Action work, TimeSpan timeout) =>
        await StartAndWaitUntilBlocked<object?>(
            () =>
            {
                work();
                return null;
            },
            timeout
        );

    private static bool IsWaiting(Thread thread) =>
        (thread.ThreadState & ThreadState.WaitSleepJoin) != 0;
}
