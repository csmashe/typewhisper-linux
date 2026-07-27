// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAi;

internal sealed class OpenAiRealtimeStreamingSession : IStreamingSession
{
    internal const string ModelId = "gpt-realtime-whisper";
    internal const int SourceSampleRate = 16_000;
    internal const int TargetSampleRate = 24_000;

    private readonly WebSocket _ws;
    private readonly OpenAiRealtimeTranscriptCollector _collector;
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Lock _audioStateLock = new();
    private readonly Dictionary<string, TaskCompletionSource<bool>> _transcriptionTerminals = [];
    // First non-cancellation fault the receive loop observed. Surfaced from
    // SendAudioAsync / FinalizeAsync so the coordinator's sender or finalize
    // path throws, the orchestrator's finalizeThrew flag flips, and batch
    // fallback fires. Without this, a server error event after one good
    // final segment would ship a truncated transcript as a clean success.
    // Mirrors XaiStreamingSession's _receiveLoopException pattern.
    private Exception? _receiveLoopException;
    // Successful appends advance this monotonically. Only
    // input_audio_buffer.committed advances the confirmed committed
    // boundary; transcription completed/failed events are asynchronous
    // per-item results and must never mutate either watermark.
    private long _appendedAudioWatermark;
    private long _committedAudioWatermark;
    private PendingExplicitCommit? _pendingExplicitCommit;
    private string? _lastCommittedItemId;
    private Task? _receiveTask;
    private bool _disposed;

    private OpenAiRealtimeStreamingSession(WebSocket ws, OpenAiRealtimeTranscriptCollector collector)
    {
        _ws = ws;
        _collector = collector;
    }

    private sealed class PendingExplicitCommit(long watermark)
    {
        public long Watermark { get; } = watermark;

        public TaskCompletionSource<string?> CommittedItemId { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    public static async Task<OpenAiRealtimeStreamingSession> ConnectAsync(
        string apiKey,
        string? language,
        string? prompt,
        bool useServerVad,
        CancellationToken ct)
    {
        var ws = CreateConfiguredWebSocket(apiKey);
        await ws.ConnectAsync(BuildRealtimeUri(), ct);

        var collector = new OpenAiRealtimeTranscriptCollector();
        var session = CreateStartedSession(ws, collector);
        await session.SendTextAsync(CreateSessionUpdatePayload(language, prompt, useServerVad), ct);
        return session;
    }

    internal static OpenAiRealtimeStreamingSession CreateConnectedSessionForTests(WebSocket ws)
    {
        // ReSharper disable once ConvertIfStatementToReturnStatement -- precondition guard; the suggested ternary-throw buries the throw.
        if (ws.State != WebSocketState.Open)
            throw new InvalidOperationException("The test WebSocket must already be open.");

        return CreateStartedSession(ws, new OpenAiRealtimeTranscriptCollector());
    }

    private static OpenAiRealtimeStreamingSession CreateStartedSession(
        WebSocket ws,
        OpenAiRealtimeTranscriptCollector collector)
    {
        var session = new OpenAiRealtimeStreamingSession(ws, collector);
        session._receiveTask = session.ReceiveLoopAsync(session._receiveCts.Token);
        return session;
    }

    public static async Task<PluginTranscriptionResult> TranscribeWavAsync(
        string apiKey,
        byte[] wavAudio,
        string? language,
        string? prompt,
        CancellationToken ct)
    {
        // Batch path: server VAD disabled. We send all PCM up front and
        // trigger transcription via the explicit commit in FinalizeAsync —
        // letting server VAD auto-commit on silence inside the file would
        // return early on the first utterance and miss the rest.
        await using var session = await ConnectAsync(apiKey, language, prompt, useServerVad: false, ct);
        var pcm = ExtractPcm16Data(wavAudio);
        const int chunkBytes = SourceSampleRate * sizeof(short) / 5; // 200ms
        for (var offset = 0; offset < pcm.Length; offset += chunkBytes)
        {
            var length = Math.Min(chunkBytes, pcm.Length - offset);
            await session.SendAudioAsync(pcm.AsMemory(offset, length), ct);
        }

        await session.FinalizeAsync(ct);
        await session.WaitForCompletedTranscriptAsync(TimeSpan.FromSeconds(10), ct);
        return new PluginTranscriptionResult(session._collector.CurrentText, language, 0, NoSpeechProbability: null);
    }

    internal static Uri BuildRealtimeUri() =>
        new("wss://api.openai.com/v1/realtime?intent=transcription");

    internal static IReadOnlyDictionary<string, string> CreateRealtimeHeaders(string apiKey) =>
        new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {apiKey}",
        };

