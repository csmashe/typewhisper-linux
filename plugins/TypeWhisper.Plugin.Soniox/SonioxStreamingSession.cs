// ReSharper disable MemberCanBePrivate.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Soniox;

/// <summary>
///     Real-time Soniox transcription over their WebSocket API
///     (<c>wss://stt-rt.soniox.com/transcribe-websocket</c>). Soniox streams tokens
///     incrementally — final tokens are sent exactly once and never repeated;
///     non-final tokens may repeat and change until they stabilize. The host
///     coordinator joins every <see cref="StreamingTranscriptEvent" /> with
///     <c>IsFinal=true</c> using a newline, so this session deliberately emits a
///     <b>single</b> final segment (the full accumulated final text) rather than a
///     final per token-batch (which would scatter newlines mid-sentence).
/// </summary>
internal sealed class SonioxStreamingSession : IStreamingSession
{
    private const string EndpointUrl = "wss://stt-rt.soniox.com/transcribe-websocket";

    // Soniox's real-time models are a separate family from the batch
    // speech:transcribe models, so the batch model selection ("default") does not
    // apply here. Pin the current real-time model (per the official soniox_examples
    // realtime sample).
    internal const string RealtimeModel = "stt-rt-v4";

    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly SonioxTranscriptAggregator _aggregator = new();
    private readonly TaskCompletionSource _finishedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _receiveTask;
    private bool _finalEmitted;

    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    public static async Task<SonioxStreamingSession> ConnectAsync(
        string apiKey,
        string? language,
        CancellationToken ct
    )
    {
        var session = new SonioxStreamingSession();
        await session._ws.ConnectAsync(new Uri(EndpointUrl), ct);

        // The config message must be the first frame on the socket.
        var config = Encoding.UTF8.GetBytes(BuildConfigMessage(apiKey, RealtimeModel, language));
        await session._ws.SendAsync(config, WebSocketMessageType.Text, true, ct);

        session._receiveTask = session.ReceiveLoopAsync(session._receiveCts.Token);
        return session;
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
    {
        if (_ws.State != WebSocketState.Open || pcm16Audio.Length == 0)
            return;
        // Soniox accepts arbitrary raw-PCM chunk sizes as binary frames.
        await _ws.SendAsync(pcm16Audio, WebSocketMessageType.Binary, true, ct);
    }

    public async Task FinalizeAsync(CancellationToken ct)
    {
        if (_ws.State == WebSocketState.Open)
        {
            // Empty frame = end-of-audio. The server flushes pending finals then
            // sends {"finished": true}.
            try
            {
                await _ws.SendAsync(
                    ReadOnlyMemory<byte>.Empty,
                    WebSocketMessageType.Text,
                    true,
                    ct
                );
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // Couldn't signal end-of-audio, but fall through and still await
                // the receive loop so a captured provider-error fault is surfaced.
            }
        }

        // Wait until the receive loop has observed "finished" (or the socket
        // closed) so the single final event is emitted — and thus appended to the
        // coordinator's final-segment buffer — before it snapshots. Bounded by the
        // caller's ct/timeout. If the receive loop captured a provider error frame
        // it faults this task, so the await rethrows and the coordinator falls back
        // to batch transcription (lossless). A caller cancel / timeout
        // (OperationCanceledException) is the grace-window backstop, not a fault.
        try { await _finishedTcs.Task.WaitAsync(ct); }
        catch (OperationCanceledException) { /* coordinator's grace window backstops */ }
    }

    internal static string BuildConfigMessage(string apiKey, string model, string? language)
    {
        var config = new Dictionary<string, object>
        {
            ["api_key"] = apiKey,
            ["model"] = model,
            ["audio_format"] = "pcm_s16le",
            ["sample_rate"] = 16000,
            ["num_channels"] = 1,
            ["enable_endpoint_detection"] = true,
        };

        if (!string.IsNullOrWhiteSpace(language)
            && !string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
        {
            config["language_hints"] = new[] { language };
        }

        return JsonSerializer.Serialize(config);
    }

    internal readonly record struct SonioxToken(string Text, bool IsFinal);

    internal sealed record SonioxMessage(
        IReadOnlyList<SonioxToken> Tokens,
        bool Finished,
        string? ErrorMessage
    );

    internal readonly record struct SonioxAggregateUpdate(
        string PreviewText,
        bool Finished,
        string FinalText
    );

    /// <summary>Reflection-free, never-throws parse of one Soniox response frame.</summary>
    internal static SonioxMessage ParseMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? error = null;
            if (root.TryGetProperty("error_message", out var errEl)
                && errEl.ValueKind == JsonValueKind.String)
            {
                error = errEl.GetString();
            }
            else if (root.TryGetProperty("error_code", out _))
            {
                error = json;
            }

            var finished = root.TryGetProperty("finished", out var finEl)
                && finEl.ValueKind == JsonValueKind.True;

            var tokens = new List<SonioxToken>();
            // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
            if (root.TryGetProperty("tokens", out var tokensEl)
                && tokensEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var tok in tokensEl.EnumerateArray())
                {
                    if (tok.ValueKind != JsonValueKind.Object)
                        continue;
                    var text = tok.TryGetProperty("text", out var textEl)
                        && textEl.ValueKind == JsonValueKind.String
                        ? textEl.GetString() ?? ""
                        : "";
                    var isFinal = tok.TryGetProperty("is_final", out var finalEl)
                        && finalEl.ValueKind == JsonValueKind.True;
                    tokens.Add(new SonioxToken(text, isFinal));
                }
            }

            return new SonioxMessage(tokens, finished, error);
        }
        catch (JsonException)
        {
            return new SonioxMessage([], false, null);
        }
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
                var closed = false;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        closed = true;
                        break;
                    }
                    messageBuffer.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (closed)
                    break;

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(
                    messageBuffer.GetBuffer(),
                    0,
                    (int)messageBuffer.Length
                );

                var message = ParseMessage(json);
                if (message.ErrorMessage is not null)
                {
                    Debug.WriteLine($"Soniox realtime error: {message.ErrorMessage}");
                    // A provider error after the stream started means the result is
                    // unreliable. Fault FinalizeAsync (rather than committing a
                    // truncated partial) so the coordinator falls back to a complete
                    // batch transcription of the captured WAV.
                    _finishedTcs.TrySetException(
                        new InvalidOperationException(
                            $"Soniox streaming error: {message.ErrorMessage}"
                        )
                    );
                    return;
                }

                var update = _aggregator.Apply(message);
                if (update.Finished)
                {
                    EmitFinal(update.FinalText);
                    return;
                }

                if (!string.IsNullOrEmpty(update.PreviewText))
                    Emit(new StreamingTranscriptEvent(update.PreviewText, IsFinal: false));
            }

            // Clean socket close without an explicit "finished": flush the
            // accumulated final so the transcript is not lost.
            EmitFinalIfPending();
        }
        catch (OperationCanceledException)
        {
            // User cancel / teardown — do NOT synthesize a final.
        }
        catch (WebSocketException ex)
        {
            Debug.WriteLine($"Soniox realtime WebSocket error: {ex.Message}");
            // A transport drop before "finished" means the stream is incomplete.
            // Fault (rather than committing a truncated partial) so the coordinator
            // falls back to a complete batch transcription of the captured WAV.
            _finishedTcs.TrySetException(
                new InvalidOperationException("Soniox streaming transport error.", ex)
            );
        }
        finally
        {
            _finishedTcs.TrySetResult();
        }
    }

    private void EmitFinalIfPending()
    {
        if (_finalEmitted)
            return;
        EmitFinal(_aggregator.FinalText);
    }

    private void EmitFinal(string finalText)
    {
        if (_finalEmitted)
            return;
        _finalEmitted = true;
        if (!string.IsNullOrWhiteSpace(finalText))
            Emit(new StreamingTranscriptEvent(finalText, IsFinal: true));
    }

    private void Emit(StreamingTranscriptEvent evt)
    {
        try
        {
            TranscriptReceived?.Invoke(evt);
        }
        catch (Exception ex)
        {
            // Isolate subscriber failures from the receive loop.
            Debug.WriteLine($"Soniox realtime subscriber failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _receiveCts.Cancel();

        if (_ws.State == WebSocketState.Open)
        {
            // Bound the close handshake; an unresponsive peer would otherwise hang
            // Dispose indefinitely. Abort is the fallback on timeout/failure.
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closeCts.Token);
            }
            catch
            {
                try { _ws.Abort(); } catch { /* best effort */ }
            }
        }

        if (_receiveTask is not null)
        {
            try { await _receiveTask; }
            catch { /* expected */ }
        }

        _finishedTcs.TrySetResult();
        // Observe any captured provider-error fault so it can't surface as an
        // unobserved task exception when FinalizeAsync was never called.
        _ = _finishedTcs.Task.Exception;
        _receiveCts.Dispose();
        _ws.Dispose();
    }
}

/// <summary>
///     Collapses Soniox's incremental token stream into the host's per-segment
///     model: final tokens (sent once) accumulate; non-final tokens are the
///     current provisional tail. The preview is final+provisional; the single
///     final segment is the accumulated final text.
/// </summary>
internal sealed class SonioxTranscriptAggregator
{
    private readonly StringBuilder _final = new();

    public string FinalText => _final.ToString().Trim();

    public SonioxStreamingSession.SonioxAggregateUpdate Apply(
        SonioxStreamingSession.SonioxMessage message
    )
    {
        var provisional = new StringBuilder();
        foreach (var token in message.Tokens)
        {
            if (IsControlToken(token.Text))
                continue;
            if (token.IsFinal)
                _final.Append(token.Text);
            else
                provisional.Append(token.Text);
        }

        var preview = (_final.ToString() + provisional).Trim();
        return new SonioxStreamingSession.SonioxAggregateUpdate(
            preview,
            message.Finished,
            FinalText
        );
    }

    // Legacy Soniox emitted <end>/<fin> control tokens at endpoints; current docs
    // show no marker, but skip them defensively so they never reach the transcript.
    internal static bool IsControlToken(string text) => text is "<end>" or "<fin>";
}
