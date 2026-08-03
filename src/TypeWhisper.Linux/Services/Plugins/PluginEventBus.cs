using System.Diagnostics;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services.Plugins;

/// <summary>
///     Thread-safe publish/subscribe event bus for plugin communication.
///     Each subscription owns a FIFO queue and an on-demand thread-pool worker, so
///     its handler is ordered and non-reentrant while separate subscriptions progress
///     independently. Handler exceptions are isolated to the event being delivered.
/// </summary>
/// <remarks>
///     Pending non-terminal <see cref="ICoalescibleEvent" /> instances use latest-wins
///     delivery: an older pending non-terminal event of the same runtime type is removed
///     and the latest event is appended at its publish position. A terminal frame
///     (<see cref="ICoalescibleEvent.IsTerminalFrame" />) is always appended and is never
///     the target of a later replacement, preserving stream-endpoint fidelity.
///     Non-coalescible events are never dropped, so bursts limited to a finite set of
///     coalescible types have bounded pending queues.
///
///     Unsubscribing abandons queued events and lets an in-flight handler complete.
///     Disposing the bus applies the same abandon policy to every subscription and
///     waits for their in-flight workers to exit, up to a bounded deadline; any handler
///     still running past the deadline is abandoned (traced) so disposal always
///     completes. Publishes after disposal are ignored.
/// </remarks>
public sealed class PluginEventBus : IPluginEventBus, IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan s_defaultDisposeTimeout = TimeSpan.FromSeconds(5);

    private readonly Dictionary<Type, List<Subscription>> _subscriptions = [];
    private readonly HashSet<Subscription> _trackedSubscriptions = [];
    private readonly Lock _lock = new();
    private readonly TimeSpan _disposeTimeout;
    private Task? _disposeTask;
    private bool _disposed;

    public PluginEventBus()
        : this(s_defaultDisposeTimeout) { }

    // Test seam: lets tests inject a short deadline to exercise abandon-on-timeout.
    internal PluginEventBus(TimeSpan disposeTimeout)
    {
        _disposeTimeout = disposeTimeout;
    }

    public void Publish<T>(T pluginEvent)
        where T : PluginEvent
    {
        var eventType = typeof(T);
        lock (_lock)
        {
            if (
                _disposed
                || !_subscriptions.TryGetValue(eventType, out var subscriptions)
            )
            {
                return;
            }

            foreach (var subscription in subscriptions)
            {
                subscription.Enqueue(pluginEvent);
            }
        }
    }

    public IDisposable Subscribe<T>(Func<T, Task> handler)
        where T : PluginEvent
    {
        var eventType = typeof(T);
        // ReSharper disable once MoveLocalFunctionAfterJumpStatement -- declared immediately above its only use, which is the next line.
        Task WrappedHandler(object obj) => handler((T)obj);
        var subscription = new Subscription(this, eventType, WrappedHandler);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_subscriptions.TryGetValue(eventType, out var subscriptions))
            {
                subscriptions = [];
                _subscriptions.Add(eventType, subscriptions);
            }

            subscriptions.Add(subscription);
            _trackedSubscriptions.Add(subscription);
        }

        return subscription;
    }

    public void Dispose()
    {
        GetOrStartDisposeTask().GetAwaiter().GetResult();
        // ReSharper disable once GCSuppressFinalizeForTypeWithoutDestructor -- satisfies CA1816; keeps the standard Dispose pattern if a finalizer is ever added.
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await GetOrStartDisposeTask().ConfigureAwait(false);
        // ReSharper disable once GCSuppressFinalizeForTypeWithoutDestructor -- satisfies CA1816; keeps the standard Dispose pattern if a finalizer is ever added.
        GC.SuppressFinalize(this);
    }

    private Task GetOrStartDisposeTask()
    {
        Subscription[] subscriptions;
        Task disposeTask;
        lock (_lock)
        {
            if (_disposeTask is not null)
            {
                return _disposeTask;
            }

            _disposed = true;
            subscriptions = _trackedSubscriptions.ToArray();
            _subscriptions.Clear();

            var completion = Task.WhenAll(
                subscriptions.Select(subscription => subscription.Completion)
            );
            disposeTask = WaitForWorkersAsync(completion);
            _disposeTask = disposeTask;
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Stop();
        }

        return disposeTask;
    }

    // Bounded wait so a hung handler can't stall process exit; on timeout the
    // in-flight workers (at most one per subscription) are simply abandoned.
    private async Task WaitForWorkersAsync(Task completion)
    {
        var finished = await Task.WhenAny(completion, Task.Delay(_disposeTimeout))
            .ConfigureAwait(false);
        if (!ReferenceEquals(finished, completion))
        {
            Trace.WriteLine(
                $"[PluginEventBus] Dispose deadline of {_disposeTimeout.TotalMilliseconds:F0}ms elapsed; abandoning in-flight handlers."
            );
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_lock)
        {
            if (
                _subscriptions.TryGetValue(
                    subscription.EventType,
                    out var subscriptions
                )
            )
            {
                subscriptions.Remove(subscription);
                if (subscriptions.Count == 0)
                {
                    _subscriptions.Remove(subscription.EventType);
                }
            }
        }

        subscription.Stop();
    }

    private void OnSubscriptionStopped(Subscription subscription)
    {
        lock (_lock)
        {
            _trackedSubscriptions.Remove(subscription);
        }
    }

    private sealed class Subscription(
        PluginEventBus owner,
        Type eventType,
        Func<object, Task> handler
    ) : IDisposable
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        // ReSharper disable once ReplaceWithPrimaryConstructorParameter -- keep an explicit named field, matching how this class projects its other ctor params into named members.
        private readonly Func<object, Task> _handler = handler;
        private readonly Lock _lock = new();
        // ReSharper disable once ReplaceWithPrimaryConstructorParameter -- keep an explicit named field, matching how this class projects its other ctor params into named members.
        private readonly PluginEventBus _owner = owner;
        private readonly LinkedList<object> _queue = [];
        private Task? _workerTask;
        private bool _stopped;

        public Task Completion => _completion.Task;

        public Type EventType { get; } = eventType;

        public void Enqueue(object pluginEvent)
        {
            lock (_lock)
            {
                if (_stopped)
                {
                    return;
                }

                if (pluginEvent is ICoalescibleEvent { IsTerminalFrame: false })
                {
                    RemovePendingNonTerminalEventOfType(pluginEvent.GetType());
                }

                _queue.AddLast(pluginEvent);
                if (_workerTask is null)
                {
                    StartWorker();
                }
            }
        }

        public void Dispose()
        {
            _owner.Unsubscribe(this);
        }

        public void Stop()
        {
            var stoppedWithoutWorker = false;
            lock (_lock)
            {
                if (_stopped)
                {
                    return;
                }

                _stopped = true;
                _queue.Clear();
                if (_workerTask is null)
                {
                    _completion.TrySetResult();
                    stoppedWithoutWorker = true;
                }
            }

            if (stoppedWithoutWorker)
            {
                _owner.OnSubscriptionStopped(this);
            }
        }

        private void RemovePendingNonTerminalEventOfType(Type eventType)
        {
            for (var node = _queue.First; node is not null; node = node.Next)
            {
                if (
                    node.Value.GetType() != eventType
                    || node.Value is not ICoalescibleEvent { IsTerminalFrame: false }
                )
                {
                    continue;
                }

                _queue.Remove(node);
                return;
            }
        }

        private void StartWorker()
        {
            _workerTask = Task.Run(ProcessQueueAsync);
            _ = _workerTask.ContinueWith(
                static (workerTask, state) =>
                    ((Subscription)state!).OnWorkerCompleted(workerTask),
                this,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }

        private async Task ProcessQueueAsync()
        {
            while (true)
            {
                object pluginEvent;
                lock (_lock)
                {
                    if (_stopped || _queue.First is null)
                    {
                        return;
                    }

                    pluginEvent = _queue.First.Value;
                    _queue.RemoveFirst();
                }

                try
                {
                    await _handler(pluginEvent).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(
                        $"[PluginEventBus] Handler for {pluginEvent.GetType().Name} threw: {ex.Message}"
                    );
                }
            }
        }

        private void OnWorkerCompleted(Task workerTask)
        {
            if (workerTask.IsFaulted)
            {
                Trace.WriteLine(
                    $"[PluginEventBus] Subscription worker threw: {workerTask.Exception}"
                );
            }

            var stopped = false;
            lock (_lock)
            {
                if (!ReferenceEquals(_workerTask, workerTask))
                {
                    return;
                }

                _workerTask = null;
                if (_stopped)
                {
                    _queue.Clear();
                    _completion.TrySetResult();
                    stopped = true;
                }
                else if (_queue.Count > 0)
                {
                    StartWorker();
                }
            }

            if (stopped)
            {
                _owner.OnSubscriptionStopped(this);
            }
        }
    }
}
