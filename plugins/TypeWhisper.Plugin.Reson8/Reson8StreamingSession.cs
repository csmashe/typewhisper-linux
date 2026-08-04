using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.Plugin.Reson8;

internal sealed class Reson8StreamingSession : IStreamingSession
{
    private readonly WebSocketSessionPump _pump;

    private Reson8StreamingSession(WebSocketSessionPump pump)
    {
        _pump = pump;
    }

    public event Action<StreamingTranscriptEvent>? TranscriptReceived
    {
        add => _pump.TranscriptReceived += value;
        remove => _pump.TranscriptReceived -= value;
    }

    public static async Task<Reson8StreamingSession> ConnectAsync(
        string apiKey,
        string baseUrl,
        string authHeader,
        string? modelId,
        string? language,
        CancellationToken ct
    )
    {
        var pump = await WebSocketSessionPump.ConnectAsync(
            new Reson8WebSocketAdapter(
                apiKey,
                baseUrl,
                authHeader,
                modelId,
                language
            ),
            ct
        );
        return new Reson8StreamingSession(pump);
    }

    internal static async Task<Reson8StreamingSession> CreateConnectedSessionForTests(
        WebSocket ws
    )
    {
        if (ws.State != WebSocketState.Open)
            throw new InvalidOperationException("The test WebSocket must already be open.");

        var pump = await WebSocketSessionPump.StartConnectedAsync(
            new Reson8WebSocketAdapter(
                "",
                "https://api.reson8.dev",
                Reson8Plugin.DefaultAuthHeader,
                null,
                null
            ),
            new ClientWebSocketTransport(ws),
            CancellationToken.None
        );
        return new Reson8StreamingSession(pump);
    }

    public static Uri BuildRealtimeUri(
        string baseUrl,
        string? modelId,
        string? language
    )
    {
        var normalizedBase = baseUrl.Trim().TrimEnd('/');
        var baseUri = new Uri(normalizedBase);
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var builder = new UriBuilder(baseUri)
        {
            // Plaintext stays plaintext so a self-hosted http:// or ws:// base URL reaches the
            // server the user configured; everything else (https, wss, anything unrecognized)
            // takes the secure default.
            Scheme =
                baseUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || baseUri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase)
                    ? "ws"
                    : "wss",
            Path = $"{basePath}/v1/speech-to-text/realtime",
        };

        var query = new List<string>
        {
            "encoding=pcm_s16le",
            "sample_rate=16000",
            "channels=1",
            "include_interim=true",
        };

        if (
            !string.IsNullOrWhiteSpace(language)
            && !language.Equals("auto", StringComparison.OrdinalIgnoreCase)
        )
        {
            query.Add($"language={Uri.EscapeDataString(language.Trim())}");
        }

        if (
            !string.IsNullOrWhiteSpace(modelId)
            && !string.Equals(
                modelId,
                Reson8Plugin.DefaultModelId,
                StringComparison.Ordinal
            )
        )
        {
            query.Add($"custom_model_id={Uri.EscapeDataString(modelId.Trim())}");
        }

        builder.Query = string.Join("&", query);
        return builder.Uri;
    }

    public static IReadOnlyDictionary<string, string> CreateStreamingHeaders(
        string apiKey,
        string authHeader
    ) =>
        new Dictionary<string, string>
        {
            [
                string.IsNullOrWhiteSpace(authHeader)
                    ? Reson8Plugin.DefaultAuthHeader
                    : authHeader.Trim()
            ] = Reson8Plugin.AuthHeaderValue(apiKey, authHeader),
        };

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct) =>
        _pump.SendAudioAsync(pcm16Audio, ct);

    public Task FinalizeAsync(CancellationToken ct) => _pump.FinalizeAsync(ct);

    public ValueTask DisposeAsync() => _pump.DisposeAsync();
}

