using System.Net.WebSockets;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.Plugin.SmallestAi;

internal sealed class SmallestAiStreamingSession : IStreamingSession
{
    private readonly WebSocketSessionPump _pump;

    private SmallestAiStreamingSession(WebSocketSessionPump pump)
    {
        _pump = pump;
    }

    public event Action<StreamingTranscriptEvent>? TranscriptReceived
    {
        add => _pump.TranscriptReceived += value;
        remove => _pump.TranscriptReceived -= value;
    }

    public static async Task<SmallestAiStreamingSession> ConnectAsync(
        string apiKey,
        string? language,
        CancellationToken ct
    )
    {
        var pump = await WebSocketSessionPump.ConnectAsync(
            new SmallestAiWebSocketAdapter(apiKey, language),
            ct
        );
        return new SmallestAiStreamingSession(pump);
    }

    internal static SmallestAiStreamingSession CreateConnectedSessionForTests(
        WebSocket ws
    )
    {
        if (ws.State != WebSocketState.Open)
            throw new InvalidOperationException("The test WebSocket must already be open.");

        var pump = WebSocketSessionPump
            .StartConnectedAsync(
                new SmallestAiWebSocketAdapter("", null),
                new ClientWebSocketTransport(ws),
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult();
        return new SmallestAiStreamingSession(pump);
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

        return new Uri(
            "wss://api.smallest.ai/waves/v1/pulse/get_text?"
                + string.Join("&", query)
        );
    }

    public static IReadOnlyDictionary<string, string> CreateStreamingHeaders(
        string apiKey
    ) =>
        new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {apiKey}",
        };

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct) =>
        _pump.SendAudioAsync(pcm16Audio, ct);

    public Task FinalizeAsync(CancellationToken ct) => _pump.FinalizeAsync(ct);

    public ValueTask DisposeAsync() => _pump.DisposeAsync();
}

internal sealed class SmallestAiWebSocketAdapter(
    string apiKey,
    string? language
) : IWebSocketSessionAdapter
{
    private readonly SmallestAiTranscriptCollector _collector = new();

    public string ProviderName => "Smallest AI Pulse";
    public WebSocketReadinessPolicy Readiness => WebSocketReadinessPolicy.Immediate;
    public WebSocketTerminalPolicy Terminal =>
        WebSocketTerminalPolicy.Require("is_last");
    public WebSocketKeepAlivePolicy? KeepAlive => null;
    public WebSocketClosePolicy ClosePolicy => WebSocketClosePolicy.Default;

    public ValueTask<WebSocketConnectionOptions> GetConnectionOptionsAsync(
        CancellationToken ct
    ) =>
        ValueTask.FromResult(
            new WebSocketConnectionOptions(
                SmallestAiStreamingSession.BuildStreamingUri(
                    language,
                    wordTimestamps: true
                ),
                SmallestAiStreamingSession.CreateStreamingHeaders(apiKey)
            )
        );

    public ValueTask<IReadOnlyList<WebSocketOutboundMessage>> OnConnectedAsync(
        CancellationToken ct
    ) =>
        ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>([]);

    public ValueTask<IReadOnlyList<WebSocketOutboundMessage>> EncodeAudioAsync(
        ReadOnlyMemory<byte> pcm16Audio,
        CancellationToken ct
    ) =>
        ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(
            pcm16Audio.Length == 0
                ? []
                : [
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
                        """{"type":"close_stream"}"""u8.ToArray(),
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
            var transcript = _collector.ApplyEvent(
                System.Text.Encoding.UTF8.GetString(completePayload.Span)
            );
            var transcripts =
                transcript is null
                    ? (IReadOnlyList<StreamingTranscriptEvent>)[]
                    : [transcript];
            var signals =
                _collector.IsLastReceived
                    ? WebSocketSessionSignal.Terminal
                    : WebSocketSessionSignal.None;
            return new WebSocketInboundResult(transcripts, signals);
        }
        catch (JsonException ex)
        {
            return new WebSocketInboundResult(
                [],
                Fault: new InvalidOperationException(
                    "Smallest AI Pulse sent malformed JSON.",
                    ex
                )
            );
        }
        catch (InvalidOperationException ex)
        {
            return new WebSocketInboundResult([], Fault: ex);
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
        if (
            !string.IsNullOrWhiteSpace(type)
            && !type.Equals("transcription", StringComparison.OrdinalIgnoreCase)
        )
        {
            return null;
        }

        var isFinal = GetBool(root, "is_final");
        var isLast = GetBool(root, "is_last");
        IsLastReceived = IsLastReceived || isLast;

        if (
            (isFinal || isLast)
            && (
                GetString(root, "language")
                ?? GetFirstString(root, "languages")
            )
                is { } detectedLanguage
            && !string.IsNullOrWhiteSpace(detectedLanguage)
        )
        {
            DetectedLanguage = detectedLanguage;
        }

        var transcript = GetString(root, "transcript")?.Trim() ?? "";
        return string.IsNullOrWhiteSpace(transcript)
            ? null
            : new StreamingTranscriptEvent(transcript, isFinal || isLast);
    }

    private static bool IsError(JsonElement root)
    {
        if (
            GetString(root, "type") is { } type
            && type.Equals("error", StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        if (
            GetString(root, "status") is { } status
            && status.Equals("error", StringComparison.OrdinalIgnoreCase)
        )
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

    private static string? GetFirstString(
        JsonElement element,
        string propertyName
    )
    {
        if (
            !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array
        )
        {
            return null;
        }

        return property
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
