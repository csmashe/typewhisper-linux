using System.Diagnostics;
using System.Text;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Consumes an LLM response stream (<see cref="IAsyncEnumerable{T}" /> of token
///     deltas), accumulates the full text, and pushes the accumulated string to
///     <c>onAccumulated</c> at most once per <c>flushInterval</c> (~30 Hz by
///     default) plus one guaranteed final flush. The coalescer is genuinely new
///     work versus C5's STT partial path: LLM tokens from fast providers arrive at
///     &gt;500 tok/s and would flood the UI thread / compositor without rate
///     limiting (see the C7 master plan, Approach). UI-agnostic — the caller's
///     <c>onAccumulated</c> does the overlay update + event publish, so both sinks
///     are protected at the source.
///     <para>
///     Fault policy mirrors <see cref="StreamingTranscriptionCoordinator" />:
///     cancellation rethrows (the user stopped) while any other fault sets
///     <see cref="Faulted" /> and returns the partial without throwing, so the
///     caller can fall back to a batch request.
///     </para>
/// </summary>
internal sealed class LlmStreamPump
{
    private static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromMilliseconds(33);

    private readonly Action<string> _onAccumulated;
    private readonly TimeSpan _flushInterval;
    private readonly StringBuilder _sb = new();

    private string? _lastEmitted;

    public LlmStreamPump(Action<string> onAccumulated, TimeSpan? flushInterval = null)
    {
        _onAccumulated = onAccumulated;
        _flushInterval = flushInterval ?? DefaultFlushInterval;
    }

    public bool Faulted { get; private set; }

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
                if (string.IsNullOrEmpty(delta)) continue;
                _sb.Append(delta);

                if (stopwatch.Elapsed.TotalMilliseconds - lastFlushMs >= _flushInterval.TotalMilliseconds)
                {
                    Emit();
                    lastFlushMs = stopwatch.Elapsed.TotalMilliseconds;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The user stopped (Escape / token). Keep the partial and rethrow so
            // the caller skips the batch retry — cancellation is not a fault.
            EmitFinal();
            throw;
        }
        catch (Exception ex)
        {
            // External plugin enumerators can throw arbitrary types
            // (HttpRequestException, IOException, WebSocketException, JsonException,
            // plugin-internal). Treat all non-cancel faults as recoverable: keep
            // the partial, flag Faulted, and let the caller fall back to batch.
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
        if (text == _lastEmitted) return;
        _lastEmitted = text;
        _onAccumulated(text);
    }

    // Force a flush of the terminal text, but only if there is text to show and it
    // differs from the last coalesced emission — avoids a redundant identical
    // callback and avoids emitting at all for an empty stream.
    private void EmitFinal()
    {
        if (_sb.Length == 0) return;
        Emit();
    }
}