internal sealed class Reson8WebSocketAdapter(
    string apiKey,
    string baseUrl,
    string authHeader,
    string? modelId,
    string? language
) : IWebSocketSessionAdapter
{
    private readonly Reson8TranscriptCollector _collector = new();

    // Written under the pump's send gate by BeginFinalizeAsync, read on the receive loop by
    // HandleMessage. Volatile so the flush_confirmation match can't observe a stale null and
    // strand FinalizeAsync waiting for a terminal signal that already arrived.
    private volatile string? _flushId;

    public string ProviderName => "Reson8";
    public WebSocketReadinessPolicy Readiness => WebSocketReadinessPolicy.Immediate;
    public WebSocketTerminalPolicy Terminal =>
        WebSocketTerminalPolicy.Require("matching flush_confirmation");
    public WebSocketKeepAlivePolicy? KeepAlive => null;
    public WebSocketClosePolicy ClosePolicy => WebSocketClosePolicy.Default;

    public ValueTask<WebSocketConnectionOptions> GetConnectionOptionsAsync(
        CancellationToken ct
    ) =>
        ValueTask.FromResult(
            new WebSocketConnectionOptions(
                Reson8StreamingSession.BuildRealtimeUri(baseUrl, modelId, language),
                Reson8StreamingSession.CreateStreamingHeaders(apiKey, authHeader)
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

    public ValueTask<WebSocketFinalizePlan> BeginFinalizeAsync(CancellationToken ct)
    {
        _flushId = Guid.NewGuid().ToString();
        var json = JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                ["type"] = "flush_request",
                ["id"] = _flushId,
            }
        );
        return ValueTask.FromResult(
            new WebSocketFinalizePlan(
                [
                    new WebSocketOutboundMessage(
                        Encoding.UTF8.GetBytes(json),
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

        var json = Encoding.UTF8.GetString(completePayload.Span);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var eventType =
                root.TryGetProperty("type", out var typeElement)
                && typeElement.ValueKind == JsonValueKind.String
                    ? typeElement.GetString()
                    : null;
            if (
                string.Equals(
                    eventType,
                    "flush_confirmation",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                var responseId =
                    root.TryGetProperty("id", out var idElement)
                    && idElement.ValueKind == JsonValueKind.String
                        ? idElement.GetString()
                        : null;
                return string.Equals(
                    responseId,
                    _flushId,
                    StringComparison.Ordinal
                )
                    ? new WebSocketInboundResult(
                        [],
                        WebSocketSessionSignal.Terminal
                    )
                    : WebSocketInboundResult.Empty;
            }

            var transcript = _collector.ApplyEvent(json);
            return transcript is null
                ? WebSocketInboundResult.Empty
                : new WebSocketInboundResult([transcript]);
        }
        catch (JsonException ex)
        {
            return new WebSocketInboundResult(
                [],
                Fault: new InvalidOperationException(
                    "Reson8 sent malformed JSON.",
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

internal sealed class Reson8TranscriptCollector
{
    private readonly List<string> _finals = [];
    private string _interim = "";

    public bool IsFlushConfirmed { get; private set; }
    public string FinalText => string.Join(" ", _finals);

    public StreamingTranscriptEvent? ApplyEvent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var type = GetString(root, "type");
        if (string.IsNullOrWhiteSpace(type))
            return null;

        if (type.Equals("flush_confirmation", StringComparison.OrdinalIgnoreCase))
        {
            IsFlushConfirmed = true;
            return null;
        }

        if (type.Contains("error", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Reson8Plugin.ExtractApiError(json));

        if (!type.Equals("transcript", StringComparison.OrdinalIgnoreCase))
            return null;

        var text = GetString(root, "text")?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var isFinal = GetBool(root, "is_final");
        if (isFinal)
        {
            _finals.Add(text);
            _interim = "";
        }
        else
        {
            _interim = text;
        }

        return new StreamingTranscriptEvent(text, isFinal);
    }

    public string ApplyEvent(StreamingTranscriptEvent evt)
    {
        if (evt.IsFinal)
        {
            var trimmed = evt.Text.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                _finals.Add(trimmed);
            _interim = "";
        }
        else
        {
            _interim = evt.Text.Trim();
        }

        return CurrentText;
    }

    private string CurrentText
    {
        get
        {
            var parts = _finals.ToList();
            if (!string.IsNullOrWhiteSpace(_interim))
                parts.Add(_interim);
            return string.Join(" ", parts);
        }
    }

    private static bool GetBool(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element)
        && element.ValueKind == JsonValueKind.True;

    private static string? GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element)
        && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
