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
    private string? _latest, _cachedInput, _cachedOutput, _activeInput;
    private TaskCompletionSource<string?>? _active;
    private Task? _worker;
    private bool _stopped, _disposed;

    public LiveCleanupSession(Func<string, CancellationToken, Task<string?>> process,
        Action<string, string> publish, TimeSpan? debounce = null)
    {
        _process = process;
        _publish = publish;
        _debounce = debounce ?? TimeSpan.FromSeconds(2);
    }

    public void Submit(string text)
    {
        lock (_gate)
        {
            if (_stopped || string.IsNullOrWhiteSpace(text) || text.Length > 8192 || text == _latest)
                return;
            _latest = text;
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
                await Task.Delay(_debounce, _cts.Token).ConfigureAwait(false);
                string input;
                TaskCompletionSource<string?> completion;
                lock (_gate)
                {
                    if (_stopped || _latest is null) return;
                    input = _latest;
                    _activeInput = input;
                    completion = _active = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                string? output = null;
                try { output = await _process(input, _cts.Token).ConfigureAwait(false); }
                finally
                {
                    lock (_gate)
                    {
                        if (!_disposed && !string.IsNullOrWhiteSpace(output))
                        {
                            _cachedInput = input;
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
        lock (_gate)
        {
            if (_disposed) return null;
            if (_cachedInput == input) return _cachedOutput;
            pending = _activeInput == input ? _active?.Task : null;
        }
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
