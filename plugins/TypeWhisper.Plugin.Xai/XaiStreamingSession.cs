using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Xai;

internal sealed class XaiStreamingSession : IStreamingSession
{
    private readonly ClientWebSocket _ws;
    private readonly XaiTranscriptCollector _collector;
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    // Set by the receive loop when transcript.done arrives (or when the loop
    // exits for any reason via the finally block). FinalizeAsync awaits this
    // before returning so the coordinator does not tear the session down
    // while xAI still has tail-end final segments to deliver.
    private readonly TaskCompletionSource<bool> _terminalSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Holds the first non-cancellation exception caught by the receive loop
    // (provider error event, WebSocket fault, JSON parse failure). Surfaced
    // on the next SendAudioAsync / FinalizeAsync call so the coordinator's
    // sender task observes the fault and triggers batch fallback — otherwise
    // a server-side error event would be silently logged and the user would
    // see an empty or partial transcript.
    private Exception? _receiveLoopException;
    private Task? _receiveTask;
    private bool _disposed;

    private XaiStreamingSession(ClientWebSocket ws, XaiTranscriptCollector collector)
    {
        _ws = ws;
        _collector = collector;
    }

    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    public static async Task<XaiStreamingSession> ConnectAsync(
        string apiKey,
        string? language,
        CancellationToken ct)
    {
        var ws = CreateConfiguredWebSocket(apiKey);
        await ws.ConnectAsync(BuildStreamingUri(language, interimResults: true), ct);

        var collector = new XaiTranscriptCollector();
        var session = new XaiStreamingSession(ws, collector);
        session._receiveTask = session.ReceiveLoopAsync(session._receiveCts.Token);
        return session;
    }

    public static Uri BuildStreamingUri(string? language, bool interimResults)
    {
        var query = new List<string>
        {
            "sample_rate=16000",
            "encoding=pcm",
            $"interim_results={(interimResults ? "true" : "false")}",
        };

        if (!string.IsNullOrWhiteSpace(language)
            && !language.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            query.Add($"language={Uri.EscapeDataString(language)}");
        }

        return new Uri("wss://api.x.ai/v1/stt?" + string.Join("&", query));
    }

