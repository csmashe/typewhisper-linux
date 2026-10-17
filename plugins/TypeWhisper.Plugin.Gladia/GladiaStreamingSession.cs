using TypeWhisper.PluginSDK.Helpers;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.Plugin.Gladia;

internal sealed class GladiaStreamingSession : IStreamingSession, IStreamingSessionHealth
{
    internal const string InitUrl = "https://api.gladia.io/v2/live";

    private readonly WebSocketSessionPump _pump;

    private GladiaStreamingSession(WebSocketSessionPump pump)
    {
        _pump = pump;
    }

    public Exception? Fault => _pump.Fault;

    public event Action<StreamingTranscriptEvent>? TranscriptReceived
    {
        add => _pump.TranscriptReceived += value;
        remove => _pump.TranscriptReceived -= value;
    }

    public static async Task<GladiaStreamingSession> ConnectAsync(
        HttpClient httpClient,
        string apiKey,
        string? language,
        IReadOnlyList<string> vocabulary,
        CancellationToken ct
    )
    {
        var pump = await WebSocketSessionPump.ConnectAsync(
            new GladiaWebSocketAdapter(httpClient, apiKey, language, vocabulary),
            ct
        );
        return new GladiaStreamingSession(pump);
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct) =>
        _pump.SendAudioAsync(pcm16Audio, ct);

    public Task FinalizeAsync(CancellationToken ct) => _pump.FinalizeAsync(ct);

    public ValueTask DisposeAsync() => _pump.DisposeAsync();

    internal static string BuildInitRequest(
        string? language,
        IReadOnlyList<string> vocabulary,
        int sampleRate
    )
    {
        var body = new Dictionary<string, object>
        {
            ["encoding"] = "wav/pcm",
            ["bit_depth"] = 16,
            ["sample_rate"] = sampleRate,
            ["channels"] = 1,
            ["messages_config"] = new Dictionary<string, object>
            {
                ["receive_partial_transcripts"] = true,
                ["receive_final_transcripts"] = true,
                // end_session is a lifecycle event, which Gladia sends only when asked.
                ["receive_lifecycle_events"] = true,
                ["receive_acknowledgments"] = true,
                ["receive_errors"] = true,
            },
        };

        if (!string.IsNullOrWhiteSpace(language))
        {
            body["language_config"] = new Dictionary<string, object>
            {
                ["languages"] = new[] { language },
            };
        }

        var realtimeProcessing = new Dictionary<string, object>();
        GladiaPlugin.AddCustomVocabulary(realtimeProcessing, vocabulary);
        if (realtimeProcessing.Count > 0)
            body["realtime_processing"] = realtimeProcessing;

        return JsonSerializer.Serialize(body);
    }

    internal static string? ParseSessionUrl(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(
                    "url",
                    out var urlElement
                )
                && urlElement.ValueKind == JsonValueKind.String
                    ? urlElement.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal sealed record GladiaError(int? StatusCode, string? Exception, string? Message);

    internal sealed record GladiaMessage(
        string MessageType,
        string? UtteranceId = null,
        string? Text = null,
        bool IsFinal = false,
        string? Language = null,
        GladiaError? Error = null,
        double? Start = null
    );

    internal static GladiaMessage ParseMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("Gladia message is not an object.");

        var type = GetString(root, "type") ?? "";

        // Acknowledgments and events report failure through "acknowledged" and "error".
        var rejected = root.TryGetProperty("acknowledged", out var acknowledged)
            && acknowledged.ValueKind == JsonValueKind.False;
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            return new GladiaMessage(type, Error: ParseError(error));
        if (rejected || type == "error")
            return new GladiaMessage(type, Error: ParseError(root));

        if (type != "transcript")
            return new GladiaMessage(type);

        if (
            !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || GetString(data, "id") is not { Length: > 0 } id
            || !data.TryGetProperty("is_final", out var finalElement)
            || finalElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !data.TryGetProperty("utterance", out var utterance)
            || utterance.ValueKind != JsonValueKind.Object
            || GetString(utterance, "text") is not { } text
        )
        {
            throw new FormatException("Gladia sent a malformed transcript.");
        }

        return new GladiaMessage(
            type,
            id,
            text,
            finalElement.ValueKind == JsonValueKind.True,
            GetString(utterance, "language"),
            Start: utterance.TryGetProperty("start", out var start)
                && start.ValueKind == JsonValueKind.Number
                && start.TryGetDouble(out var seconds)
                    ? seconds
                    : null
        );
    }

