using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.SmallestAi;

internal sealed class SmallestAiStreamingSession : IStreamingSession
{
    private const int TeardownTimeoutMs = 2000;

    private readonly WebSocket _ws;
    private readonly SmallestAiTranscriptCollector _collector;
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource _lastResponseReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _disposeGate = new();
    private readonly Lock _operationGate = new();
    private Task? _receiveTask;
    private Task? _disposeTask;
    private int _activeOperations;
    private bool _disposed;

    private SmallestAiStreamingSession(WebSocket ws, SmallestAiTranscriptCollector collector)
    {
        _ws = ws;
        _collector = collector;
    }

    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    public static async Task<SmallestAiStreamingSession> ConnectAsync(
        string apiKey,
        string? language,
        CancellationToken ct)
    {
        var ws = CreateConfiguredWebSocket(apiKey);
        await ws.ConnectAsync(BuildStreamingUri(language, wordTimestamps: true), ct);

        return CreateStartedSession(ws);
    }

    internal static SmallestAiStreamingSession CreateConnectedSessionForTests(WebSocket ws)
    {
        // ReSharper disable once ConvertIfStatementToReturnStatement -- precondition guard; the suggested ternary-throw buries the throw.
        if (ws.State != WebSocketState.Open)
            throw new InvalidOperationException("The test WebSocket must already be open.");

        return CreateStartedSession(ws);
    }

    private static SmallestAiStreamingSession CreateStartedSession(WebSocket ws)
    {
        var session = new SmallestAiStreamingSession(ws, new SmallestAiTranscriptCollector());
        session._receiveTask = session.ReceiveLoopAsync(session._receiveCts.Token);
        return session;
    }

    public static Uri BuildStreamingUri(string? language, bool wordTimestamps)
    {
        var query = new List<string>
        {
            "encoding=linear16",
            "sample_rate=16000",
        };

        var normalizedLanguage = SmallestAiPlugin.NormalizeLanguage(language);
        if (normalizedLanguage is not null)
            query.Insert(0, $"language={Uri.EscapeDataString(normalizedLanguage)}");

        if (wordTimestamps)
            query.Add("word_timestamps=true");

        return new Uri("wss://api.smallest.ai/waves/v1/pulse/get_text?" + string.Join("&", query));
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
        if (!TryBeginOperation())
            return;

        try
        {
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
        finally
        {
            EndOperation();
        }
    }

    public async Task FinalizeAsync(CancellationToken ct)
    {
        if (!TryBeginOperation())
            return;

        try
        {
            if (_ws.State != WebSocketState.Open)
                return;

            await _sendLock.WaitAsync(ct);
            try
            {
                if (_ws.State != WebSocketState.Open)
                    return;

                await SendTextAsync("""{"type":"close_stream"}""", ct);
            }
            finally
            {
                _sendLock.Release();
            }

            await _lastResponseReceived.Task.WaitAsync(ct);
        }
        finally
        {
            EndOperation();
        }
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
                    {
                        _lastResponseReceived.TrySetResult();
                        return;
                    }
                    messageBuffer.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                var transcriptEvent = _collector.ApplyEvent(json);
                if (transcriptEvent is not null)
                    TranscriptReceived?.Invoke(transcriptEvent);

                if (_collector.IsLastReceived)
                    _lastResponseReceived.TrySetResult();
            }
        }
        catch (OperationCanceledException ex)
        {
            Debug.WriteLine($"Smallest AI Pulse receive loop canceled: {ex.Message}");
        }
        catch (WebSocketException ex)
        {
            Debug.WriteLine($"Smallest AI Pulse WebSocket error: {ex.Message}");
            _lastResponseReceived.TrySetException(ex);
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"Smallest AI Pulse parse error: {ex.Message}");
            // Surface the parse failure so a malformed frame arriving before the
            // terminal is_last event does not leave FinalizeAsync waiting forever.
            _lastResponseReceived.TrySetException(ex);
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine($"Smallest AI Pulse stream error: {ex.Message}");
            _lastResponseReceived.TrySetException(ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        TaskCompletionSource? disposeCompletion = null;
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                disposeCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                _disposeTask = disposeCompletion.Task;
            }

