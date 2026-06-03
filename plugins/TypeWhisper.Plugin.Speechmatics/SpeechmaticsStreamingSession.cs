using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Speechmatics;

/// <summary>
///     Real-time Speechmatics transcription over their v2 WebSocket API
///     (<c>wss://eu.rt.speechmatics.com/v2</c>). The handshake is stateful:
///     <c>StartRecognition</c> → wait for <c>RecognitionStarted</c> → stream raw
///     PCM16 binary frames → <c>EndOfStream</c> → <c>EndOfTranscript</c>.
///     <c>AddTranscript</c> finals fire on a <c>max_delay</c> timer (not at speech
///     boundaries), so — like Soniox — this session accumulates them into a single
///     final segment rather than emitting a newline-joined final per segment.
/// </summary>
internal sealed class SpeechmaticsStreamingSession : IStreamingSession
{
    private const string EndpointUrl = "wss://eu.rt.speechmatics.com/v2";

    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly SpeechmaticsTranscriptAggregator _aggregator = new();
    private readonly TaskCompletionSource _finishedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _receiveTask;
    private int _seqNo;
    private bool _finalEmitted;

    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    public static async Task<SpeechmaticsStreamingSession> ConnectAsync(
        string apiKey,
        string? language,
        CancellationToken ct
    )
    {
        var session = new SpeechmaticsStreamingSession();
        session._ws.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
        await session._ws.ConnectAsync(new Uri(EndpointUrl), ct);

        var start = Encoding.UTF8.GetBytes(BuildStartRecognition(language, 16000));
        await session._ws.SendAsync(start, WebSocketMessageType.Text, true, ct);

        // Speechmatics rejects audio before RecognitionStarted, so block the
        // handshake here. Throw on an Error frame so the coordinator faults to
        // batch fallback rather than streaming into a dead socket.
        await session.AwaitRecognitionStartedAsync(ct);

        session._receiveTask = session.ReceiveLoopAsync(session._receiveCts.Token);
        return session;
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
    {
        if (_ws.State != WebSocketState.Open || pcm16Audio.Length == 0)
            return;
        await _ws.SendAsync(pcm16Audio, WebSocketMessageType.Binary, true, ct);
        Interlocked.Increment(ref _seqNo);
    }

    public async Task FinalizeAsync(CancellationToken ct)
    {
        if (_ws.State == WebSocketState.Open)
        {
            try
            {
                var eos = Encoding.UTF8.GetBytes(BuildEndOfStream(Volatile.Read(ref _seqNo)));
                await _ws.SendAsync(eos, WebSocketMessageType.Text, true, ct);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // Couldn't signal end-of-audio, but fall through and still await
                // the receive loop so a captured provider-error fault is surfaced.
            }
        }

        // If the receive loop captured an Error frame it faults this task, so the
        // await rethrows and the coordinator falls back to batch transcription
        // (lossless). A caller cancel / timeout (OperationCanceledException) is the
        // grace-window backstop, not a fault.
        try { await _finishedTcs.Task.WaitAsync(ct); }
        catch (OperationCanceledException) { /* coordinator's grace window backstops */ }
    }

    internal static string BuildStartRecognition(string? language, int sampleRate)
    {
        var lang = string.IsNullOrWhiteSpace(language)
            || string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : language;

        return JsonSerializer.Serialize(
            new Dictionary<string, object>
            {
                ["message"] = "StartRecognition",
                ["audio_format"] = new Dictionary<string, object>
                {
                    ["type"] = "raw",
                    ["encoding"] = "pcm_s16le",
                    ["sample_rate"] = sampleRate,
                },
                ["transcription_config"] = new Dictionary<string, object>
                {
                    ["language"] = lang,
                    ["operating_point"] = "enhanced",
                    ["enable_partials"] = true,
                    ["max_delay"] = 2,
                },
            }
        );
    }

    internal static string BuildEndOfStream(int lastSeqNo) =>
        JsonSerializer.Serialize(
            new Dictionary<string, object>
            {
                ["message"] = "EndOfStream",
                ["last_seq_no"] = lastSeqNo,
            }
        );

    internal readonly record struct SpeechmaticsMessage(
        string MessageType,
        string? Transcript,
        string? ErrorReason
    );

    internal readonly record struct SpeechmaticsUpdate(
        string PreviewText,
        bool Completed,
        string FinalText
    );

    /// <summary>Reflection-free, never-throws parse of one Speechmatics frame.</summary>
    internal static SpeechmaticsMessage ParseMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var messageType = root.TryGetProperty("message", out var msgEl)
                && msgEl.ValueKind == JsonValueKind.String
                ? msgEl.GetString() ?? ""
                : "";

            // The plain-text segment lives under metadata.transcript in the
            // documented real-time schema; fall back to a root-level "transcript"
            // defensively in case the shape varies across regions/API versions.
            string? transcript = null;
            if (root.TryGetProperty("metadata", out var metaEl)
                && metaEl.ValueKind == JsonValueKind.Object
                && metaEl.TryGetProperty("transcript", out var metaTrEl)
                && metaTrEl.ValueKind == JsonValueKind.String)
            {
                transcript = metaTrEl.GetString();
            }
            else if (root.TryGetProperty("transcript", out var trEl)
                && trEl.ValueKind == JsonValueKind.String)
            {
                transcript = trEl.GetString();
            }

            string? errorReason = null;
            if (messageType == "Error")
            {
                errorReason = root.TryGetProperty("reason", out var rEl)
                    && rEl.ValueKind == JsonValueKind.String
                    ? rEl.GetString()
                    : json;
            }

            return new SpeechmaticsMessage(messageType, transcript, errorReason);
        }
        catch (JsonException)
        {
            return new SpeechmaticsMessage("", null, null);
        }
    }

    private async Task AwaitRecognitionStartedAsync(CancellationToken ct)
    {
        while (true)
        {
            var json = await ReceiveTextMessageAsync(ct);
            if (json is null)
                throw new InvalidOperationException(
                    "Speechmatics closed the socket before RecognitionStarted."
                );

            var message = ParseMessage(json);
            if (message.MessageType == "RecognitionStarted")
                return;
            if (message.MessageType == "Error")
                throw new InvalidOperationException(
                    $"Speechmatics StartRecognition failed: {message.ErrorReason}"
                );
            // RecognitionStarted may be preceded by Info/Warning frames — keep reading.
        }
    }

    private async Task<string?> ReceiveTextMessageAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var messageBuffer = new MemoryStream();
        while (true)
        {
            var result = await _ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            messageBuffer.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
        }

        return Encoding.UTF8.GetString(
            messageBuffer.GetBuffer(),
            0,
            (int)messageBuffer.Length
        );
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var json = await ReceiveTextMessageAsync(ct);
                if (json is null)
                    break;

                var message = ParseMessage(json);
                if (message.MessageType == "Error")
                {
                    Debug.WriteLine($"Speechmatics realtime error: {message.ErrorReason}");
                    // A provider error after RecognitionStarted means the result is
                    // unreliable. Fault FinalizeAsync (rather than committing a
                    // truncated partial) so the coordinator falls back to a complete
                    // batch transcription of the captured WAV.
                    _finishedTcs.TrySetException(
                        new InvalidOperationException(
                            $"Speechmatics streaming error: {message.ErrorReason}"
                        )
                    );
                    return;
                }

                var update = _aggregator.Apply(message);
                if (update.Completed)
                {
                    EmitFinal(update.FinalText);
                    return;
                }

                if (message.MessageType is "AddTranscript" or "AddPartialTranscript"
                    && !string.IsNullOrEmpty(update.PreviewText))
                {
                    Emit(new StreamingTranscriptEvent(update.PreviewText, IsFinal: false));
                }
            }

            EmitFinalIfPending();
        }
        catch (OperationCanceledException)
        {
            // User cancel / teardown — do NOT synthesize a final.
        }
        catch (WebSocketException ex)
        {
            Debug.WriteLine($"Speechmatics realtime WebSocket error: {ex.Message}");
            // A transport drop before EndOfTranscript means the stream is incomplete.
            // Fault (rather than committing a truncated partial) so the coordinator
            // falls back to a complete batch transcription of the captured WAV.
            _finishedTcs.TrySetException(
                new InvalidOperationException("Speechmatics streaming transport error.", ex)
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
            Debug.WriteLine($"Speechmatics realtime subscriber failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _receiveCts.Cancel();

        if (_ws.State == WebSocketState.Open)
        {
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
///     Collapses Speechmatics' timer-driven <c>AddTranscript</c> segments into the
///     host's single-final model: finals accumulate; the latest
///     <c>AddPartialTranscript</c> is the provisional tail. Preview is
///     final+tail; the single final is the accumulated text.
/// </summary>
internal sealed class SpeechmaticsTranscriptAggregator
{
    private readonly StringBuilder _final = new();
    private string _partialTail = "";

    public string FinalText => _final.ToString().Trim();

    public SpeechmaticsStreamingSession.SpeechmaticsUpdate Apply(
        SpeechmaticsStreamingSession.SpeechmaticsMessage message
    )
    {
        switch (message.MessageType)
        {
            case "AddTranscript":
                _final.Append(message.Transcript);
                _partialTail = "";
                break;
            case "AddPartialTranscript":
                _partialTail = message.Transcript ?? "";
                break;
        }

        var completed = message.MessageType == "EndOfTranscript";
        var preview = (_final.ToString() + _partialTail).Trim();
        return new SpeechmaticsStreamingSession.SpeechmaticsUpdate(preview, completed, FinalText);
    }
}