    internal static ClientWebSocket CreateConfiguredWebSocket(string apiKey)
    {
        var ws = new ClientWebSocket();
        foreach (var header in CreateRealtimeHeaders(apiKey))
            ws.Options.SetRequestHeader(header.Key, header.Value);
        return ws;
    }

    internal static string CreateSessionUpdatePayload(string? language, string? prompt, bool useServerVad)
    {
        var transcription = new Dictionary<string, object?>
        {
            ["model"] = ModelId,
        };

        if (!string.IsNullOrWhiteSpace(language))
            transcription["language"] = language;

        // Forward caller-supplied prompt so realtime gets the same
        // transcription guidance as the batch whisper path. Upstream
        // verbatim drops it; the fork's HTTP transcription API merges
        // request prompt + language hints + dictionary terms before
        // calling TranscribeAsync, so omitting prompt here would
        // regress prompt-guided transcription for realtime-model
        // requests.
        if (!string.IsNullOrWhiteSpace(prompt))
            transcription["prompt"] = prompt;

        // Streaming path needs server VAD: with turn_detection null OpenAI
        // buffers audio until an explicit commit and only emits transcripts
        // on commit, so live dictation would show no partials/finals until
        // the user stops. Server VAD auto-commits per utterance, yielding
        // continuous events. Batch path keeps turn_detection null so the
        // explicit FinalizeAsync commit drives the single completion.
        object? turnDetection = useServerVad
            ? new Dictionary<string, object?> { ["type"] = "server_vad" }
            : null;

        var payload = new Dictionary<string, object?>
        {
            ["type"] = "session.update",
            ["session"] = new Dictionary<string, object?>
            {
                ["type"] = "transcription",
                ["audio"] = new Dictionary<string, object?>
                {
                    ["input"] = new Dictionary<string, object?>
                    {
                        ["format"] = new Dictionary<string, object?>
                        {
                            ["type"] = "audio/pcm",
                            ["rate"] = TargetSampleRate,
                        },
                        ["transcription"] = transcription,
                        ["turn_detection"] = turnDetection,
                    },
                },
            },
        };

        return JsonSerializer.Serialize(payload);
    }

