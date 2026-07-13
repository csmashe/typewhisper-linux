using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationToggleGateTests
{
    [Fact]
    public void StopDuringStartup_IsRememberedAndReportedToStartupCompleter()
    {
        using var gate = new DictationToggleGate();
        Assert.True(gate.TryBeginStartup(() => { }));

        var stopResult = gate.TryAcquireForStop();

        Assert.Equal(DictationStopGateResult.PendingStartupCompletion, stopResult);
        Assert.True(gate.HasPendingStop);
        Assert.True(gate.CompleteStartupAndRelease().HasPendingStop);
        Assert.False(gate.HasPendingStop);

        Assert.Equal(DictationStopGateResult.Acquired, gate.TryAcquireForStop());
        gate.Release();
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public void CancelDuringStartup_CarriesCancelIntentToStartupCompleter()
    {
        using var gate = new DictationToggleGate();
        Assert.True(gate.TryBeginStartup(() => { }));

        var stopResult = gate.TryAcquireForStop(wasCancel: true);

        Assert.Equal(DictationStopGateResult.PendingStartupCompletion, stopResult);
        var deferred = gate.CompleteStartupAndRelease();
        Assert.True(deferred.HasPendingStop);
        Assert.True(deferred.WasCancel);
    }

    [Fact]
    public void NonCancelStopDuringStartup_ReportsWasCancelFalse()
    {
        using var gate = new DictationToggleGate();
        Assert.True(gate.TryBeginStartup(() => { }));

        gate.TryAcquireForStop(wasCancel: false);

        var deferred = gate.CompleteStartupAndRelease();
        Assert.True(deferred.HasPendingStop);
        Assert.False(deferred.WasCancel);
    }

    [Fact]
    public void LaterOrdinaryStop_DoesNotClearEarlierCancelIntent()
    {
        using var gate = new DictationToggleGate();
        Assert.True(gate.TryBeginStartup(() => { }));

        gate.TryAcquireForStop(wasCancel: true);
        gate.TryAcquireForStop(wasCancel: false);

        var deferred = gate.CompleteStartupAndRelease();
        Assert.True(deferred.HasPendingStop);
        Assert.True(deferred.WasCancel);
    }

    [Fact]
    public void StopDuringNonStartupOwnership_IsNotRemembered()
    {
        using var gate = new DictationToggleGate();
        Assert.True(gate.TryAcquire());

        var stopResult = gate.TryAcquireForStop();

        Assert.Equal(DictationStopGateResult.Busy, stopResult);
        Assert.False(gate.HasPendingStop);

        gate.Release();
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public void StartupReleaseBetweenStopAttempts_LetsRetryAcquire()
    {
        using var gate = new DictationToggleGate();
        Assert.True(gate.TryBeginStartup(() => { }));
        var startupReportedPendingStop = true;

        var stopResult = gate.TryAcquireForStop(
            beforeRememberingPendingStop:
            // ReSharper disable once AccessToDisposedClosure -- callback runs synchronously inside TryAcquireForStop, well before the `using var gate` is disposed at scope end.
            () => startupReportedPendingStop = gate.CompleteStartupAndRelease().HasPendingStop
        );

        Assert.False(startupReportedPendingStop);
        Assert.Equal(DictationStopGateResult.Acquired, stopResult);
        Assert.False(gate.HasPendingStop);
        Assert.Equal(0, gate.CurrentCount);

        gate.Release();
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public void NewStartup_ClearsStalePendingStop()
    {
        using var gate = new DictationToggleGate();
        Assert.True(gate.TryBeginStartup(() => { }));
        var nonStartupHolderAcquired = false;

        var stopResult = gate.TryAcquireForStop(beforeRememberingPendingStop: () =>
        {
            // ReSharper disable AccessToDisposedClosure -- callback runs synchronously inside TryAcquireForStop, well before the `using var gate` is disposed at scope end.
            Assert.False(gate.CompleteStartupAndRelease().HasPendingStop);
            nonStartupHolderAcquired = gate.TryAcquire();
            // ReSharper restore AccessToDisposedClosure
        });

        Assert.True(nonStartupHolderAcquired);
        Assert.Equal(DictationStopGateResult.PendingStartupCompletion, stopResult);
        Assert.True(gate.HasPendingStop);

        gate.Release();
        Assert.True(gate.TryBeginStartup(() => { }));
        Assert.False(gate.HasPendingStop);
        Assert.False(gate.CompleteStartupAndRelease().HasPendingStop);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public void ThrowingInitializer_ReleasesGateAndLetsSubsequentStartupSucceed()
    {
        using var gate = new DictationToggleGate();

        Assert.Throws<InvalidOperationException>(() =>
            gate.TryBeginStartup(() => throw new InvalidOperationException()));

        Assert.Equal(1, gate.CurrentCount);
        Assert.True(gate.TryBeginStartup(() => { }));
    }

    [Fact]
    public async Task ParallelStartStopCycles_ConsumePendingOnceAndRestoreSemaphore()
    {
        const int workerCount = 8;
        const int cyclesPerWorker = 200;

        var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
        {
            using var gate = new DictationToggleGate();
            var startupsWithPendingStop = 0;
            var pendingStopsConsumed = 0;

            for (var cycle = 0; cycle < cyclesPerWorker; cycle++)
            {
                Assert.True(gate.TryBeginStartup(() => { }));

                DictationStopGateResult stopResult;
                bool startupMustHonorStop;
                if (cycle % 2 == 0)
                {
                    stopResult = gate.TryAcquireForStop();
                    startupMustHonorStop = gate.CompleteStartupAndRelease().HasPendingStop;
                }
                else
                {
                    // ReSharper disable once AccessToDisposedClosure -- awaited via Task.WhenAll below, so it completes before the worker's `using var gate` is disposed at scope end.
                    var stopTask = Task.Run(() => gate.TryAcquireForStop());
                    var completionTask = Task.Run(gate.CompleteStartupAndRelease);
                    await Task.WhenAll(stopTask, completionTask);
                    stopResult = await stopTask;
                    startupMustHonorStop = (await completionTask).HasPendingStop;
                }

                if (startupMustHonorStop)
                {
                    startupsWithPendingStop++;
                    Assert.Equal(
                        DictationStopGateResult.PendingStartupCompletion,
                        stopResult
                    );
                    Assert.Equal(
                        DictationStopGateResult.Acquired,
                        gate.TryAcquireForStop()
                    );
                    pendingStopsConsumed++;
                }
                else
                {
                    Assert.Equal(DictationStopGateResult.Acquired, stopResult);
                }

                gate.Release();
                Assert.False(gate.HasPendingStop);
                Assert.Equal(1, gate.CurrentCount);
            }

            Assert.True(startupsWithPendingStop > 0);
            Assert.Equal(startupsWithPendingStop, pendingStopsConsumed);
        }));

        await Task.WhenAll(workers);
    }
}
