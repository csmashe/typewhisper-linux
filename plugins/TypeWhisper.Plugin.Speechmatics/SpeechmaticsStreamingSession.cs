using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.Plugin.Speechmatics;

internal sealed class SpeechmaticsStreamingSession : IStreamingSession
{
    private const string EndpointUrl = "wss://eu.rt.speechmatics.com/v2";

    private readonly WebSocketSessionPump _pump;

    private SpeechmaticsStreamingSession(WebSocketSessionPump pump)
    {
        _pump = pump;
    }

    public event Action<StreamingTranscriptEvent>? TranscriptReceived
    {
        add => _pump.TranscriptReceived += value;
        remove => _pump.TranscriptReceived -= value;
    }

    public static async Task<SpeechmaticsStreamingSession> ConnectAsync(
        string apiKey,
        string? language,
        CancellationToken ct
    )
    {
        var pump = await WebSocketSessionPump.ConnectAsync(
            new SpeechmaticsWebSocketAdapter(apiKey, language),
            ct
        );
        return new SpeechmaticsStreamingSession(pump);
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct) =>
        _pump.SendAudioAsync(pcm16Audio, ct);

    public Task FinalizeAsync(CancellationToken ct) => _pump.FinalizeAsync(ct);

    public ValueTask DisposeAsync() => _pump.DisposeAsync();

    internal static string BuildStartRecognition(string? language, int sampleRate)
    {
        var normalizedLanguage =
            !string.IsNullOrWhiteSpace(language)
            && !language.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? language
                : throw new ArgumentException(
                    "Speechmatics requires an explicit language.",
                    nameof(language)
                );

        return JsonSerializer.Serialize(
            new Dictionary<string, object>
            {
                ["message"] = "StartRecognition",
                ["audio_format"] = new Dictionary<string, object>
                {
                    ["type"] = "raw",
                    ["encoding"] = "pcm_s16le",
                    ["sample_rate"] = sampleRate,
                },
                ["transcription_config"] = new Dictionary<string, object>
                {
                    ["language"] = normalizedLanguage,
                    ["operating_point"] = "enhanced",
                    ["enable_partials"] = true,
                    ["max_delay"] = 2,
                },
            }
        );
    }

    internal static string BuildEndOfStream(int lastSeqNo) =>
        JsonSerializer.Serialize(
            new Dictionary<string, object>
            {
                ["message"] = "EndOfStream",
                ["last_seq_no"] = lastSeqNo,
            }
        );

    internal readonly record struct SpeechmaticsMessage(
        string MessageType,
        string? Transcript,
        string? ErrorReason
    );

    internal readonly record struct SpeechmaticsUpdate(
        string PreviewText,
        bool Completed,
        string FinalText
    );

    internal static SpeechmaticsMessage ParseMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var messageType =
                root.TryGetProperty("message", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString() ?? ""
                    : "";

            string? transcript = null;
            if (
                root.TryGetProperty("metadata", out var metadataElement)
                && metadataElement.ValueKind == JsonValueKind.Object
                && metadataElement.TryGetProperty(
                    "transcript",
                    out var metadataTranscriptElement
                )
                && metadataTranscriptElement.ValueKind == JsonValueKind.String
            )
            {
                transcript = metadataTranscriptElement.GetString();
            }
            else if (
                root.TryGetProperty("transcript", out var transcriptElement)
                && transcriptElement.ValueKind == JsonValueKind.String
            )
            {
                transcript = transcriptElement.GetString();
            }

            string? errorReason = null;
            if (messageType == "Error")
            {
                errorReason =
                    root.TryGetProperty("reason", out var reasonElement)
                    && reasonElement.ValueKind == JsonValueKind.String
                        ? reasonElement.GetString()
                        : json;
            }

            return new SpeechmaticsMessage(
                messageType,
                transcript,
                errorReason
            );
        }
        catch (JsonException)
        {
            return new SpeechmaticsMessage("", null, null);
        }
    }

    internal static Uri RealtimeUri => new(EndpointUrl);
}