    public static IReadOnlyDictionary<string, string> CreateStreamingHeaders(string apiKey) =>
        new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {apiKey}",
        };

    private static ClientWebSocket CreateConfiguredWebSocket(string apiKey)
    {
        var ws = new ClientWebSocket();
        foreach (var header in CreateStreamingHeaders(apiKey))
            ws.Options.SetRequestHeader(header.Key, header.Value);
        return ws;
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
    {
        if (_disposed) return;

        // Receive loop saw a protocol/transport error: surface it so the
        // coordinator's sender task faults and triggers batch fallback.
        ThrowIfReceiveLoopFaulted();

        if (_ws.State != WebSocketState.Open || pcm16Audio.Length == 0)
            return;

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws.State != WebSocketState.Open)
                return;

            await _ws.SendAsync(pcm16Audio, WebSocketMessageType.Binary, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task FinalizeAsync(CancellationToken ct)
    {
        if (_disposed) return;

        // If the socket is still open, send audio.done and wait for the
        // terminal event (transcript.done or close/error). When the socket
        // is already in a closing/closed state — for example because the
        // server sent an error event followed by Close — the receive loop
        // has already exited; we skip the send/wait but still surface any
        // captured fault below. The ThrowIfReceiveLoopFaulted call is the
        // single point where receive-loop failures reach the coordinator
        // via FinalizeAsync, so it must run regardless of socket state.
        if (_ws.State == WebSocketState.Open)
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await SendTextAsync("""{"type":"audio.done"}""", ct);
            }
            finally
            {
                _sendLock.Release();
            }

            // Wait for the receive loop to surface transcript.done (or to
            // exit via close/error/cancel — the receive loop's finally block
            // sets the signal in all paths). The caller's ct already carries
            // the coordinator's FinalizeSessionTimeoutMs bound, so this
            // won't hang beyond that. xAI's protocol has an explicit done
            // event; using it is strictly better than relying on the
            // coordinator's universal 500 ms grace window for tail-end finals.
            try { await _terminalSignal.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { /* timeout / caller cancel: best effort */ }
        }

        // Surface a provider-side error so the coordinator's FinalizeAsync
        // rethrows and DictationOrchestrator's finalizeThrew flag flips,
        // triggering batch fallback. Without this, a stop-path race where
        // the server closes before our caller stops recording would leave
        // a partial transcript looking like a successful streaming result.
        ThrowIfReceiveLoopFaulted();
    }

    private void ThrowIfReceiveLoopFaulted()
    {
        var ex = Volatile.Read(ref _receiveLoopException);
        if (ex is null) return;
        throw new InvalidOperationException(
            $"xAI streaming session faulted: {ex.Message}", ex);
    }

    private async Task SendTextAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var messageBuffer = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                messageBuffer.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    messageBuffer.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                var transcriptEvent = _collector.ApplyEvent(json);
                if (transcriptEvent is not null)
                    TranscriptReceived?.Invoke(transcriptEvent);
                if (_collector.IsTerminal)
                    _terminalSignal.TrySetResult(true);
            }
        }
        catch (OperationCanceledException ex)
        {
            // Normal teardown — DisposeAsync cancelled _receiveCts. Not a fault.
            Debug.WriteLine($"xAI STT receive loop canceled: {ex.Message}");
        }
        catch (WebSocketException ex)
        {
            Debug.WriteLine($"xAI STT WebSocket error: {ex.Message}");
            Interlocked.CompareExchange(ref _receiveLoopException, ex, null);
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"xAI STT parse error: {ex.Message}");
            Interlocked.CompareExchange(ref _receiveLoopException, ex, null);
        }
        catch (InvalidOperationException ex)
        {
            // Raised by XaiTranscriptCollector for "error"-typed events and
            // malformed payloads — propagate as a session fault.
            Debug.WriteLine($"xAI STT stream error: {ex.Message}");
            Interlocked.CompareExchange(ref _receiveLoopException, ex, null);
        }
        finally
        {
            // "No more events will arrive" is true whether we exited via
            // transcript.done, a Close frame, cancellation, or any error.
            // Unblock FinalizeAsync in all paths.
            _terminalSignal.TrySetResult(true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _receiveCts.Cancel();

        await _sendLock.WaitAsync(CancellationToken.None);
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                // Bound the close handshake. An unresponsive peer would
                // otherwise hang DisposeAsync indefinitely, blocking the
                // coordinator's teardown chain (recording stop, app exit).
                // Mirrors the Deepgram session's close-handshake guard.
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closeCts.Token); }
                catch (OperationCanceledException ex)
                {
                    Debug.WriteLine($"xAI STT WebSocket close timed out: {ex.Message}");
                    try { _ws.Abort(); } catch { /* best effort */ }
                }
                catch (WebSocketException ex)
                {
                    Debug.WriteLine($"xAI STT WebSocket close error: {ex.Message}");
                    try { _ws.Abort(); } catch { /* best effort */ }
                }
                catch (InvalidOperationException ex)
                {
                    Debug.WriteLine($"xAI STT WebSocket close skipped: {ex.Message}");
                }
            }
        }
        finally
        {
            _sendLock.Release();
        }

        if (_receiveTask is not null)
        {
            try { await _receiveTask; }
            catch (OperationCanceledException ex)
            {
                Debug.WriteLine($"xAI STT receive loop canceled during dispose: {ex.Message}");
            }
            catch (WebSocketException ex)
            {
                Debug.WriteLine($"xAI STT receive loop closed during dispose: {ex.Message}");
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"xAI STT receive loop parse error during dispose: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                Debug.WriteLine($"xAI STT receive loop stopped during dispose: {ex.Message}");
            }
        }

        _sendLock.Dispose();
        _receiveCts.Dispose();
        _ws.Dispose();
    }
}

internal sealed class XaiTranscriptCollector
{
    private readonly List<string> _finals = [];
    private string _interim = "";
    private string? _doneText;
    private string? _detectedLanguage;
    private double _duration;

    /// <summary>
    ///     True once a terminal event (transcript.done) has been applied. The
    ///     session uses this to unblock <c>FinalizeAsync</c> as soon as xAI
    ///     declares the stream complete, instead of relying on the
    ///     coordinator's generic grace window.
    /// </summary>
    public bool IsTerminal { get; private set; }

