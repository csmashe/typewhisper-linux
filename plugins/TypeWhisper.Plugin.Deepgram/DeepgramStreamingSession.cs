using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Deepgram;

internal sealed class DeepgramStreamingSession : IStreamingSession
{
    private readonly WebSocket _ws;
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly TaskCompletionSource _terminalCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _sessionFault;
    private readonly Task? _receiveTask;
    private int _metadataReceived;
    private bool _disposed;

    internal DeepgramStreamingSession(WebSocket ws)
    {
        _ws = ws;
        _receiveTask = ReceiveLoopAsync(_receiveCts.Token);
    }

    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    public static async Task<DeepgramStreamingSession> ConnectAsync(
        string apiKey,
        string model,
        string? language,
        CancellationToken ct
    )
    {
        var ws = new ClientWebSocket();

        // Deepgram's streaming WebSocket does not accept detect_language=true
        // (it's batch-only). For an unspecified language Nova-3 supports
        // language=multi for code-switching; older models default to English
        // when language is omitted, so Nova-2 has no auto-detect option here.
        var isUnspecified =
            string.IsNullOrEmpty(language)
            || string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase);
        var langParam = isUnspecified
            ? model.StartsWith("nova-3", StringComparison.OrdinalIgnoreCase)
                ? "&language=multi"
                : string.Empty
            : $"&language={Uri.EscapeDataString(language!)}";
        var url =
            $"wss://api.deepgram.com/v1/listen?model={Uri.EscapeDataString(model)}&encoding=linear16&sample_rate=16000&interim_results=true&punctuate=true&smart_format=true{langParam}";

        ws.Options.SetRequestHeader("Authorization", $"Token {apiKey}");
        try
        {
            await ws.ConnectAsync(new Uri(url), ct);
            return new DeepgramStreamingSession(ws);
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
            ThrowIfClosedBeforeMetadata();
            return;
        }

        try
        {
            await _ws.SendAsync(pcm16Audio, WebSocketMessageType.Binary, true, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            CaptureFault(
                new InvalidOperationException("Deepgram streaming audio send failed.", ex)
            );
            throw;
        }
    }

    public async Task FinalizeAsync(CancellationToken ct)
    {
        if (_disposed)
            return;

        ThrowIfFaulted();
        if (Volatile.Read(ref _metadataReceived) == 0)
        {
            if (_ws.State != WebSocketState.Open)
            {
                ThrowIfClosedBeforeMetadata();
                return;
            }

            var msg = """{"type":"CloseStream"}"""u8.ToArray();
            try
            {
                await _ws.SendAsync(msg, WebSocketMessageType.Text, true, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                CaptureFault(
                    new InvalidOperationException(
                        "Deepgram CloseStream send failed.",
                        ex
                    )
                );
                throw;
            }
        }

        await _terminalCompletion.Task.WaitAsync(ct);
        ThrowIfFaulted();
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
                ProcessMessage(json);

                if (Volatile.Read(ref _metadataReceived) != 0)
                    return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // DisposeAsync owns this token. Local teardown is not a stream fault.
        }
        catch (OperationCanceledException ex)
        {
            CaptureFault(
                new InvalidOperationException("Deepgram streaming receive was canceled.", ex)
            );
        }
        catch (WebSocketException ex)
        {
            CaptureFault(
                new InvalidOperationException("Deepgram streaming transport failed.", ex)
            );
        }
        catch (JsonException ex)
        {
            CaptureFault(new InvalidOperationException("Deepgram sent malformed JSON.", ex));
        }
        catch (InvalidOperationException ex)
        {
            CaptureFault(ex);
        }
        catch (Exception ex)
        {
            CaptureFault(
                new InvalidOperationException("Deepgram streaming receive failed.", ex)
            );
        }
        finally
        {
            if (ct.IsCancellationRequested)
            {
                _terminalCompletion.TrySetResult();
            }
            else if (
                Volatile.Read(ref _metadataReceived) == 0
                && Volatile.Read(ref _sessionFault) is null
            )
            {
                CaptureFault(
                    new InvalidOperationException(
                        "Deepgram streaming receive ended before Metadata."
                    )
                );
            }
        }
    }

    private void ProcessMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var typeEl)
            || typeEl.ValueKind != JsonValueKind.String
        )
        {
            throw new InvalidOperationException(
                "Deepgram sent a malformed streaming message."
            );
        }

        switch (typeEl.GetString())
        {
            case "Results":
                ProcessResults(root);
                break;
            case "Metadata":
                Volatile.Write(ref _metadataReceived, 1);
                _terminalCompletion.TrySetResult();
                break;
            case "Error":
                throw new InvalidOperationException(
                    $"Deepgram streaming provider error: {ExtractError(root)}"
                );
        }
    }

    private void ProcessResults(JsonElement root)
    {
        if (
            !root.TryGetProperty("channel", out var channel)
            || channel.ValueKind != JsonValueKind.Object
            || !channel.TryGetProperty("alternatives", out var alternatives)
            || alternatives.ValueKind != JsonValueKind.Array
            || alternatives.GetArrayLength() == 0
            || alternatives[0].ValueKind != JsonValueKind.Object
            || !alternatives[0].TryGetProperty("transcript", out var transcriptEl)
            || transcriptEl.ValueKind != JsonValueKind.String
        )
        {
            throw new InvalidOperationException("Deepgram sent a malformed Results message.");
        }

        var transcript = transcriptEl.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(transcript))
            return;

        var isFinal =
            root.TryGetProperty("is_final", out var finalEl)
            && finalEl.ValueKind is JsonValueKind.True or JsonValueKind.False
            && finalEl.GetBoolean();
        Emit(new StreamingTranscriptEvent(transcript, isFinal));
    }

    private void Emit(StreamingTranscriptEvent transcriptEvent)
    {
        try
        {
            TranscriptReceived?.Invoke(transcriptEvent);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Deepgram streaming subscriber failed: {ex.Message}");
        }
    }

    private static string ExtractError(JsonElement root)
    {
        foreach (var propertyName in new[] { "description", "message", "error" })
        {
            if (
                root.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.GetString())
            )
            {
                return property.GetString()!;
            }
        }

        return "Unknown provider error.";
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
            $"Deepgram streaming socket closed {status}{reason} before Metadata."
        );
    }

    private void ThrowIfClosedBeforeMetadata()
    {
        ThrowIfFaulted();
        if (Volatile.Read(ref _metadataReceived) != 0)
            return;

        CaptureFault(
            new InvalidOperationException(
                $"Deepgram streaming socket is {_ws.State} before Metadata."
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

        _ = _terminalCompletion.Task.Exception;
        _receiveCts.Dispose();
        _ws.Dispose();
    }
}
