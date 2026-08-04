using System.Net.WebSockets;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.Plugin.Deepgram;

internal sealed class DeepgramStreamingSession : IStreamingSession
{
    private readonly WebSocketSessionPump _pump;

    private DeepgramStreamingSession(WebSocketSessionPump pump)
    {
        _pump = pump;
    }

    internal static async Task<DeepgramStreamingSession> CreateConnectedSessionForTests(
        WebSocket ws
    )
    {
        var pump = await WebSocketSessionPump.StartConnectedAsync(
            new DeepgramWebSocketAdapter("", "nova-3", null),
            new ClientWebSocketTransport(ws),
            CancellationToken.None
        );
        return new DeepgramStreamingSession(pump);
    }

    public event Action<StreamingTranscriptEvent>? TranscriptReceived
    {
        add => _pump.TranscriptReceived += value;
        remove => _pump.TranscriptReceived -= value;
    }

    public static async Task<DeepgramStreamingSession> ConnectAsync(
        string apiKey,
        string model,
        string? language,
        CancellationToken ct
    )
    {
        var pump = await WebSocketSessionPump.ConnectAsync(
            new DeepgramWebSocketAdapter(apiKey, model, language),
            ct
        );
        return new DeepgramStreamingSession(pump);
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct) =>
        _pump.SendAudioAsync(pcm16Audio, ct);

    public Task FinalizeAsync(CancellationToken ct) => _pump.FinalizeAsync(ct);

    public ValueTask DisposeAsync() => _pump.DisposeAsync();
}

internal sealed class DeepgramWebSocketAdapter(
    string apiKey,
    string model,
    string? language
) : IWebSocketSessionAdapter
{
    public string ProviderName => "Deepgram";
    public WebSocketReadinessPolicy Readiness => WebSocketReadinessPolicy.Immediate;
    public WebSocketTerminalPolicy Terminal =>
        WebSocketTerminalPolicy.Require("Metadata");
    public WebSocketKeepAlivePolicy? KeepAlive => null;
    public WebSocketClosePolicy ClosePolicy => WebSocketClosePolicy.Default;

    public ValueTask<WebSocketConnectionOptions> GetConnectionOptionsAsync(
        CancellationToken ct
    )
    {
        var isUnspecified =
            string.IsNullOrEmpty(language)
            || string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase);
        var languageParameter = isUnspecified
            ? model.StartsWith("nova-3", StringComparison.OrdinalIgnoreCase)
                ? "&language=multi"
                : string.Empty
            : $"&language={Uri.EscapeDataString(language!)}";
        var uri = new Uri(
            $"wss://api.deepgram.com/v1/listen?model={Uri.EscapeDataString(model)}"
                + "&encoding=linear16&sample_rate=16000&interim_results=true"
                + $"&punctuate=true&smart_format=true{languageParameter}"
        );
        IReadOnlyDictionary<string, string> headers =
            new Dictionary<string, string>
            {
                ["Authorization"] = $"Token {apiKey}",
            };
        return ValueTask.FromResult(new WebSocketConnectionOptions(uri, headers));
    }

    public ValueTask<IReadOnlyList<WebSocketOutboundMessage>> OnConnectedAsync(
        CancellationToken ct
    ) =>
        ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>([]);

    public ValueTask<IReadOnlyList<WebSocketOutboundMessage>> EncodeAudioAsync(
        ReadOnlyMemory<byte> pcm16Audio,
        CancellationToken ct
    ) =>
        ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(
            [
                new WebSocketOutboundMessage(
                    pcm16Audio.ToArray(),
                    WebSocketMessageType.Binary
                ),
            ]
        );

    public ValueTask<WebSocketFinalizePlan> BeginFinalizeAsync(CancellationToken ct) =>
        ValueTask.FromResult(
            new WebSocketFinalizePlan(
                [
                    new WebSocketOutboundMessage(
                        """{"type":"CloseStream"}"""u8.ToArray(),
                        WebSocketMessageType.Text
                    ),
                ]
            )
        );

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
                return Fault("Deepgram sent a malformed streaming message.");
            }

            return typeElement.GetString() switch
            {
                "Results" => HandleResults(root),
                "Metadata" => new WebSocketInboundResult(
                    [],
                    WebSocketSessionSignal.Terminal
                ),
                "Error" => Fault(
                    $"Deepgram streaming provider error: {ExtractError(root)}"
                ),
                _ => WebSocketInboundResult.Empty,
            };
        }
        catch (JsonException ex)
        {
            return new WebSocketInboundResult(
                [],
                Fault: new InvalidOperationException(
                    "Deepgram sent malformed JSON.",
                    ex
                )
            );
        }
    }

    private static WebSocketInboundResult HandleResults(JsonElement root)
    {
        if (
            !root.TryGetProperty("channel", out var channel)
            || channel.ValueKind != JsonValueKind.Object
            || !channel.TryGetProperty("alternatives", out var alternatives)
            || alternatives.ValueKind != JsonValueKind.Array
            || alternatives.GetArrayLength() == 0
            || alternatives[0].ValueKind != JsonValueKind.Object
            || !alternatives[0].TryGetProperty("transcript", out var transcriptElement)
            || transcriptElement.ValueKind != JsonValueKind.String
        )
        {
            return Fault("Deepgram sent a malformed Results message.");
        }

        var transcript = transcriptElement.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(transcript))
            return WebSocketInboundResult.Empty;

        var isFinal =
            root.TryGetProperty("is_final", out var finalElement)
            && finalElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            && finalElement.GetBoolean();
        return new WebSocketInboundResult(
            [new StreamingTranscriptEvent(transcript, isFinal)]
        );
    }

    private static WebSocketInboundResult Fault(string message) =>
        new([], Fault: new InvalidOperationException(message));

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
}
