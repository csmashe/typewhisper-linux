using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class PluginEventBusTests
{
    private readonly PluginEventBus _bus = new();

    [Fact]
    public async Task Subscribe_ReceivesPublishedEvent()
    {
        var tcs = new TaskCompletionSource<RecordingStartedEvent>();

        _bus.Subscribe<RecordingStartedEvent>(e =>
        {
            tcs.SetResult(e);
            return Task.CompletedTask;
        });

        var published = new RecordingStartedEvent();
        _bus.Publish(published);

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(published.Timestamp, received.Timestamp);
    }

    [Fact]
    public async Task Subscribe_ReceivesCorrectEventType()
    {
        var tcs = new TaskCompletionSource<TranscriptionCompletedEvent>();
        var wrongTypeCalled = false;

        _bus.Subscribe<RecordingStartedEvent>(_ =>
        {
            wrongTypeCalled = true;
            return Task.CompletedTask;
        });

        _bus.Subscribe<TranscriptionCompletedEvent>(e =>
        {
            tcs.SetResult(e);
            return Task.CompletedTask;
        });

        var published = new TranscriptionCompletedEvent { Text = "hello" };
        _bus.Publish(published);

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("hello", received.Text);

        await Task.Delay(100);
        Assert.False(wrongTypeCalled);
    }

    [Fact]
    public async Task MultipleSubscribers_AllReceiveEvent()
    {
        var tcs1 = new TaskCompletionSource<RecordingStartedEvent>();
        var tcs2 = new TaskCompletionSource<RecordingStartedEvent>();

        _bus.Subscribe<RecordingStartedEvent>(e =>
        {
            tcs1.SetResult(e);
            return Task.CompletedTask;
        });

        _bus.Subscribe<RecordingStartedEvent>(e =>
        {
            tcs2.SetResult(e);
            return Task.CompletedTask;
        });

        _bus.Publish(new RecordingStartedEvent());

        await Task.WhenAll(
            tcs1.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            tcs2.Task.WaitAsync(TimeSpan.FromSeconds(2))
        );

        Assert.True(tcs1.Task.IsCompletedSuccessfully);
        Assert.True(tcs2.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public void Publish_WithNoSubscribers_DoesNotThrow()
    {
        var ex = Record.Exception(() => _bus.Publish(new RecordingStartedEvent()));
        Assert.Null(ex);
    }

    [Fact]
    public async Task Dispose_Subscription_StopsReceivingEvents()
    {
        var callCount = 0;

        var subscription = _bus.Subscribe<RecordingStartedEvent>(_ =>
        {
            Interlocked.Increment(ref callCount);
            return Task.CompletedTask;
        });

        _bus.Publish(new RecordingStartedEvent());
        await Task.Delay(200);
        Assert.Equal(1, callCount);

        subscription.Dispose();

        _bus.Publish(new RecordingStartedEvent());
        await Task.Delay(200);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void Dispose_Subscription_TwiceDoesNotThrow()
    {
        var subscription = _bus.Subscribe<RecordingStartedEvent>(_ => Task.CompletedTask);

        var ex = Record.Exception(() =>
        {
            subscription.Dispose();
            subscription.Dispose();
        });

        Assert.Null(ex);
    }

    [Fact]
    public async Task ExceptionInHandler_DoesNotAffectOtherHandlers()
    {
        var tcs = new TaskCompletionSource<bool>();

        _bus.Subscribe<RecordingStartedEvent>(_ => throw new InvalidOperationException("Boom!"));

        _bus.Subscribe<RecordingStartedEvent>(_ =>
        {
            tcs.SetResult(true);
            return Task.CompletedTask;
        });

        _bus.Publish(new RecordingStartedEvent());

        var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result);
    }

    [Fact]
    public async Task Publish_EventDataIsPreserved()
    {
        var tcs = new TaskCompletionSource<RecordingStoppedEvent>();

        _bus.Subscribe<RecordingStoppedEvent>(e =>
        {
            tcs.SetResult(e);
            return Task.CompletedTask;
        });

        _bus.Publish(new RecordingStoppedEvent { DurationSeconds = 42.5 });

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(42.5, received.DurationSeconds);
    }

    [Fact]
    public async Task ConcurrentPublishAndSubscribe_DoesNotThrow()
    {
        var received = 0;

        var subscriptions = new List<IDisposable>();
        var subscribeTasks = Enumerable
            .Range(0, 10)
            .Select(_ =>
                Task.Run(() =>
                {
                    var sub = _bus.Subscribe<RecordingStartedEvent>(_ =>
                    {
                        Interlocked.Increment(ref received);
                        return Task.CompletedTask;
                    });
                    lock (subscriptions)
                    {
                        subscriptions.Add(sub);
                    }
                })
            );

        var publishTasks = Enumerable
            .Range(0, 10)
            .Select(_ =>
                Task.Run(() =>
                {
                    _bus.Publish(new RecordingStartedEvent());
                })
            );

        var ex = await Record.ExceptionAsync(async () =>
        {
            await Task.WhenAll(subscribeTasks.Concat(publishTasks));
        });

        Assert.Null(ex);

        foreach (var sub in subscriptions)
        {
            sub.Dispose();
        }
    }

    [Fact]
    public async Task Publish_ToSlowSubscriber_DeliversFifoWithoutReentrancy()
    {
        const int eventCount = 12;
        var handlerGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var firstEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var allDelivered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var received = new List<int>();
        var activeHandlers = 0;
        var delivered = 0;
        var overlapped = 0;

        _bus.Subscribe<SequencedEvent>(async pluginEvent =>
        {
            if (Interlocked.Increment(ref activeHandlers) > 1)
            {
                // ReSharper disable once AccessToModifiedClosure -- overlapped is written from the handler and read in the test body; access is Interlocked by design.
                Interlocked.Exchange(ref overlapped, 1);
            }

            lock (received)
            {
                received.Add(pluginEvent.Sequence);
            }

            firstEntered.TrySetResult(true);
            try
            {
                await handlerGate.Task;
                await Task.Delay(10);
            }
            finally
            {
                Interlocked.Decrement(ref activeHandlers);
                if (Interlocked.Increment(ref delivered) == eventCount)
                {
                    allDelivered.TrySetResult(true);
                }
            }
        });

        for (var sequence = 0; sequence < eventCount; sequence++)
        {
            _bus.Publish(new SequencedEvent(sequence));
        }

        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(100);
        }
        finally
        {
            handlerGate.TrySetResult(true);
        }

        await allDelivered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, Volatile.Read(ref overlapped));
        lock (received)
        {
            Assert.Equal(Enumerable.Range(0, eventCount), received);
        }
    }

    [Fact]
    public async Task Publish_SlowSubscriber_DoesNotDelayIndependentSubscriber()
    {
        var slowHandlerGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var slowHandlerEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var slowHandlerCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var fastHandlerCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        _bus.Subscribe<SequencedEvent>(async _ =>
        {
            slowHandlerEntered.TrySetResult(true);
            await slowHandlerGate.Task;
            slowHandlerCompleted.TrySetResult(true);
        });
        _bus.Subscribe<SequencedEvent>(_ =>
        {
            fastHandlerCompleted.TrySetResult(true);
            return Task.CompletedTask;
        });

        _bus.Publish(new SequencedEvent(0));

        try
        {
            await slowHandlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await fastHandlerCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(slowHandlerCompleted.Task.IsCompleted);
        }
        finally
        {
            slowHandlerGate.TrySetResult(true);
        }

        await slowHandlerCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Publish_CoalescesLatestByType_WithoutDroppingDurableEvent()
    {
        var handlerGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var firstEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var latestDelivered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var received = new List<string>();

        _bus.Subscribe<PluginEvent>(async pluginEvent =>
        {
            var description = pluginEvent switch
            {
                PartialTranscriptionUpdateEvent partial =>
                    $"partial:{partial.PartialText}",
                LlmResponseTokenEvent token => $"llm:{token.AccumulatedText}",
                TranscriptionCompletedEvent completed => $"completed:{completed.Text}",
                _ => throw new InvalidOperationException(
                    $"Unexpected event type {pluginEvent.GetType().Name}."
                ),
            };

            lock (received)
            {
                received.Add(description);
            }

            if (
                pluginEvent
                is PartialTranscriptionUpdateEvent
                {
                    PartialText: "partial-0",
                }
            )
            {
                firstEntered.TrySetResult(true);
                await handlerGate.Task;
            }

            if (
                pluginEvent
                is LlmResponseTokenEvent
                {
                    AccumulatedText: "llm-2",
                }
            )
            {
                latestDelivered.TrySetResult(true);
            }
        });

        _bus.Publish<PluginEvent>(
            new PartialTranscriptionUpdateEvent { PartialText = "partial-0" }
        );

        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            _bus.Publish<PluginEvent>(
                new PartialTranscriptionUpdateEvent { PartialText = "partial-1" }
            );
            _bus.Publish<PluginEvent>(
                new PartialTranscriptionUpdateEvent { PartialText = "partial-2" }
            );
            _bus.Publish<PluginEvent>(
                new LlmResponseTokenEvent { AccumulatedText = "llm-1" }
            );
            _bus.Publish<PluginEvent>(
                new TranscriptionCompletedEvent { Text = "durable" }
            );
            _bus.Publish<PluginEvent>(
                new PartialTranscriptionUpdateEvent { PartialText = "partial-3" }
            );
            _bus.Publish<PluginEvent>(
                new LlmResponseTokenEvent { AccumulatedText = "llm-2" }
            );

            await Task.Delay(100);
            lock (received)
            {
                Assert.Equal(["partial:partial-0"], received);
            }
        }
        finally
        {
            handlerGate.TrySetResult(true);
        }

        await latestDelivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lock (received)
        {
            Assert.Equal(
                [
                    "partial:partial-0",
                    "completed:durable",
                    "partial:partial-3",
                    "llm:llm-2",
                ],
                received
            );
        }
    }

    [Fact]
    public async Task Dispose_Subscription_DiscardsQueueAndCompletesInFlightHandler()
    {
        var handlerGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var firstEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var inFlightCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var received = new List<int>();

        var subscription = _bus.Subscribe<SequencedEvent>(async pluginEvent =>
        {
            lock (received)
            {
                received.Add(pluginEvent.Sequence);
            }

            if (pluginEvent.Sequence == 0)
            {
                firstEntered.TrySetResult(true);
            }

            await handlerGate.Task;
            if (pluginEvent.Sequence == 0)
            {
                inFlightCompleted.TrySetResult(true);
            }
        });

        _bus.Publish(new SequencedEvent(0));

        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            for (var sequence = 1; sequence < 6; sequence++)
            {
                _bus.Publish(new SequencedEvent(sequence));
            }

            await Task.Delay(100);
            subscription.Dispose();
        }
        finally
        {
            handlerGate.TrySetResult(true);
        }

        await inFlightCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        lock (received)
        {
            Assert.Equal([0], received);
        }
    }

    [Fact]
    public async Task ExceptionInHandler_DoesNotStopLaterQueuedEvent()
    {
        var handlerGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var firstEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var secondDelivered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        _bus.Subscribe<SequencedEvent>(async pluginEvent =>
        {
            if (pluginEvent.Sequence == 0)
            {
                firstEntered.TrySetResult(true);
                await handlerGate.Task;
                throw new InvalidOperationException("Boom!");
            }

            secondDelivered.TrySetResult(true);
        });

        _bus.Publish(new SequencedEvent(0));

        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            _bus.Publish(new SequencedEvent(1));
        }
        finally
        {
            handlerGate.TrySetResult(true);
        }

        await secondDelivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DisposeAsync_AbandonsQueueAndWaitsForInFlightWorker()
    {
        var handlerGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var firstEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var inFlightCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var calls = 0;

        _bus.Subscribe<SequencedEvent>(async pluginEvent =>
        {
            // ReSharper disable once AccessToModifiedClosure -- calls is incremented from the handler and read in the test body; access is Interlocked by design.
            Interlocked.Increment(ref calls);
            if (pluginEvent.Sequence == 0)
            {
                firstEntered.TrySetResult(true);
                await handlerGate.Task;
                inFlightCompleted.TrySetResult(true);
            }
        });

        _bus.Publish(new SequencedEvent(0));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        _bus.Publish(new SequencedEvent(1));

        var disposeTask = _bus.DisposeAsync().AsTask();
        try
        {
            await Task.Delay(100);
            Assert.False(disposeTask.IsCompleted);
        }
        finally
        {
            handlerGate.TrySetResult(true);
        }

        await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(inFlightCompleted.Task.IsCompletedSuccessfully);
        Assert.Equal(1, Volatile.Read(ref calls));

        _bus.Publish(new SequencedEvent(2));
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task TranscriptionCompletedEvent_FullPayload()
    {
        var tcs = new TaskCompletionSource<TranscriptionCompletedEvent>();

        _bus.Subscribe<TranscriptionCompletedEvent>(e =>
        {
            tcs.SetResult(e);
            return Task.CompletedTask;
        });

        _bus.Publish(
            new TranscriptionCompletedEvent
            {
                Text = "Hello world",
                DetectedLanguage = "en",
                DurationSeconds = 3.5,
                ModelId = "whisper-large-v3",
            }
        );

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Hello world", received.Text);
        Assert.Equal("en", received.DetectedLanguage);
        Assert.Equal(3.5, received.DurationSeconds);
        Assert.Equal("whisper-large-v3", received.ModelId);
    }

    [Fact]
    public async Task TextInsertedEvent_IsDelivered()
    {
        var tcs = new TaskCompletionSource<TextInsertedEvent>();

        _bus.Subscribe<TextInsertedEvent>(e =>
        {
            tcs.SetResult(e);
            return Task.CompletedTask;
        });

        _bus.Publish(new TextInsertedEvent { Text = "inserted", AppName = "notepad" });

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("inserted", received.Text);
        Assert.Equal("notepad", received.AppName);
    }

    [Fact]
    public async Task TranscriptionFailedEvent_IsDelivered()
    {
        var tcs = new TaskCompletionSource<TranscriptionFailedEvent>();

        _bus.Subscribe<TranscriptionFailedEvent>(e =>
        {
            tcs.SetResult(e);
            return Task.CompletedTask;
        });

        _bus.Publish(new TranscriptionFailedEvent { ErrorMessage = "timeout", ModelId = "m1" });

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("timeout", received.ErrorMessage);
        Assert.Equal("m1", received.ModelId);
    }

    [Fact]
    public async Task Publish_QueuedTerminalFrame_SurvivesBurstOfLaterNonTerminalSameType()
    {
        var handlerGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var firstEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var latestDelivered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var received = new List<string>();

        _bus.Subscribe<LlmResponseTokenEvent>(async pluginEvent =>
        {
            lock (received)
            {
                received.Add(pluginEvent.AccumulatedText);
            }

            if (pluginEvent.AccumulatedText == "gate")
            {
                firstEntered.TrySetResult(true);
                await handlerGate.Task;
            }

            if (pluginEvent.AccumulatedText == "non-final-latest")
            {
                latestDelivered.TrySetResult(true);
            }
        });

        _bus.Publish(new LlmResponseTokenEvent { AccumulatedText = "gate" });

        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            _bus.Publish(
                new LlmResponseTokenEvent { AccumulatedText = "final", IsFinal = true }
            );
            _bus.Publish(new LlmResponseTokenEvent { AccumulatedText = "non-final-1" });
            _bus.Publish(new LlmResponseTokenEvent { AccumulatedText = "non-final-2" });
            _bus.Publish(
                new LlmResponseTokenEvent { AccumulatedText = "non-final-latest" }
            );
        }
        finally
        {
            handlerGate.TrySetResult(true);
        }

        await latestDelivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lock (received)
        {
            // "final" survives the burst undisplaced; only the latest non-final coalesces in.
            Assert.Equal(["gate", "final", "non-final-latest"], received);
        }
    }

    [Fact]
    public async Task Publish_TerminalFrame_NeverReplacesPendingNonTerminalSameType()
    {
        var handlerGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var firstEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var finalDelivered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var received = new List<string>();

        _bus.Subscribe<LlmResponseTokenEvent>(async pluginEvent =>
        {
            lock (received)
            {
                received.Add(pluginEvent.AccumulatedText);
            }

            if (pluginEvent.AccumulatedText == "gate")
            {
                firstEntered.TrySetResult(true);
                await handlerGate.Task;
            }

            if (pluginEvent is { IsFinal: true })
            {
                finalDelivered.TrySetResult(true);
            }
        });

        _bus.Publish(new LlmResponseTokenEvent { AccumulatedText = "gate" });

        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            _bus.Publish(new LlmResponseTokenEvent { AccumulatedText = "non-final" });
            _bus.Publish(
                new LlmResponseTokenEvent { AccumulatedText = "final", IsFinal = true }
            );
        }
        finally
        {
            handlerGate.TrySetResult(true);
        }

        await finalDelivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lock (received)
        {
            // The terminal frame appends after the pending non-final one rather than
            // coalescing it away; both are delivered, order preserved.
            Assert.Equal(["gate", "non-final", "final"], received);
        }
    }

    [Fact]
    public async Task Dispose_WithHungHandler_ReturnsWithinBoundedDeadline()
    {
        var handlerGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var firstEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var bus = new PluginEventBus(TimeSpan.FromMilliseconds(200));

        bus.Subscribe<SequencedEvent>(async _ =>
        {
            firstEntered.TrySetResult(true);
            await handlerGate.Task;
        });

        bus.Publish(new SequencedEvent(0));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            // Handler never returns; the outer WaitAsync fails the test if disposal doesn't
            // complete within its own bounded deadline.
            // ReSharper disable once DisposeOnUsingVariable -- explicit dispose is the assertion under test; the await using re-dispose at scope end is idempotent.
            await bus.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            handlerGate.TrySetResult(true);
        }
    }

    private sealed record SequencedEvent(int Sequence) : PluginEvent;
}