    private static GladiaError ParseError(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
            return new GladiaError(null, null, error.GetString());
        if (error.ValueKind != JsonValueKind.Object)
            return new GladiaError(null, null, null);

        var status = error.TryGetProperty("status_code", out var code) ? ParseStatusCode(code) : null;
        return new GladiaError(status, GetString(error, "exception"), GetString(error, "message"));
    }

    // status_code may be a number or a string.
    private static int? ParseStatusCode(JsonElement code) =>
        code.ValueKind switch
        {
            JsonValueKind.Number when code.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(
                code.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed
            ) => parsed,
            _ => null,
        };

    // Built from the documented fields only: the raw payload is never echoed.
    internal static PluginRequestException CreateRequestException(GladiaError error)
    {
        var kind = error.StatusCode switch
        {
            401 => PluginRequestFailureKind.Authentication,
            402 or 403 => PluginRequestFailureKind.Permission,
            408 => PluginRequestFailureKind.Timeout,
            413 => PluginRequestFailureKind.RequestTooLarge,
            429 => PluginRequestFailureKind.RateLimit,
            >= 500 => PluginRequestFailureKind.ServerError,
            null => PluginRequestFailureKind.Unknown,
            _ => PluginRequestFailureKind.InvalidRequest,
        };

        var description = new StringBuilder("Gladia streaming error: ");
        if (!string.IsNullOrWhiteSpace(error.Exception))
            description.Append(error.Exception).Append(": ");
        description.Append(
            !string.IsNullOrWhiteSpace(error.Message) ? error.Message
            : error.StatusCode is { } status ? $"HTTP {status}"
            : "request rejected"
        );

        return new PluginRequestException(description.ToString(), kind, error.StatusCode);
    }

    internal static PluginRequestException CreateIncompleteException(
        string message = "Gladia returned an invalid or incomplete live response.",
        Exception? inner = null
    ) =>
        new(message, PluginRequestFailureKind.OutputIncomplete, innerException: inner);

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

internal sealed class GladiaWebSocketAdapter(
    HttpClient httpClient,
    string apiKey,
    string? language,
    IReadOnlyList<string> vocabulary
) : IWebSocketSessionAdapter
{
    // Bounds the per-session bookkeeping; a dictation never comes near either.
    private const int MaxFinalUtterances = 10_000;
    private const int MaxPendingUtterances = 100;

    // HandleMessage runs only on the receive loop, so these need no lock.
    private readonly Dictionary<string, string> _finals = new(StringComparer.Ordinal);
    // Unconfirmed partials by utterance id.
    private readonly Dictionary<string, (string Text, double? Start)> _pending = new(StringComparer.Ordinal);

    // Set on the send side, read by HandleMessage on the receive loop.
    private volatile bool _stopRecordingSent;

    public string ProviderName => "Gladia";
    public WebSocketReadinessPolicy Readiness => WebSocketReadinessPolicy.Immediate;
    public WebSocketTerminalPolicy Terminal =>
        WebSocketTerminalPolicy.Require("end_session");
    public WebSocketKeepAlivePolicy? KeepAlive => null;
    public WebSocketClosePolicy ClosePolicy => WebSocketClosePolicy.Default;

    public async ValueTask<WebSocketConnectionOptions> GetConnectionOptionsAsync(
        CancellationToken ct
    )
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            GladiaStreamingSession.InitUrl
        );
        request.Headers.Add("x-gladia-key", apiKey);
        request.Content = new StringContent(
            GladiaStreamingSession.BuildInitRequest(language, vocabulary, 16000),
            Encoding.UTF8,
            "application/json"
        );

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(
            httpClient, request, HttpCompletionOption.ResponseContentRead, ct,
            (errorResponse, errorBody) => $"Gladia live init failed {(int)errorResponse.StatusCode}: {errorBody}");
        var json = await response.Content.ReadAsStringAsync(ct);