    internal static string CreateAudioAppendPayload(ReadOnlySpan<byte> pcm16Audio)
    {
        var resampled = Resample16KPcmTo24K(pcm16Audio);
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "input_audio_buffer.append",
            ["audio"] = Convert.ToBase64String(resampled),
        });
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
    {
        if (_disposed) return;

        // Surface receive-loop faults before sending: a closed-socket
        // SendAudioAsync would otherwise silently no-op and starve the
        // coordinator of the fault that should trigger batch fallback.
        ThrowIfReceiveLoopFaulted();

        if (_ws.State != WebSocketState.Open || pcm16Audio.Length == 0)
            return;

        await _sendLock.WaitAsync(ct);
        try
        {
            if (_ws.State != WebSocketState.Open)
                return;

            await SendTextAsync(CreateAudioAppendPayload(pcm16Audio.Span), ct);
            lock (_audioStateLock)
            {
                _appendedAudioWatermark += pcm16Audio.Length;
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task FinalizeAsync(CancellationToken ct)
    {
        if (_disposed) return;

        while (true)
        {
            ThrowIfReceiveLoopFaulted();

            PendingExplicitCommit? pendingCommit = null;
            Task? committedItemTranscription = null;
            var sendCommit = false;

            await _sendLock.WaitAsync(ct);
            try
            {
                ThrowIfReceiveLoopFaulted();

                lock (_audioStateLock)
                {
                    if (_appendedAudioWatermark > _committedAudioWatermark)
                    {
                        pendingCommit = _pendingExplicitCommit;
                        if (pendingCommit is null)
                        {
                            pendingCommit = new PendingExplicitCommit(_appendedAudioWatermark);
                            _pendingExplicitCommit = pendingCommit;
                            sendCommit = true;
                        }
                    }
                    else if (_lastCommittedItemId is { } itemId)
                    {
                        committedItemTranscription = GetTranscriptionTerminalLocked(itemId).Task;
                    }
                }

                if (sendCommit)
                {
                    if (_ws.State != WebSocketState.Open)
                    {
                        var exception = new InvalidOperationException(
                            "OpenAI realtime session closed before pending audio could be committed.");
                        AbandonPendingCommit(pendingCommit!, exception);
                        throw exception;
                    }

                    try
                    {
                        await SendTextAsync("""{"type":"input_audio_buffer.commit"}""", ct);
                    }
                    catch (Exception ex)
                    {
                        AbandonPendingCommit(pendingCommit!, ex);
                        throw;
                    }
                }
            }
            finally
            {
                _sendLock.Release();
            }

            if (pendingCommit is not null)
            {
                var itemId = await pendingCommit.CommittedItemId.Task.WaitAsync(ct);
                ThrowIfReceiveLoopFaulted();

                if (!string.IsNullOrWhiteSpace(itemId))
                {
                    Task transcriptionTerminal;
                    lock (_audioStateLock)
                    {
                        transcriptionTerminal = GetTranscriptionTerminalLocked(itemId).Task;
                    }

                    await transcriptionTerminal.WaitAsync(ct);
                    ThrowIfReceiveLoopFaulted();
                }

                lock (_audioStateLock)
                {
                    if (_appendedAudioWatermark <= _committedAudioWatermark)
                        return;
                }

                // Audio was appended after the commit boundary was captured.
                // Loop and commit that later generation as well.
                continue;
            }

            if (committedItemTranscription is not null)
                await committedItemTranscription.WaitAsync(ct);

            ThrowIfReceiveLoopFaulted();
            return;
        }
    }

    private void ThrowIfReceiveLoopFaulted()
    {
        var ex = Volatile.Read(ref _receiveLoopException);
        if (ex is null) return;
        throw new InvalidOperationException(
            $"OpenAI realtime session faulted: {ex.Message}", ex);
    }

    private async Task SendTextAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private void AbandonPendingCommit(PendingExplicitCommit pendingCommit, Exception exception)
    {
        lock (_audioStateLock)
        {
            if (ReferenceEquals(_pendingExplicitCommit, pendingCommit))
                _pendingExplicitCommit = null;
        }

        if (exception is OperationCanceledException canceled)
            pendingCommit.CommittedItemId.TrySetCanceled(canceled.CancellationToken);
        else
            pendingCommit.CommittedItemId.TrySetException(exception);
    }

    private TaskCompletionSource<bool> GetTranscriptionTerminalLocked(string itemId)
    {
        if (_transcriptionTerminals.TryGetValue(itemId, out var terminal))
            return terminal;

        terminal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _transcriptionTerminals[itemId] = terminal;
        return terminal;
    }

    private void HandleBufferCommitted(string? itemId)
    {
        PendingExplicitCommit? explicitCommit;
        lock (_audioStateLock)
        {
            explicitCommit = _pendingExplicitCommit;
            var boundary = explicitCommit?.Watermark ?? _appendedAudioWatermark;
            _committedAudioWatermark = Math.Max(_committedAudioWatermark, boundary);
            _pendingExplicitCommit = null;

            if (!string.IsNullOrWhiteSpace(itemId))
            {
                _lastCommittedItemId = itemId;
                GetTranscriptionTerminalLocked(itemId);
            }
        }

        explicitCommit?.CommittedItemId.TrySetResult(itemId);
    }

    private void HandleTranscriptionCompleted(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return;

        lock (_audioStateLock)
        {
            GetTranscriptionTerminalLocked(itemId).TrySetResult(true);
        }
    }

    private void CaptureReceiveLoopException(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _receiveLoopException, exception, null) is not null)
            return;

        PendingExplicitCommit? pendingCommit;
        TaskCompletionSource<bool>[] transcriptionTerminals;
        lock (_audioStateLock)
        {
            pendingCommit = _pendingExplicitCommit;
            transcriptionTerminals = _transcriptionTerminals.Values.ToArray();
        }

        pendingCommit?.CommittedItemId.TrySetException(exception);
        foreach (var terminal in transcriptionTerminals)
            terminal.TrySetException(exception);
    }

    private void CaptureReceiveLoopClosure(CancellationToken ct)
    {
        // Deliberate disposal cancels the receive token — an orderly shutdown,
        // not a fault. Any other exit strands finalize's commit/transcription
        // waiters, so publish a terminal fault to release them. Idempotent: a
        // real earlier fault wins via CaptureReceiveLoopException.
        if (ct.IsCancellationRequested)
            return;
        CaptureReceiveLoopException(new InvalidOperationException(
            "OpenAI realtime session closed before transcription completed."));
    }

    private static (string? Type, string? ItemId) GetProtocolEventMetadata(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var typeElement)
            && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;
        var itemId = root.TryGetProperty("item_id", out var itemIdElement)
            && itemIdElement.ValueKind == JsonValueKind.String
                ? itemIdElement.GetString()
                : null;
        return (type, itemId);
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
                        CaptureReceiveLoopClosure(ct);
                        return;
                    }
                    messageBuffer.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                var (eventType, itemId) = GetProtocolEventMetadata(json);
                var applied = _collector.ApplyEvent(json, out var transcriptEvent);

