// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.Plugin.OpenAi;

internal sealed class OpenAiRealtimeStreamingSession : IStreamingSession
{
    internal const string ModelId = "gpt-realtime-whisper";
    internal const int SourceSampleRate = 16_000;
    internal const int TargetSampleRate = 24_000;

    private readonly WebSocketSessionPump _pump;
    private readonly OpenAiRealtimeWebSocketAdapter _adapter;

    private OpenAiRealtimeStreamingSession(
        WebSocketSessionPump pump,
        OpenAiRealtimeWebSocketAdapter adapter
    )
    {
        _pump = pump;
        _adapter = adapter;
    }

    public event Action<StreamingTranscriptEvent>? TranscriptReceived
    {
        add => _pump.TranscriptReceived += value;
        remove => _pump.TranscriptReceived -= value;
    }

    public static async Task<OpenAiRealtimeStreamingSession> ConnectAsync(
        string apiKey,
        string? language,
        string? prompt,
        bool useServerVad,
        CancellationToken ct
    )
    {
        var adapter = new OpenAiRealtimeWebSocketAdapter(
            apiKey,
            language,
            prompt,
            useServerVad,
            sendSessionUpdate: true
        );
        var pump = await WebSocketSessionPump.ConnectAsync(adapter, ct);
        return new OpenAiRealtimeStreamingSession(pump, adapter);
    }

    internal static OpenAiRealtimeStreamingSession CreateConnectedSessionForTests(
        WebSocket ws
    )
    {
        if (ws.State != WebSocketState.Open)
            throw new InvalidOperationException("The test WebSocket must already be open.");

        var adapter = new OpenAiRealtimeWebSocketAdapter(
            "",
            null,
            null,
            useServerVad: true,
            sendSessionUpdate: false
        );
        var pump = WebSocketSessionPump
            .StartConnectedAsync(
                adapter,
                new ClientWebSocketTransport(ws),
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();
        return new OpenAiRealtimeStreamingSession(pump, adapter);
    }

    public static async Task<PluginTranscriptionResult> TranscribeWavAsync(
        string apiKey,
        byte[] wavAudio,
        string? language,
        string? prompt,
        CancellationToken ct
    )
    {
        await using var session = await ConnectAsync(
            apiKey,
            language,
            prompt,
            useServerVad: false,
            ct
        );
        var pcm = ExtractPcm16Data(wavAudio);
        const int chunkBytes = SourceSampleRate * sizeof(short) / 5;
        for (var offset = 0; offset < pcm.Length; offset += chunkBytes)
        {
            var length = Math.Min(chunkBytes, pcm.Length - offset);
            await session.SendAudioAsync(pcm.AsMemory(offset, length), ct);
        }

        await session.FinalizeAsync(ct);
        await session.WaitForCompletedTranscriptAsync(
            TimeSpan.FromSeconds(10),
            ct
        );
        return new PluginTranscriptionResult(
            session._adapter.Collector.CurrentText,
            language,
            0,
            NoSpeechProbability: null
        );
    }

    internal static Uri BuildRealtimeUri() =>
        new("wss://api.openai.com/v1/realtime?intent=transcription");

    internal static IReadOnlyDictionary<string, string> CreateRealtimeHeaders(
        string apiKey
    ) =>
        new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {apiKey}",
        };

