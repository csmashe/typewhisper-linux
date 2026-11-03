using System.Net.WebSockets;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.Plugin.AssemblyAi;

internal sealed class AssemblyAiStreamingSession : IStreamingSession, IStreamingSessionHealth
{
    private readonly WebSocketSessionPump _pump;

    private AssemblyAiStreamingSession(WebSocketSessionPump pump)
    {
        _pump = pump;
    }

    internal static async Task<AssemblyAiStreamingSession> CreateConnectedSessionForTests(
        WebSocket ws
    )
    {
        var pump = await WebSocketSessionPump.StartConnectedAsync(
            new AssemblyAiWebSocketAdapter("", null),
            new ClientWebSocketTransport(ws),
            CancellationToken.None
        );
        return new AssemblyAiStreamingSession(pump);
    }

    public Exception? Fault => _pump.Fault;

    public event Action<StreamingTranscriptEvent>? TranscriptReceived
    {
        add => _pump.TranscriptReceived += value;
        remove => _pump.TranscriptReceived -= value;
    }

    public static async Task<AssemblyAiStreamingSession> ConnectAsync(
        string apiKey,
        string modelId,
        string? language,
        IReadOnlyList<string> keyterms,
        CancellationToken ct
    )
    {
        var pump = await WebSocketSessionPump.ConnectAsync(
            new AssemblyAiWebSocketAdapter(apiKey, language, keyterms, modelId),
            ct
        );
        return new AssemblyAiStreamingSession(pump);
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct) =>
        _pump.SendAudioAsync(pcm16Audio, ct);

    public Task FinalizeAsync(CancellationToken ct) => _pump.FinalizeAsync(ct);

    public ValueTask DisposeAsync() => _pump.DisposeAsync();
}

