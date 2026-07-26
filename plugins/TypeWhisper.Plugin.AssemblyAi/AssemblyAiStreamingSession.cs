using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.AssemblyAi;

internal sealed class AssemblyAiStreamingSession : IStreamingSession
{
    private readonly WebSocket _ws;
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly TaskCompletionSource _terminalCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly MemoryStream _audioBuffer = new();
    private Exception? _sessionFault;
    private readonly Task? _receiveTask;
    private int _terminationReceived;
    private bool _disposed;

    // AssemblyAI requires chunks between 50-1000ms (800-16000 samples at 16kHz = 1600-32000 bytes)
    private const int MinChunkBytes = 1600; // 50ms at 16kHz, 16-bit

    internal AssemblyAiStreamingSession(WebSocket ws)
    {
        _ws = ws;
        _receiveTask = ReceiveLoopAsync(_receiveCts.Token);
    }

    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    public static async Task<AssemblyAiStreamingSession> ConnectAsync(
        string apiKey,
        string? language,
        CancellationToken ct
    )
    {
        var ws = new ClientWebSocket();

        var url = "wss://streaming.assemblyai.com/v3/ws?sample_rate=16000&format_turns=true";
        // The default streaming model is English-only; opt into the multilingual
        // variant only when a non-English language is requested. Match by prefix
        // so locale variants like "en-US" stay on the English model.
        if (!string.IsNullOrEmpty(language)
            && !language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            url += "&speech_model=universal-streaming-multilingual";
        }

        ws.Options.SetRequestHeader("Authorization", apiKey);
        try
        {
            await ws.ConnectAsync(new Uri(url), ct);
            return new AssemblyAiStreamingSession(ws);
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
            ThrowIfClosedBeforeTermination();
            return;
        }

        _audioBuffer.Write(pcm16Audio.Span);

        // A residual smaller than MinChunkBytes is deliberately left unflushed
        // here; flushing it is unchanged and out of scope for this change.
        if (_audioBuffer.Length < MinChunkBytes)
            return;

        var chunk = _audioBuffer.ToArray();
        _audioBuffer.SetLength(0);

        try
        {
            await _ws.SendAsync(chunk, WebSocketMessageType.Binary, true, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            CaptureFault(
                new InvalidOperationException("AssemblyAI streaming audio send failed.", ex)
            );
            throw;
        }
    }

    public async Task FinalizeAsync(CancellationToken ct)
    {
        if (_disposed)
            return;

        ThrowIfFaulted();
        if (Volatile.Read(ref _terminationReceived) == 0)
        {
            if (_ws.State != WebSocketState.Open)
            {
                ThrowIfClosedBeforeTermination();
                return;
            }

            var msg = """{"type":"Terminate"}"""u8.ToArray();
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
                        "AssemblyAI streaming termination send failed.",
                        ex
                    )
                );
                throw;
            }
        }

        // The coordinator supplies the finalization deadline through ct. Do not
        // turn that cancellation into success: it must remain able to select the
        // complete-WAV batch fallback.
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

                if (Volatile.Read(ref _terminationReceived) != 0)
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
                new InvalidOperationException("AssemblyAI streaming receive was canceled.", ex)
            );
        }
        catch (WebSocketException ex)
        {
            CaptureFault(
                new InvalidOperationException("AssemblyAI streaming transport failed.", ex)
            );
        }
        catch (JsonException ex)
        {
            CaptureFault(
                new InvalidOperationException("AssemblyAI sent malformed JSON.", ex)
            );
        }
        catch (InvalidOperationException ex)
        {
            CaptureFault(ex);
        }
        catch (Exception ex)
        {
            CaptureFault(
                new InvalidOperationException("AssemblyAI streaming receive failed.", ex)
            );
        }
        finally
        {
            if (ct.IsCancellationRequested)
            {
                _terminalCompletion.TrySetResult();
            }
            else if (
                Volatile.Read(ref _terminationReceived) == 0
                && Volatile.Read(ref _sessionFault) is null
            )
            {
                CaptureFault(
                    new InvalidOperationException(
                        "AssemblyAI streaming receive ended before Termination."
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
                "AssemblyAI sent a malformed streaming message."
            );
        }

        switch (typeEl.GetString())
        {
            case "Turn":
                ProcessTurn(root);
                break;
            case "Termination":
                Volatile.Write(ref _terminationReceived, 1);
                _terminalCompletion.TrySetResult();
                break;
            case "Error":
                throw new InvalidOperationException(
                    $"AssemblyAI streaming provider error: {ExtractError(root)}"
                );
        }
    }

    private void ProcessTurn(JsonElement root)
    {
        if (
            !root.TryGetProperty("transcript", out var textEl)
            || textEl.ValueKind != JsonValueKind.String
        )
        {
            throw new InvalidOperationException("AssemblyAI sent a malformed Turn message.");
        }

        var transcript = textEl.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(transcript))
            return;

        var isEndOfTurn =
            root.TryGetProperty("end_of_turn", out var eotEl)
            && eotEl.ValueKind is JsonValueKind.True or JsonValueKind.False
            && eotEl.GetBoolean();
        var isFormatted =
            root.TryGetProperty("turn_is_formatted", out var formattedEl)
            && formattedEl.ValueKind is JsonValueKind.True or JsonValueKind.False
            && formattedEl.GetBoolean();

        // With format_turns=true AssemblyAI sends an unformatted end-of-turn
        // message followed by the formatted replacement. Expose the former only
        // as interim text and commit the formatted terminal turn exactly once.
        Emit(new StreamingTranscriptEvent(transcript, isEndOfTurn && isFormatted));
    }

    private void Emit(StreamingTranscriptEvent transcriptEvent)
    {
        try
        {
            TranscriptReceived?.Invoke(transcriptEvent);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AssemblyAI streaming subscriber failed: {ex.Message}");
        }
    }

    private static string ExtractError(JsonElement root)
    {
        foreach (var propertyName in new[] { "error", "message", "detail" })
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
            $"AssemblyAI streaming socket closed {status}{reason} before Termination."
        );
    }

    private void ThrowIfClosedBeforeTermination()
    {
        ThrowIfFaulted();
        if (Volatile.Read(ref _terminationReceived) != 0)
            return;

        CaptureFault(
            new InvalidOperationException(
                $"AssemblyAI streaming socket is {_ws.State} before Termination."
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
            // Cap the graceful close handshake; a wedged remote could otherwise
            // hang DisposeAsync indefinitely. On timeout or any failure, abort
            // the socket so cleanup can proceed.
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    null,
                    closeCts.Token
                );
            }
            catch (Exception ex)
                when (ex is OperationCanceledException or WebSocketException)
            {
                try { _ws.Abort(); } catch { /* best effort */ }
            }
            catch
            { /* best effort */
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
        // ReSharper disable once MethodHasAsyncOverload -- MemoryStream has no async disposal work; DisposeAsync would only add overhead here.
        _audioBuffer.Dispose();
        _receiveCts.Dispose();
        _ws.Dispose();
    }
}
