using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.Plugin.Meta;

internal sealed class MetaRealtimeStreamingSession(WebSocketSessionPump pump) : IStreamingSession, IStreamingSessionHealth
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

    public Exception? Fault => pump.Fault;

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
    IPluginLocalization? localization = null) : IWebSocketSessionAdapter, IWebSocketCloseAwareAdapter
{
    private readonly MetaRealtimeTranscriptCollector _collector = new(options.Mode);

    // Receive-loop only: HandleMessage is the pump's single reader, so no synchronization is needed.
    private bool _handshakeSeen;

    // Set by BeginFinalizeAsync on the send side before endStream goes out; read on the receive loop.
    private volatile bool _endStreamSent;

    public string ProviderName => "Meta";
    public WebSocketReadinessPolicy Readiness => WebSocketReadinessPolicy.Require("sessionId");

    // Only PUSH_TO_TALK ends with a final transcript. DIARIZATION completes each turn with
    // speechComplete, and the session ends when Meta closes normally after endStream.
    public WebSocketTerminalPolicy Terminal => _collector.UsesDiarization
        ? WebSocketTerminalPolicy.Require("normal closure after every turn completed")
        : WebSocketTerminalPolicy.Require("final transcript");
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

    public ValueTask<WebSocketFinalizePlan> BeginFinalizeAsync(CancellationToken ct)
    {
        _endStreamSent = true;
        return ValueTask.FromResult(new WebSocketFinalizePlan(
            [new WebSocketOutboundMessage("{\"type\":\"endStream\"}"u8.ToArray(), WebSocketMessageType.Text)]));
    }

    public WebSocketInboundResult HandleClose(WebSocketCloseStatus? status, string? description)
    {
        if (!_collector.UsesDiarization || !_endStreamSent || status != WebSocketCloseStatus.NormalClosure)
            return WebSocketInboundResult.Empty;

        return _collector.AllTurnsComplete
            ? new WebSocketInboundResult([], WebSocketSessionSignal.Terminal)
            : new WebSocketInboundResult([], Fault: new InvalidOperationException(
                "Meta closed the realtime session before completing every turn."));
    }

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

            // Audio sent after an early final would be dropped; faulting keeps it for batch fallback.
            if (update.IsTerminal && !_endStreamSent)
                throw new InvalidOperationException("Meta completed the transcript before the end of audio.");

            return new WebSocketInboundResult(transcript is null ? [] : [transcript],
                update.IsTerminal ? WebSocketSessionSignal.Terminal : WebSocketSessionSignal.None);
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
    private readonly List<string> _reported = [];
    private int? _activeTurnId;
    private int? _lastReportedTurnId;
    private string _interim = "";

    internal MetaRealtimeTranscriptCollector(string mode)
    {
        UsesDiarization = mode.Equals("DIARIZATION", StringComparison.OrdinalIgnoreCase);
    }

    internal bool UsesDiarization { get; }

    internal bool AllTurnsComplete =>
        _turns.Values.All(turn => turn.Complete) && string.IsNullOrWhiteSpace(_interim);

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
        var isTerminal = false;
        string? finalText = null;
        switch (type)
        {
            case "speechStart":
                var startedTurnId = GetTurnId(root);
                GetOrCreateTurn(startedTurnId);
                _activeTurnId = startedTurnId;
                _interim = "";
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
            {
                var completedTurnId = GetTurnId(root);
                var completedText = GetTranscript(root);
                var turn = GetOrCreateTurn(completedTurnId);
                if (turn.Reported)
                    return default;
                turn.Transcript = completedText;
                turn.Complete = true;
                finalText = DrainCompletedTurns();
                if (_activeTurnId == completedTurnId)
                {
                    _activeTurnId = null;
                    _interim = "";
                }
                isFinalEvent = true;
                break;
            }

            case "transcript":
                var transcript = GetTranscript(root);
                var isFinal = root.TryGetProperty("final", out var finalElement)
                    && finalElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                    && finalElement.GetBoolean();
                // In DIARIZATION a final flag ends no turn and no stream; speechComplete does.
                if (isFinal && !UsesDiarization)
                {
                    finalText = GetUnreportedTerminalText(transcript);
                    _interim = "";
                    isFinalEvent = true;
                    isTerminal = true;
                }
                // A partial belongs to the latest speechStart; once that turn completed, a late one is stale.
                else if (!UsesDiarization || _activeTurnId is not null || _turns.Count == 0)
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
            isFinalEvent,
            isTerminal);
    }

    private string GetUnreportedTerminalText(string transcript)
    {
        var remaining = transcript;
        if (_reported.Count > 0 && !string.IsNullOrWhiteSpace(transcript))
        {
            var joined = string.Join(" ", _reported);
            var lines = string.Join("\n", _reported);
            if (IsSegmentPrefix(transcript, joined))
                remaining = transcript[joined.Length..];
            else if (IsSegmentPrefix(transcript, lines))
                remaining = transcript[lines.Length..];
            else
            {
                Trace.WriteLine($"Meta terminal transcript mismatch: revised reported final text; reported segments={_reported.Count}; reported length={joined.Length}; terminal length={transcript.Length}.");
                throw new InvalidOperationException("Meta terminal transcript revised previously reported final text.");
            }
        }
        else if (string.IsNullOrWhiteSpace(transcript))
        {
            remaining = string.Join("\n", _turns.Values
                .Where(turn => turn is { Complete: true, Reported: false }
                    && !string.IsNullOrWhiteSpace(turn.Transcript))
                .OrderBy(turn => turn.Id)
                .Select(turn => turn.Transcript)
                .Concat(string.IsNullOrWhiteSpace(_interim) ? [] : new[] { _interim }));
        }

        var activeTurn = _activeTurnId is { } id ? GetOrCreateTurn(id) : null;
        if (string.IsNullOrWhiteSpace(remaining) || activeTurn?.Reported == true)
            return "";

        return remaining.StartsWith('\n') || remaining.StartsWith('\r')
            ? remaining.Trim()
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

    // Reports completed turns in turnId order up to the first one still open; Meta documents
    // overlapping turns but not where numbering starts or whether it is contiguous.
    private string DrainCompletedTurns()
    {
        var parts = new List<string>();
        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator -- the loop breaks at the first open turn and mutates each turn it reports; a query would hide both.
        foreach (var turn in _turns.Values.Where(turn => !turn.Reported).OrderBy(turn => turn.Id).ToList())
        {
            if (!turn.Complete)
                break;
            turn.Reported = true;
            _lastReportedTurnId = turn.Id;
            if (string.IsNullOrWhiteSpace(turn.Transcript))
                continue;
            _reported.Add(turn.Transcript);
            parts.Add(UsesDiarization ? FormatTurn(turn) : turn.Transcript);
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
        // Final segments are appended, so an earlier turn can no longer be placed before a reported one.
        if (id < _lastReportedTurnId)
            throw new JsonException("Meta reported a turn after a later turn was already final.");
        turn = new Turn(id);
        _turns[id] = turn;
        return turn;
    }

    private static string GetTranscript(JsonElement root) =>
        root.TryGetProperty("transcript", out var transcriptElement)
        && transcriptElement.ValueKind == JsonValueKind.String
            ? transcriptElement.GetString()!.Trim()
            : throw new JsonException("Meta returned no string transcript in a realtime event.");

    private static int GetTurnId(JsonElement root) =>
        root.TryGetProperty("turnId", out var element)
        && element.ValueKind == JsonValueKind.Number
        && element.TryGetInt32(out var value)
            ? value
            : throw new JsonException("Meta returned no valid turnId in a realtime turn event.");
}

internal readonly record struct MetaRealtimeUpdate(
    StreamingTranscriptEvent? Transcript,
    bool IsFinalEvent,
    bool IsTerminal = false);