internal sealed class SpeechmaticsWebSocketAdapter(
    string apiKey,
    string? language
) : IWebSocketSessionAdapter
{
    private readonly SpeechmaticsTranscriptAggregator _aggregator = new();
    private int _ready;
    private int _sequenceNumber;

    public string ProviderName => "Speechmatics";
    public WebSocketReadinessPolicy Readiness =>
        WebSocketReadinessPolicy.Require("RecognitionStarted");
    public WebSocketTerminalPolicy Terminal =>
        WebSocketTerminalPolicy.Require("EndOfTranscript");
    public WebSocketKeepAlivePolicy? KeepAlive => null;
    public WebSocketClosePolicy ClosePolicy => WebSocketClosePolicy.Default;

    public ValueTask<WebSocketConnectionOptions> GetConnectionOptionsAsync(
        CancellationToken ct
    )
    {
        IReadOnlyDictionary<string, string> headers =
            new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {apiKey}",
            };
        return ValueTask.FromResult(
            new WebSocketConnectionOptions(
                SpeechmaticsStreamingSession.RealtimeUri,
                headers
            )
        );
    }

    public ValueTask<IReadOnlyList<WebSocketOutboundMessage>> OnConnectedAsync(
        CancellationToken ct
    ) =>
        ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(
            [
                new WebSocketOutboundMessage(
                    Encoding.UTF8.GetBytes(
                        SpeechmaticsStreamingSession.BuildStartRecognition(
                            language,
                            16000
                        )
                    ),
                    WebSocketMessageType.Text
                ),
            ]
        );

    public ValueTask<IReadOnlyList<WebSocketOutboundMessage>> EncodeAudioAsync(
        ReadOnlyMemory<byte> pcm16Audio,
        CancellationToken ct
    )
    {
        if (pcm16Audio.Length == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(
                []
            );
        }

        _sequenceNumber++;
        return ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(
            [
                new WebSocketOutboundMessage(
                    pcm16Audio.ToArray(),
                    WebSocketMessageType.Binary
                ),
            ]
        );
    }

    public ValueTask<WebSocketFinalizePlan> BeginFinalizeAsync(CancellationToken ct) =>
        ValueTask.FromResult(
            new WebSocketFinalizePlan(
                [
                    new WebSocketOutboundMessage(
                        Encoding.UTF8.GetBytes(
                            SpeechmaticsStreamingSession.BuildEndOfStream(
                                _sequenceNumber
                            )
                        ),
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
            using (JsonDocument.Parse(completePayload)) { }
        }
        catch (JsonException ex)
        {
            return new WebSocketInboundResult(
                [],
                Fault: new InvalidOperationException(
                    "Speechmatics sent malformed JSON.",
                    ex
                )
            );
        }

        var message = SpeechmaticsStreamingSession.ParseMessage(
            Encoding.UTF8.GetString(completePayload.Span)
        );
        switch (message.MessageType)
        {
            case "RecognitionStarted":
                Volatile.Write(ref _ready, 1);
                return new WebSocketInboundResult(
                    [],
                    WebSocketSessionSignal.Ready
                );
            case "Error":
            {
                var prefix =
                    Volatile.Read(ref _ready) == 0
                        ? "Speechmatics StartRecognition failed"
                        : "Speechmatics streaming error";
                return new WebSocketInboundResult(
                    [],
                    Fault: new InvalidOperationException(
                        $"{prefix}: {message.ErrorReason}"
                    )
                );
            }
        }

        var update = _aggregator.Apply(message);
        // ReSharper disable once InvertIf -- the terminal branch is the significant one and
        // stays first; inverting buries it behind the preview-transcript ternary.
        if (update.Completed)
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

        return message.MessageType
                is "AddTranscript"
                    or "AddPartialTranscript"
            && !string.IsNullOrEmpty(update.PreviewText)
                ? new WebSocketInboundResult(
                    [
                        new StreamingTranscriptEvent(
                            update.PreviewText,
                            IsFinal: false
                        ),
                    ]
                )
                : WebSocketInboundResult.Empty;
    }
}

internal sealed class SpeechmaticsTranscriptAggregator
{
    private readonly StringBuilder _final = new();
    private string _partialTail = "";

    private string FinalText => _final.ToString().Trim();

    public SpeechmaticsStreamingSession.SpeechmaticsUpdate Apply(
        SpeechmaticsStreamingSession.SpeechmaticsMessage message
    )
    {
        switch (message.MessageType)
        {
            case "AddTranscript":
                _final.Append(message.Transcript);
                _partialTail = "";
                break;
            case "AddPartialTranscript":
                _partialTail = message.Transcript ?? "";
                break;
        }

        var completed = message.MessageType == "EndOfTranscript";
        var preview = (_final + _partialTail).Trim();
        return new SpeechmaticsStreamingSession.SpeechmaticsUpdate(
            preview,
            completed,
            FinalText
        );
    }
}