        var url =
            GladiaStreamingSession.ParseSessionUrl(json)
            ?? throw new InvalidOperationException(
                "Gladia live init response missing 'url'."
            );
        return new WebSocketConnectionOptions(new Uri(url));
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
            pcm16Audio.Length == 0
                ? []
                : [
                    new WebSocketOutboundMessage(
                        pcm16Audio.ToArray(),
                        WebSocketMessageType.Binary
                    ),
                ]
        );

    public ValueTask<WebSocketFinalizePlan> BeginFinalizeAsync(CancellationToken ct)
    {
        _stopRecordingSent = true;
        return ValueTask.FromResult(
            new WebSocketFinalizePlan(
                [
                    new WebSocketOutboundMessage(
                        """{"type":"stop_recording"}"""u8.ToArray(),
                        WebSocketMessageType.Text
                    ),
                ]
            )
        );
    }

    public WebSocketInboundResult HandleMessage(
        WebSocketMessageType type,
        ReadOnlyMemory<byte> completePayload
    )
    {
        if (type != WebSocketMessageType.Text)
            return WebSocketInboundResult.Empty;

        GladiaStreamingSession.GladiaMessage message;
        try
        {
            message = GladiaStreamingSession.ParseMessage(
                Encoding.UTF8.GetString(completePayload.Span)
            );
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return Fault(GladiaStreamingSession.CreateIncompleteException(inner: ex));
        }

        if (message.Error is not null)
            return Fault(GladiaStreamingSession.CreateRequestException(message.Error));

        if (message.MessageType == "end_session")
            return EndSession();

        return message.MessageType == "transcript"
            ? HandleTranscript(message)
            : WebSocketInboundResult.Empty;
    }

    private WebSocketInboundResult EndSession()
    {
        // Gladia also ends a session on its own (duration limit, inactivity); audio sent
        // after that point was never transcribed.
        if (!_stopRecordingSent)
        {
            return Fault(GladiaStreamingSession.CreateIncompleteException(
                "Gladia ended the live session before recording stopped."
            ));
        }

        // An utterance still unconfirmed at end_session is a dropped tail.
        if (_pending.Values.Any(entry => !string.IsNullOrWhiteSpace(entry.Text)))
        {
            return Fault(GladiaStreamingSession.CreateIncompleteException(
                "Gladia ended the live session before confirming every utterance."
            ));
        }

        return new WebSocketInboundResult([], WebSocketSessionSignal.Terminal);
    }

    private WebSocketInboundResult HandleTranscript(GladiaStreamingSession.GladiaMessage message)
    {
        var id = message.UtteranceId!;
        var text = message.Text!;

        if (_finals.TryGetValue(id, out var confirmed))
        {
            // A repeated final must not insert it twice, nor a late partial reopen it.
            return message.IsFinal && !string.Equals(confirmed, text, StringComparison.Ordinal)
                ? Fault(GladiaStreamingSession.CreateIncompleteException(
                    "Gladia sent conflicting final transcripts for one utterance."
                ))
                : WebSocketInboundResult.Empty;
        }

        if (message.IsFinal)
        {
            if (_finals.Count >= MaxFinalUtterances)
                return Fault(GladiaStreamingSession.CreateIncompleteException());

            _finals.Add(id, text);
            _pending.Remove(id);
            if (message.Start is { } start)
                DropPendingStartedBefore(start);
            return string.IsNullOrWhiteSpace(text)
                ? WebSocketInboundResult.Empty
                : new WebSocketInboundResult(
                    [new StreamingTranscriptEvent(text, true) { DetectedLanguage = message.Language }]
                );
        }

        _pending[id] = (text, message.Start);
        if (_pending.Count > MaxPendingUtterances)
            return Fault(GladiaStreamingSession.CreateIncompleteException());

        return string.IsNullOrWhiteSpace(text)
            ? WebSocketInboundResult.Empty
            : new WebSocketInboundResult([new StreamingTranscriptEvent(text, false)]);
    }

    // Gladia may discard a partial without confirming it. One starting before a confirmed
    // utterance was superseded; a later or undated one may still be a dropped tail.
    private void DropPendingStartedBefore(double start)
    {
        foreach (var (pendingId, entry) in _pending.ToArray())
        {
            if (entry.Start < start)
                _pending.Remove(pendingId);
        }
    }

    private static WebSocketInboundResult Fault(Exception exception) =>
        new([], Fault: exception);
}
