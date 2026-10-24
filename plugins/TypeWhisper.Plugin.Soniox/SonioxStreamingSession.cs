// ReSharper disable MemberCanBePrivate.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.Plugin.Soniox;

internal sealed class SonioxStreamingSession : IStreamingSession, IStreamingSessionHealth
{
    internal const string RealtimeModel = "stt-rt-v5";

    private readonly WebSocketSessionPump _pump;

    private SonioxStreamingSession(WebSocketSessionPump pump)
    {
        _pump = pump;
    }

    public Exception? Fault => _pump.Fault;

    public event Action<StreamingTranscriptEvent>? TranscriptReceived
    {
        add => _pump.TranscriptReceived += value;
        remove => _pump.TranscriptReceived -= value;
    }

    public static async Task<SonioxStreamingSession> ConnectAsync(
        string apiKey,
        Uri realtimeUri,
        IReadOnlyList<string> languageHints,
        CancellationToken ct
    )
    {
        var pump = await WebSocketSessionPump.ConnectAsync(
            new SonioxWebSocketAdapter(apiKey, realtimeUri, languageHints),
            ct
        );
        return new SonioxStreamingSession(pump);
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct) =>
        _pump.SendAudioAsync(pcm16Audio, ct);

    public Task FinalizeAsync(CancellationToken ct) => _pump.FinalizeAsync(ct);

    public ValueTask DisposeAsync() => _pump.DisposeAsync();

    // The key goes in the Authorization header: from 2027-01-15 Soniox refuses keys sent only here.
    internal static string BuildConfigMessage(
        string model,
        IReadOnlyList<string> languageHints
    )
    {
        var config = new Dictionary<string, object>
        {
            ["model"] = model,
            ["audio_format"] = "pcm_s16le",
            ["sample_rate"] = 16000,
            ["num_channels"] = 1,
            ["enable_endpoint_detection"] = true,
            // Tokens carry a language only when identification is on.
            ["enable_language_identification"] = true,
        };

        if (languageHints.Count > 0)
        {
            config["language_hints"] = languageHints;
        }

        return JsonSerializer.Serialize(config);
    }

    internal static IReadOnlyDictionary<string, string> BuildConnectionHeaders(string apiKey) =>
        new Dictionary<string, string> { ["Authorization"] = "Bearer " + apiKey };

    internal readonly record struct SonioxToken(string Text, bool IsFinal, string? Language = null);

    internal sealed record SonioxError(
        int? Code,
        string? Type,
        string? Message,
        string? RequestId
    );

    internal sealed record SonioxMessage(
        IReadOnlyList<SonioxToken> Tokens,
        bool Finished,
        SonioxError? Error = null
    );

    internal readonly record struct SonioxAggregateUpdate(
        string PreviewText,
        bool Finished,
        string FinalText,
        string? PreviewLanguage,
        string? FinalLanguage
    );

    internal static SonioxMessage ParseMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("Soniox response is not an object.");

        var errorCode = root.TryGetProperty("error_code", out var codeElement)
            && codeElement.ValueKind == JsonValueKind.Number
            && codeElement.TryGetInt32(out var code)
                ? code
                : (int?)null;
        var errorType = GetString(root, "error_type");
        var errorMessage = GetString(root, "error_message");
        if (
            root.TryGetProperty("error_code", out _)
            || errorType is not null
            || errorMessage is not null
        )
        {
            return new SonioxMessage(
                [],
                false,
                new SonioxError(errorCode, errorType, errorMessage, GetString(root, "request_id"))
            );
        }

        var finished = false;
        if (root.TryGetProperty("finished", out var finishedElement))
        {
            if (finishedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new FormatException("Soniox 'finished' is not a boolean.");
            finished = finishedElement.ValueKind == JsonValueKind.True;
        }

        if (
            !root.TryGetProperty("tokens", out var tokensElement)
            || tokensElement.ValueKind != JsonValueKind.Array
        )
        {
            throw new FormatException("Soniox response has no token array.");
        }

        var tokens = new List<SonioxToken>(tokensElement.GetArrayLength());
        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
        // -- a query would box JsonElement's struct enumerator on every message.
        foreach (var token in tokensElement.EnumerateArray())
        {
            if (
                token.ValueKind != JsonValueKind.Object
                || !token.TryGetProperty("text", out var textElement)
                || textElement.ValueKind != JsonValueKind.String
                || !token.TryGetProperty("is_final", out var finalElement)
                || finalElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            )
            {
                throw new FormatException("Soniox sent a malformed token.");
            }

            tokens.Add(new SonioxToken(
                textElement.GetString()!,
                finalElement.ValueKind == JsonValueKind.True,
                GetString(token, "language")
            ));
        }

        return new SonioxMessage(tokens, finished);
    }

