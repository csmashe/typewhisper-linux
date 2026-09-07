// ReSharper disable MemberCanBePrivate.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.Plugin.Soniox;

internal sealed class SonioxStreamingSession : IStreamingSession
{
    private const string EndpointUrl =
        "wss://stt-rt.soniox.com/transcribe-websocket";
    internal const string RealtimeModel = "stt-rt-v4";

    private readonly WebSocketSessionPump _pump;

    private SonioxStreamingSession(WebSocketSessionPump pump)
    {
        _pump = pump;
    }

    public event Action<StreamingTranscriptEvent>? TranscriptReceived
    {
        add => _pump.TranscriptReceived += value;
        remove => _pump.TranscriptReceived -= value;
    }

    public static async Task<SonioxStreamingSession> ConnectAsync(
        string apiKey,
        string? language,
        CancellationToken ct
    )
    {
        var pump = await WebSocketSessionPump.ConnectAsync(
            new SonioxWebSocketAdapter(apiKey, language),
            ct
        );
        return new SonioxStreamingSession(pump);
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct) =>
        _pump.SendAudioAsync(pcm16Audio, ct);

    public Task FinalizeAsync(CancellationToken ct) => _pump.FinalizeAsync(ct);

    public ValueTask DisposeAsync() => _pump.DisposeAsync();

    internal static string BuildConfigMessage(
        string apiKey,
        string model,
        string? language
    )
    {
        var config = new Dictionary<string, object>
        {
            ["api_key"] = apiKey,
            ["model"] = model,
            ["audio_format"] = "pcm_s16le",
            ["sample_rate"] = 16000,
            ["num_channels"] = 1,
            ["enable_endpoint_detection"] = true,
        };

        if (!string.IsNullOrWhiteSpace(language))
        {
            config["language_hints"] = new[] { language };
        }

        return JsonSerializer.Serialize(config);
    }

    internal readonly record struct SonioxToken(string Text, bool IsFinal);

    internal sealed record SonioxMessage(
        IReadOnlyList<SonioxToken> Tokens,
        bool Finished,
        string? ErrorMessage
    );

    internal readonly record struct SonioxAggregateUpdate(
        string PreviewText,
        bool Finished,
        string FinalText
    );

    internal static SonioxMessage ParseMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            string? error = null;
            if (
                root.TryGetProperty("error_message", out var errorElement)
                && errorElement.ValueKind == JsonValueKind.String
            )
            {
                error = errorElement.GetString();
            }
            else if (root.TryGetProperty("error_code", out _))
            {
                error = json;
            }

            var finished =
                root.TryGetProperty("finished", out var finishedElement)
                && finishedElement.ValueKind == JsonValueKind.True;

            var tokens = new List<SonioxToken>();
            // ReSharper disable once InvertIf -- inverting would duplicate the
            // `return new SonioxMessage(...)` tail on both exits.
            if (
                root.TryGetProperty("tokens", out var tokensElement)
                && tokensElement.ValueKind == JsonValueKind.Array
            )
            {
                // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
                // -- a query would box JsonElement's struct enumerator on every message.
                foreach (var token in tokensElement.EnumerateArray())
                {
                    if (token.ValueKind != JsonValueKind.Object)
                        continue;
                    var text =
                        token.TryGetProperty("text", out var textElement)
                        && textElement.ValueKind == JsonValueKind.String
                            ? textElement.GetString() ?? ""
                            : "";
                    var isFinal =
                        token.TryGetProperty("is_final", out var finalElement)
                        && finalElement.ValueKind == JsonValueKind.True;
                    tokens.Add(new SonioxToken(text, isFinal));
                }
            }

            return new SonioxMessage(tokens, finished, error);
        }
        catch (JsonException)
        {
            return new SonioxMessage([], false, null);
        }
    }

    internal static Uri RealtimeUri => new(EndpointUrl);
}

internal sealed class SonioxWebSocketAdapter(
    string apiKey,
    string? language
) : IWebSocketSessionAdapter
{
    private readonly SonioxTranscriptAggregator _aggregator = new();

    public string ProviderName => "Soniox";
    public WebSocketReadinessPolicy Readiness => WebSocketReadinessPolicy.Immediate;
    public WebSocketTerminalPolicy Terminal =>
        WebSocketTerminalPolicy.Require("finished");
    public WebSocketKeepAlivePolicy? KeepAlive => null;
    public WebSocketClosePolicy ClosePolicy => WebSocketClosePolicy.Default;

    public ValueTask<WebSocketConnectionOptions> GetConnectionOptionsAsync(
        CancellationToken ct
    ) =>
        ValueTask.FromResult(
            new WebSocketConnectionOptions(SonioxStreamingSession.RealtimeUri)
        );

    public ValueTask<IReadOnlyList<WebSocketOutboundMessage>> OnConnectedAsync(
        CancellationToken ct
    ) =>
        ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(
            [
                new WebSocketOutboundMessage(
                    Encoding.UTF8.GetBytes(
                        SonioxStreamingSession.BuildConfigMessage(
                            apiKey,
                            SonioxStreamingSession.RealtimeModel,
                            language
                        )
                    ),
                    WebSocketMessageType.Text
                ),
            ]
        );

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
                        ReadOnlyMemory<byte>.Empty,
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

        var json = Encoding.UTF8.GetString(completePayload.Span);
        try
        {
            using (JsonDocument.Parse(completePayload)) { }
        }
        catch (JsonException ex)
        {
            return new WebSocketInboundResult(
                [],
                Fault: new InvalidOperationException(
                    "Soniox sent malformed JSON.",
                    ex
                )
            );
        }

        var message = SonioxStreamingSession.ParseMessage(json);
        if (message.ErrorMessage is not null)
        {
            return new WebSocketInboundResult(
                [],
                Fault: new InvalidOperationException(
                    $"Soniox streaming error: {message.ErrorMessage}"
                )
            );
        }

        var update = _aggregator.Apply(message);
        // ReSharper disable once InvertIf -- the terminal branch is the significant one and
        // stays first; inverting buries it behind the preview-transcript ternary.
        if (update.Finished)
        {
            var transcripts =
                string.IsNullOrWhiteSpace(update.FinalText)
                    ? (IReadOnlyList<StreamingTranscriptEvent>)[]
                    : [
                        new StreamingTranscriptEvent(
                            update.FinalText,
                            IsFinal: true
                        ),
                    ];
            return new WebSocketInboundResult(
                transcripts,
                WebSocketSessionSignal.Terminal
            );
        }

        return string.IsNullOrEmpty(update.PreviewText)
            ? WebSocketInboundResult.Empty
            : new WebSocketInboundResult(
                [
                    new StreamingTranscriptEvent(
                        update.PreviewText,
                        IsFinal: false
                    ),
                ]
            );
    }
}

internal sealed class SonioxTranscriptAggregator
{
    private readonly StringBuilder _final = new();

    public string FinalText => _final.ToString().Trim();

    public SonioxStreamingSession.SonioxAggregateUpdate Apply(
        SonioxStreamingSession.SonioxMessage message
    )
    {
        var provisional = new StringBuilder();
        foreach (var token in message.Tokens)
        {
            if (IsControlToken(token.Text))
                continue;
            if (token.IsFinal)
                _final.Append(token.Text);
            else
                provisional.Append(token.Text);
        }

        var preview = (_final.ToString() + provisional).Trim();
        return new SonioxStreamingSession.SonioxAggregateUpdate(
            preview,
            message.Finished,
            FinalText
        );
    }

    internal static bool IsControlToken(string text) => text is "<end>" or "<fin>";
}
