using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.Plugin.Gemini;

internal sealed class GeminiStreamingSession : IStreamingSession
{
    private const string LiveWebSocketUrl =
        "wss://generativelanguage.googleapis.com/ws/"
        + "google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    internal const string FinalizePayload = """{"realtimeInput":{"audioStreamEnd":true}}""";

    private static readonly string[] s_responseModalities = ["TEXT"];

    private readonly WebSocketSessionPump _pump;

    private GeminiStreamingSession(WebSocketSessionPump pump)
    {
        _pump = pump;
    }

    public event Action<StreamingTranscriptEvent>? TranscriptReceived
    {
        add => _pump.TranscriptReceived += value;
        remove => _pump.TranscriptReceived -= value;
    }

    public static async Task<GeminiStreamingSession> ConnectAsync(
        string apiKey,
        string liveModelId,
        IReadOnlyList<string> languageHints,
        IReadOnlyList<string> customVocabulary,
        GeminiTranscriptionMode mode,
        CancellationToken ct)
    {
        var pump = await WebSocketSessionPump.ConnectAsync(
            new GeminiWebSocketAdapter(apiKey, liveModelId, languageHints, customVocabulary, mode),
            ct);
        return new GeminiStreamingSession(pump);
    }

    internal static async Task<GeminiStreamingSession> CreateConnectedSessionForTests(WebSocket ws)
    {
        var pump = await WebSocketSessionPump.StartConnectedAsync(
            new GeminiWebSocketAdapter(
                "",
                GeminiPlugin.DefaultLiveTranscriptionModel,
                [],
                [],
                GeminiTranscriptionMode.Smart),
            new ClientWebSocketTransport(ws),
            CancellationToken.None);
        return new GeminiStreamingSession(pump);
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct) =>
        _pump.SendAudioAsync(pcm16Audio, ct);

    public Task FinalizeAsync(CancellationToken ct) => _pump.FinalizeAsync(ct);

    public ValueTask DisposeAsync() => _pump.DisposeAsync();

    internal static Uri BuildWebSocketUri(string apiKey) =>
        new($"{LiveWebSocketUrl}?key={Uri.EscapeDataString(apiKey)}");

    internal static string CreateSetupPayload(
        string liveModelId,
        IReadOnlyList<string> languageHints,
        IReadOnlyList<string> customVocabulary,
        GeminiTranscriptionMode mode)
    {
        // The Live API takes camelCase keys and upper-case mode names, unlike the REST payload.
        var transcription = new Dictionary<string, object?>
        {
            ["languageCodes"] = languageHints,
            ["mode"] = mode == GeminiTranscriptionMode.Smart ? "SMART" : "VERBATIM",
        };
        if (customVocabulary.Count > 0)
            transcription["customVocabulary"] = customVocabulary;

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["setup"] = new Dictionary<string, object?>
            {
                ["model"] = $"models/{NormalizeModelId(liveModelId)}",
                ["generationConfig"] = new Dictionary<string, object?>
                {
                    ["responseModalities"] = s_responseModalities,
                },
                ["inputAudioTranscription"] = transcription,
            },
        });
    }

    internal static string CreateAudioPayload(ReadOnlySpan<byte> pcm16Audio) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["realtimeInput"] = new Dictionary<string, object?>
            {
                ["audio"] = new Dictionary<string, object?>
                {
                    ["data"] = Convert.ToBase64String(pcm16Audio),
                    ["mimeType"] = "audio/pcm;rate=16000",
                },
            },
        });

    private static string NormalizeModelId(string modelId) =>
        modelId.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? modelId["models/".Length..]
            : modelId;
}

internal sealed class GeminiWebSocketAdapter(
    string apiKey,
    string liveModelId,
    IReadOnlyList<string> languageHints,
    IReadOnlyList<string> customVocabulary,
    GeminiTranscriptionMode mode
) : IWebSocketSessionAdapter
{
    public string ProviderName => "Gemini";
    public WebSocketReadinessPolicy Readiness =>
        WebSocketReadinessPolicy.Require("setupComplete");

    // Google documents no server signal after audioStreamEnd, so a session that ends cleanly
    // must not fault; the coordinator's grace window still collects finals sent after finalize.
    public WebSocketTerminalPolicy Terminal => WebSocketTerminalPolicy.None;
    public WebSocketKeepAlivePolicy? KeepAlive => null;
    public WebSocketClosePolicy ClosePolicy => WebSocketClosePolicy.Default;

    public ValueTask<WebSocketConnectionOptions> GetConnectionOptionsAsync(CancellationToken ct) =>
        ValueTask.FromResult(
            new WebSocketConnectionOptions(GeminiStreamingSession.BuildWebSocketUri(apiKey)));

    public ValueTask<IReadOnlyList<WebSocketOutboundMessage>> OnConnectedAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(
            [
                new WebSocketOutboundMessage(
                    Encoding.UTF8.GetBytes(
                        GeminiStreamingSession.CreateSetupPayload(
                            liveModelId,
                            languageHints,
                            customVocabulary,
                            mode)),
                    WebSocketMessageType.Text),
            ]);

    public ValueTask<IReadOnlyList<WebSocketOutboundMessage>> EncodeAudioAsync(
        ReadOnlyMemory<byte> pcm16Audio,
        CancellationToken ct
    ) =>
        ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(
            pcm16Audio.Length == 0
                ? []
                : [
                    new WebSocketOutboundMessage(
                        Encoding.UTF8.GetBytes(
                            GeminiStreamingSession.CreateAudioPayload(pcm16Audio.Span)),
                        WebSocketMessageType.Text),
                ]);

    public ValueTask<WebSocketFinalizePlan> BeginFinalizeAsync(CancellationToken ct) =>
        ValueTask.FromResult(
            new WebSocketFinalizePlan(
                [
                    new WebSocketOutboundMessage(
                        Encoding.UTF8.GetBytes(GeminiStreamingSession.FinalizePayload),
                        WebSocketMessageType.Text),
                ]));

    public WebSocketInboundResult HandleMessage(
        WebSocketMessageType type,
        ReadOnlyMemory<byte> completePayload
    )
    {
        // The Live API sends its JSON events as text or binary frames interchangeably.
        if (type is not (WebSocketMessageType.Text or WebSocketMessageType.Binary))
            return WebSocketInboundResult.Empty;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(completePayload);
        }
        catch (JsonException ex)
        {
            return new WebSocketInboundResult(
                [],
                Fault: new InvalidOperationException("Gemini sent malformed JSON.", ex));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return WebSocketInboundResult.Empty;

            // Readiness arrives as an empty object, so only an explicit false or null is a denial.
            if (TryGetProperty(root, "setupComplete", "setup_complete", out var setupComplete)
                && setupComplete.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
            {
                return new WebSocketInboundResult([], WebSocketSessionSignal.Ready);
            }

            if (root.TryGetProperty("error", out var error)
                && error.ValueKind is JsonValueKind.Object or JsonValueKind.String)
            {
                var message = TryGetString(error, "message")
                    ?? (error.ValueKind == JsonValueKind.String ? error.GetString() : null)
                    ?? "Unknown Gemini Live error";
                return new WebSocketInboundResult(
                    [],
                    Fault: new InvalidOperationException($"Gemini streaming error: {message}"));
            }

            if (!TryGetProperty(root, "serverContent", "server_content", out var serverContent))
                return WebSocketInboundResult.Empty;

            // turnComplete/generationComplete end a model turn, not the session, so no Terminal
            // is raised: nothing awaits it (Terminal.None) and it would end the receive loop --
            // including inside the coordinator's post-finalize grace window, where a turn that
            // began before the user stopped can complete ahead of the tail transcript. The loop
            // ends at dispose instead, and the Live API's 10-minute cap closes the socket
            // normally, which is a clean end rather than a fault.

            // Each inputTranscription frame is one finalized utterance; the coordinator joins
            // them with newlines, so they are emitted as they arrive rather than aggregated.
            if (TryGetTranscriptText(serverContent, "inputTranscription", "input_transcription")
                is { } finalText)
            {
                return new WebSocketInboundResult(
                    [new StreamingTranscriptEvent(finalText, IsFinal: true)]);
            }

            return TryGetTranscriptText(
                    serverContent,
                    "interimInputTranscription",
                    "interim_input_transcription") is { } interimText
                ? new WebSocketInboundResult(
                    [new StreamingTranscriptEvent(interimText, IsFinal: false)])
                : WebSocketInboundResult.Empty;
        }
    }

    private static string? TryGetTranscriptText(
        JsonElement serverContent,
        string camelCaseName,
        string snakeCaseName) =>
        TryGetProperty(serverContent, camelCaseName, snakeCaseName, out var transcription)
        && TryGetString(transcription, "text") is { } text
        && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    // A Live frame can carry null or a scalar where an object is documented, and probing a
    // non-object throws, which the pump would report as a session fault.
    private static bool TryGetProperty(
        JsonElement element,
        string camelCaseName,
        string snakeCaseName,
        out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
            && (element.TryGetProperty(camelCaseName, out value)
                || element.TryGetProperty(snakeCaseName, out value));
    }

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