            disposeTask = _disposeTask;
        }

        if (disposeCompletion is not null)
            _ = CompleteDisposalAsync(disposeCompletion);

        return new ValueTask(disposeTask);
    }

    private async Task CompleteDisposalAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Smallest AI Pulse disposal error: {ex.Message}");
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    private async Task DisposeCoreAsync()
    {
        BeginDisposal();
        using var teardownCts = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(TeardownTimeoutMs)
        );
        var teardownToken = teardownCts.Token;

        try
        {
            // ReSharper disable once MethodHasAsyncOverload -- Cancel() is fine in these teardown paths; CancelAsync() only defers callbacks, with no benefit here.
            _receiveCts.Cancel();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Smallest AI Pulse receive cancellation error: {ex.Message}");
        }
        _lastResponseReceived.TrySetResult();
        _ = _lastResponseReceived.Task.Exception;

        var sendLockAcquired = false;
        var abortInvoked = false;
        Task? closeTask = null;

        try
        {
            try
            {
                await _sendLock.WaitAsync(teardownToken);
                sendLockAcquired = true;
            }
            catch (OperationCanceledException) when (teardownToken.IsCancellationRequested)
            {
                AbortSocket(ref abortInvoked);
            }

            if (sendLockAcquired && _ws.State == WebSocketState.Open)
            {
                try
                {
                    closeTask = _ws.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        null,
                        teardownToken
                    );
                    await closeTask.WaitAsync(teardownToken);
                }
                catch (OperationCanceledException) when (teardownToken.IsCancellationRequested)
                {
                    Debug.WriteLine("Smallest AI Pulse WebSocket close timed out.");
                    AbortSocket(ref abortInvoked);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Smallest AI Pulse WebSocket close error: {ex.Message}");
                    AbortSocket(ref abortInvoked);
                }
            }
        }
        finally
        {
            if (sendLockAcquired)
                _sendLock.Release();
        }

        var cleanupTask = CleanupResourcesAsync(closeTask);
        try
        {
            await cleanupTask.WaitAsync(teardownToken);
        }
        catch (OperationCanceledException) when (teardownToken.IsCancellationRequested)
        {
            AbortSocket(ref abortInvoked);
            // Cleanup is deliberately detached after the shared deadline. It
            // observes every operation and owns all resource disposal.
            _ = cleanupTask;
        }
    }

    private void BeginDisposal()
    {
        lock (_operationGate)
        {
            _disposed = true;
            if (_activeOperations == 0)
                _operationsDrained.TrySetResult();
        }
    }

    private bool TryBeginOperation()
    {
        lock (_operationGate)
        {
            if (_disposed)
                return false;

            _activeOperations++;
            return true;
        }
    }

    private void EndOperation()
    {
        lock (_operationGate)
        {
            _activeOperations--;
            if (_disposed && _activeOperations == 0)
                _operationsDrained.TrySetResult();
        }
    }

    private void AbortSocket(ref bool abortInvoked)
    {
        if (abortInvoked)
            return;

        abortInvoked = true;
        try { _ws.Abort(); }
        catch (Exception ex)
        {
            Debug.WriteLine($"Smallest AI Pulse WebSocket abort error: {ex.Message}");
        }
    }

    private async Task CleanupResourcesAsync(Task? closeTask)
    {
        var closeObservation = ObserveOperationAsync(closeTask, "close");
        var sendObservation = ObserveOperationAsync(_operationsDrained.Task, "send");
        var receiveObservation = ObserveOperationAsync(_receiveTask, "receive");
        await Task.WhenAll(closeObservation, sendObservation, receiveObservation);

        TryDispose(_sendLock, "send semaphore");
        TryDispose(_receiveCts, "receive cancellation source");
        TryDispose(_ws, "WebSocket");
    }

    private static async Task ObserveOperationAsync(Task? operation, string operationName)
    {
        if (operation is null)
            return;

        try
        {
            await operation;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Smallest AI Pulse {operationName} operation stopped during disposal: {ex.Message}"
            );
        }
    }

    private static void TryDispose(IDisposable resource, string resourceName)
    {
        try { resource.Dispose(); }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"Smallest AI Pulse {resourceName} disposal error: {ex.Message}"
            );
        }
    }
}

internal sealed class SmallestAiTranscriptCollector
{
    public string? DetectedLanguage { get; private set; }
    public bool IsLastReceived { get; private set; }

    public StreamingTranscriptEvent? ApplyEvent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (IsError(root))
            throw new InvalidOperationException(SmallestAiPlugin.ExtractApiError(root));

        var type = GetString(root, "type");
        if (!string.IsNullOrWhiteSpace(type)
            && !type.Equals("transcription", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var isFinal = GetBool(root, "is_final");
        var isLast = GetBool(root, "is_last");
        // Record the terminal signal before bailing on an empty transcript: a
        // no-speech / trailing-silence stream can end with an is_last message
        // that carries no text, and FinalizeAsync blocks until IsLastReceived.
        IsLastReceived = IsLastReceived || isLast;

        if ((isFinal || isLast)
            && (GetString(root, "language") ?? GetFirstString(root, "languages")) is { } language
            && !string.IsNullOrWhiteSpace(language))
        {
            DetectedLanguage = language;
        }

        var transcript = GetString(root, "transcript")?.Trim() ?? "";
        // ReSharper disable once ConvertIfStatementToReturnStatement -- subjective style; kept as an explicit if.
        if (string.IsNullOrWhiteSpace(transcript))
            return null;

        return new StreamingTranscriptEvent(transcript, isFinal || isLast);
    }

    private static bool IsError(JsonElement root)
    {
        if (GetString(root, "type") is { } type
            && type.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (GetString(root, "status") is { } status
            && status.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return root.TryGetProperty("error", out var error)
            && error.ValueKind is JsonValueKind.Object or JsonValueKind.String;
    }

    private static bool GetBool(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element)
        && element.ValueKind == JsonValueKind.True;

    private static string? GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element)
        && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string? GetFirstString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
