using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.Plugin.Gladia;

internal sealed class GladiaStreamingSession : IStreamingSession
{
    internal const string InitUrl = "https://api.gladia.io/v2/live";

    private readonly WebSocketSessionPump _pump;

    private GladiaStreamingSession(WebSocketSessionPump pump)
    {
        _pump = pump;
    }

    public event Action<StreamingTranscriptEvent>? TranscriptReceived
    {
        add => _pump.TranscriptReceived += value;
        remove => _pump.TranscriptReceived -= value;
    }

    public static async Task<GladiaStreamingSession> ConnectAsync(
        HttpClient httpClient,
        string apiKey,
        string? language,
        CancellationToken ct
    )
    {
        var pump = await WebSocketSessionPump.ConnectAsync(
            new GladiaWebSocketAdapter(httpClient, apiKey, language),
            ct
        );
        return new GladiaStreamingSession(pump);
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct) =>
        _pump.SendAudioAsync(pcm16Audio, ct);

    public Task FinalizeAsync(CancellationToken ct) => _pump.FinalizeAsync(ct);

    public ValueTask DisposeAsync() => _pump.DisposeAsync();

    internal static string BuildInitRequest(string? language, int sampleRate)
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
            },
        };

        if (!string.IsNullOrWhiteSpace(language))
        {
            body["language_config"] = new Dictionary<string, object>
            {
                ["languages"] = new[] { language },
            };
        }

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

    internal readonly record struct GladiaMessage(
        string MessageType,
        string? Text,
        bool IsFinal
    );

    internal static GladiaMessage ParseMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var type =
                root.TryGetProperty("type", out var typeElement)
                && typeElement.ValueKind == JsonValueKind.String
                    ? typeElement.GetString() ?? ""
                    : "";

            if (
                type != "transcript"
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
            )
            {
                return new GladiaMessage(type, null, false);
            }

            var isFinal =
                data.TryGetProperty("is_final", out var finalElement)
                && finalElement.ValueKind == JsonValueKind.True;

            string? text = null;
            if (
                data.TryGetProperty("utterance", out var utterance)
                && utterance.ValueKind == JsonValueKind.Object
                && utterance.TryGetProperty("text", out var textElement)
                && textElement.ValueKind == JsonValueKind.String
            )
            {
                text = textElement.GetString();
            }

            return new GladiaMessage(type, text, isFinal);
        }
        catch (JsonException)
        {
            return new GladiaMessage("", null, false);
        }
    }
}

internal sealed class GladiaWebSocketAdapter(
    HttpClient httpClient,
    string apiKey,
    string? language
) : IWebSocketSessionAdapter
{
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
            GladiaStreamingSession.BuildInitRequest(language, 16000),
            Encoding.UTF8,
            "application/json"
        );

        using var response = await httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Gladia live init failed {(int)response.StatusCode}: {json}"
            );
        }

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

    public ValueTask<WebSocketFinalizePlan> BeginFinalizeAsync(CancellationToken ct) =>
        ValueTask.FromResult(
            new WebSocketFinalizePlan(
                [
                    new WebSocketOutboundMessage(
                        """{"type":"stop_recording"}"""u8.ToArray(),
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

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(completePayload);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return new WebSocketInboundResult(
                [],
                Fault: new InvalidOperationException(
                    "Gladia sent malformed JSON.",
                    ex
                )
            );
        }

        var message = GladiaStreamingSession.ParseMessage(
            Encoding.UTF8.GetString(completePayload.Span)
        );
        if (
            message.MessageType.Contains(
                "error",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            var error = ExtractError(root);
            return new WebSocketInboundResult(
                [],
                Fault: new InvalidOperationException(
                    $"Gladia streaming provider error: {error}"
                )
            );
        }

        if (message.MessageType == "end_session")
        {
            return new WebSocketInboundResult(
                [],
                WebSocketSessionSignal.Terminal
            );
        }

        return message.MessageType == "transcript"
            && !string.IsNullOrWhiteSpace(message.Text)
                ? new WebSocketInboundResult(
                    [
                        new StreamingTranscriptEvent(
                            message.Text!,
                            message.IsFinal
                        ),
                    ]
                )
                : WebSocketInboundResult.Empty;
    }

    private static string ExtractError(JsonElement root)
    {
        foreach (var propertyName in new[] { "message", "error", "detail" })
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

        return root.GetRawText();
    }
}
