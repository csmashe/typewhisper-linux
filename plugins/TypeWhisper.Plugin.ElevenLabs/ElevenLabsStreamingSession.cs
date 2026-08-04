// ReSharper disable MemberCanBePrivate.Global
// Plugin types are instantiated by the host via reflection and invoked through plugin interfaces
// and JSON settings binding; the analyzer cannot see those consumers, so these .Global inspections misfire.

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.Plugin.ElevenLabs;

internal sealed class ElevenLabsStreamingSession : IStreamingSession
{
    internal const int MinimumBufferedChunkBytes = 3200;

    private readonly WebSocketSessionPump _pump;

    private ElevenLabsStreamingSession(WebSocketSessionPump pump)
    {
        _pump = pump;
    }

    internal static async Task<ElevenLabsStreamingSession> CreateConnectedSessionForTests(
        WebSocket ws
    )
    {
        var pump = await WebSocketSessionPump.StartConnectedAsync(
            new ElevenLabsWebSocketAdapter("", "scribe_v2_realtime", null),
            new ClientWebSocketTransport(ws),
            CancellationToken.None
        );
        return new ElevenLabsStreamingSession(pump);
    }

    public event Action<StreamingTranscriptEvent>? TranscriptReceived
    {
        add => _pump.TranscriptReceived += value;
        remove => _pump.TranscriptReceived -= value;
    }

    public static async Task<ElevenLabsStreamingSession> ConnectAsync(
        string apiKey,
        string realtimeModelId,
        string? language,
        CancellationToken ct
    )
    {
        var pump = await WebSocketSessionPump.ConnectAsync(
            new ElevenLabsWebSocketAdapter(apiKey, realtimeModelId, language),
            ct
        );
        return new ElevenLabsStreamingSession(pump);
    }

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct) =>
        _pump.SendAudioAsync(pcm16Audio, ct);

    public Task FinalizeAsync(CancellationToken ct) => _pump.FinalizeAsync(ct);

    public ValueTask DisposeAsync() => _pump.DisposeAsync();

    internal static Uri BuildRealtimeUri(string realtimeModelId, string? language)
    {
        var query = new List<string>
        {
            $"model_id={Uri.EscapeDataString(realtimeModelId)}",
            "audio_format=pcm_16000",
            "commit_strategy=vad",
            "include_timestamps=true",
            "include_language_detection=true",
        };

        if (!string.IsNullOrWhiteSpace(language))
            query.Add($"language_code={Uri.EscapeDataString(language)}");

        return new Uri(
            "wss://api.elevenlabs.io/v1/speech-to-text/realtime?"
                + string.Join("&", query)
        );
    }

    internal static string BuildAudioChunkPayload(byte[] pcm16Audio, bool commit) =>
        JsonSerializer.Serialize(
            new Dictionary<string, object>
            {
                ["message_type"] = "input_audio_chunk",
                ["audio_base_64"] = Convert.ToBase64String(pcm16Audio),
                ["sample_rate"] = 16000,
                ["commit"] = commit,
            }
        );

    internal static bool TryParseTranscriptEvent(
        string json,
        out StreamingTranscriptEvent? transcriptEvent,
        out string? error
    ) =>
        TryParseTranscriptEvent(
            json,
            out transcriptEvent,
            out error,
            out _,
            out _
        );

    internal static bool TryParseTranscriptEvent(
        string json,
        out StreamingTranscriptEvent? transcriptEvent,
        out string? error,
        out bool isCommittedTranscript,
        out string? messageType
    )
    {
        transcriptEvent = null;
        error = null;
        isCommittedTranscript = false;
        messageType = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("message_type", out var messageTypeElement)
                || messageTypeElement.ValueKind != JsonValueKind.String
            )
            {
                error = "ElevenLabs sent a malformed message without a message_type.";
                return false;
            }

            messageType = messageTypeElement.GetString();
            if (string.IsNullOrWhiteSpace(messageType))
            {
                error = "ElevenLabs sent a malformed message with an empty message_type.";
                return false;
            }

            if (messageType == "session_started")
                return false;

            if (IsErrorMessageType(messageType))
            {
                error = ExtractErrorMessage(root) ?? json;
                return false;
            }

            // ReSharper disable once ConvertIfStatementToSwitchStatement -- the middle
            // arm (IsErrorMessageType) is a predicate call, so only the tail could
            // become a switch; splitting would read worse.
            if (messageType == "partial_transcript")
            {
                if (!TryGetText(root, out var text))
                {
                    error = "ElevenLabs sent a malformed partial transcript.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(text))
                    return false;

                transcriptEvent = new StreamingTranscriptEvent(text, IsFinal: false);
                return true;
            }

            if (
                messageType
                is "committed_transcript"
                    or "committed_transcript_with_timestamps"
            )
            {
                isCommittedTranscript = true;
                if (!TryGetText(root, out var text))
                {
                    error = "ElevenLabs sent a malformed committed transcript.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(text))
                    return false;

                transcriptEvent = new StreamingTranscriptEvent(text, IsFinal: true);
                return true;
            }
        }
        catch (JsonException ex)
        {
            error = $"ElevenLabs sent malformed JSON: {ex.Message}";
        }

        return false;
    }

    private static bool IsErrorMessageType(string messageType) =>
        messageType switch
        {
            "auth_error"
            or "quota_exceeded"
            or "transcriber_error"
            or "input_error"
            or "error"
            or "commit_throttled"
            or "unaccepted_terms"
            or "rate_limited"
            or "queue_overflow"
            or "resource_exhausted"
            or "session_time_limit_exceeded"
            or "chunk_size_exceeded"
            or "insufficient_audio_activity"
            or "scribe_auth_error"
            or "scribe_quota_exceeded"
            or "scribe_throttled"
            or "scribe_unaccepted_terms"
            or "scribe_rate_limited"
            or "scribe_queue_overflow"
            or "scribe_resource_exhausted"
            or "scribe_session_time_limit_exceeded"
            or "scribe_input_error"
            or "scribe_chunk_size_exceeded"
            or "scribe_insufficient_audio_activity"
            or "scribe_transcriber_error"
            or "scribe_error" => true,
            _ => messageType.Contains("error", StringComparison.OrdinalIgnoreCase),
        };

    private static bool TryGetText(JsonElement root, out string text)
    {
        text = "";
        if (
            !root.TryGetProperty("text", out var textElement)
            || textElement.ValueKind != JsonValueKind.String
        )
        {
            return false;
        }

        text = textElement.GetString() ?? "";
        return true;
    }

    private static string? ExtractErrorMessage(JsonElement root)
    {
        foreach (var propertyName in new[] { "error", "message", "details" })
        {
            if (
                root.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.GetString())
            )
            {
                return property.GetString();
            }
        }

        return null;
    }
}

