using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.Plugin.Meta;

internal sealed class MetaRealtimeStreamingSession(WebSocketSessionPump pump) : IStreamingSession
{
    internal static string CreateHandshakeJson(
        string apiKey,
        string modelId,
        string mode,
        IReadOnlyList<string> languageBias,
        IReadOnlyList<string> keywords)
    {
        var body = new Dictionary<string, object?>
        {
            ["authorization"] = new Dictionary<string, string>
            {
                ["accessToken"] = $"Bearer {apiKey}",
            },
            ["audioEncoding"] = "PCM_16KHZ",
            ["model"] = modelId,
            ["mode"] = mode,
            ["partialMode"] = "CUMULATIVE",
            ["emitAudioProgress"] = false,
        };
        if (languageBias.Count > 0)
            body["languageBias"] = languageBias;
        if (keywords.Count > 0)
            body["keywords"] = keywords;

        return JsonSerializer.Serialize(body);
    }

    public event Action<StreamingTranscriptEvent>? TranscriptReceived
    {
        add => pump.TranscriptReceived += value;
        remove => pump.TranscriptReceived -= value;
    }

    internal static async Task<MetaRealtimeStreamingSession> ConnectAsync(
        string apiKey, string modelId, string mode, IReadOnlyList<string> languageBias,
        IReadOnlyList<string> keywords, CancellationToken ct, IPluginLocalization? localization = null) =>
        new(await WebSocketSessionPump.ConnectAsync(
            new MetaWebSocketAdapter(
                new MetaRealtimeConnectionOptions(apiKey, modelId, mode, languageBias, keywords), localization),
            ct));

    internal static async Task<MetaRealtimeStreamingSession> CreateConnectedSessionForTests(
        IWebSocketTransport transport, MetaRealtimeConnectionOptions options) =>
        new(await WebSocketSessionPump.StartConnectedAsync(
            new MetaWebSocketAdapter(options), transport, CancellationToken.None));

    public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct) => pump.SendAudioAsync(pcm16Audio, ct);
    public Task FinalizeAsync(CancellationToken ct) => pump.FinalizeAsync(ct);
    public ValueTask DisposeAsync() => pump.DisposeAsync();
}

