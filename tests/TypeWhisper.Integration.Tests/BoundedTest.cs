using System.Collections.Concurrent;

namespace TypeWhisper.Integration.Tests;

internal static class BoundedTest
{
    internal static readonly TimeSpan s_innerTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromSeconds(15);

    // WaitAsync abandons work rather than canceling it, and the orchestrator and socket
    // APIs under test take no CancellationToken, so a timed-out operation keeps running
    // and keeps owning sockets and XDG state. Recording what was abandoned lets the next
    // test fail with the real cause instead of cascading through ResetApplicationState.
    private static readonly ConcurrentBag<Task> s_abandoned = [];

    internal static async Task RunAsync(Func<Task> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        ThrowIfEarlierWorkIsStillRunning();

        // Task.Run also places synchronous construction/teardown work inside the
        // whole-test deadline, before the async body reaches its first await.
        var run = Task.Run(body);
        try
        {
            await run.WaitAsync(s_testTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Abandon(run);
            throw;
        }
    }

    internal static async Task<T> WaitAsync<T>(Task<T> task)
    {
        try
        {
            return await task.WaitAsync(s_innerTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Abandon(task);
            throw;
        }
    }

    internal static async Task WaitAsync(Task task)
    {
        try
        {
            await task.WaitAsync(s_innerTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Abandon(task);
            throw;
        }
    }

    private static void Abandon(Task task)
    {
        // Recording an already-finished task costs nothing, because the guard counts only
        // work that is still live. That is what keeps an inner timeout surfacing through
        // an outer wait — where the body has already unwound — from poisoning the run.
        s_abandoned.Add(task);
        _ = task.ContinueWith(
            static abandoned => _ = abandoned.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted
                | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    private static void ThrowIfEarlierWorkIsStillRunning()
    {
        var live = s_abandoned.Count(static task => !task.IsCompleted);
        if (live == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{live} operation(s) abandoned at an earlier integration-test deadline are "
            + "still running and still own sockets and XDG state in this process. Failures "
            + "from here are cascades — diagnose the first timeout."
        );
    }
}
