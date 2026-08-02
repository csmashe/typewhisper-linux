using System.Diagnostics;
using System.Net.WebSockets;
using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class StreamingTranscriptionCoordinatorTests
{
    [Fact]
    public async Task AcceptAudioFrame_BeforeStartAsync_QueuesInPendingBuffer()
    {
        var session = new FakeStreamingSession();
        var connectTcs = new TaskCompletionSource<IStreamingSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new FakePlugin
        {
            OnStartStreaming = _ => connectTcs.Task,
        };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, "en", 1, (_, _) => { }, _ => { });

        var startTask = coord.StartAsync(CancellationToken.None);

        coord.AcceptAudioFrame(MakeMarkedFrame(1), 16000);
        coord.AcceptAudioFrame(MakeMarkedFrame(2), 16000);
        coord.AcceptAudioFrame(MakeMarkedFrame(3), 16000);

        Assert.Empty(session.SentChunks);

        connectTcs.SetResult(session);
        await startTask;

        await WaitForAsync(() => session.SentChunks.Count == 3, TimeSpan.FromSeconds(2));

        Assert.Equal(3, session.SentChunks.Count);
        Assert.Equal(1, ReadMarker(session.SentChunks[0]));
        Assert.Equal(2, ReadMarker(session.SentChunks[1]));
        Assert.Equal(3, ReadMarker(session.SentChunks[2]));
    }

    [Fact]
    public async Task AcceptAudioFrame_AfterStartAsync_TryWritesIntoChannel()
    {
        var session = new FakeStreamingSession();
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, _ => { });

        await coord.StartAsync(CancellationToken.None);

        coord.AcceptAudioFrame(MakeMarkedFrame(10), 16000);
        coord.AcceptAudioFrame(MakeMarkedFrame(11), 16000);

        await WaitForAsync(() => session.SentChunks.Count == 2, TimeSpan.FromSeconds(2));
        Assert.Equal(10, ReadMarker(session.SentChunks[0]));
        Assert.Equal(11, ReadMarker(session.SentChunks[1]));
    }

    [Fact]
    public async Task PendingBuffer_AtCapacity_DropsOldest()
    {
        var session = new FakeStreamingSession();
        var connectTcs = new TaskCompletionSource<IStreamingSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new FakePlugin { OnStartStreaming = _ => connectTcs.Task };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, _ => { });

        var startTask = coord.StartAsync(CancellationToken.None);

        // 200 frames × ~8 KB each = ~1.6 MB total; pending cap is 1 MB.
        const int sampleCount = 4096; // 8192 bytes PCM16
        for (var i = 0; i < 200; i++)
        {
            coord.AcceptAudioFrame(MakeMarkedFrame(i, sampleCount), 16000);
        }

        connectTcs.SetResult(session);
        await startTask;

        // Give sender time to drain the flushed channel.
        await WaitForAsync(() => session.SentChunks.Count >= 100, TimeSpan.FromSeconds(2));
        await Task.Delay(100); // settle

        // At most ~128 chunks fit in 1 MB; channel cap is also 128. Allow some
        // margin for the exact size math.
        Assert.True(session.SentChunks.Count <= 130,
            $"Expected ≤130 chunks (pending cap enforced), got {session.SentChunks.Count}");
        Assert.True(session.SentChunks.Count >= 100,
            $"Expected ≥100 chunks delivered, got {session.SentChunks.Count}");

        var markers = session.SentChunks.Select(ReadMarker).ToList();

        // Most recent (199) must survive.
        Assert.Contains(199, markers);
        // Oldest (0..50) must be dropped.
        Assert.DoesNotContain(0, markers);
        Assert.DoesNotContain(50, markers);
    }

    [Fact]
    public async Task Channel_AtCapacity_DropsOldest()
    {
        var session = new FakeStreamingSession();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Block the first SendAudioAsync call until released so the channel fills.
        var blockOnce = 0;
        session.OnSendAudio = async _ =>
        {
            if (Interlocked.Exchange(ref blockOnce, 1) == 0)
            {
                await gate.Task;
            }
        };
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, _ => { });

        await coord.StartAsync(CancellationToken.None);

        for (var i = 0; i < 200; i++)
        {
            coord.AcceptAudioFrame(MakeMarkedFrame(i, 16), 16000);
        }

        // Give the channel a moment to settle the dropping behavior.
        await Task.Delay(50);
        gate.SetResult();

        // FinalizeAsync drains the sender. Use it to wait for completion.
        await coord.FinalizeAsync(CancellationToken.None);

        // Channel capacity is 128; one chunk is already in flight at gate.
        // Expected count: roughly 128–129 chunks. Loose bound for CI stability.
        Assert.True(session.SentChunks.Count <= 130,
            $"Expected ≤130 chunks (channel cap enforced), got {session.SentChunks.Count}");
        Assert.True(session.SentChunks.Count >= 100,
            $"Expected ≥100 chunks delivered, got {session.SentChunks.Count}");

        var markers = session.SentChunks.Select(ReadMarker).ToList();
        Assert.Contains(199, markers);
        // Some early markers must be dropped (most chunks 5-50 should not be present).
        var earlyPresent = markers.Count(m => m is >= 5 and <= 50);
        Assert.True(earlyPresent < 10,
            $"Expected most early markers (5-50) dropped, got {earlyPresent} present");
    }

    [Fact]
    public async Task Sender_SerializesChunks_A9Regression()
    {
        var session = new FakeStreamingSession();
        // Small per-send delay to maximize concurrency-violation probability.
        session.OnSendAudio = async _ => await Task.Delay(1);
        Exception? observedFault = null;
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, ex => observedFault = ex);

        await coord.StartAsync(CancellationToken.None);

        for (var i = 0; i < 100; i++)
        {
            coord.AcceptAudioFrame(MakeMarkedFrame(i), 16000);
        }

        await coord.FinalizeAsync(CancellationToken.None);

        // The FakeStreamingSession's SemaphoreSlim throws on concurrent entry and the
        // sender catches it as InvalidOperationException → HandleFault. So if A9
        // ever broke, observedFault would be non-null with the A9 message.
        Assert.Null(observedFault);
        Assert.False(coord.Faulted);
        Assert.True(session.SentChunks.Count > 0);
    }

    [Fact]
    public async Task AcceptAudioFrame_DoesNotBlockSender_A10Regression()
    {
        var session = new FakeStreamingSession();
        session.OnSendAudio = async _ => await Task.Delay(200);
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, _ => { });

        await coord.StartAsync(CancellationToken.None);

        // Warm up to avoid JIT cost in the timed loop.
        coord.AcceptAudioFrame(MakeMarkedFrame(0), 16000);

        var sw = Stopwatch.StartNew();
        for (var i = 1; i <= 10; i++)
        {
            coord.AcceptAudioFrame(MakeMarkedFrame(i), 16000);
        }
        sw.Stop();

        // Writer must not block even while a 200 ms send is in flight.
        // 50 ms upper bound: tolerant to CI noise but well below the 200 ms send.
        Assert.True(sw.ElapsedMilliseconds < 50,
            $"AcceptAudioFrame should not block on sender; took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Fault_OnSendException_CallsOnFault_AndSetsFaultedTrue()
    {
        var session = new FakeStreamingSession();
        session.OnSendAudio = _ => throw new WebSocketException("simulated transport failure");
        var faultTcs = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, ex => faultTcs.TrySetResult(ex));

        await coord.StartAsync(CancellationToken.None);
        coord.AcceptAudioFrame(MakeMarkedFrame(1), 16000);

        var observed = await faultTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<WebSocketException>(observed);
        Assert.True(coord.Faulted);
    }

    [Fact]
    public async Task Fault_OnSendException_GenericException_RoutesViaOnFault()
    {
        // Regression: plugin SendAudioAsync implementations can throw arbitrary
        // exception types (HttpRequestException from REST-style streamers,
        // plugin-internal exceptions, etc.). The sender must route ALL non-cancel
        // failures through HandleFault so Phase 4's batch fallback fires.
        var session = new FakeStreamingSession();
        session.OnSendAudio = _ => throw new HttpRequestException("simulated REST failure");
        var faultTcs = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, ex => faultTcs.TrySetResult(ex));

        await coord.StartAsync(CancellationToken.None);
        coord.AcceptAudioFrame(MakeMarkedFrame(1), 16000);

        var observed = await faultTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<HttpRequestException>(observed);
        Assert.True(coord.Faulted);
    }

    [Fact]
    public async Task Sender_ProviderCancellationWithLiveToken_RoutesViaOnFault()
    {
        var session = new FakeStreamingSession
        {
            OnSendAudio = _ => throw new OperationCanceledException("provider canceled"),
        };
        var faultTcs = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var plugin = new FakePlugin
        {
            OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session),
        };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, ex => faultTcs.TrySetResult(ex));
        await coord.StartAsync(CancellationToken.None);
        coord.AcceptAudioFrame(MakeMarkedFrame(1), 16000);

        var observed = await faultTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<OperationCanceledException>(observed);
        Assert.True(coord.Faulted);
    }

    [Fact]
    public async Task Sender_PrivateTimeout_RoutesViaOnFault()
    {
        var session = new FakeStreamingSession
        {
            OnSendAudio = _ => throw new TimeoutException("provider deadline"),
        };
        var faultTcs = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var plugin = new FakePlugin
        {
            OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session),
        };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, ex => faultTcs.TrySetResult(ex));
        await coord.StartAsync(CancellationToken.None);
        coord.AcceptAudioFrame(MakeMarkedFrame(1), 16000);

        var observed = await faultTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<TimeoutException>(observed);
        Assert.True(coord.Faulted);
    }

    [Fact]
    public async Task Sender_DisposeCancellation_DoesNotFault()
    {
        var sendEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var session = new FakeStreamingSession
        {
            OnSendAudioWithCancellation = async (_, ct) =>
            {
                sendEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            },
        };
        var faults = new List<Exception>();
        var plugin = new FakePlugin
        {
            OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session),
        };
        var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, faults.Add);

        await coord.StartAsync(CancellationToken.None);
        coord.AcceptAudioFrame(MakeMarkedFrame(1), 16000);
        await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await coord.DisposeAsync();

        Assert.Empty(faults);
        Assert.False(coord.Faulted);
    }

    [Fact]
    public async Task Sender_DependencyFaultRacingCallerCancellation_CancellationWins()
    {
        using var callerCts = new CancellationTokenSource();
        var session = new FakeStreamingSession
        {
            OnSendAudioWithCancellation = async (_, _) =>
            {
                // ReSharper disable once AccessToDisposedClosure -- the coordinator is declared after
                // callerCts, so it is disposed (stopping the sender) before callerCts goes away.
                await callerCts.CancelAsync();
                await Task.Yield();
                throw new HttpRequestException("provider failed during cancellation");
            },
        };
        var faults = new List<Exception>();
        var plugin = new FakePlugin
        {
            OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session),
        };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, faults.Add);
        await coord.StartAsync(callerCts.Token);
        coord.AcceptAudioFrame(MakeMarkedFrame(1), 16000);
        // ReSharper disable once MethodSupportsCancellation -- callerCts is cancelled by the send
        // hook, so passing it here would abort the very wait that lets the sender settle.
        await Task.Delay(100);

        Assert.Empty(faults);
        Assert.False(coord.Faulted);
    }

    [Fact]
    public async Task Fault_OnConnectException_PropagatesViaOnFault()
    {
        var faultTcs = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new FakePlugin
        {
            OnStartStreaming = _ => Task.FromException<IStreamingSession>(
                new HttpRequestException("auth failed (simulated)")),
        };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, ex => faultTcs.TrySetResult(ex));

        // StartAsync swallows the connect exception and routes it via onFault.
        await coord.StartAsync(CancellationToken.None);

        var observed = await faultTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<HttpRequestException>(observed);
        Assert.True(coord.Faulted);

        // Verify no audio is delivered after fault.
        coord.AcceptAudioFrame(MakeMarkedFrame(1), 16000);
        await Task.Delay(50);
        // No fake session was ever returned, so SentChunks doesn't apply here.
        // Faulted alone guards the path.
    }

    [Fact]
    public async Task FinalizeAsync_RethrowsSessionFinalizeFault_ForOrchestratorFallback()
    {
        // Regression: the coordinator used to swallow exceptions from
        // session.FinalizeAsync, which left DictationOrchestrator unable to
        // detect provider errors that arrive AFTER the last audio chunk is
        // sent (no more SendAudioAsync calls trigger the sender-side fault
        // path). The orchestrator's TeardownStreamingSessionAsync ORs
        // coordinator.Faulted with finalizeThrew — rethrowing here lights
        // finalizeThrew so batch fallback runs.
        var session = new FakeStreamingSession();
        session.OnFinalize = _ => throw new InvalidOperationException("simulated provider error event");
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, _ => { });

        await coord.StartAsync(CancellationToken.None);
        coord.AcceptAudioFrame(MakeMarkedFrame(1), 16000);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coord.FinalizeAsync(CancellationToken.None));
        Assert.Contains("faulted during finalize", ex.Message);
        Assert.NotNull(ex.InnerException);
        Assert.Contains("simulated provider error event", ex.InnerException!.Message);
    }

    [Fact]
    public async Task FinalizeAsync_ProviderCancellationWithLiveCaller_IsProviderFault()
    {
        var session = new FakeStreamingSession
        {
            OnFinalize = _ => throw new OperationCanceledException("provider canceled finalize"),
        };
        var plugin = new FakePlugin
        {
            OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session),
        };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, _ => { });
        await coord.StartAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coord.FinalizeAsync(CancellationToken.None));
        Assert.IsType<OperationCanceledException>(ex.InnerException);
        Assert.Contains("provider canceled finalize", ex.InnerException!.Message);
    }

    [Fact]
    public async Task FinalizeAsync_SenderDrainTimeout_WithEarlierFinal_ThrowsWithoutConcurrentFinalize()
    {
        var session = new FakeStreamingSession();
        var sendEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnSendAudio = async _ =>
        {
            sendEntered.TrySetResult();
            await releaseSend.Task;
        };
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        var coord = new StreamingTranscriptionCoordinator(
            plugin,
            null,
            1,
            (_, _) => { },
            _ => { },
            finalizeSenderTimeout: TimeSpan.FromMilliseconds(100)
        );

        // ReSharper disable once RedundantAssignment
        // Initializer is required for definite assignment: observed is read at the
        // Assert below (outside the try) but only assigned inside it.
        Exception? observed = null;
        try
        {
            await coord.StartAsync(CancellationToken.None);
            session.RaiseFinal("earlier final");
            coord.AcceptAudioFrame(MakeMarkedFrame(1), 16000);
            await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

            observed = await Record.ExceptionAsync(
                () => coord.FinalizeAsync(CancellationToken.None));
        }
        finally
        {
            releaseSend.TrySetResult();
            await coord.DisposeAsync();
        }

        var timeout = Assert.IsType<TimeoutException>(observed);
        Assert.Contains("sender-drain deadline", timeout.Message);
        Assert.True(coord.HasFinalText, "The timeout path must reject even a nonempty snapshot.");
        Assert.False(coord.Faulted);
        Assert.Equal(0, session.FinalizeCallCount);
        Assert.False(session.FinalizeObservedDuringSend);
    }

    [Fact]
    public async Task DisposeAsync_StuckSenderAndBlockedDispose_BoundedByDeadline()
    {
        // After FinalizeAsync times out a sender that ignores cancellation, the
        // orchestrator awaits DisposeAsync before the complete-WAV batch fallback.
        // A never-draining sender AND a blocked provider DisposeAsync must still
        // let teardown finish within the deadline. Neither blocked task is
        // released before dispose.
        var session = new FakeStreamingSession();
        var sendEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var neverReleased = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnSendAudio = async _ =>
        {
            sendEntered.TrySetResult();
            await neverReleased.Task;
        };
        session.OnDispose = () => neverReleased.Task;
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        var timeout = TimeSpan.FromMilliseconds(100);
        var coord = new StreamingTranscriptionCoordinator(
            plugin,
            null,
            1,
            (_, _) => { },
            _ => { },
            finalizeSenderTimeout: timeout,
            finalizeSessionTimeout: timeout
        );

        try
        {
            await coord.StartAsync(CancellationToken.None);
            session.RaiseFinal("earlier final");
            coord.AcceptAudioFrame(MakeMarkedFrame(1), 16000);
            await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

            _ = await Record.ExceptionAsync(
                () => coord.FinalizeAsync(CancellationToken.None));

            // The stuck sender already set _skipSessionFinalize, so DisposeAsync
            // must neither re-wait the sender for a full deadline nor block on the
            // provider's non-returning DisposeAsync. The bound absorbs one
            // dispose-timeout plus scheduling slack, well under a regression's ~4s.
            var sw = Stopwatch.StartNew();
            await coord.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 1000,
                $"DisposeAsync must be bounded when sender and dispose block; took {sw.ElapsedMilliseconds} ms");
        }
        finally
        {
            neverReleased.TrySetResult();
        }
    }

    [Fact]
    public async Task FinalizeAsync_SenderFaultDuringDrain_UsesExistingFaultSemantics()
    {
        var session = new FakeStreamingSession();
        var sendEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnSendAudio = async _ =>
        {
            sendEntered.TrySetResult();
            await releaseSend.Task;
            throw new HttpRequestException("sender failed while draining");
        };
        var faultTcs = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, ex => faultTcs.TrySetResult(ex));

        await coord.StartAsync(CancellationToken.None);
        session.RaiseFinal("earlier final");
        coord.AcceptAudioFrame(MakeMarkedFrame(1), 16000);
        await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var finalizeTask = coord.FinalizeAsync(CancellationToken.None);
        releaseSend.TrySetResult();

        var text = await finalizeTask;
        var fault = await faultTcs.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("earlier final", text);
        Assert.IsType<HttpRequestException>(fault);
        Assert.True(coord.Faulted);
    }

    [Fact]
    public async Task FinalizeAsync_SessionFinalizeTimeout_WithEarlierFinal_Throws()
    {
        var session = new FakeStreamingSession();
        var releaseFinalize = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        // Deliberately ignore the token: the coordinator's hard wait must still
        // reject the partial transcript once the session-finalize deadline expires.
        session.OnFinalize = _ => releaseFinalize.Task;
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        var coord = new StreamingTranscriptionCoordinator(
            plugin,
            null,
            1,
            (_, _) => { },
            _ => { },
            finalizeSessionTimeout: TimeSpan.FromMilliseconds(100)
        );

        // ReSharper disable once RedundantAssignment
        // Initializer is required for definite assignment: observed is read at the
        // Assert below (outside the try) but only assigned inside it.
        Exception? observed = null;
        try
        {
            await coord.StartAsync(CancellationToken.None);
            session.RaiseFinal("earlier final");

            observed = await Record.ExceptionAsync(
                () => coord.FinalizeAsync(CancellationToken.None));
        }
        finally
        {
            releaseFinalize.TrySetResult();
            await coord.DisposeAsync();
        }

        var timeout = Assert.IsType<TimeoutException>(observed);
        Assert.Contains("session-finalize deadline", timeout.Message);
        Assert.True(coord.HasFinalText, "The timeout path must reject even a nonempty snapshot.");
        Assert.False(coord.Faulted);
    }

    [Fact]
    public async Task FinalizeAsync_SessionFinalizeDeadline_CancellationHonoringPlugin_StillThrows()
    {
        // Race regression: the bundled provider sessions (Soniox, Speechmatics,
        // Gladia, xAI) honor the cancellation token and return NORMALLY the instant
        // the session-finalize deadline cancels them. If the coordinator trusted the
        // WhenAny winner, that normal completion would look like clean success and
        // admit a truncated transcript. The deadline must win regardless.
        var session = new FakeStreamingSession();
        var deadlineReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnFinalize = async ct =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // Swallow like the real provider sessions do: return normally.
            }

            deadlineReached.TrySetResult();
        };
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        var coord = new StreamingTranscriptionCoordinator(
            plugin,
            null,
            1,
            (_, _) => { },
            _ => { },
            finalizeSessionTimeout: TimeSpan.FromMilliseconds(100)
        );

        // ReSharper disable once RedundantAssignment
        // Initializer is required for definite assignment: observed is read at the
        // Assert below (outside the try) but only assigned inside it.
        Exception? observed = null;
        try
        {
            await coord.StartAsync(CancellationToken.None);
            session.RaiseFinal("earlier final");

            observed = await Record.ExceptionAsync(
                () => coord.FinalizeAsync(CancellationToken.None));
        }
        finally
        {
            await coord.DisposeAsync();
        }

        var timeout = Assert.IsType<TimeoutException>(observed);
        Assert.Contains("session-finalize deadline", timeout.Message);
        Assert.True(coord.HasFinalText, "A deadline-cancelled finalize must not admit the partial snapshot.");
        Assert.False(coord.Faulted);
    }

    [Fact]
    public async Task FinalizeAsync_DrainsChannel_BeforeSessionFinalize()
    {
        var session = new FakeStreamingSession();
        var sentMarks = new List<long>();
        var sendLock = new object();
        session.OnSendAudio = async _ =>
        {
            await Task.Delay(20);
            lock (sendLock) sentMarks.Add(Stopwatch.GetTimestamp());
        };
        long? finalizeMark = null;
        session.OnFinalize = _ =>
        {
            finalizeMark = Stopwatch.GetTimestamp();
            return Task.CompletedTask;
        };

        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, _ => { });

        await coord.StartAsync(CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            coord.AcceptAudioFrame(MakeMarkedFrame(i), 16000);
        }

        await coord.FinalizeAsync(CancellationToken.None);

        Assert.Equal(5, sentMarks.Count);
        Assert.NotNull(finalizeMark);
        Assert.All(sentMarks, m => Assert.True(m < finalizeMark!.Value,
            "session.FinalizeAsync must run AFTER all SendAudioAsync calls"));
    }

    [Fact]
    public async Task FinalizeAsync_PreConnectQueuedAudio_DrainedBeforeSessionFinalize()
    {
        // Regression for the StartAsync publish race: audio queued in the
        // pre-connect pending buffer must be flushed AND sent before
        // session.FinalizeAsync runs. The fix folds the sender-task assignment
        // and the pending-queue flush into the same publishing lock so that no
        // observer (FinalizeAsync, AcceptAudioFrame, Dispose) can see a
        // half-initialized coordinator with pending PCM still in the queue.
        var session = new FakeStreamingSession();
        var sendStamps = new List<long>();
        var sendLock = new object();
        session.OnSendAudio = async _ =>
        {
            await Task.Delay(20);
            lock (sendLock) sendStamps.Add(Stopwatch.GetTimestamp());
        };
        long? finalizeStamp = null;
        session.OnFinalize = _ =>
        {
            finalizeStamp = Stopwatch.GetTimestamp();
            return Task.CompletedTask;
        };

        var connectTcs = new TaskCompletionSource<IStreamingSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new FakePlugin { OnStartStreaming = _ => connectTcs.Task };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, _ => { });

        var startTask = coord.StartAsync(CancellationToken.None);

        // Queue 5 chunks while connect is pending: they land in the pending buffer.
        for (var i = 0; i < 5; i++)
        {
            coord.AcceptAudioFrame(MakeMarkedFrame(i), 16000);
        }

        connectTcs.SetResult(session);
        await startTask;

        await coord.FinalizeAsync(CancellationToken.None);

        Assert.Equal(5, sendStamps.Count);
        Assert.NotNull(finalizeStamp);
        Assert.All(sendStamps, m => Assert.True(m < finalizeStamp!.Value,
            "Pending-queued audio must be sent before session.FinalizeAsync runs."));
    }

    [Fact]
    public async Task FinalizeAsync_ReturnsJoinedIsFinalSegments()
    {
        var session = new FakeStreamingSession();
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, _ => { });

        await coord.StartAsync(CancellationToken.None);

        session.RaiseFinal("hello");
        session.RaiseFinal("world");

        var result = await coord.FinalizeAsync(CancellationToken.None);
        Assert.Equal("hello\nworld", result);
        Assert.False(coord.Faulted);
    }

    [Fact]
    public async Task FinalizeAsync_GraceWindow_WaitsForLateTrailingFinal()
    {
        var session = new FakeStreamingSession();
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, _ => { });

        await coord.StartAsync(CancellationToken.None);

        // ReSharper disable once AccessToDisposedClosure — finalizeTask is awaited
        // below (before this method's await-using disposes coord); the WaitAsync
        // timeout is only a deadlock guard.
        var finalizeTask = Task.Run(() => coord.FinalizeAsync(CancellationToken.None));

        // Give FinalizeAsync ~150 ms to reach the grace window.
        await Task.Delay(150);
        session.RaiseFinal("late");

        var result = await finalizeTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains("late", result);
    }

    [Fact]
    public async Task FinalizeAsync_GraceWindow_DebouncesMultipleLateFinals()
    {
        // Regression: a provider can flush more than one final segment at EOF
        // (Deepgram, AssemblyAI, Soniox all do this for multi-utterance audio).
        // The first late final must NOT short-circuit the grace window; finals
        // arriving within the quiet-period debounce must all be appended before
        // FinalizeAsync returns.
        var session = new FakeStreamingSession();
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, _ => { });

        await coord.StartAsync(CancellationToken.None);

        // ReSharper disable once AccessToDisposedClosure — finalizeTask is awaited
        // below (before this method's await-using disposes coord); the WaitAsync
        // timeout is only a deadlock guard.
        var finalizeTask = Task.Run(() => coord.FinalizeAsync(CancellationToken.None));

        // Give FinalizeAsync time to reach its grace-window debounce loop.
        await Task.Delay(50);

        // Provider flushes two finals ~80 ms apart — within the quiet period,
        // so the second must reset the debounce and land in the result.
        session.RaiseFinal("hello");
        await Task.Delay(80);
        session.RaiseFinal("world");

        var result = await finalizeTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("hello\nworld", result);
    }

    [Fact]
    public async Task FinalizeAsync_GraceWindow_TimesOutAt500ms()
    {
        var session = new FakeStreamingSession();
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, _ => { });

        await coord.StartAsync(CancellationToken.None);

        var sw = Stopwatch.StartNew();
        var result = await coord.FinalizeAsync(CancellationToken.None);
        sw.Stop();

        Assert.Equal(string.Empty, result);
        Assert.True(sw.ElapsedMilliseconds < 1500,
            $"FinalizeAsync grace window must time out near 500 ms, took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task FinalizeAsync_CallerCancellationDuringSessionFinalize_ReturnsSnapshotWithoutFault()
    {
        // Regression: caller-supplied ct must collapse each phase's wait — sender
        // drain, session.FinalizeAsync, and the grace window — instead of forcing
        // the caller to block for the full timeout sum (up to ~4.5s) when an
        // aborted shutdown needs to tear down quickly.
        var session = new FakeStreamingSession();
        // session.FinalizeAsync honors ct: blocks until ct fires.
        var finalizeCalls = 0;
        session.OnFinalize = ct => Interlocked.Increment(ref finalizeCalls) == 1
            ? Task.Delay(Timeout.Infinite, ct)
            : Task.CompletedTask;
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, _ => { });

        await coord.StartAsync(CancellationToken.None);
        session.RaiseFinal("earlier final");

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);

        var sw = Stopwatch.StartNew();
        var result = await coord.FinalizeAsync(cts.Token);
        sw.Stop();

        // Without ct propagation this would block ~2s (sessionTimeout) + 500ms grace.
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"FinalizeAsync should honor caller's cancellation; took {sw.ElapsedMilliseconds} ms");
        Assert.Equal("earlier final", result);
        Assert.False(coord.Faulted);
    }

    [Fact]
    public async Task Dispose_BeforeStartAsync_DoesNotThrow()
    {
        var plugin = new FakePlugin();
        var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, _ => { });

        await coord.DisposeAsync();
        // Second dispose should also be safe.
        await coord.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_AfterFault_DoesNotThrow()
    {
        var plugin = new FakePlugin
        {
            OnStartStreaming = _ => Task.FromException<IStreamingSession>(
                new HttpRequestException("simulated")),
        };
        var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, _ => { });

        await coord.StartAsync(CancellationToken.None);
        Assert.True(coord.Faulted);

        await coord.DisposeAsync();
    }

    [Fact]
    public async Task FinalizeAsync_WhileConnectPending_DisposesLateArrivingSession()
    {
        // Regression: FinalizeAsync runs while StartAsync is still awaiting the
        // plugin's connect. The plugin ignores cancellation and resolves anyway.
        // The coordinator must NOT publish that late session — FinalizeAsync
        // already returned, so a published session would silently keep running
        // and accept audio with no one to receive the transcript or finalize it.
        var session = new FakeStreamingSession();
        var connectTcs = new TaskCompletionSource<IStreamingSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new FakePlugin { OnStartStreaming = _ => connectTcs.Task };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, _ => { });

        var startTask = coord.StartAsync(CancellationToken.None);

        // Queue audio while connect is pending — it lands in the pending buffer.
        coord.AcceptAudioFrame(MakeMarkedFrame(1), 16000);

        // Finalize before connect resolves.
        var finalizeResult = await coord.FinalizeAsync(CancellationToken.None);

        // Now the plugin's connect resolves with a real session.
        connectTcs.SetResult(session);

        await startTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(string.Empty, finalizeResult);
        Assert.True(session.Disposed,
            "Late-arriving session must be disposed when FinalizeAsync already returned.");
        Assert.Empty(session.SentChunks);
    }

    [Fact]
    public async Task Dispose_WhileConnectPending_PluginHonorsCancellation_DoesNotFault()
    {
        // Regression: a well-behaved plugin that observes the cancellation token
        // from StartStreamingAsync throws OperationCanceledException when Dispose
        // cancels _cts mid-handshake. That must be treated as normal teardown,
        // NOT routed through HandleFault — otherwise Phase 4 would interpret the
        // dispose-cancel as a streaming transport failure and trigger spurious
        // batch fallback.
        var faultCalled = false;
        var plugin = new FakePlugin
        {
            OnStartStreaming = ct => Task.Run<IStreamingSession>(async () =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            }, ct),
        };

        var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, _ => faultCalled = true);

        var startTask = coord.StartAsync(CancellationToken.None);

        // Give the plugin a moment to enter its cancellable wait, then dispose.
        await Task.Delay(50);
        await coord.DisposeAsync();

        await startTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(faultCalled, "Cancellation during connect must not be reported as a fault.");
        Assert.False(coord.Faulted, "Cancellation during connect must not set Faulted.");
    }

    [Fact]
    public async Task Dispose_WhileConnectPending_DisposesLateArrivingSession()
    {
        // Regression: DisposeAsync runs while StartAsync is still awaiting the
        // plugin's connect. The plugin then ignores cancellation and resolves
        // anyway. The coordinator must NOT publish that late session, and must
        // dispose it locally — otherwise the WebSocket/session leaks because
        // DisposeAsync already snapshotted nulls and won't come back for it.
        var session = new FakeStreamingSession();
        var connectTcs = new TaskCompletionSource<IStreamingSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new FakePlugin
        {
            // Deliberately ignore cancellation — simulate a misbehaving plugin
            // or a native WebSocket that resolves just before honoring cancel.
            OnStartStreaming = _ => connectTcs.Task,
        };

        var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, _ => { });

        var startTask = coord.StartAsync(CancellationToken.None);

        // Dispose before connect resolves.
        await coord.DisposeAsync();

        // Now the plugin's connect resolves with a real session (cancellation ignored).
        connectTcs.SetResult(session);

        // StartAsync's continuation runs on the thread pool; it must observe
        // _disposed and tear the late session down rather than publish it.
        await startTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(session.Disposed,
            "Late-arriving session must be disposed when coordinator was already disposed.");
    }

    [Fact]
    public async Task Dispose_DuringFinalize_DoesNotThrow()
    {
        var session = new FakeStreamingSession();
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, _ => { });

        await coord.StartAsync(CancellationToken.None);

        coord.AcceptAudioFrame(MakeMarkedFrame(1), 16000);

        var finalizeTask = Task.Run(() => coord.FinalizeAsync(CancellationToken.None));
        await Task.Delay(50);
        await coord.DisposeAsync();

        // FinalizeAsync should complete without throwing observable to the caller.
        var result = await finalizeTask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(result);
    }

    [Fact]
    public async Task OnPartial_FiresWithCorrectSessionVersion()
    {
        var session = new FakeStreamingSession();
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };
        (int Version, string Text)? observed = null;

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 42, (v, t) => observed = (v, t), _ => { });

        await coord.StartAsync(CancellationToken.None);
        session.RaisePartial("ping");

        await WaitForAsync(() => observed is not null, TimeSpan.FromSeconds(1));

        Assert.NotNull(observed);
        Assert.Equal(42, observed!.Value.Version);
        Assert.Equal("ping", observed.Value.Text);
    }

    [Fact]
    public async Task OnPartial_CallbackThrow_DoesNotCrashSession()
    {
        var session = new FakeStreamingSession();
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };
        var partialCount = 0;

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1,
            (_, _) =>
            {
                Interlocked.Increment(ref partialCount);
                throw new InvalidOperationException("callback intentionally throws");
            },
            _ => Assert.Fail("Callback-throw must not propagate as a fault"));

        await coord.StartAsync(CancellationToken.None);

        session.RaisePartial("one");
        session.RaisePartial("two");
        session.RaisePartial("three");

        await WaitForAsync(() => partialCount == 3, TimeSpan.FromSeconds(1));

        Assert.Equal(3, partialCount);
        Assert.False(coord.Faulted);
    }

    // ---- helpers ----

    private static float[] MakeMarkedFrame(int marker, int sampleCount = 32)
    {
        var arr = new float[sampleCount];
        // Encode marker in first sample so we can recover it from the PCM16 output.
        // (marker + 1)/1000 stays well within [-1, 1] for marker up to ~900 and
        // round-trips through ToPcm16 to within ±1.
        arr[0] = (marker + 1) / 1000f;
        return arr;
    }

    private static int ReadMarker(byte[] chunk)
    {
        var lo = chunk[0];
        var hi = chunk[1];
        var s = (short)(lo | (hi << 8));
        return (int)Math.Round(s / 32767f * 1000f) - 1;
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        if (!condition())
        {
            throw new TimeoutException($"Condition not met within {timeout.TotalMilliseconds} ms");
        }
    }

    // ---- test doubles ----

    private sealed class FakeStreamingSession : IStreamingSession
    {
        public event Action<StreamingTranscriptEvent>? TranscriptReceived;
        public readonly List<byte[]> SentChunks = [];
        private readonly SemaphoreSlim _sendConcurrencyGuard = new(1, 1);
        public Func<byte[], Task>? OnSendAudio;
        public Func<byte[], CancellationToken, Task>? OnSendAudioWithCancellation;
        public Func<CancellationToken, Task>? OnFinalize;
        public Func<Task>? OnDispose;
        public bool Disposed;
        private int _finalizeCallCount;
        private int _finalizeObservedDuringSend;
        private int _sendInFlight;

        public int FinalizeCallCount => Volatile.Read(ref _finalizeCallCount);
        public bool FinalizeObservedDuringSend =>
            Volatile.Read(ref _finalizeObservedDuringSend) == 1;

        public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            // A9 regression: any concurrent caller throws.
            if (!await _sendConcurrencyGuard.WaitAsync(0, ct))
            {
                throw new InvalidOperationException("Concurrent SendAudioAsync (A9 violation)");
            }
            try
            {
                Interlocked.Increment(ref _sendInFlight);
                var copy = pcm16.ToArray();
                lock (SentChunks) SentChunks.Add(copy);
                if (OnSendAudioWithCancellation is not null)
                {
                    await OnSendAudioWithCancellation(copy, ct);
                }
                if (OnSendAudio is not null) await OnSendAudio(copy);
            }
            finally
            {
                Interlocked.Decrement(ref _sendInFlight);
                _sendConcurrencyGuard.Release();
            }
        }

        public Task FinalizeAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _finalizeCallCount);
            if (Volatile.Read(ref _sendInFlight) > 0)
            {
                Volatile.Write(ref _finalizeObservedDuringSend, 1);
            }

            return OnFinalize?.Invoke(ct) ?? Task.CompletedTask;
        }

        public void RaisePartial(string text) =>
            TranscriptReceived?.Invoke(new StreamingTranscriptEvent(text, false));

        public void RaiseFinal(string text) =>
            TranscriptReceived?.Invoke(new StreamingTranscriptEvent(text, true));

        public async ValueTask DisposeAsync()
        {
            Disposed = true;
            if (OnDispose is not null) await OnDispose();
        }
    }

    private sealed class FakePlugin : ITranscriptionEnginePlugin
    {
        public Func<CancellationToken, Task<IStreamingSession>>? OnStartStreaming;

        public string PluginId => "com.test.fake.streaming";
        public string PluginName => "Fake Streaming";
        public string PluginVersion => "1.0.0";
        public string ProviderId => "fake-stream";
        public string ProviderDisplayName => "Fake Streaming";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels => [];
        public string? SelectedModelId => null;
        public bool SupportsTranslation => false;
        public bool SupportsStreaming => true;

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public void SelectModel(string modelId) { }
        public void Dispose() { }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct) =>
            (OnStartStreaming ?? throw new NotSupportedException())(ct);
    }
}