internal sealed class ElevenLabsWebSocketAdapter(
    string apiKey,
    string realtimeModelId,
    string? language
) : IWebSocketSessionAdapter
{
    private readonly MemoryStream _audioBuffer = new();
    private int _finalCommitPending;
    private string? _lastCommittedText;
    private string? _lastCommittedMessageType;

    public string ProviderName => "ElevenLabs";
    public WebSocketReadinessPolicy Readiness => WebSocketReadinessPolicy.Immediate;
    public WebSocketTerminalPolicy Terminal =>
        WebSocketTerminalPolicy.Require("the final committed transcript");
    public WebSocketKeepAlivePolicy? KeepAlive => null;
    public WebSocketClosePolicy ClosePolicy => WebSocketClosePolicy.Default;

    public ValueTask<WebSocketConnectionOptions> GetConnectionOptionsAsync(
        CancellationToken ct
    )
    {
        IReadOnlyDictionary<string, string> headers =
            new Dictionary<string, string>
            {
                ["xi-api-key"] = apiKey,
            };
        return ValueTask.FromResult(
            new WebSocketConnectionOptions(
                ElevenLabsStreamingSession.BuildRealtimeUri(
                    realtimeModelId,
                    language
                ),
                headers
            )
        );
    }

    public ValueTask<IReadOnlyList<WebSocketOutboundMessage>> OnConnectedAsync(
        CancellationToken ct
    ) =>
        ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>([]);

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

        _audioBuffer.Write(pcm16Audio.Span);
        if (_audioBuffer.Length < ElevenLabsStreamingSession.MinimumBufferedChunkBytes)
        {
            return ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(
                []
            );
        }

        var chunk = _audioBuffer.ToArray();
        _audioBuffer.SetLength(0);
        return ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(
            [CreateAudioMessage(chunk, commit: false)]
        );
    }

    public ValueTask<WebSocketFinalizePlan> BeginFinalizeAsync(CancellationToken ct)
    {
        Volatile.Write(ref _finalCommitPending, 1);
        var chunk = _audioBuffer.Length == 0 ? [] : _audioBuffer.ToArray();
        _audioBuffer.SetLength(0);
        return ValueTask.FromResult(
            new WebSocketFinalizePlan(
                [CreateAudioMessage(chunk, commit: true)]
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
        var parsed = ElevenLabsStreamingSession.TryParseTranscriptEvent(
            json,
            out var transcript,
            out var error,
            out var committed,
            out var messageType
        );
        if (!string.IsNullOrWhiteSpace(error))
        {
            return new WebSocketInboundResult(
                [],
                Fault: new InvalidOperationException(
                    $"ElevenLabs streaming provider error: {error}"
                )
            );
        }

        IReadOnlyList<StreamingTranscriptEvent> transcripts = [];
        if (parsed && transcript is not null)
        {
            var duplicateVariant =
                committed
                && string.Equals(
                    transcript.Text,
                    _lastCommittedText,
                    StringComparison.Ordinal
                )
                && !string.Equals(
                    messageType,
                    _lastCommittedMessageType,
                    StringComparison.Ordinal
                );
            if (!duplicateVariant)
                transcripts = [transcript];

            if (duplicateVariant)
            {
                _lastCommittedText = null;
                _lastCommittedMessageType = null;
            }
            else if (committed)
            {
                _lastCommittedText = transcript.Text;
                _lastCommittedMessageType = messageType;
            }
        }

        var signals =
            committed && Volatile.Read(ref _finalCommitPending) != 0
                ? WebSocketSessionSignal.Terminal
                : WebSocketSessionSignal.None;
        return new WebSocketInboundResult(transcripts, signals);
    }

    private static WebSocketOutboundMessage CreateAudioMessage(
        byte[] audio,
        bool commit
    ) =>
        new(
            Encoding.UTF8.GetBytes(
                ElevenLabsStreamingSession.BuildAudioChunkPayload(audio, commit)
            ),
            WebSocketMessageType.Text
        );
}
