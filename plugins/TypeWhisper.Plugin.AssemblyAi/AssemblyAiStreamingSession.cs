using System.Net.WebSockets;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.Plugin.AssemblyAi;

internal sealed class AssemblyAiStreamingSession : IStreamingSession
{
    private readonly WebSocketSessionPump _pump;

    internal AssemblyAiStreamingSession(WebSocket ws)
        : this(
            WebSocketSessionPump
                .StartConnectedAsync(
                    new AssemblyAiWebSocketAdapter("", null),
                    new ClientWebSocketTransport(ws),
                    CancellationToken.None
                )
                .GetAwaiter()
                .GetResult()
        ) { }

    private AssemblyAiStreamingSession(WebSocketSessionPump pump)
    {
        _pump = pump;
    }

    public event Action<StreamingTranscriptEvent>? TranscriptReceived
    {
        add => _pump.TranscriptReceived += value;
        remove => _pump.TranscriptReceived -= value;
    }

    public static async Task<AssemblyAiStreamingSession> ConnectAsync(
        string apiKey,
        string? language,
        CancellationToken ct
    )
    {
        var pump = await WebSocketSessionPump.ConnectAsync(
            new AssemblyAiWebSocketAdapter(apiKey, language),
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
    string? language
) : IWebSocketSessionAdapter
{
    internal const int MinimumChunkBytes = 1600;

    private readonly MemoryStream _audioBuffer = new();

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
        var url =
            "wss://streaming.assemblyai.com/v3/ws?sample_rate=16000&format_turns=true";
        if (
            !string.IsNullOrEmpty(language)
            && !language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
        )
        {
            url += "&speech_model=universal-streaming-multilingual";
        }

        IReadOnlyDictionary<string, string> headers =
            new Dictionary<string, string>
            {
                ["Authorization"] = apiKey,
            };
        return ValueTask.FromResult(
            new WebSocketConnectionOptions(new Uri(url), headers)
        );
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

        var chunk = _audioBuffer.ToArray();
        _audioBuffer.SetLength(0);
        return ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(
            [new WebSocketOutboundMessage(chunk, WebSocketMessageType.Binary)]
        );
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
                "Termination" => new WebSocketInboundResult(
                    [],
                    WebSocketSessionSignal.Terminal
                ),
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

    private static WebSocketInboundResult HandleTurn(JsonElement root)
    {
        if (
            !root.TryGetProperty("transcript", out var textElement)
            || textElement.ValueKind != JsonValueKind.String
        )
        {
            return Fault("AssemblyAI sent a malformed Turn message.");
        }

        var transcript = textElement.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(transcript))
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
        return new WebSocketInboundResult(
            [
                new StreamingTranscriptEvent(
                    transcript,
                    isEndOfTurn && isFormatted
                ),
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