internal sealed class AssemblyAiWebSocketAdapter(
    string apiKey,
    string? language,
    IReadOnlyList<string>? keyterms = null,
    string modelId = AssemblyAiModels.DefaultId
) : IWebSocketSessionAdapter
{
    // AssemblyAI accepts 50-1000 ms per message (16 kHz PCM16).
    internal const int MinimumChunkBytes = 1600;
    internal const int MaximumChunkBytes = 32000;

    private readonly MemoryStream _audioBuffer = new();

    // Receive-loop state only (HandleMessage), so it needs no synchronization.
    private int _lastTurnOrder = -1;
    private bool _lastTurnFinal = true;
    // An empty unfinished turn loses nothing, so only pending text counts.
    private bool _lastTurnPendingText;

    public string ProviderName => "AssemblyAI";
    public WebSocketReadinessPolicy Readiness => WebSocketReadinessPolicy.Immediate;
    public WebSocketTerminalPolicy Terminal =>
        WebSocketTerminalPolicy.Require("Termination");
    public WebSocketKeepAlivePolicy? KeepAlive => null;
    public WebSocketClosePolicy ClosePolicy => WebSocketClosePolicy.Default;

    public ValueTask<WebSocketConnectionOptions> GetConnectionOptionsAsync(
        CancellationToken ct
    )
    {
        var url = "wss://streaming.assemblyai.com/v3/ws?" + ModelQuery(modelId, AssemblyAiModels.NormalizeLanguage(language));
        if (keyterms is { Count: > 0 })
            url += "&keyterms_prompt=" + Uri.EscapeDataString(JsonSerializer.Serialize(keyterms));

        IReadOnlyDictionary<string, string> headers =
            new Dictionary<string, string>
            {
                ["Authorization"] = apiKey,
            };
        return ValueTask.FromResult(
            new WebSocketConnectionOptions(new Uri(url), headers)
        );
    }

    // Always named: AssemblyAI's default model has changed price before.
    private static string ModelQuery(string modelId, string? language)
    {
        var query = "sample_rate=16000&encoding=pcm_s16le";
        if (AssemblyAiModels.Find(modelId) is { IsPro: false })
        {
            return language is not null && AssemblyAiModels.IsEnglish(language)
                ? query + "&speech_model=universal-streaming-english&format_turns=true"
                : query + "&speech_model=universal-streaming-multilingual&format_turns=true&language_detection=true";
        }

        query += "&speech_model=" + AssemblyAiModels.ProStreamingModelId + "&language_detection=true";
        if (language is not null)
        {
            query += "&language_codes="
                + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { AssemblyAiModels.BaseLanguage(language) }));
        }

        return query;
    }

    public ValueTask<IReadOnlyList<WebSocketOutboundMessage>> OnConnectedAsync(
        CancellationToken ct
    ) =>
        ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>([]);

    public ValueTask<IReadOnlyList<WebSocketOutboundMessage>> EncodeAudioAsync(
        ReadOnlyMemory<byte> pcm16Audio,
        CancellationToken ct
    )
    {
        _audioBuffer.Write(pcm16Audio.Span);
        if (_audioBuffer.Length < MinimumChunkBytes)
        {
            return ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(
                []
            );
        }

        // A buffered residual plus a full-size write can exceed the 1000 ms limit.
        var messages = new List<WebSocketOutboundMessage>();
        var bufferedAudio = _audioBuffer.GetBuffer();
        var offset = 0;
        while (_audioBuffer.Length - offset >= MinimumChunkBytes)
        {
            // Whole samples only; an odd trailing byte waits for its other half.
            var count = Math.Min(MaximumChunkBytes, (int)_audioBuffer.Length - offset) & ~1;
            messages.Add(
                new WebSocketOutboundMessage(
                    bufferedAudio.AsSpan(offset, count).ToArray(),
                    WebSocketMessageType.Binary
                )
            );
            offset += count;
        }

        var remaining = (int)_audioBuffer.Length - offset;
        bufferedAudio.AsSpan(offset, remaining).CopyTo(bufferedAudio);
        _audioBuffer.SetLength(remaining);
        _audioBuffer.Position = remaining;
        return ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(messages);
    }

    public ValueTask<WebSocketFinalizePlan> BeginFinalizeAsync(CancellationToken ct)
    {
        var messages = new List<WebSocketOutboundMessage>(2);
        if (_audioBuffer.Length != 0)
        {
            // AssemblyAI rejects chunks under 50 ms; pad the residual with
            // silence instead of dropping it.
            var residual = _audioBuffer.ToArray();
            if (residual.Length < MinimumChunkBytes)
                Array.Resize(ref residual, MinimumChunkBytes);

            messages.Add(
                new WebSocketOutboundMessage(residual, WebSocketMessageType.Binary)
            );
            _audioBuffer.SetLength(0);
        }

        messages.Add(
            new WebSocketOutboundMessage(
                """{"type":"Terminate"}"""u8.ToArray(),
                WebSocketMessageType.Text
            )
        );
        return ValueTask.FromResult(new WebSocketFinalizePlan(messages));
    }

    public WebSocketInboundResult HandleMessage(
        WebSocketMessageType type,
        ReadOnlyMemory<byte> completePayload
    )
    {
        if (type != WebSocketMessageType.Text)
            return WebSocketInboundResult.Empty;

        try
        {
            using var document = JsonDocument.Parse(completePayload);
            var root = document.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String
            )
            {
                return Fault("AssemblyAI sent a malformed streaming message.");
            }

            return typeElement.GetString() switch
            {
                "Turn" => HandleTurn(root),
                // Termination follows the last Turn, so an unconfirmed turn would be lost text.
                "Termination" => !_lastTurnPendingText
                    ? new WebSocketInboundResult([], WebSocketSessionSignal.Terminal)
                    : Fault("AssemblyAI ended before the final transcript was confirmed."),
                "Error" => Fault(
                    $"AssemblyAI streaming provider error: {ExtractError(root)}"
                ),
                _ => WebSocketInboundResult.Empty,
            };
        }
        catch (JsonException ex)
        {
            return new WebSocketInboundResult(
                [],
                Fault: new InvalidOperationException(
                    "AssemblyAI sent malformed JSON.",
                    ex
                )
            );
        }
    }

    private WebSocketInboundResult HandleTurn(JsonElement root)
    {
        if (
            !root.TryGetProperty("transcript", out var textElement)
            || textElement.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("turn_order", out var orderElement)
            || orderElement.ValueKind != JsonValueKind.Number
            || !orderElement.TryGetInt32(out var turnOrder)
            || turnOrder < 0
        )
        {
            return Fault("AssemblyAI sent a malformed Turn message.");
        }

        if (turnOrder < _lastTurnOrder)
            return Fault("AssemblyAI sent turns out of order.");
        if (turnOrder > _lastTurnOrder && _lastTurnPendingText)
            return Fault("AssemblyAI started a turn before confirming the previous one.");
        // A re-delivered final turn is not repeated speech.
        if (turnOrder == _lastTurnOrder && _lastTurnFinal)
            return WebSocketInboundResult.Empty;

        var isEndOfTurn =
            root.TryGetProperty("end_of_turn", out var endOfTurnElement)
            && endOfTurnElement.ValueKind
                is JsonValueKind.True
                    or JsonValueKind.False
            && endOfTurnElement.GetBoolean();
        var isFormatted =
            root.TryGetProperty("turn_is_formatted", out var formattedElement)
            && formattedElement.ValueKind
                is JsonValueKind.True
                    or JsonValueKind.False
            && formattedElement.GetBoolean();
        var isFinal = isEndOfTurn && isFormatted;
        var transcript = textElement.GetString() ?? "";
        _lastTurnOrder = turnOrder;
        _lastTurnFinal = isFinal;
        _lastTurnPendingText = !isFinal && !string.IsNullOrWhiteSpace(transcript);

        if (string.IsNullOrWhiteSpace(transcript))
            return WebSocketInboundResult.Empty;

        return new WebSocketInboundResult(
            [
                new StreamingTranscriptEvent(transcript, isFinal)
                {
                    DetectedLanguage = root.TryGetProperty("language_code", out var languageElement)
                        && languageElement.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(languageElement.GetString())
                            ? languageElement.GetString()
                            : null,
                },
            ]
        );
    }

    private static WebSocketInboundResult Fault(string message) =>
        new([], Fault: new InvalidOperationException(message));

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
}
