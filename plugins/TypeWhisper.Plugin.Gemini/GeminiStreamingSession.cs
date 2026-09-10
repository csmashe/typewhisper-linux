using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.Plugin.Gemini;

internal sealed class GeminiStreamingSession : IStreamingSession, IStreamingSessionHealth
{
    private const string LiveWebSocketUrl =
        "wss://generativelanguage.googleapis.com/ws/"
        + "google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    internal const string FinalizePayload = """{"realtimeInput":{"audioStreamEnd":true}}""";

    private static readonly string[] s_responseModalities = ["TEXT"];

    private readonly WebSocketSessionPump _pump;
    private readonly GeminiWebSocketAdapter _adapter;
    private readonly IWebSocketTransport _transport;

    private GeminiStreamingSession(WebSocketSessionPump pump, GeminiWebSocketAdapter adapter, IWebSocketTransport transport)
    {
        _pump = pump;
        _adapter = adapter;
        _transport = transport;
    }

    public Exception? Fault => _pump.Fault;

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
        var transport = new ClientWebSocketTransport();
        var pump = await WebSocketSessionPump.ConnectAsync(
            adapter, ct, transportFactory: new SessionTransportFactory(transport));
        return new GeminiStreamingSession(pump, adapter, transport);
    }

    private sealed class SessionTransportFactory(IWebSocketTransport transport) : IWebSocketTransportFactory
    {
        public IWebSocketTransport Create() => transport;
    }

    internal static Task<GeminiStreamingSession> CreateConnectedSessionForTests(WebSocket ws) =>
        CreateConnectedSessionForTests(new ClientWebSocketTransport(ws));

    internal static async Task<GeminiStreamingSession> CreateConnectedSessionForTests(IWebSocketTransport transport)
    {
        var adapter = new GeminiWebSocketAdapter(
            "", GeminiPlugin.DefaultLiveTranscriptionModel, [], [], GeminiTranscriptionMode.Smart);
        var pump = await WebSocketSessionPump.StartConnectedAsync(adapter, transport, CancellationToken.None);
        return new GeminiStreamingSession(pump, adapter, transport);
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct) =>
        _pump.SendAudioAsync(pcm16Audio, ct);

    public async Task FinalizeAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfPumpFaulted();
        var completedUtterances = _adapter.CompletedPendingUtterances;
        await _pump.FinalizeAsync(ct);

        // Always collect after EOF: the first interim may still be in flight.
        var deadline = Environment.TickCount64 + 1500;
        while (_transport.State == WebSocketState.Open)
        {
            ct.ThrowIfCancellationRequested();
            ThrowIfPumpFaulted();
            if ((_adapter.CompletedPendingUtterances != completedUtterances && !_adapter.HasPendingUtterance)
                || Environment.TickCount64 >= deadline)
                break;
            await Task.Delay(25, ct);
        }

        ct.ThrowIfCancellationRequested();
        ThrowIfPumpFaulted();
        if (_adapter.HasPendingUtterance)
        {
            if (_transport.State != WebSocketState.Open)
                throw new PluginRequestException(
                    "Gemini streaming connection closed before the tail transcript finalized.",
                    PluginRequestFailureKind.ServerError);
            throw new TimeoutException("Gemini streaming tail transcript did not finalize in time.");
        }
    }

    private void ThrowIfPumpFaulted()
    {
        if (Fault is { } fault)
            ExceptionDispatchInfo.Capture(fault).Throw();
    }

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
    private int _hasPendingUtterance;
    private int _completedPendingUtterances;
    internal int CompletedPendingUtterances => Volatile.Read(ref _completedPendingUtterances);
    internal bool HasPendingUtterance => Volatile.Read(ref _hasPendingUtterance) != 0;

    public string ProviderName => "Gemini";
    public WebSocketReadinessPolicy Readiness =>
        WebSocketReadinessPolicy.Require("setupComplete");

    // Google documents no server signal after audioStreamEnd, so a session that ends cleanly
    // must not fault solely for lacking a terminal signal. The session collects pending tails.
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
            // normally; the session rejects closure if a known utterance remains pending.

            // Each inputTranscription frame is one finalized utterance; the coordinator joins
            // them with newlines, so they are emitted as they arrive rather than aggregated.
            if (TryGetTranscriptText(serverContent, "inputTranscription", "input_transcription")
                is { } finalText)
            {
                if (Interlocked.Exchange(ref _hasPendingUtterance, 0) != 0)
                    Interlocked.Increment(ref _completedPendingUtterances);
                return new WebSocketInboundResult(
                    [new StreamingTranscriptEvent(finalText, IsFinal: true)]);
            }

            if (TryGetTranscriptText(serverContent, "interimInputTranscription", "interim_input_transcription")
                is not { } interimText)
            {
                return WebSocketInboundResult.Empty;
            }

            Volatile.Write(ref _hasPendingUtterance, 1);
            return new WebSocketInboundResult(
                [new StreamingTranscriptEvent(interimText, IsFinal: false)]);
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
