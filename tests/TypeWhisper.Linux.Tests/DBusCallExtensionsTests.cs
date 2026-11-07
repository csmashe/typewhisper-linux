using System.Runtime.CompilerServices;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

// A D-Bus call abandoned on timeout keeps running, and when its reply later arrives as an error
// nothing awaits it: the runtime reported it as an UnobservedTaskException, which reached Sentry
// as a bare Tmds.DBus stack (TYPEWHISPER-LINUXDOTNET-4).
public class DBusCallExtensionsTests
{
    [Fact]
    public void Call_that_faults_after_the_timeout_is_not_reported_unobserved()
    {
        Assert.False(LateFaultReportedUnobserved(static (call, timeout) => call.WaitOrAbandonAsync(timeout)));
    }

    // Control: proves the probe sees an abandoned fault, so the pass above means something.
    [Fact]
    public void Plain_WaitAsync_leaves_the_late_fault_unobserved()
    {
        Assert.True(LateFaultReportedUnobserved(static (call, timeout) => call.WaitAsync(timeout)));
    }

    [Fact]
    public async Task Fault_within_the_timeout_still_reaches_the_caller()
    {
        var call = new TaskCompletionSource<int>();
        var waited = call.Task.WaitOrAbandonAsync(TimeSpan.FromSeconds(30));
        call.SetException(new InvalidOperationException("denied"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => waited);
    }

    [Fact]
    public async Task Reply_within_the_timeout_is_returned()
    {
        var call = new TaskCompletionSource<int>();
        var waited = call.Task.WaitOrAbandonAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        call.SetResult(42);

        Assert.Equal(42, await waited);
    }

    private static bool LateFaultReportedUnobserved(Func<Task, TimeSpan, Task> wait)
    {
        var lateFault = new InvalidOperationException("reply arrived after the caller gave up");
        var reported = false;

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            AbandonCallThatFaultsLate(wait, lateFault);
            for (var i = 0; i < 2; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        return reported;

        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (!e.Exception.InnerExceptions.Contains(lateFault))
            {
                return;
            }

            reported = true;
            e.SetObserved();
        }
    }

    // Kept out of line so no frame of the caller still roots the abandoned call when the GC runs.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AbandonCallThatFaultsLate(Func<Task, TimeSpan, Task> wait, Exception lateFault)
    {
        var call = new TaskCompletionSource();
        var waited = wait(call.Task, TimeSpan.Zero);
        Assert.IsType<TimeoutException>(waited.Exception?.InnerException);
        call.SetException(lateFault);
    }
}
