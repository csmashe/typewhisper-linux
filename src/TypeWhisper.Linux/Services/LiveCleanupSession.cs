using System.Diagnostics;

namespace TypeWhisper.Linux.Services;

// One revisable draft and one request at a time. A later hypothesis replaces pending
// work; it never queues a request per token or commits text to the target application.
internal sealed class LiveCleanupSession : IDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<string, CancellationToken, Task<string?>> _process;
    private readonly Action<string, string> _publish;
    private readonly TimeSpan _debounce;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, CancellationToken, Task<string>>? _prepare;
    private long _latestChangedAt;
    private string? _latest, _cachedInput, _cachedOutput, _activeInput;
    private TaskCompletionSource<string?>? _active;
    private Task? _worker;
    private bool _stopped, _disposed;

    public LiveCleanupSession(Func<string, CancellationToken, Task<string?>> process,
        Action<string, string> publish, TimeSpan? debounce = null, TimeProvider? timeProvider = null,
        Func<string, CancellationToken, Task<string>>? prepare = null)
    {
        _process = process;
        _publish = publish;
        _debounce = debounce ?? TimeSpan.FromSeconds(2);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _prepare = prepare;
    }

    public void Submit(string text)
    {
        lock (_gate)
        {
            if (_stopped || string.IsNullOrWhiteSpace(text) || text.Length > 8192 || text == _latest)
                return;
            _latest = text;
            _latestChangedAt = _timeProvider.GetTimestamp();
            if (_worker is null || _worker.IsCompleted)
                _worker = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                string input;
                TaskCompletionSource<string?> completion;
                while (true)
                {
                    TimeSpan remaining;
                    lock (_gate)
                    {
                        if (_stopped || _latest is null) return;
                        // Time spent behind the previous request counts toward the quiet
                        // period. A revision during the delay gets its own quiet period.
                        remaining = _debounce - _timeProvider.GetElapsedTime(_latestChangedAt);
                        if (remaining <= TimeSpan.Zero)
                        {
                            input = _latest;
                            _activeInput = input;
                            completion = _active = new(TaskCreationOptions.RunContinuationsAsynchronously);
                            break;
                        }
                    }
                    await Task.Delay(remaining, _timeProvider, _cts.Token).ConfigureAwait(false);
                }
                string? output = null;
                var prepared = input;
                try
                {
                    if (_prepare is not null)
                        prepared = await _prepare(input, _cts.Token).ConfigureAwait(false);
                    lock (_gate) _activeInput = prepared;
                    output = await _process(prepared, _cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate)
                    {
                        if (!_disposed && !string.IsNullOrWhiteSpace(output))
                        {
                            _cachedInput = prepared;
                            _cachedOutput = output;
                            if (!_stopped && _latest == input) _publish(input, output);
                        }
                        completion.TrySetResult(_disposed ? null : output);
                    }
                }
                lock (_gate)
                {
                    if (_stopped || _latest == input)
                    {
                        _worker = null;
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Trace.WriteLine($"[LiveCleanup] Background cleanup failed: {ex.GetType().Name}");
        }
    }

    public void StopScheduling() { lock (_gate) _stopped = true; }

    public async Task<string?> ReuseAsync(string input, CancellationToken token)
    {
        Task<string?>? pending;
        Task? cancellation = null;
        lock (_gate)
        {
            if (_disposed) return null;
            if (_cachedInput == input) return _cachedOutput;
            pending = _activeInput == input ? _active?.Task : null;
            // Final cleanup needs the provider next. Release work that cannot be reused
            // rather than letting an obsolete request compete with the final request.
            if (pending is null && _stopped) cancellation = _cts.CancelAsync();
        }
        if (cancellation is not null)
            _ = cancellation.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return pending is null ? null : await pending.WaitAsync(token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Task? worker;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = _stopped = true;
            worker = _worker;
        }
        var cancellation = _cts.CancelAsync();
        _ = Task.WhenAll(worker ?? Task.CompletedTask, cancellation).ContinueWith(
            task => { _ = task.Exception; _cts.Dispose(); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
