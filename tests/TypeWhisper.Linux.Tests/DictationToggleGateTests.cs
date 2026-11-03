using System.Diagnostics.CodeAnalysis;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

[SuppressMessage(
    "ReSharper",
    "AccessToDisposedClosure",
    Justification = "Test lambdas always finish inside the using scope that owns the gate; every worker is awaited before the test method returns."
)]
[SuppressMessage(
    "ReSharper",
    "AccessToModifiedClosure",
    Justification = "Captured locals are deliberately shared between the concurrent worker and the asserting test body."
)]
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

    [Fact]
    public async Task Close_WhileOwned_WaitsForRelease()
    {
        using var gate = new DictationToggleGate();
        Assert.True(gate.TryAcquire());

        var close = gate.CloseAsync(TimeSpan.FromSeconds(5));

        Assert.False(close.IsCompleted);
        gate.Release();
        Assert.True(await close);
    }

    [Fact]
    public async Task QueuedWaitAsync_WinsReleaseAgainstBargingTryAcquire()
    {
        using var gate = new DictationToggleGate();
        Assert.True(gate.TryAcquire());

        var waiter = gate.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(waiter.IsCompleted);

        using var stopCompetition = new ManualResetEventSlim();
        using var releaseBarger = new ManualResetEventSlim();
        using var bargerIsCompeting = new ManualResetEventSlim();
        var bargerAcquired = 0;
        var barger = Task.Run(() =>
        {
            while (!stopCompetition.IsSet)
            {
                if (gate.TryAcquire())
                {
                    Interlocked.Exchange(ref bargerAcquired, 1);
                    releaseBarger.Wait(TimeSpan.FromSeconds(5));
                    gate.Release();
                    return;
                }

                bargerIsCompeting.Set();
                Thread.Yield();
            }
        });

        Assert.True(bargerIsCompeting.Wait(TimeSpan.FromSeconds(1)));
        gate.Release();

        bool waiterAcquired;
        try
        {
            waiterAcquired = await waiter.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (TimeoutException)
        {
            waiterAcquired = false;
        }
        finally
        {
            stopCompetition.Set();
            releaseBarger.Set();
        }

        await barger.WaitAsync(TimeSpan.FromSeconds(5));
        var finalWaiterResult = await waiter;
        if (finalWaiterResult)
        {
            gate.Release();
        }

        Assert.True(waiterAcquired);
        Assert.True(finalWaiterResult);
        // Relies on SemaphoreSlim.Release handing the permit to the queued waiter
        // while m_currentCount stays zero, so the barger's TryAcquire never sees it.
        // That is current runtime behavior, not a DictationToggleGate contract.
        Assert.Equal(0, Volatile.Read(ref bargerAcquired));
    }

    [Fact]
    public async Task AcquireAfterClose_RejectedWithoutThrow()
    {
        using var gate = new DictationToggleGate();
        Assert.True(await gate.CloseAsync(TimeSpan.FromSeconds(5)));

        var exception = Record.Exception(() => gate.TryAcquire());

        Assert.Null(exception);
        Assert.False(gate.TryAcquire());
        Assert.False(gate.TryBeginStartup(() => { }));
        Assert.Equal(DictationStopGateResult.Busy, gate.TryAcquireForStop());
        Assert.False(await gate.WaitAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task ReleaseAfterClose_DoesNotThrow()
    {
        using var gate = new DictationToggleGate();
        Assert.True(gate.TryAcquire());
        var close = gate.CloseAsync(TimeSpan.FromSeconds(5));

        var exception = Record.Exception(gate.Release);

        Assert.Null(exception);
        Assert.True(await close);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task CloseTimeout_ReportsFalseWithoutDisposingSemaphore()
    {
        using var gate = new DictationToggleGate();
        Assert.True(gate.TryAcquire());

        Assert.False(await gate.CloseAsync(TimeSpan.Zero));

        var exception = Record.Exception(gate.Release);
        Assert.Null(exception);
        Assert.Equal(1, gate.CurrentCount);
        Assert.False(gate.TryAcquire());
    }

    [Fact]
    public async Task OrchestratorDisposal_AsyncCloseRecordsOutcomeAndDisposeDoesNotRewait()
    {
        // Exercises the static close/consume seams; the three-line instance wiring that
        // stores and reads _toggleGateCloseOutcome is not constructible here without the
        // full orchestrator dependency graph and is covered by review instead.
        using var gate = new DictationToggleGate();
        Assert.True(gate.TryBeginStartup(() => { }));
        var disposed = 0;
        var closeEntries = 0;
        var recordedOutcome = ToggleGateCloseOutcome.Unknown;
        var asynchronousClose = DictationOrchestrator.CloseToggleGateAsync(
            TimeSpan.FromSeconds(5),
            () => Volatile.Write(ref disposed, 1),
            budget =>
            {
                Interlocked.Increment(ref closeEntries);
                return gate.CloseAsync(budget);
            },
            outcome => recordedOutcome = outcome
        );

        try
        {
            Assert.True(
                SpinWait.SpinUntil(
                    () => Volatile.Read(ref disposed) == 1,
                    TimeSpan.FromSeconds(1)
                ),
                "Disposal must become visible before it waits for the startup gate owner."
            );
            Assert.True(gate.IsClosed);

            // This is the same shutdown revalidation used after startup feedback: even though
            // input remains allowed, the gate-owning start can no longer open the microphone.
            Assert.False(
                DictationOrchestrator.IsCaptureStartAllowed(
                    Volatile.Read(ref disposed) != 0,
                    inputAllowed: true
                )
            );
        }
        finally
        {
            gate.CompleteStartupAndRelease();
        }

        Assert.True(await asynchronousClose);
        Assert.Equal(ToggleGateCloseOutcome.Idle, recordedOutcome);

        var disposeObservedIdle = DictationOrchestrator.CloseToggleGateForDispose(
            recordedOutcome,
            () =>
            {
                Interlocked.Increment(ref closeEntries);
                return gate.CloseAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
        );

        Assert.True(disposeObservedIdle);
        Assert.Equal(1, Volatile.Read(ref closeEntries));
    }
}
