using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Reson8;

internal sealed class Reson8StreamingSession : IStreamingSession
{
    private const int TeardownTimeoutMs = 2000;

    private readonly WebSocket _ws;
    private readonly Reson8TranscriptCollector _collector;
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TaskCompletionSource _flushConfirmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _disposeGate = new();
    private readonly Lock _operationGate = new();
    private Task? _receiveTask;
    private Task? _disposeTask;
    private int _activeOperations;
    private bool _disposed;

    private Reson8StreamingSession(WebSocket ws, Reson8TranscriptCollector collector)
    {
        _ws = ws;
        _collector = collector;
    }

    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    public static async Task<Reson8StreamingSession> ConnectAsync(
        string apiKey,
        string baseUrl,
        string authHeader,
        string? modelId,
        string? language,
        CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        foreach (var header in CreateStreamingHeaders(apiKey, authHeader))
            ws.Options.SetRequestHeader(header.Key, header.Value);

        await ws.ConnectAsync(BuildRealtimeUri(baseUrl, modelId, language), ct);

        return CreateStartedSession(ws);
    }

    internal static Reson8StreamingSession CreateConnectedSessionForTests(WebSocket ws)
    {
        // ReSharper disable once ConvertIfStatementToReturnStatement -- the guard throws; folding it into a ternary would need a throw expression.
        if (ws.State != WebSocketState.Open)
            throw new InvalidOperationException("The test WebSocket must already be open.");

        return CreateStartedSession(ws);
    }

    private static Reson8StreamingSession CreateStartedSession(WebSocket ws)
    {
        var session = new Reson8StreamingSession(ws, new Reson8TranscriptCollector());
        session._receiveTask = session.ReceiveLoopAsync(session._receiveCts.Token);
        return session;
    }

    public static Uri BuildRealtimeUri(string baseUrl, string? modelId, string? language)
    {
        var normalizedBase = baseUrl.Trim().TrimEnd('/');
        var baseUri = new Uri(normalizedBase);

        // Preserve any base-URL path prefix (e.g. a reverse proxy mounted under
        // /gateway) by appending the realtime endpoint to it, mirroring how the
        // prerecorded URI is built. Overwriting Path here would route streaming
        // to the wrong endpoint for proxied deployments.
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ? "ws" : "wss",
            Path = $"{basePath}/v1/speech-to-text/realtime",
        };

        var query = new List<string>
        {
            "encoding=pcm_s16le",
            "sample_rate=16000",
            "channels=1",
            "include_interim=true",
        };

        if (!string.IsNullOrWhiteSpace(language)
            && !language.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            query.Add($"language={Uri.EscapeDataString(language.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(modelId)
            && !string.Equals(modelId, Reson8Plugin.DefaultModelId, StringComparison.Ordinal))
        {
            query.Add($"custom_model_id={Uri.EscapeDataString(modelId.Trim())}");
        }

        builder.Query = string.Join("&", query);
        return builder.Uri;
    }

    public static IReadOnlyDictionary<string, string> CreateStreamingHeaders(string apiKey, string authHeader) =>
        new Dictionary<string, string>
        {
            [string.IsNullOrWhiteSpace(authHeader) ? Reson8Plugin.DefaultAuthHeader : authHeader.Trim()] =
                Reson8Plugin.AuthHeaderValue(apiKey, authHeader),
        };

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
                if (_ws.State == WebSocketState.Open)
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

            var json = $$"""{"type":"flush_request","id":"{{Guid.NewGuid()}}"}""";
            await _sendLock.WaitAsync(ct);
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    var payload = Encoding.UTF8.GetBytes(json);
                    await _ws.SendAsync(payload, WebSocketMessageType.Text, true, ct);
                }
            }
            finally
            {
                _sendLock.Release();
            }

            await _flushConfirmed.Task.WaitAsync(ct);
        }
        finally
        {
            EndOperation();
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
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _flushConfirmed.TrySetResult();
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

                if (_collector.IsFlushConfirmed)
                    _flushConfirmed.TrySetResult();
            }
        }
        catch (OperationCanceledException ex)
        {
            Debug.WriteLine($"Reson8 receive loop canceled: {ex.Message}");
        }
        catch (WebSocketException ex)
        {
            Debug.WriteLine($"Reson8 WebSocket error: {ex.Message}");
            _flushConfirmed.TrySetException(ex);
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"Reson8 parse error: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            Debug.WriteLine($"Reson8 stream error: {ex.Message}");
            _flushConfirmed.TrySetException(ex);
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
            Debug.WriteLine($"Reson8 disposal error: {ex.Message}");
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
            Debug.WriteLine($"Reson8 receive cancellation error: {ex.Message}");
        }
        _flushConfirmed.TrySetResult();
        _ = _flushConfirmed.Task.Exception;

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
                    Debug.WriteLine("Reson8 WebSocket close timed out.");
                    AbortSocket(ref abortInvoked);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Reson8 WebSocket close error: {ex.Message}");
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
            Debug.WriteLine($"Reson8 WebSocket abort error: {ex.Message}");
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
                $"Reson8 {operationName} operation stopped during disposal: {ex.Message}"
            );
        }
    }

    private static void TryDispose(IDisposable resource, string resourceName)
    {
        try { resource.Dispose(); }
        catch (Exception ex)
        {
            Debug.WriteLine($"Reson8 {resourceName} disposal error: {ex.Message}");
        }
    }
}

internal sealed class Reson8TranscriptCollector
{
    private readonly List<string> _finals = [];
    private string _interim = "";

    public bool IsFlushConfirmed { get; private set; }
    public string FinalText => string.Join(" ", _finals);

    public StreamingTranscriptEvent? ApplyEvent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var type = GetString(root, "type");
        if (string.IsNullOrWhiteSpace(type))
            return null;

        if (type.Equals("flush_confirmation", StringComparison.OrdinalIgnoreCase))
        {
            IsFlushConfirmed = true;
            return null;
        }

        if (type.Contains("error", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Reson8Plugin.ExtractApiError(json));

        if (!type.Equals("transcript", StringComparison.OrdinalIgnoreCase))
            return null;

        var text = GetString(root, "text")?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var isFinal = GetBool(root, "is_final");
        if (isFinal)
        {
            _finals.Add(text);
            _interim = "";
        }
        else
        {
            _interim = text;
        }

        return new StreamingTranscriptEvent(text, isFinal);
    }

    public string ApplyEvent(StreamingTranscriptEvent evt)
    {
        if (evt.IsFinal)
        {
            var trimmed = evt.Text.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                _finals.Add(trimmed);
            _interim = "";
        }
        else
        {
            _interim = evt.Text.Trim();
        }

        return CurrentText;
    }

    private string CurrentText
    {
        get
        {
            var parts = _finals.ToList();
            if (!string.IsNullOrWhiteSpace(_interim))
                parts.Add(_interim);
            return string.Join(" ", parts);
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
}