internal sealed class MetaWebSocketAdapter(
    MetaRealtimeConnectionOptions options,
    IPluginLocalization? localization = null) : IWebSocketSessionAdapter
{
    private readonly MetaRealtimeTranscriptCollector _collector = new(options.Mode);

    // Receive-loop only: HandleMessage is the pump's single reader, so no synchronization is needed.
    private bool _handshakeSeen;

    public string ProviderName => "Meta";
    public WebSocketReadinessPolicy Readiness => WebSocketReadinessPolicy.Require("sessionId");
    public WebSocketTerminalPolicy Terminal => WebSocketTerminalPolicy.Require("final transcript");
    public WebSocketKeepAlivePolicy? KeepAlive => null;
    public WebSocketClosePolicy ClosePolicy => WebSocketClosePolicy.Default;

    public ValueTask<WebSocketConnectionOptions> GetConnectionOptionsAsync(CancellationToken ct) =>
        ValueTask.FromResult(new WebSocketConnectionOptions(
            new Uri("wss://api.meta.ai/v1/asr/realtime"),
            new Dictionary<string, string> { ["Authorization"] = $"Bearer {options.ApiKey}" }));

    public ValueTask<IReadOnlyList<WebSocketOutboundMessage>> OnConnectedAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(
            [new WebSocketOutboundMessage(Encoding.UTF8.GetBytes(MetaRealtimeStreamingSession.CreateHandshakeJson(
                options.ApiKey, options.ModelId, options.Mode, options.LanguageBias, options.Keywords)), WebSocketMessageType.Text)]);

    public ValueTask<IReadOnlyList<WebSocketOutboundMessage>> EncodeAudioAsync(
        ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<WebSocketOutboundMessage>>(
            pcm16Audio.IsEmpty ? [] : [new WebSocketOutboundMessage(pcm16Audio.ToArray(), WebSocketMessageType.Binary)]);

    public ValueTask<WebSocketFinalizePlan> BeginFinalizeAsync(CancellationToken ct) =>
        ValueTask.FromResult(new WebSocketFinalizePlan(
            [new WebSocketOutboundMessage("{\"type\":\"endStream\"}"u8.ToArray(), WebSocketMessageType.Text)]));

    public WebSocketInboundResult HandleMessage(WebSocketMessageType type, ReadOnlyMemory<byte> completePayload)
    {
        if (type != WebSocketMessageType.Text)
            return WebSocketInboundResult.Empty;

        try
        {
            using var document = JsonDocument.Parse(completePayload);
            var root = document.RootElement;
            // Only the first message after connect is the handshake ack; later messages may
            // carry a sessionId of their own and must still reach the transcript handling.
            // ReSharper disable once InvertIf -- the ack block reads as one unit ahead of the transcript path.
            if (!_handshakeSeen)
            {
                _handshakeSeen = true;
                if (!root.TryGetProperty("sessionId", out var sessionId)
                    || sessionId.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(sessionId.GetString()))
                {
                    var rejection = root.TryGetProperty("message", out var reason)
                        && reason.ValueKind == JsonValueKind.String
                            ? reason.GetString()
                            : null;
                    throw new InvalidOperationException(rejection ?? "Meta rejected the realtime handshake.");
                }

                return new WebSocketInboundResult([], WebSocketSessionSignal.Ready);
            }

            var update = _collector.Apply(Encoding.UTF8.GetString(completePayload.Span));
            var transcript = update.Transcript;
            if (transcript is not null && localization is not null && _collector.UsesDiarization)
            {
                var text = string.Join("\n", transcript.Text.Split('\n').Select(line =>
                {
                    var separator = line.IndexOf(": ", StringComparison.Ordinal);
                    return separator > 8 && line.StartsWith("Speaker ", StringComparison.Ordinal)
                        ? localization.GetString("Transcript.SpeakerLabel", line[8..separator]) + line[separator..]
                        : line;
                }));
                transcript = transcript with { Text = text };
            }

            var terminal = root.TryGetProperty("type", out var eventType)
                && eventType.ValueKind == JsonValueKind.String
                && eventType.GetString() == "transcript"
                && root.TryGetProperty("final", out var final) && final.ValueKind == JsonValueKind.True;
            return new WebSocketInboundResult(transcript is null ? [] : [transcript],
                terminal ? WebSocketSessionSignal.Terminal : WebSocketSessionSignal.None);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new WebSocketInboundResult([], Fault: new InvalidOperationException("Meta realtime transcription failed.", ex));
        }
    }
}

internal sealed class MetaRealtimeTranscriptCollector
{
    private sealed class Turn(int id)
    {
        public int Id { get; } = id;
        public string Transcript { get; set; } = "";
        public string? Speaker { get; set; }
        public bool Reported { get; set; }
        public bool Complete { get; set; }
    }
    private readonly Dictionary<int, Turn> _turns = [];
    private readonly List<(string Formatted, string Plain)> _reported = [];
    private int _nextTurnId = 1;
    private int? _activeTurnId;
    private string _interim = "";

    internal MetaRealtimeTranscriptCollector(string mode)
    {
        UsesDiarization = mode.Equals("DIARIZATION", StringComparison.OrdinalIgnoreCase);
    }

    internal bool UsesDiarization { get; }

    internal MetaRealtimeUpdate Apply(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            return default;

        var type = typeElement.GetString();
        if (type == "error")
        {
            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;
            throw new InvalidOperationException(message ?? "Meta realtime transcription failed.");
        }

        var isFinalEvent = false;
        string? finalText = null;
        switch (type)
        {
            case "speechStart":
                if (TryGetInt(root, "turnId", out var startedTurnId))
                {
                    GetOrCreateTurn(startedTurnId);
                    _activeTurnId = startedTurnId;
                    _interim = "";
                }
                break;

            case "speaker":
                if (_activeTurnId is { } speakerTurnId)
                {
                    var turn = GetOrCreateTurn(speakerTurnId);
                    turn.Speaker = root.TryGetProperty("label", out var labelElement)
                        ? labelElement.GetString()
                        : null;
                }
                break;

            case "speechEnd":
                break;

            case "speechComplete":
                if (TryGetInt(root, "turnId", out var completedTurnId))
                {
                    var turn = GetOrCreateTurn(completedTurnId);
                    if (turn.Reported)
                        return default;
                    turn.Transcript = GetTranscript(root);
                    turn.Complete = true;
                    finalText = DrainCompletedTurns();
                    if (_activeTurnId == completedTurnId)
                    {
                        _activeTurnId = null;
                        _interim = "";
                    }
                    isFinalEvent = true;
                }
                break;

            case "transcript":
                var transcript = GetTranscript(root);
                var isFinal = root.TryGetProperty("final", out var finalElement)
                    && finalElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                    && finalElement.GetBoolean();
                if (isFinal)
                {
                    finalText = GetUnreportedTerminalText(transcript);
                    _interim = "";
                    isFinalEvent = true;
                }
                else
                {
                    _interim = transcript;
                }
                break;

            default:
                return default;
        }

        var snapshot = isFinalEvent ? finalText : BuildSnapshot();
        return new MetaRealtimeUpdate(
            string.IsNullOrWhiteSpace(snapshot)
                ? null
                : new StreamingTranscriptEvent(snapshot, isFinalEvent),
            isFinalEvent);
    }

    private string GetUnreportedTerminalText(string transcript)
    {
        var remaining = transcript;
        if (_reported.Count > 0 && !string.IsNullOrWhiteSpace(transcript))
        {
            var formatted = string.Join(UsesDiarization ? "\n" : " ",
                _reported.Select(turn => turn.Formatted));
            var plain = string.Join(" ", _reported.Select(turn => turn.Plain));
            var lines = string.Join("\n", _reported.Select(turn => turn.Formatted));
            if (IsSegmentPrefix(transcript, formatted))
                remaining = transcript[formatted.Length..];
            else if (IsSegmentPrefix(transcript, lines))
                remaining = transcript[lines.Length..];
            else if (IsSegmentPrefix(transcript, plain))
                remaining = transcript[plain.Length..];
            else
            {
                Trace.WriteLine($"Meta terminal transcript revised reported final text. Reported: '{formatted}'; terminal: '{transcript}'.");
                throw new InvalidOperationException("Meta terminal transcript revised previously reported final text.");
            }
        }
        else if (string.IsNullOrWhiteSpace(transcript))
        {
            // Format the interim per line, as BuildSnapshot does, so a skipped-turn remainder
            // does not leave the interim line unlabelled next to its labelled neighbours.
            var interim = string.IsNullOrWhiteSpace(_interim) ? "" : FormatInterim();
            remaining = string.Join("\n", _turns.Values
                .Where(turn => turn is { Complete: true, Reported: false }
                    && !string.IsNullOrWhiteSpace(turn.Transcript))
                .OrderBy(turn => turn.Id)
                .Select(turn => UsesDiarization ? FormatTurn(turn) : turn.Transcript)
                .Concat(interim.Length == 0 ? [] : new[] { interim }));
        }

        var activeTurn = _activeTurnId is { } id ? GetOrCreateTurn(id) : null;
        if (string.IsNullOrWhiteSpace(remaining) || activeTurn?.Reported == true)
            return "";

        if (UsesDiarization || remaining.StartsWith('\n') || remaining.StartsWith('\r'))
            remaining = remaining.Trim();

        return UsesDiarization && !remaining.StartsWith("Speaker ", StringComparison.OrdinalIgnoreCase)
            ? FormatText(remaining, activeTurn?.Speaker)
            : remaining;
    }

    private static bool IsSegmentPrefix(string transcript, string reported)
    {
        if (!transcript.StartsWith(reported, StringComparison.Ordinal))
            return false;
        if (transcript.Length == reported.Length)
            return true;
        var next = transcript[reported.Length];
        return char.IsWhiteSpace(next)
            || (reported.Length > 0 && ".!?…。！？".Contains(reported[^1]));
    }

    private string DrainCompletedTurns()
    {
        var parts = new List<string>();
        while (_turns.TryGetValue(_nextTurnId, out var turn) && turn.Complete)
        {
            turn.Reported = true;
            _nextTurnId++;
            if (string.IsNullOrWhiteSpace(turn.Transcript))
                continue;
            var formatted = UsesDiarization ? FormatTurn(turn) : turn.Transcript;
            _reported.Add((formatted, turn.Transcript));
            parts.Add(formatted);
        }

        return string.Join("\n", parts);
    }

    private string BuildSnapshot()
    {
        var parts = _turns.Values
            .Where(turn => !string.IsNullOrWhiteSpace(turn.Transcript))
            .OrderBy(turn => turn.Id)
            .Select(turn => UsesDiarization ? FormatTurn(turn) : turn.Transcript)
            .ToList();
        if (parts.Count > 0)
        {
            // ReSharper disable once InvertIf -- inverting would duplicate the join below into both branches.
            if (!string.IsNullOrWhiteSpace(_interim))
                parts.Add(FormatInterim());

            return string.Join(UsesDiarization ? "\n" : " ", parts);
        }

        if (!UsesDiarization)
            return _interim;

        // ReSharper disable once InvertIf -- inverting would duplicate the join below into both branches.
        if (!string.IsNullOrWhiteSpace(_interim))
            parts.Add(FormatInterim());

        return string.Join("\n", parts);
    }

    private string FormatInterim() =>
        UsesDiarization
            ? FormatText(_interim, (_activeTurnId is { } id ? GetOrCreateTurn(id) : null)?.Speaker)
            : _interim;

    private static string FormatTurn(Turn turn) => FormatText(turn.Transcript, turn.Speaker);

    private static string FormatText(string text, string? speaker) =>
        MetaPlugin.NormalizeSpeakerLabel(speaker) is { } label
            ? $"{label}: {text}"
            : text;

    private Turn GetOrCreateTurn(int id)
    {
        if (_turns.TryGetValue(id, out var turn))
            return turn;
        turn = new Turn(id);
        _turns[id] = turn;
        return turn;
    }

    private static string GetTranscript(JsonElement root) =>
        root.TryGetProperty("transcript", out var transcriptElement)
            ? transcriptElement.GetString()?.Trim() ?? ""
            : "";

    private static bool TryGetInt(JsonElement root, string propertyName, out int value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }
}

internal readonly record struct MetaRealtimeUpdate(
    StreamingTranscriptEvent? Transcript,
    bool IsFinalEvent);