    internal static string CreateSessionUpdatePayload(
        string? language,
        string? prompt,
        bool useServerVad
    )
    {
        var transcription = new Dictionary<string, object?>
        {
            ["model"] = ModelId,
        };

        if (!string.IsNullOrWhiteSpace(language))
            transcription["language"] = language;
        if (!string.IsNullOrWhiteSpace(prompt))
            transcription["prompt"] = prompt;

        object? turnDetection =
            useServerVad
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

    internal static string CreateAudioAppendPayload(
        ReadOnlySpan<byte> pcm16Audio
    )
    {
        var resampled = Resample16KPcmTo24K(pcm16Audio);
        return JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["type"] = "input_audio_buffer.append",
                ["audio"] = Convert.ToBase64String(resampled),
            }
        );
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct) =>
        _pump.SendAudioAsync(pcm16Audio, ct);

    public Task FinalizeAsync(CancellationToken ct) => _pump.FinalizeAsync(ct);

    public ValueTask DisposeAsync() => _pump.DisposeAsync();

    private async Task WaitForCompletedTranscriptAsync(
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            timeoutCts.Token
        );
        while (!linked.Token.IsCancellationRequested)
        {
            if (_adapter.Collector.HasCompletedTranscript)
                return;
            await Task.Delay(50, linked.Token);
        }
    }

    internal static byte[] Resample16KPcmTo24K(
        ReadOnlySpan<byte> pcm16Audio
    )
    {
        var sourceSampleCount = pcm16Audio.Length / sizeof(short);
        if (sourceSampleCount == 0)
            return [];

        var targetSampleCount = Math.Max(
            1,
            (int)Math.Round(
                sourceSampleCount * (double)TargetSampleRate / SourceSampleRate
            )
        );
        var output = new byte[targetSampleCount * sizeof(short)];

        for (var targetIndex = 0; targetIndex < targetSampleCount; targetIndex++)
        {
            var sourcePosition =
                targetIndex * (double)SourceSampleRate / TargetSampleRate;
            var lowerIndex = Math.Min(
                (int)Math.Floor(sourcePosition),
                sourceSampleCount - 1
            );
            var upperIndex = Math.Min(lowerIndex + 1, sourceSampleCount - 1);
            var fraction = sourcePosition - lowerIndex;
            var lower = ReadSample(pcm16Audio, lowerIndex);
            var upper = ReadSample(pcm16Audio, upperIndex);
            var sample = (short)Math.Clamp(
                (int)Math.Round(lower + (upper - lower) * fraction),
                short.MinValue,
                short.MaxValue
            );
            BinaryPrimitives.WriteInt16LittleEndian(
                output.AsSpan(targetIndex * sizeof(short)),
                sample
            );
        }

        return output;
    }

    private static short ReadSample(
        ReadOnlySpan<byte> pcm16Audio,
        int sampleIndex
    ) =>
        BinaryPrimitives.ReadInt16LittleEndian(
            pcm16Audio.Slice(sampleIndex * sizeof(short), sizeof(short))
        );

    internal static byte[] ExtractPcm16Data(byte[] wavAudio)
    {
        if (wavAudio.Length <= 44)
            return [];

        for (var offset = 12; offset + 8 <= wavAudio.Length;)
        {
            var chunkId = Encoding.ASCII.GetString(wavAudio, offset, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(
                wavAudio.AsSpan(offset + 4, 4)
            );
            var dataStart = offset + 8;
            if (chunkSize < 0 || chunkSize > wavAudio.Length - dataStart)
                break;
            if (chunkId == "data")
                return wavAudio[dataStart..(dataStart + chunkSize)];
            offset = dataStart + chunkSize + (chunkSize & 1);
        }

        return wavAudio[44..];
    }
}