                switch (eventType)
                {
                    case "input_audio_buffer.committed":
                        HandleBufferCommitted(itemId);
                        break;
                    case "conversation.item.input_audio_transcription.completed":
                        HandleTranscriptionCompleted(itemId);
                        break;
                }

                if (applied && transcriptEvent is not null)
                    TranscriptReceived?.Invoke(transcriptEvent);

                // ApplyEvent sets _collector.Error on `error` and
                // `conversation.item.input_audio_transcription.failed`
                // payloads but returns false — meaning we'd otherwise
                // keep looping until the server closes. Promote it to a
                // captured fault so the next SendAudioAsync / FinalizeAsync
                // throws and triggers batch fallback.
                // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
                if (_collector.Error is { } providerError)
                {
                    CaptureReceiveLoopException(new InvalidOperationException(providerError));
                    return;
                }
            }

            // Loop exited because the socket left the Open state (peer Abort,
            // CloseSent, etc.) rather than via a close frame, fault, or
            // deliberate disposal. Fault pending finalize waiters so they
            // don't hang until the caller's token.
            CaptureReceiveLoopClosure(ct);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            Debug.WriteLine($"OpenAI realtime WebSocket error: {ex.Message}");
            CaptureReceiveLoopException(ex);
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"OpenAI realtime parse error: {ex.Message}");
            CaptureReceiveLoopException(ex);
        }
    }

    private async Task WaitForCompletedTranscriptAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        while (!linked.Token.IsCancellationRequested)
        {
            // Surface a post-commit provider/transport fault immediately
            // instead of polling for completion until the 10 s timeout
            // and then masking the real cause with a generic
            // OperationCanceledException.
            ThrowIfReceiveLoopFaulted();
            if (_collector.HasCompletedTranscript)
                return;
            await Task.Delay(50, linked.Token);
        }
    }

    internal static byte[] Resample16KPcmTo24K(ReadOnlySpan<byte> pcm16Audio)
    {
        var sourceSampleCount = pcm16Audio.Length / sizeof(short);
        if (sourceSampleCount == 0)
            return [];

        var targetSampleCount = Math.Max(1, (int)Math.Round(sourceSampleCount * (double)TargetSampleRate / SourceSampleRate));
        var output = new byte[targetSampleCount * sizeof(short)];

        for (var targetIndex = 0; targetIndex < targetSampleCount; targetIndex++)
        {
            var sourcePosition = targetIndex * (double)SourceSampleRate / TargetSampleRate;
            var lowerIndex = Math.Min((int)Math.Floor(sourcePosition), sourceSampleCount - 1);
            var upperIndex = Math.Min(lowerIndex + 1, sourceSampleCount - 1);
            var fraction = sourcePosition - lowerIndex;
            var lower = ReadSample(pcm16Audio, lowerIndex);
            var upper = ReadSample(pcm16Audio, upperIndex);
            var sample = (short)Math.Clamp(
                (int)Math.Round(lower + (upper - lower) * fraction),
                short.MinValue,
                short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(targetIndex * sizeof(short)), sample);
        }

        return output;
    }

    private static short ReadSample(ReadOnlySpan<byte> pcm16Audio, int sampleIndex) =>
        BinaryPrimitives.ReadInt16LittleEndian(pcm16Audio.Slice(sampleIndex * sizeof(short), sizeof(short)));

    internal static byte[] ExtractPcm16Data(byte[] wavAudio)
    {
        if (wavAudio.Length <= 44)
            return [];

        for (var offset = 12; offset + 8 <= wavAudio.Length; )
        {
            var chunkId = Encoding.ASCII.GetString(wavAudio, offset, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(wavAudio.AsSpan(offset + 4, 4));
            var dataStart = offset + 8;
            // Reject malformed chunk sizes: negative (e.g. -8 would
            // pin offset and spin the loop, ignoring cancellation)
            // OR large enough that `dataStart + chunkSize` overflows
            // the signed int and wraps to a negative range end
            // — wavAudio[dataStart..wrapped] would then throw
            // ArgumentOutOfRangeException despite the <= guard below.
            // Treat as parse failure and fall back to the legacy
            // header-skip path.
            if (chunkSize < 0 || chunkSize > wavAudio.Length - dataStart)
                break;
            if (chunkId == "data")
                return wavAudio[dataStart..(dataStart + chunkSize)];
            // RIFF word-alignment: an odd-sized chunk is followed by a
            // single pad byte before the next chunk header. Skipping past
            // chunkSize alone would land on the pad and miss the real
            // `data` chunk, then fall back to `wavAudio[44..]` and ship
            // header/metadata bytes as PCM. Upstream verbatim missed this.
            offset = dataStart + chunkSize + (chunkSize & 1);
        }

        return wavAudio[44..];
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await _receiveCts.CancelAsync();

        if (_ws.State == WebSocketState.Open)
        {
            // Bound the close handshake. An unresponsive peer (stalled
            // server, dropped network) would otherwise hang DisposeAsync
            // indefinitely — the coordinator's CleanupSessionAsync wraps
            // FinalizeAsync in a 2s CTS but awaits DisposeAsync unbounded,
            // so this is the only place that can prevent a hung teardown
            // from blocking dictation stop or app exit. Mirrors
            // XaiStreamingSession's close-handshake guard.
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closeCts.Token); }
            catch (OperationCanceledException ex)
            {
                Debug.WriteLine($"OpenAI realtime WebSocket close timed out: {ex.Message}");
                try { _ws.Abort(); } catch { /* best effort */ }
            }
            catch (WebSocketException ex)
            {
                Debug.WriteLine($"OpenAI realtime WebSocket close error: {ex.Message}");
                try { _ws.Abort(); } catch { /* best effort */ }
            }
            catch (InvalidOperationException ex)
            {
                Debug.WriteLine($"OpenAI realtime WebSocket close skipped: {ex.Message}");
            }
        }

        if (_receiveTask is not null)
        {
            try { await _receiveTask; }
            catch
            {
                //nada
            }
        }

        _sendLock.Dispose();
        _receiveCts.Dispose();
        _ws.Dispose();
    }
}

