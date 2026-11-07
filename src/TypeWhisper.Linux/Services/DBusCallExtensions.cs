namespace TypeWhisper.Linux.Services;

internal static class DBusCallExtensions
{
    // WaitAsync can't cancel a hung D-Bus call: it stays pending, and when a reply or disconnect later
    // faults it, nothing awaits it and the runtime reports an UnobservedTaskException. The caller has
    // already handled the timeout, so that late fault is observed and dropped.
    public static Task WaitOrAbandonAsync(this Task call, TimeSpan timeout, CancellationToken ct = default)
    {
        ObserveFault(call);
        return call.WaitAsync(timeout, ct);
    }

    public static Task<T> WaitOrAbandonAsync<T>(this Task<T> call, TimeSpan timeout, CancellationToken ct = default)
    {
        ObserveFault(call);
        return call.WaitAsync(timeout, ct);
    }

    public static Task WaitOrAbandonAsync(this Task call, CancellationToken ct) =>
        call.WaitOrAbandonAsync(Timeout.InfiniteTimeSpan, ct);

    public static Task<T> WaitOrAbandonAsync<T>(this Task<T> call, CancellationToken ct) =>
        call.WaitOrAbandonAsync(Timeout.InfiniteTimeSpan, ct);

    private static void ObserveFault(Task call)
    {
        _ = call.ContinueWith(
            static faulted => _ = faulted.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }
}