internal sealed class OpenAiRealtimeWebSocketAdapter(
    string apiKey,
    string? language,
    string? prompt,
    bool useServerVad,
    bool sendSessionUpdate
) : IWebSocketSessionAdapter
{
    private readonly Lock _audioStateLock = new();
    private readonly HashSet<string> _completedItemIds = [];
    private readonly HashSet<string> _committedItemIds = [];
    private long _appendedAudioWatermark;
    private long _committedAudioWatermark;
    private long? _pendingExplicitCommitWatermark;
    private string? _lastCommittedItemId;
    private string? _finalItemId;
    private bool _finalizationStarted;

    internal OpenAiRealtimeTranscriptCollector Collector { get; } = new();

    public string ProviderName => "OpenAI realtime";
    public WebSocketReadinessPolicy Readiness => WebSocketReadinessPolicy.Immediate;
    public WebSocketTerminalPolicy Terminal =>
        WebSocketTerminalPolicy.Require(
            "every committed item's transcription completion"
        );
    public WebSocketKeepAlivePolicy? KeepAlive => null;
    public WebSocketClosePolicy ClosePolicy => WebSocketClosePolicy.Default;

    public ValueTask<WebSocketConnectionOptions> GetConnectionOptionsAsync(
        CancellationToken ct
    ) =>
        ValueTask.FromResult(
            new WebSocketConnectionOptions(
                OpenAiRealtimeStreamingSession.BuildRealtimeUri(),
                OpenAiRealtimeStreamingSession.CreateRealtimeHeaders(apiKey)
            )
        );

    public ValueTask<IReadOnlyList<WebSocketOutboundMessage>> OnConnectedAsync(
        CancellationToken ct
    ) =>
        ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(
            sendSessionUpdate
                ? [
                    TextMessage(
                        OpenAiRealtimeStreamingSession.CreateSessionUpdatePayload(
                            language,
                            prompt,
                            useServerVad
                        )
                    ),
                ]
                : []
        );

    public ValueTask<IReadOnlyList<WebSocketOutboundMessage>> EncodeAudioAsync(
        ReadOnlyMemory<byte> pcm16Audio,
        CancellationToken ct
    )
    {
        if (pcm16Audio.Length == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(
                []
            );
        }

        lock (_audioStateLock)
        {
            _appendedAudioWatermark += pcm16Audio.Length;
        }

        return ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(
            [
                TextMessage(
                    OpenAiRealtimeStreamingSession.CreateAudioAppendPayload(
                        pcm16Audio.Span
                    )
                ),
            ]
        );
    }

    public ValueTask<WebSocketFinalizePlan> BeginFinalizeAsync(CancellationToken ct)
    {
        lock (_audioStateLock)
        {
            _finalizationStarted = true;
            if (_appendedAudioWatermark > _committedAudioWatermark)
            {
                _pendingExplicitCommitWatermark = _appendedAudioWatermark;
                return ValueTask.FromResult(
                    new WebSocketFinalizePlan(
                        [TextMessage("""{"type":"input_audio_buffer.commit"}""")]
                    )
                );
            }

            _finalItemId = _lastCommittedItemId;
            var alreadyTerminal = _finalItemId is null || TranscriptionsSettled();
            return ValueTask.FromResult(
                new WebSocketFinalizePlan([], alreadyTerminal)
            );
        }
    }

    public WebSocketInboundResult HandleMessage(
        WebSocketMessageType type,
        ReadOnlyMemory<byte> completePayload
    )
    {
        if (type != WebSocketMessageType.Text)
            return WebSocketInboundResult.Empty;

        var json = Encoding.UTF8.GetString(completePayload.Span);
        string? eventType;
        string? itemId;
        try
        {
            (eventType, itemId) = GetProtocolEventMetadata(json);
        }
        catch (JsonException ex)
        {
            return new WebSocketInboundResult(
                [],
                Fault: new InvalidOperationException(
                    "OpenAI realtime sent malformed JSON.",
                    ex
                )
            );
        }

        if (string.IsNullOrWhiteSpace(eventType))
        {
            return new WebSocketInboundResult(
                [],
                Fault: new InvalidOperationException(
                    "OpenAI realtime sent a malformed streaming message."
                )
            );
        }

        if (
            eventType
                is "input_audio_buffer.committed"
                    or "conversation.item.input_audio_transcription.completed"
            && string.IsNullOrWhiteSpace(itemId)
        )
        {
            return new WebSocketInboundResult(
                [],
                Fault: new InvalidOperationException(
                    $"OpenAI realtime {eventType} event omitted item_id."
                )
            );
        }

        bool applied;
        StreamingTranscriptEvent? transcript;
        try
        {
            applied = Collector.ApplyEvent(json, out transcript);
        }
        catch (Exception ex)
        {
            return new WebSocketInboundResult([], Fault: ex);
        }

        bool terminal;
        lock (_audioStateLock)
        {
            switch (eventType)
            {
                case "input_audio_buffer.committed":
                {
                    var boundary =
                        _pendingExplicitCommitWatermark
                        ?? _appendedAudioWatermark;
                    _committedAudioWatermark = Math.Max(
                        _committedAudioWatermark,
                        boundary
                    );
                    var wasExplicit = _pendingExplicitCommitWatermark is not null;
                    _pendingExplicitCommitWatermark = null;
                    if (!string.IsNullOrWhiteSpace(itemId))
                    {
                        _lastCommittedItemId = itemId;
                        _committedItemIds.Add(itemId);
                        if (_finalizationStarted && wasExplicit)
                            _finalItemId = itemId;
                    }

                    break;
                }
                case "conversation.item.input_audio_transcription.completed":
                    if (!string.IsNullOrWhiteSpace(itemId))
                        _completedItemIds.Add(itemId);

                    break;
            }

            terminal = TranscriptionsSettled();
        }

        if (Collector.Error is { } providerError)
        {
            return new WebSocketInboundResult(
                [],
                Fault: new InvalidOperationException(providerError)
            );
        }

        var transcripts =
            applied && transcript is not null
                ? (IReadOnlyList<StreamingTranscriptEvent>)[transcript]
                : [];
        return new WebSocketInboundResult(
            transcripts,
            terminal
                ? WebSocketSessionSignal.Terminal
                : WebSocketSessionSignal.None
        );
    }

    // Server VAD can leave several committed items in flight, and completions can
    // arrive out of commit order — waiting on only the final item would drop text
    // from an earlier item still transcribing. Callers must hold _audioStateLock.
    private bool TranscriptionsSettled() =>
        _finalizationStarted
        && _finalItemId is not null
        && _completedItemIds.Contains(_finalItemId)
        && _committedItemIds.IsSubsetOf(_completedItemIds);

    private static WebSocketOutboundMessage TextMessage(string json) =>
        new(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text);

    private static (string? Type, string? ItemId) GetProtocolEventMetadata(
        string json
    )
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type =
            root.TryGetProperty("type", out var typeElement)
            && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;
        var itemId =
            root.TryGetProperty("item_id", out var itemIdElement)
            && itemIdElement.ValueKind == JsonValueKind.String
                ? itemIdElement.GetString()
                : null;
        return (type, itemId);
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
            parts.AddRange(
                _deltaTexts
                    .Where(pair => !_completedTexts.ContainsKey(pair.Key))
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => pair.Value)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
            );
            return string.Join(" ", parts).Trim();
        }
    }

    public bool HasCompletedTranscript => _completedOrder.Count > 0;
    public bool IsSessionReady { get; private set; }
    public string? Error { get; private set; }

    public bool ApplyEvent(
        string json,
        out StreamingTranscriptEvent? transcriptEvent
    )
    {
        transcriptEvent = null;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!root.TryGetProperty("type", out var typeElement))
            return false;

        var type = typeElement.GetString();
        switch (type)
        {
            case "conversation.item.input_audio_transcription.delta":
            {
                var itemId =
                    GetString(root, "item_id")
                    ?? Guid.NewGuid().ToString("N");
                var delta = GetString(root, "delta") ?? "";
                var itemText =
                    _deltaTexts.TryGetValue(itemId, out var current)
                        ? current + delta
                        : delta;
                _deltaTexts[itemId] = itemText;
                transcriptEvent = new StreamingTranscriptEvent(itemText, false);
                return !string.IsNullOrWhiteSpace(itemText);
            }
            case "conversation.item.input_audio_transcription.completed":
            {
                var itemId =
                    GetString(root, "item_id")
                    ?? Guid.NewGuid().ToString("N");
                var transcript = (GetString(root, "transcript") ?? "").Trim();
                if (!_completedTexts.ContainsKey(itemId))
                    _completedOrder.Add(itemId);
                _completedTexts[itemId] = transcript;
                _deltaTexts.Remove(itemId);
                if (string.IsNullOrWhiteSpace(transcript))
                    return false;
                transcriptEvent = new StreamingTranscriptEvent(transcript, true);
                return true;
            }
            case "session.updated":
            case "transcription_session.updated":
                IsSessionReady = true;
                return false;
            case "conversation.item.input_audio_transcription.failed":
            case "error":
                Error =
                    ExtractErrorMessage(root)
                    ?? "OpenAI realtime transcription failed";
                return false;
            default:
                return false;
        }
    }

    private static string? GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element)
        && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string? ExtractErrorMessage(JsonElement root)
    {
        // ReSharper disable once InvertIf -- inverting would duplicate the
        // GetString(root, "message") fallback on both exits.
        if (root.TryGetProperty("error", out var error))
        {
            // ReSharper disable once ConvertIfStatementToSwitchStatement -- the Object arm
            // falls through to the shared fallback, which a switch could only re-express
            // with a `when` clause and an empty arm.
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
