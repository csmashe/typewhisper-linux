using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.Plugin.Gemini;

internal sealed class GeminiStreamingSession : IStreamingSession, IStreamingSessionHealth, IStreamingSessionFinalizeTimeout
{
    private const string LiveWebSocketUrl =
        "wss://generativelanguage.googleapis.com/ws/"
        + "google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    internal const string ActivityStartPayload = """{"realtimeInput":{"activityStart":{}}}""";
    internal const string ActivityEndPayload = """{"realtimeInput":{"activityEnd":{}}}""";

    private static readonly string[] s_responseModalities = ["TEXT"];

    private readonly WebSocketSessionPump _pump;

    private GeminiStreamingSession(WebSocketSessionPump pump)
    {
        _pump = pump;
    }

    public Exception? Fault => _pump.Fault;

    // The whole recording is one activity, so its only final transcript follows activityEnd.
    public TimeSpan FinalizeTimeout => TimeSpan.FromSeconds(15);

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
        var adapter = new GeminiWebSocketAdapter(apiKey, liveModelId, languageHints, customVocabulary, mode);
        return new GeminiStreamingSession(await WebSocketSessionPump.ConnectAsync(adapter, ct));
    }

    internal static Task<GeminiStreamingSession> CreateConnectedSessionForTests(WebSocket ws) =>
        CreateConnectedSessionForTests(new ClientWebSocketTransport(ws));

    internal static async Task<GeminiStreamingSession> CreateConnectedSessionForTests(IWebSocketTransport transport)
    {
        var adapter = new GeminiWebSocketAdapter(
            "", GeminiPlugin.DefaultLiveTranscriptionModel, [], [], GeminiTranscriptionMode.Smart);
        return new GeminiStreamingSession(
            await WebSocketSessionPump.StartConnectedAsync(adapter, transport, CancellationToken.None));
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
                // One manually marked activity per recording: nothing is dropped as silence.
                ["realtimeInputConfig"] = new Dictionary<string, object?>
                {
                    ["automaticActivityDetection"] = new Dictionary<string, object?>
                    {
                        ["disabled"] = true,
                    },
                },
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
    private const decimal Pcm16BytesPerSecond = 16000 * 2;

    // An acknowledged end may be rounded; anything earlier than this means audio is missing.
    private const decimal AudioOffsetToleranceSeconds = 0.01m;

    // Send side, serialized by the pump's send gate.
    private bool _activityStarted;

    // Shared with the receive loop.
    private long _sentAudioBytes;
    private int _activityEnded;

    // Receive loop only.
    private bool _finalReceived;
    private bool _activityEndAcknowledged;

    public string ProviderName => "Gemini";
    public WebSocketReadinessPolicy Readiness =>
        WebSocketReadinessPolicy.Require("setupComplete");

    // Not in the API reference; upstream's live checks show one final after activityEnd and
    // ACTIVITY_END at the full sent-audio offset, even across silence. Anything else falls back to batch.
    public WebSocketTerminalPolicy Terminal =>
        WebSocketTerminalPolicy.Require("the final transcript and activity end");

    public WebSocketKeepAlivePolicy? KeepAlive => null;
    public WebSocketClosePolicy ClosePolicy => WebSocketClosePolicy.Default;

    public ValueTask<WebSocketConnectionOptions> GetConnectionOptionsAsync(CancellationToken ct) =>
        ValueTask.FromResult(
            new WebSocketConnectionOptions(GeminiStreamingSession.BuildWebSocketUri(apiKey)));

    public ValueTask<IReadOnlyList<WebSocketOutboundMessage>> OnConnectedAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(
            [
                Text(GeminiStreamingSession.CreateSetupPayload(
                    liveModelId,
                    languageHints,
                    customVocabulary,
                    mode)),
            ]);

    public ValueTask<IReadOnlyList<WebSocketOutboundMessage>> EncodeAudioAsync(
        ReadOnlyMemory<byte> pcm16Audio,
        CancellationToken ct
    )
    {
        if (pcm16Audio.Length == 0)
            return ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>([]);

        var audio = Text(GeminiStreamingSession.CreateAudioPayload(pcm16Audio.Span));
        Interlocked.Add(ref _sentAudioBytes, pcm16Audio.Length);
        if (_activityStarted)
            return ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>([audio]);

        // activityStart is only accepted after setupComplete, which the pump awaits before audio.
        _activityStarted = true;
        return ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(
            [Text(GeminiStreamingSession.ActivityStartPayload), audio]);
    }

    public ValueTask<WebSocketFinalizePlan> BeginFinalizeAsync(CancellationToken ct)
    {
        // Without audio there is nothing to transcribe or acknowledge.
        if (!_activityStarted)
            return ValueTask.FromResult(new WebSocketFinalizePlan([], AlreadyTerminal: true));

        Volatile.Write(ref _activityEnded, 1);
        return ValueTask.FromResult(
            new WebSocketFinalizePlan([Text(GeminiStreamingSession.ActivityEndPayload)]));
    }

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

            // voiceActivity sits beside the message union, so it can share a frame with serverContent.
            if (TryGetProperty(root, "voiceActivity", "voice_activity", out var activity)
                && TryGetString(activity, "type") == "ACTIVITY_END"
                && AcknowledgeActivityEnd(activity) is { } activityFault)
            {
                return new WebSocketInboundResult([], Fault: activityFault);
            }

            var transcripts = new List<StreamingTranscriptEvent>(1);
            if (TryGetProperty(root, "serverContent", "server_content", out var serverContent))
            {
                // turnComplete/generationComplete end a model turn, not the transcription.
                if (TryGetProperty(serverContent, "inputTranscription", "input_transcription", out var final)
                    && final.ValueKind == JsonValueKind.Object)
                {
                    // One activity has one final; an early or second one means the server split the recording.
                    if (Volatile.Read(ref _activityEnded) == 0 || _finalReceived)
                    {
                        return new WebSocketInboundResult(
                            [],
                            Fault: new InvalidOperationException(
                                "Gemini finalized an unexpected transcription segment."));
                    }

                    if (TryGetString(final, "text") is not { } finalText)
                    {
                        return new WebSocketInboundResult(
                            [],
                            Fault: new InvalidOperationException(
                                "Gemini sent a final transcription without text."));
                    }

                    _finalReceived = true;
                    if (!string.IsNullOrWhiteSpace(finalText))
                        transcripts.Add(new StreamingTranscriptEvent(finalText.Trim(), IsFinal: true));
                }
                else if (!_finalReceived
                         && TryGetTranscriptText(serverContent, "interimInputTranscription", "interim_input_transcription")
                             is { } interimText)
                {
                    transcripts.Add(new StreamingTranscriptEvent(interimText, IsFinal: false));
                }
            }

            // The final and the acknowledgement can arrive in either order.
            if (_finalReceived && _activityEndAcknowledged)
                return new WebSocketInboundResult(transcripts, WebSocketSessionSignal.Terminal);

            return transcripts.Count == 0
                ? WebSocketInboundResult.Empty
                : new WebSocketInboundResult(transcripts);
        }
    }

    private InvalidOperationException? AcknowledgeActivityEnd(JsonElement activity)
    {
        if (Volatile.Read(ref _activityEnded) == 0 || _activityEndAcknowledged)
            return new InvalidOperationException("Gemini acknowledged an unexpected audio boundary.");

        var sentSeconds = Interlocked.Read(ref _sentAudioBytes) / Pcm16BytesPerSecond;
        if (TryParseDurationSeconds(
                TryGetString(activity, "audioOffset") ?? TryGetString(activity, "audio_offset"))
            is not { } offset
            || offset + AudioOffsetToleranceSeconds < sentSeconds)
        {
            return new InvalidOperationException(
                "Gemini acknowledged less audio than was sent.");
        }

        _activityEndAcknowledged = true;
        return null;
    }

    // Protobuf Duration JSON: decimal seconds with an "s" suffix, e.g. "3.5s".
    internal static decimal? TryParseDurationSeconds(string? value) =>
        value is { Length: > 1 } && value[^1] == 's'
        && decimal.TryParse(
            value.AsSpan(0, value.Length - 1),
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out var seconds)
            ? seconds
            : null;

    private static WebSocketOutboundMessage Text(string json) =>
        new(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text);

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
