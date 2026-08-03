// ReSharper disable MemberCanBePrivate.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.ElevenLabs;

internal sealed class ElevenLabsStreamingSession : IStreamingSession
{
    internal const int MinimumBufferedChunkBytes = 3200; // 100ms at 16kHz, 16-bit mono

    private readonly WebSocket _ws;
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly MemoryStream _audioBuffer = new();
    private readonly TaskCompletionSource _terminalCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _sessionFault;
    private readonly Task? _receiveTask;
    private int _finalCommitSent;
    private int _finalCommitPending;
    private int _terminalCommitReceived;
    private bool _disposed;

    internal ElevenLabsStreamingSession(WebSocket ws)
    {
        _ws = ws;
        _receiveTask = ReceiveLoopAsync(_receiveCts.Token);
    }

    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    public static async Task<ElevenLabsStreamingSession> ConnectAsync(
        string apiKey,
        string realtimeModelId,
        string? language,
        CancellationToken ct
    )
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("xi-api-key", apiKey);
        try
        {
            await ws.ConnectAsync(BuildRealtimeUri(realtimeModelId, language), ct);
            return new ElevenLabsStreamingSession(ws);
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
    {
        if (_disposed)
            return;

        ThrowIfFaulted();
        if (_ws.State != WebSocketState.Open)
        {
            ThrowIfClosedBeforeTerminalCommit();
            return;
        }

        if (pcm16Audio.Length == 0)
            return;

        try
        {
            await _sendLock.WaitAsync(ct);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_disposed)
                return;

            ThrowIfFaulted();
            if (_ws.State != WebSocketState.Open)
            {
                ThrowIfClosedBeforeTerminalCommit();
                return;
            }

            _audioBuffer.Write(pcm16Audio.Span);
            if (_audioBuffer.Length < MinimumBufferedChunkBytes)
                return;

            var chunk = _audioBuffer.ToArray();
            _audioBuffer.SetLength(0);
            try
            {
                await SendAudioPayloadAsync(chunk, commit: false, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                CaptureFault(
                    new InvalidOperationException(
                        "ElevenLabs streaming audio send failed.",
                        ex
                    )
                );
                throw;
            }
        }
        finally
        {
            try
            {
                _sendLock.Release();
            }
            catch (ObjectDisposedException) { }
        }
    }

    public async Task FinalizeAsync(CancellationToken ct)
    {
        if (_disposed)
            return;

        ThrowIfFaulted();
        if (Volatile.Read(ref _finalCommitSent) == 0)
        {
            try
            {
                await _sendLock.WaitAsync(ct);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                if (_disposed)
                    return;

                ThrowIfFaulted();
                if (Volatile.Read(ref _finalCommitSent) == 0)
                {
                    if (_ws.State != WebSocketState.Open)
                    {
                        ThrowIfClosedBeforeTerminalCommit();
                        return;
                    }

                    // Arm the response waiter before sending so a fast provider
                    // response cannot race past it. Earlier VAD commits do not
                    // complete this source because it is armed only for the
                    // explicit final commit.
                    Volatile.Write(ref _finalCommitPending, 1);
                    Volatile.Write(ref _finalCommitSent, 1);

                    // Always send a terminal commit, including an empty chunk.
                    // An exact chunk-boundary flush still needs a committed
                    // response before the coordinator may accept the stream.
                    var chunk = _audioBuffer.Length == 0 ? [] : _audioBuffer.ToArray();
                    _audioBuffer.SetLength(0);
                    try
                    {
                        await SendAudioPayloadAsync(chunk, commit: true, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        CaptureFault(
                            new InvalidOperationException(
                                "ElevenLabs final commit send failed.",
                                ex
                            )
                        );
                        throw;
                    }
                }
            }
            finally
            {
                try
                {
                    _sendLock.Release();
                }
                catch (ObjectDisposedException) { }
            }
        }

        await _terminalCompletion.Task.WaitAsync(ct);
        ThrowIfFaulted();
    }

    internal static Uri BuildRealtimeUri(string realtimeModelId, string? language)
    {
        var query = new List<string>
        {
            $"model_id={Uri.EscapeDataString(realtimeModelId)}",
            "audio_format=pcm_16000",
            "commit_strategy=vad",
            "include_timestamps=true",
            "include_language_detection=true",
        };

        if (!string.IsNullOrWhiteSpace(language))
            query.Add($"language_code={Uri.EscapeDataString(language)}");

        return new Uri(
            "wss://api.elevenlabs.io/v1/speech-to-text/realtime?" + string.Join("&", query)
        );
    }

    internal static string BuildAudioChunkPayload(byte[] pcm16Audio, bool commit) =>
        JsonSerializer.Serialize(
            new Dictionary<string, object>
            {
                ["message_type"] = "input_audio_chunk",
                ["audio_base_64"] = Convert.ToBase64String(pcm16Audio),
                ["sample_rate"] = 16000,
                ["commit"] = commit,
            }
        );

    internal static bool TryParseTranscriptEvent(
        string json,
        out StreamingTranscriptEvent? transcriptEvent,
        out string? error
    ) =>
        TryParseTranscriptEvent(
            json,
            out transcriptEvent,
            out error,
            out _
        );

    private static bool TryParseTranscriptEvent(
        string json,
        out StreamingTranscriptEvent? transcriptEvent,
        out string? error,
        out bool isCommittedTranscript
    )
    {
        transcriptEvent = null;
        error = null;
        isCommittedTranscript = false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("message_type", out var messageTypeEl)
                || messageTypeEl.ValueKind != JsonValueKind.String
            )
            {
                error = "ElevenLabs sent a malformed message without a message_type.";
                return false;
            }

            var messageType = messageTypeEl.GetString();
            if (string.IsNullOrWhiteSpace(messageType))
            {
                error = "ElevenLabs sent a malformed message with an empty message_type.";
                return false;
            }

            if (messageType == "session_started")
                return false;

            if (IsErrorMessageType(messageType))
            {
                error = ExtractErrorMessage(root) ?? json;
                return false;
            }

            // ReSharper disable once ConvertIfStatementToSwitchStatement -- subjective control-flow style; the if-chain reads fine here.
            if (messageType is "partial_transcript")
            {
                if (!TryGetText(root, out var text))
                {
                    error = "ElevenLabs sent a malformed partial transcript.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(text))
                    return false;

                transcriptEvent = new StreamingTranscriptEvent(text, IsFinal: false);
                return true;
            }

            if (messageType is "committed_transcript" or "committed_transcript_with_timestamps")
            {
                isCommittedTranscript = true;
                if (!TryGetText(root, out var text))
                {
                    error = "ElevenLabs sent a malformed committed transcript.";
                    return false;
                }

                // An empty committed transcript is still the acknowledgement for
                // an empty final commit and must unblock FinalizeAsync.
                if (string.IsNullOrWhiteSpace(text))
                    return false;

                transcriptEvent = new StreamingTranscriptEvent(text, IsFinal: true);
                return true;
            }
        }
        catch (JsonException ex)
        {
            error = $"ElevenLabs sent malformed JSON: {ex.Message}";
        }

        return false;
    }

    private static bool IsErrorMessageType(string messageType) =>
        messageType switch
        {
            "auth_error"
            or "quota_exceeded"
            or "transcriber_error"
            or "input_error"
            or "error"
            or "commit_throttled"
            or "unaccepted_terms"
            or "rate_limited"
            or "queue_overflow"
            or "resource_exhausted"
            or "session_time_limit_exceeded"
            or "chunk_size_exceeded"
            or "insufficient_audio_activity"
            or "scribe_auth_error"
            or "scribe_quota_exceeded"
            or "scribe_throttled"
            or "scribe_unaccepted_terms"
            or "scribe_rate_limited"
            or "scribe_queue_overflow"
            or "scribe_resource_exhausted"
            or "scribe_session_time_limit_exceeded"
            or "scribe_input_error"
            or "scribe_chunk_size_exceeded"
            or "scribe_insufficient_audio_activity"
            or "scribe_transcriber_error"
            or "scribe_error" => true,
            _ => messageType.Contains("error", StringComparison.OrdinalIgnoreCase),
        };

    private async Task SendAudioPayloadAsync(byte[] chunk, bool commit, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(BuildAudioChunkPayload(chunk, commit));
        await _ws.SendAsync(payload, WebSocketMessageType.Text, true, ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var messageBuffer = new MemoryStream();

        try
        {
            while (true)
            {
                messageBuffer.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        CaptureFault(CreatePrematureCloseException(result));
                        return;
                    }

                    messageBuffer.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(
                    messageBuffer.GetBuffer(),
                    0,
                    (int)messageBuffer.Length
                );
                if (
                    TryParseTranscriptEvent(
                        json,
                        out var transcriptEvent,
                        out var error,
                        out var isCommittedTranscript
                    )
                )
                {
                    Emit(transcriptEvent!);
                }

                if (!string.IsNullOrWhiteSpace(error))
                    throw new InvalidOperationException(
                        $"ElevenLabs streaming provider error: {error}"
                    );

                // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
                if (
                    isCommittedTranscript
                    && Volatile.Read(ref _finalCommitPending) != 0
                )
                {
                    Volatile.Write(ref _terminalCommitReceived, 1);
                    _terminalCompletion.TrySetResult();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // DisposeAsync owns this token. Local teardown is not a stream fault.
        }
        catch (OperationCanceledException ex)
        {
            CaptureFault(
                new InvalidOperationException("ElevenLabs streaming receive was canceled.", ex)
            );
        }
        catch (WebSocketException ex)
        {
            CaptureFault(
                new InvalidOperationException("ElevenLabs streaming transport failed.", ex)
            );
        }
        catch (InvalidOperationException ex)
        {
            CaptureFault(ex);
        }
        catch (Exception ex)
        {
            CaptureFault(
                new InvalidOperationException("ElevenLabs streaming receive failed.", ex)
            );
        }
        finally
        {
            if (ct.IsCancellationRequested)
            {
                _terminalCompletion.TrySetResult();
            }
            else if (
                Volatile.Read(ref _terminalCommitReceived) == 0
                && Volatile.Read(ref _sessionFault) is null
            )
            {
                CaptureFault(
                    new InvalidOperationException(
                        "ElevenLabs streaming receive ended before the final committed transcript."
                    )
                );
            }
        }
    }

    private void Emit(StreamingTranscriptEvent transcriptEvent)
    {
        try
        {
            TranscriptReceived?.Invoke(transcriptEvent);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ElevenLabs realtime subscriber failed: {ex.Message}");
        }
    }

    private static bool TryGetText(JsonElement root, out string text)
    {
        text = "";
        if (
            !root.TryGetProperty("text", out var textEl)
            || textEl.ValueKind != JsonValueKind.String
        )
        {
            return false;
        }

        text = textEl.GetString() ?? "";
        return true;
    }

    private static string? ExtractErrorMessage(JsonElement root)
    {
        foreach (var propertyName in new[] { "error", "message", "details" })
        {
            if (
                root.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.GetString())
            )
            {
                return property.GetString();
            }
        }

        return null;
    }

    private static InvalidOperationException CreatePrematureCloseException(
        WebSocketReceiveResult result
    )
    {
        var status = result.CloseStatus is { } closeStatus
            ? $"{(int)closeStatus} ({closeStatus})"
            : "without a close status";
        var reason = string.IsNullOrWhiteSpace(result.CloseStatusDescription)
            ? ""
            : $": {result.CloseStatusDescription}";
        return new InvalidOperationException(
            $"ElevenLabs streaming socket closed {status}{reason} before the final committed transcript."
        );
    }

    private void ThrowIfClosedBeforeTerminalCommit()
    {
        ThrowIfFaulted();
        if (Volatile.Read(ref _terminalCommitReceived) != 0)
            return;

        CaptureFault(
            new InvalidOperationException(
                $"ElevenLabs streaming socket is {_ws.State} before the final committed transcript."
            )
        );
        ThrowIfFaulted();
    }

    private void CaptureFault(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _sessionFault, exception, null) is null)
            _terminalCompletion.TrySetException(exception);
    }

