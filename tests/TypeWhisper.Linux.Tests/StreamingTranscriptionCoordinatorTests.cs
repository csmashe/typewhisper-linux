using System.Diagnostics;
using System.Net.Http;
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
            OnStartStreaming = _ => connectTcs.Task
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
        var earlyPresent = markers.Count(m => m >= 5 && m <= 50);
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
            coord.AcceptAudioFrame(MakeMarkedFrame(i, 32), 16000);
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
        coord.AcceptAudioFrame(MakeMarkedFrame(0, 32), 16000);

        var sw = Stopwatch.StartNew();
        for (var i = 1; i <= 10; i++)
        {
            coord.AcceptAudioFrame(MakeMarkedFrame(i, 32), 16000);
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
        coord.AcceptAudioFrame(MakeMarkedFrame(1, 32), 16000);

        var observed = await faultTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<WebSocketException>(observed);
        Assert.True(coord.Faulted);
    }

    [Fact]
    public async Task Fault_OnSendException_GenericException_RoutesViaOnFault()
    {
        // Regression: plugin SendAudioAsync implementations can throw arbitrary
        // exception types (HttpRequestException from REST-style streamers,
        // plugin-internal exceptions, etc). The sender must route ALL non-cancel
        // failures through HandleFault so Phase 4's batch fallback fires.
        var session = new FakeStreamingSession();
        session.OnSendAudio = _ => throw new HttpRequestException("simulated REST failure");
        var faultTcs = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, ex => faultTcs.TrySetResult(ex));

        await coord.StartAsync(CancellationToken.None);
        coord.AcceptAudioFrame(MakeMarkedFrame(1, 32), 16000);

        var observed = await faultTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<HttpRequestException>(observed);
        Assert.True(coord.Faulted);
    }

    [Fact]
    public async Task Fault_OnConnectException_PropagatesViaOnFault()
    {
        var faultTcs = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new FakePlugin
        {
            OnStartStreaming = _ => Task.FromException<IStreamingSession>(
                new HttpRequestException("auth failed (simulated)"))
        };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, ex => faultTcs.TrySetResult(ex));

        // StartAsync swallows the connect exception and routes it via onFault.
        await coord.StartAsync(CancellationToken.None);

        var observed = await faultTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<HttpRequestException>(observed);
        Assert.True(coord.Faulted);

        // Verify no audio is delivered after fault.
        coord.AcceptAudioFrame(MakeMarkedFrame(1, 32), 16000);
        await Task.Delay(50);
        // No fake session was ever returned, so SentChunks doesn't apply here.
        // Faulted alone guards the path.
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
            coord.AcceptAudioFrame(MakeMarkedFrame(i, 32), 16000);
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
            coord.AcceptAudioFrame(MakeMarkedFrame(i, 32), 16000);
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
    }

    [Fact]
    public async Task FinalizeAsync_GraceWindow_WaitsForLateTrailingFinal()
    {
        var session = new FakeStreamingSession();
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, _ => { });

        await coord.StartAsync(CancellationToken.None);

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
    public async Task FinalizeAsync_HonorsCallerCancellation()
    {
        // Regression: caller-supplied ct must collapse each phase's wait — sender
        // drain, session.FinalizeAsync, and the grace window — instead of forcing
        // the caller to block for the full timeout sum (up to ~4.5s) when an
        // aborted shutdown needs to tear down quickly.
        var session = new FakeStreamingSession();
        // session.FinalizeAsync honors ct: blocks until ct fires.
        session.OnFinalize = ct => Task.Delay(Timeout.Infinite, ct);
        var plugin = new FakePlugin { OnStartStreaming = _ => Task.FromResult<IStreamingSession>(session) };

        await using var coord = new StreamingTranscriptionCoordinator(
            plugin, null, 1, (_, _) => { }, _ => { });

        await coord.StartAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);

        var sw = Stopwatch.StartNew();
        var result = await coord.FinalizeAsync(cts.Token);
        sw.Stop();

        // Without ct propagation this would block ~2s (sessionTimeout) + 500ms grace.
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"FinalizeAsync should honor caller's cancellation; took {sw.ElapsedMilliseconds} ms");
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
                new HttpRequestException("simulated"))
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
        coord.AcceptAudioFrame(MakeMarkedFrame(1, 32), 16000);

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
            }, ct)
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
            OnStartStreaming = _ => connectTcs.Task
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

        coord.AcceptAudioFrame(MakeMarkedFrame(1, 32), 16000);

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
        public readonly List<byte[]> SentChunks = new();
        public readonly SemaphoreSlim SendConcurrencyGuard = new(1, 1);
        public Func<byte[], Task>? OnSendAudio;
        public Func<CancellationToken, Task>? OnFinalize;
        public bool Disposed;

        public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            // A9 regression: any concurrent caller throws.
            if (!await SendConcurrencyGuard.WaitAsync(0, ct))
            {
                throw new InvalidOperationException("Concurrent SendAudioAsync (A9 violation)");
            }
            try
            {
                var copy = pcm16.ToArray();
                lock (SentChunks) SentChunks.Add(copy);
                if (OnSendAudio is not null) await OnSendAudio(copy);
            }
            finally
            {
                SendConcurrencyGuard.Release();
            }
        }

        public Task FinalizeAsync(CancellationToken ct) =>
            OnFinalize?.Invoke(ct) ?? Task.CompletedTask;

        public void RaisePartial(string text) =>
            TranscriptReceived?.Invoke(new StreamingTranscriptEvent(text, false));

        public void RaiseFinal(string text) =>
            TranscriptReceived?.Invoke(new StreamingTranscriptEvent(text, true));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
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

        public Task<IStreamingSession> StartStreamingAsync(string? language, CancellationToken ct)
        {
            if (OnStartStreaming is null) throw new NotSupportedException();
            return OnStartStreaming(ct);
        }
    }
}