    public StreamingTranscriptEvent? ApplyEvent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeEl)
            || typeEl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Invalid xAI STT event.");
        }

        return typeEl.GetString() switch
        {
            "transcript.created" => null,
            "transcript.partial" => ApplyPartialEvent(root),
            "transcript.done" => ApplyDoneEvent(root),
            "error" => throw new InvalidOperationException(ExtractErrorMessage(root) ?? "Unknown xAI STT error"),
            _ => null,
        };
    }

    public PluginTranscriptionResult FinalResult(string? fallbackLanguage)
    {
        var text = !string.IsNullOrWhiteSpace(_doneText)
            ? _doneText!
            : string.Join(" ", _finals).Trim();

        return new PluginTranscriptionResult(text, _detectedLanguage ?? fallbackLanguage ?? "", _duration);
    }

    // StreamingTranscriptionCoordinator treats every IsFinal=true event as a
    // new immutable segment to append to _finalSegments. We MUST emit per-
    // segment deltas (not cumulative text) here — otherwise three finals of
    // "hello", "hello world", "hello world" produce "hello\nhello world\n
    // hello world" in the coordinator's output. xAI's per-segment partial-
    // finals carry the segment text directly; the trailing speech_final=true
    // cumulative-summary and the transcript.done cumulative are suppressed
    // because the coordinator already has the segments.
    private StreamingTranscriptEvent? ApplyPartialEvent(JsonElement root)
    {
        var text = GetString(root, "text")?.Trim() ?? "";
        var isFinal = GetBool(root, "is_final");
        var speechFinal = GetBool(root, "speech_final");
        RememberMetadata(root);

        if (isFinal)
        {
            _interim = "";
            if (string.IsNullOrWhiteSpace(text))
                return null;

            // Cumulative speech-final summary: xAI sometimes emits a final
            // event with speech_final=true whose text is the entire stream so
            // far. Re-appending it would duplicate the transcript. Detect by
            // checking that text starts with the joined-existing-finals AT A
            // WORD BOUNDARY — bare StartsWith would misclassify utterances
            // like "I" → "I'm here" (next segment's "I'm" starts with "I" by
            // coincidence, not as a cumulative summary). We do NOT dedup on
            // exact-text alone — a user repeating the same word in two
            // finalized segments (e.g. "yes" twice) is a legitimate repetition.
            if (speechFinal && _finals.Count > 0)
            {
                var joined = string.Join(" ", _finals);
                if (text.StartsWith(joined, StringComparison.Ordinal)
                    && (text.Length == joined.Length || text[joined.Length] == ' '))
                {
                    return null;
                }
            }

            _finals.Add(text);
            return new StreamingTranscriptEvent(text, IsFinal: true);
        }

        _interim = text;
        return new StreamingTranscriptEvent(text, IsFinal: false);
    }

    private StreamingTranscriptEvent? ApplyDoneEvent(JsonElement root)
    {
        var text = GetString(root, "text")?.Trim() ?? "";
        RememberMetadata(root);
        _interim = "";
        IsTerminal = true;

        if (string.IsNullOrWhiteSpace(text))
            return null;

        _doneText = text;

        // transcript.done's text is the cumulative session transcript. If no
        // segment finals arrived, surface it as the single final.
        if (_finals.Count == 0)
            return new StreamingTranscriptEvent(text, IsFinal: true);

        // If done text matches what we already emitted, suppress. If it
        // EXTENDS what we have (e.g. xAI didn't emit a final for the trailing
        // utterance and only delivered it via the done event), emit just the
        // suffix as a delta so the coordinator gets the tail without
        // double-appending the head.
        var joined = string.Join(" ", _finals);
        if (text.Equals(joined, StringComparison.Ordinal))
            return null;

        // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
        if (text.StartsWith(joined, StringComparison.Ordinal)
            && text.Length > joined.Length
            && text[joined.Length] == ' ')
        {
            var delta = text[(joined.Length + 1)..].Trim();
            if (delta.Length == 0)
                return null;
            _finals.Add(delta);
            return new StreamingTranscriptEvent(delta, IsFinal: true);
        }

        // Done text diverges from our finals (a correction or reordering).
        // Don't try to reconcile — suppress to avoid duplicating text. The
        // already-emitted finals remain the user's transcript.
        return null;
    }

    private void RememberMetadata(JsonElement root)
    {
        if (GetString(root, "language") is { } language && !string.IsNullOrWhiteSpace(language))
            _detectedLanguage = language;

        if (root.TryGetProperty("duration", out var durationEl)
            && durationEl.ValueKind == JsonValueKind.Number
            && durationEl.TryGetDouble(out var duration))
        {
            _duration = duration;
        }
    }

    private static bool GetBool(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element)
        && element.ValueKind == JsonValueKind.True;

    private static string? GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element)
        && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string? ExtractErrorMessage(JsonElement root)
    {
        // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.Object && GetString(error, "message") is { } objectMessage)
                return objectMessage;
            if (error.ValueKind == JsonValueKind.String)
                return error.GetString();
        }

        return GetString(root, "message");
    }
}