    private void ThrowIfFaulted()
    {
        var exception = Volatile.Read(ref _sessionFault);
        if (exception is not null)
            ExceptionDispatchInfo.Capture(exception).Throw();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        // ReSharper disable once MethodHasAsyncOverload -- Cancel() is fine in these teardown paths; CancelAsync() only defers callbacks, with no benefit here.
        _receiveCts.Cancel();
        _terminalCompletion.TrySetResult();

        await _sendLock.WaitAsync(CancellationToken.None);
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                // Bound the handshake: an unresponsive peer with CancellationToken.None
                // would otherwise hang Dispose indefinitely. Abort is the fallback
                // when the close handshake fails or times out.
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _ws.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        null,
                        closeCts.Token
                    );
                }
                catch
                {
                    try { _ws.Abort(); } catch { /* best effort */ }
                }
            }

            if (_receiveTask is not null)
            {
                try
                {
                    await _receiveTask;
                }
                catch
                { /* expected */
                }
            }

            // ReSharper disable once MethodHasAsyncOverload -- MemoryStream has no async disposal work; DisposeAsync would only add overhead here.
            _audioBuffer.Dispose();
        }
        finally
        {
            _sendLock.Release();
            _sendLock.Dispose();
            _receiveCts.Dispose();
            _ws.Dispose();
        }

        _ = _terminalCompletion.Task.Exception;
    }
}