internal sealed class OpenAiRealtimeTranscriptCollector
{
    private readonly List<string> _completedOrder = [];
    private readonly Dictionary<string, string> _completedTexts = [];
    private readonly Dictionary<string, string> _deltaTexts = [];

    public string CurrentText
    {
        get
        {
            var parts = _completedOrder
                .Where(_completedTexts.ContainsKey)
                .Select(id => _completedTexts[id])
                .ToList();
            parts.AddRange(_deltaTexts
                .Where(pair => !_completedTexts.ContainsKey(pair.Key))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Value)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
            return string.Join(" ", parts).Trim();
        }
    }

    public bool HasCompletedTranscript => _completedOrder.Count > 0;
    public bool IsSessionReady { get; private set; }
    public string? Error { get; private set; }

    public bool ApplyEvent(string json, out StreamingTranscriptEvent? transcriptEvent)
    {
        transcriptEvent = null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeEl))
            return false;

        var type = typeEl.GetString();
        switch (type)
        {
            case "conversation.item.input_audio_transcription.delta":
            {
                var itemId = GetString(root, "item_id") ?? Guid.NewGuid().ToString("N");
                var delta = GetString(root, "delta") ?? "";
                var itemText = _deltaTexts.TryGetValue(itemId, out var current)
                    ? current + delta
                    : delta;
                _deltaTexts[itemId] = itemText;
                // Emit just THIS item's accumulated delta — not CurrentText.
                // StreamingTranscriptionCoordinator treats every IsFinal event
                // as a new immutable segment; partials likewise should not
                // carry prior completed items, or the host's stabilizer would
                // try to extend an already-finalized segment with the next
                // utterance's interim text. Mirrors XaiTranscriptCollector.
                transcriptEvent = new StreamingTranscriptEvent(itemText, false);
                return !string.IsNullOrWhiteSpace(itemText);
            }
            case "conversation.item.input_audio_transcription.completed":
            {
                var itemId = GetString(root, "item_id") ?? Guid.NewGuid().ToString("N");
                var transcript = (GetString(root, "transcript") ?? "").Trim();
                // Record the completion even when the transcript is empty —
                // OpenAI emits an empty completed event for silent audio,
                // and WaitForCompletedTranscriptAsync polls
                // HasCompletedTranscript to know when to return. Without
                // this, TranscribeWavAsync on a silent clip would block
                // for the full 10s timeout and then throw instead of
                // returning an empty PluginTranscriptionResult.
                if (!_completedTexts.ContainsKey(itemId))
                    _completedOrder.Add(itemId);
                _completedTexts[itemId] = transcript;
                _deltaTexts.Remove(itemId);
                if (string.IsNullOrWhiteSpace(transcript))
                    return false;
                // Emit just THIS item's transcript — not CurrentText. The
                // coordinator appends each IsFinal text to _finalSegments
                // with a newline separator (StreamingTranscriptionCoordinator
                // line 364-372). Emitting cumulative text here would produce
                // "hello\nhello world" for two completed items "hello" then
                // "world". CurrentText stays cumulative for TranscribeWavAsync,
                // which wants the joined batch transcript.
                transcriptEvent = new StreamingTranscriptEvent(transcript, true);
                return true;
            }
            case "session.updated":
            case "transcription_session.updated":
                IsSessionReady = true;
                return false;
            case "conversation.item.input_audio_transcription.failed":
            case "error":
                Error = ExtractErrorMessage(root) ?? "OpenAI realtime transcription failed";
                return false;
            default:
                return false;
        }
    }

    private static string? GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string? ExtractErrorMessage(JsonElement root)
    {
        // ReSharper disable once InvertIf -- subjective nesting-style suggestion; kept as-is.
        if (root.TryGetProperty("error", out var error))
        {
            // ReSharper disable once ConvertIfStatementToSwitchStatement -- subjective control-flow style; the if-chain reads fine here.
            if (error.ValueKind == JsonValueKind.Object)
            {
                if (GetString(error, "message") is { } message)
                    return message;
                if (GetString(error, "type") is { } type)
                    return type;
            }
            if (error.ValueKind == JsonValueKind.String)
                return error.GetString();
        }

        return GetString(root, "message");
    }
}