    // Built from the documented fields only: the raw payload is never echoed.
    internal static PluginRequestException CreateRequestException(SonioxError error)
    {
        var kind = error.Code switch
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

        var description = new StringBuilder("Soniox streaming error: ");
        if (!string.IsNullOrWhiteSpace(error.Type))
            description.Append(error.Type).Append(": ");
        description.Append(
            !string.IsNullOrWhiteSpace(error.Message) ? error.Message
            : error.Code is { } code ? $"HTTP {code}"
            : "request rejected"
        );
        if (!string.IsNullOrWhiteSpace(error.RequestId))
            description.Append(" (request_id: ").Append(error.RequestId).Append(')');

        return new PluginRequestException(description.ToString(), kind, error.Code);
    }

    internal static PluginRequestException CreateIncompleteException(Exception? inner = null) =>
        new(
            "Soniox returned an invalid or incomplete live response.",
            PluginRequestFailureKind.OutputIncomplete,
            innerException: inner
        );

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}

internal sealed class SonioxWebSocketAdapter(
    string apiKey,
    Uri realtimeUri,
    IReadOnlyList<string> languageHints
) : IWebSocketSessionAdapter
{
    private readonly SonioxTranscriptAggregator _aggregator = new();
    // Set on the send side, read by HandleMessage on the receive loop.
    private volatile bool _endOfAudioSent;

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
            new WebSocketConnectionOptions(
                realtimeUri,
                SonioxStreamingSession.BuildConnectionHeaders(apiKey)
            )
        );

    public ValueTask<IReadOnlyList<WebSocketOutboundMessage>> OnConnectedAsync(
        CancellationToken ct
    ) =>
        ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(
            [
                new WebSocketOutboundMessage(
                    Encoding.UTF8.GetBytes(
                        SonioxStreamingSession.BuildConfigMessage(
                            SonioxStreamingSession.RealtimeModel,
                            languageHints
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

    public ValueTask<WebSocketFinalizePlan> BeginFinalizeAsync(CancellationToken ct)
    {
        _endOfAudioSent = true;
        return ValueTask.FromResult(
            new WebSocketFinalizePlan(
                [
                    new WebSocketOutboundMessage(
                        ReadOnlyMemory<byte>.Empty,
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

        SonioxStreamingSession.SonioxAggregateUpdate update;
        try
        {
            var message = SonioxStreamingSession.ParseMessage(
                Encoding.UTF8.GetString(completePayload.Span)
            );
            if (message.Error is not null)
            {
                return new WebSocketInboundResult(
                    [],
                    Fault: SonioxStreamingSession.CreateRequestException(message.Error)
                );
            }

            update = _aggregator.Apply(message, _endOfAudioSent);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return new WebSocketInboundResult(
                [],
                Fault: SonioxStreamingSession.CreateIncompleteException(ex)
            );
        }

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
                        )
                        {
                            DetectedLanguage = update.FinalLanguage,
                        },
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
                    )
                    {
                        DetectedLanguage = update.PreviewLanguage,
                    },
                ]
            );
    }
}

internal sealed class SonioxTranscriptAggregator
{
    internal const int MaximumTranscriptLength = 1024 * 1024;

    private readonly StringBuilder _final = new();
    private readonly HashSet<string> _finalLanguages = new(StringComparer.OrdinalIgnoreCase);
    private string _provisional = "";

    public string FinalText => _final.ToString().Trim();

    /// <summary>Adds a message's final tokens to the transcript and returns the updated preview.</summary>
    /// <exception cref="FormatException">The message breaks Soniox's protocol or the transcript bound.</exception>
    public SonioxStreamingSession.SonioxAggregateUpdate Apply(
        SonioxStreamingSession.SonioxMessage message,
        bool endOfAudioSent
    )
    {
        var provisional = new StringBuilder();
        var previewLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in message.Tokens)
        {
            if (!string.IsNullOrWhiteSpace(token.Language))
            {
                var language = token.Language.Trim();
                previewLanguages.Add(language);
                if (token.IsFinal)
                    _finalLanguages.Add(language);
            }

            if (IsControlToken(token.Text))
                continue;
            if (token.IsFinal)
            {
                // Soniox lists final tokens before non-final ones.
                if (provisional.Length > 0)
                    throw new FormatException("Soniox sent a final token after a non-final one.");
                _final.Append(token.Text);
            }
            else
            {
                provisional.Append(token.Text);
            }

            if (_final.Length + provisional.Length > MaximumTranscriptLength)
                throw new FormatException("Soniox live transcript exceeded its length limit.");
        }

        _provisional = provisional.ToString();
        if (message.Finished)
        {
            if (!endOfAudioSent)
                throw new FormatException("Soniox finished the stream before the end of audio.");
            // A tail still provisional here would be dropped. An earlier one may have been retracted.
            if (!string.IsNullOrWhiteSpace(_provisional))
                throw new FormatException("Soniox finished the stream with unconfirmed text.");
        }

        var preview = (_final + _provisional).Trim();
        return new SonioxStreamingSession.SonioxAggregateUpdate(
            preview,
            message.Finished,
            FinalText,
            previewLanguages.Count == 1 ? previewLanguages.Single() : null,
            _finalLanguages.Count == 1 ? _finalLanguages.Single() : null
        );
    }

    internal static bool IsControlToken(string text) => text is "<end>" or "<fin>";
}
