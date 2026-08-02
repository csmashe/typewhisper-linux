using System.Diagnostics;
using System.Text;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Consumes an LLM token-delta stream, accumulates the full text, and calls
///     <c>onAccumulated</c> at most once per <c>flushInterval</c> (~30 Hz) plus a
///     guaranteed final flush. Rate-limiting is necessary because fast providers
///     emit &gt;500 tok/s and would flood the UI dispatcher without it.
///     Cancellation rethrows; any other fault sets <see cref="Faulted" /> and
///     returns the partial so the caller can fall back to a batch request.
/// </summary>
internal sealed class LlmStreamPump
{
    private static readonly TimeSpan s_defaultFlushInterval = TimeSpan.FromMilliseconds(33);
    private readonly TimeSpan _flushInterval;

    private readonly Action<string> _onAccumulated;
    private readonly StringBuilder _sb = new();

    private string? _lastEmitted;

    public LlmStreamPump(Action<string> onAccumulated, TimeSpan? flushInterval = null)
    {
        _onAccumulated = onAccumulated;
        _flushInterval = flushInterval ?? s_defaultFlushInterval;
    }

    public bool Faulted { get; private set; }

    /// <summary>
    ///     True once the source yielded at least one item (even ""). Distinguishes
    ///     a zero-item stream (proxy EOF, empty 200 — fall back to batch) from a
    ///     legitimately empty result delivered as a single chunk (bulk-yield path —
    ///     do NOT re-run, as that would duplicate the request).
    /// </summary>
    public bool ReceivedAnyChunk { get; private set; }

    /// <summary>
    ///     Consumes <paramref name="source" />, coalescing deltas to
    ///     <c>onAccumulated</c> at no more than the flush rate. Returns the full
    ///     accumulated string. On a mid-stream fault, sets <see cref="Faulted" />
    ///     and returns what accumulated (no throw). Always emits a final flush of
    ///     the terminal text when any text accumulated.
    /// </summary>
    public async Task<string> RunAsync(IAsyncEnumerable<string> source, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var lastFlushMs = -_flushInterval.TotalMilliseconds;

        try
        {
            await foreach (var delta in source.WithCancellation(ct))
            {
                // Mark receipt before the empty-skip: a lone "" chunk still signals
                // the source produced output, distinguishing it from a zero-item stream.
                ReceivedAnyChunk = true;
                if (string.IsNullOrEmpty(delta))
                {
                    continue;
                }

                _sb.Append(delta);

                if (stopwatch.Elapsed.TotalMilliseconds - lastFlushMs < _flushInterval.TotalMilliseconds)
                {
                    continue;
                }

                Emit();
                lastFlushMs = stopwatch.Elapsed.TotalMilliseconds;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller stopped — rethrow so the caller skips the batch retry.
            EmitFinal();
            throw;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Caller cancellation wins if a dependency fault completes in the
            // same race window.
            EmitFinal();
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex)
        {
            // Plugin enumerators can throw arbitrary types, including an OCE while
            // the caller token is still live. Treat every dependency fault as
            // recoverable: keep the partial and let the caller fall back.
            Trace.WriteLine($"[LlmStreamPump] Fault: {ex.GetType().Name}: {ex.Message}");
            Faulted = true;
            EmitFinal();
            return _sb.ToString();
        }

        EmitFinal();
        return _sb.ToString();
    }

    private void Emit()
    {
        var text = _sb.ToString();
        if (text == _lastEmitted)
        {
            return;
        }

        _lastEmitted = text;
        _onAccumulated(text);
    }

    // Force a terminal flush only when there is new text to emit.
    private void EmitFinal()
    {
        if (_sb.Length == 0)
        {
            return;
        }

        Emit();
    }
}
