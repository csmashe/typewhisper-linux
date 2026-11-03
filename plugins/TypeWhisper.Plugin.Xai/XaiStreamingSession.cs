using System.Net.WebSockets;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.WebSockets;

namespace TypeWhisper.Plugin.Xai;

internal sealed class XaiStreamingSession : IStreamingSession
{
    private static readonly TimeSpan s_providerReadinessTimeout =
        TimeSpan.FromSeconds(10);
    // Matches WebSocketClosePolicy.Default: disposal is trying to leave, not to finish a
    // handshake, so it waits only as long as the pump's own teardown budget.
    private static readonly TimeSpan s_disposePumpWait = TimeSpan.FromSeconds(2);

    private readonly Task<WebSocketSessionPump> _pumpTask;

    // Non-null only while a handshake this session owns may still be in flight. Cancelling it
    // routes through StartConnectedAsync's own failure path, which aborts and disposes the
    // transport -- a bare WaitAsync would return from disposal with the socket still live.
    private readonly CancellationTokenSource? _startupCts;
    private readonly TimeSpan _disposePumpWait;
    private readonly Lock _subscriberGate = new();
    private Action<StreamingTranscriptEvent>? _pendingSubscribers;
    private bool _subscribersAttached;

    private XaiStreamingSession(
        Task<WebSocketSessionPump> pumpTask,
        CancellationTokenSource? startupCts = null,
        TimeSpan? disposePumpWait = null
    )
    {
        _pumpTask = pumpTask;
        _startupCts = startupCts;
        _disposePumpWait = disposePumpWait ?? s_disposePumpWait;
        _ = AttachPendingSubscribersAsync();
    }

    public event Action<StreamingTranscriptEvent>? TranscriptReceived
    {
        add
        {
            if (value is null)
                return;
            lock (_subscriberGate)
            {
                if (_subscribersAttached)
                    _pumpTask.Result.TranscriptReceived += value;
                else
                    _pendingSubscribers += value;
            }
        }
        remove
        {
            if (value is null)
                return;
            lock (_subscriberGate)
            {
                if (_subscribersAttached)
                    _pumpTask.Result.TranscriptReceived -= value;
                else
                    _pendingSubscribers -= value;
            }
        }
    }

    public static async Task<XaiStreamingSession> ConnectAsync(
        string apiKey,
        string? language,
        IReadOnlyList<string> keyterms,
        CancellationToken ct
    )
    {
        // The readiness deadline covers only the wait for transcript.created. The
        // handshake runs on the caller's token so a slow DNS/TLS connect is not
        // misreported as a missing readiness frame.
        var adapter = new XaiWebSocketAdapter(apiKey, language, keyterms);
        var transport = new ClientWebSocketTransport();
        var transportOwned = true;
        try
        {
            var connectionOptions = await adapter.GetConnectionOptionsAsync(ct);
            await transport.ConnectAsync(connectionOptions, ct);

            using var readinessCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readinessCts.CancelAfter(s_providerReadinessTimeout);
            transportOwned = false;
            var pump = await WebSocketSessionPump.StartConnectedAsync(
                adapter,
                transport,
                readinessCts.Token
            );
            return new XaiStreamingSession(Task.FromResult(pump));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                "xAI did not send transcript.created within 10 seconds."
            );
        }
        finally
        {
            if (transportOwned)
                await transport.DisposeAsync();
        }
    }

    // disposePumpWait shortens the teardown budget so a test covering the stuck-handshake path
    // does not hold a worker for the full production wait.
    internal static XaiStreamingSession CreateConnectedSessionForTests(
        WebSocket ws,
        TimeSpan? disposePumpWait = null
    )
    {
        if (ws.State != WebSocketState.Open)
            throw new InvalidOperationException("The test WebSocket must already be open.");

        // No readiness deadline here by design, so disposal is the only thing that can end a
        // handshake the peer never answers -- hand it the token it needs to do that.
        var startupCts = new CancellationTokenSource();
        var pumpTask = WebSocketSessionPump.StartConnectedAsync(
            new XaiWebSocketAdapter("", null, []),
            new ClientWebSocketTransport(ws),
            startupCts.Token
        );
        return new XaiStreamingSession(pumpTask, startupCts, disposePumpWait);
    }

    internal static async Task<XaiStreamingSession> CreateConnectedSessionForTests(
        WebSocket ws,
        TimeSpan readinessTimeout,
        CancellationToken ct
    )
    {
        if (ws.State != WebSocketState.Open)
            throw new InvalidOperationException("The test WebSocket must already be open.");

        var transport = new ClientWebSocketTransport(ws);
        using var readinessCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readinessCts.CancelAfter(readinessTimeout);
        var pumpTask = WebSocketSessionPump.StartConnectedAsync(
            new XaiWebSocketAdapter("", null, []),
            transport,
            readinessCts.Token
        );
        try
        {
            await pumpTask;
            return new XaiStreamingSession(pumpTask);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"xAI did not send transcript.created within "
                    + $"{readinessTimeout.TotalSeconds:g} seconds."
            );
        }
    }

    public static Uri BuildStreamingUri(
        string? language,
        bool interimResults,
        IReadOnlyList<string> keyterms
    )
    {
        var query = new List<string>
        {
            "model=" + Uri.EscapeDataString(XaiPlugin.SttApiModelId),
            "sample_rate=16000",
            "encoding=pcm",
            $"interim_results={(interimResults ? "true" : "false")}",
        };

        if (!string.IsNullOrWhiteSpace(language))
        {
            query.Add($"language={Uri.EscapeDataString(language)}");
        }

        query.AddRange(keyterms.Select(keyterm => "keyterm=" + Uri.EscapeDataString(keyterm)));

        return new Uri("wss://api.x.ai/v1/stt?" + string.Join("&", query));
    }

    public static IReadOnlyDictionary<string, string> CreateStreamingHeaders(
        string apiKey
    ) =>
        new Dictionary<string, string>
        {
            ["Authorization"] = $"Bearer {apiKey}",
        };

    public async Task SendAudioAsync(
        ReadOnlyMemory<byte> pcm16Audio,
        CancellationToken ct
    )
    {
        var pump = await _pumpTask.WaitAsync(ct);
        await pump.SendAudioAsync(pcm16Audio, ct);
    }

    public async Task FinalizeAsync(CancellationToken ct)
    {
        var pump = await _pumpTask.WaitAsync(ct);
        await pump.FinalizeAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            WebSocketSessionPump pump;
            try
            {
                pump = await _pumpTask.WaitAsync(_disposePumpWait);
            }
            catch (TimeoutException) when (_startupCts is not null)
            {
                // Cancel rather than just stop waiting: StartConnectedAsync's failure path aborts
                // and disposes the socket, so the second wait returns once teardown has run.
                await _startupCts.CancelAsync();
                pump = await _pumpTask.WaitAsync(_disposePumpWait);
            }

            await pump.DisposeAsync();
        }
        catch
        {
            _ = _pumpTask.Exception;
        }
        finally
        {
            _startupCts?.Dispose();
        }
    }

    private async Task AttachPendingSubscribersAsync()
    {
        try
        {
            var pump = await _pumpTask;
            lock (_subscriberGate)
            {
                if (_pendingSubscribers is not null)
                    pump.TranscriptReceived += _pendingSubscribers;
                _pendingSubscribers = null;
                _subscribersAttached = true;
            }
        }
        catch
        {
            _ = _pumpTask.Exception;
        }
    }
}

internal sealed class XaiWebSocketAdapter(
    string apiKey,
    string? language,
    IReadOnlyList<string> keyterms
) : IWebSocketSessionAdapter
{
    private readonly XaiTranscriptCollector _collector = new();

    public string ProviderName => "xAI";
    public WebSocketReadinessPolicy Readiness =>
        WebSocketReadinessPolicy.Require("transcript.created");
    public WebSocketTerminalPolicy Terminal =>
        WebSocketTerminalPolicy.Require("transcript.done");
    public WebSocketKeepAlivePolicy? KeepAlive => null;
    public WebSocketClosePolicy ClosePolicy => WebSocketClosePolicy.Default;

    public ValueTask<WebSocketConnectionOptions> GetConnectionOptionsAsync(
        CancellationToken ct
    ) =>
        ValueTask.FromResult(
            new WebSocketConnectionOptions(
                XaiStreamingSession.BuildStreamingUri(
                    language,
                    interimResults: true,
                    keyterms
                ),
                XaiStreamingSession.CreateStreamingHeaders(apiKey)
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

    public ValueTask<WebSocketFinalizePlan> BeginFinalizeAsync(CancellationToken ct) =>
        ValueTask.FromResult(
            new WebSocketFinalizePlan(
                [
                    new WebSocketOutboundMessage(
                        """{"type":"audio.done"}"""u8.ToArray(),
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
            var transcript = _collector.ApplyEvent(
                System.Text.Encoding.UTF8.GetString(completePayload.Span)
            );
            var transcripts =
                transcript is null
                    ? (IReadOnlyList<StreamingTranscriptEvent>)[]
                    : [transcript];
            var signals = WebSocketSessionSignal.None;
            if (_collector.IsReady)
                signals |= WebSocketSessionSignal.Ready;
            if (_collector.IsTerminal)
                signals |= WebSocketSessionSignal.Terminal;
            return new WebSocketInboundResult(transcripts, signals);
        }
        catch (JsonException ex)
        {
            return new WebSocketInboundResult(
                [],
                Fault: new InvalidOperationException(
                    "xAI sent malformed JSON.",
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

// xAI's speech_final normally restates its utterance's chunk finals, and the host appends every
// final, so chunks are held until speech_final or transcript.done settles the utterance.
internal sealed class XaiTranscriptCollector
{
    // Event timestamps carry two decimals; a speech_final that restates its utterance starts
    // where the first chunk did, while a tail-only one starts at least a chunk later.
    private const double UtteranceStartTolerance = 0.05;

    private readonly List<string> _finals = [];
    private readonly List<(string Text, double? Start)> _pendingChunks = [];
    private string? _doneText;
    private string? _detectedLanguage;
    private double _duration;

    public bool IsTerminal { get; private set; }
    public bool IsReady { get; private set; }

    public StreamingTranscriptEvent? ApplyEvent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (
            !root.TryGetProperty("type", out var typeEl)
            || typeEl.ValueKind != JsonValueKind.String
        )
        {
            throw new InvalidOperationException("Invalid xAI STT event.");
        }

        return typeEl.GetString() switch
        {
            "transcript.created" => ApplyCreatedEvent(),
            "transcript.partial" => ApplyPartialEvent(root),
            "transcript.done" => ApplyDoneEvent(root),
            "error" => throw new InvalidOperationException(
                ExtractErrorMessage(root) ?? "Unknown xAI STT error"
            ),
            _ => null,
        };
    }

    private StreamingTranscriptEvent? ApplyCreatedEvent()
    {
        IsReady = true;
        return null;
    }

    public PluginTranscriptionResult FinalResult(string? fallbackLanguage)
    {
        var text =
            !string.IsNullOrWhiteSpace(_doneText)
                ? _doneText!
                : JoinNonBlank(_finals.Concat(_pendingChunks.Select(chunk => chunk.Text)));

        return new PluginTranscriptionResult(
            text,
            _detectedLanguage ?? fallbackLanguage ?? "",
            _duration
        );
    }

    private StreamingTranscriptEvent? ApplyPartialEvent(JsonElement root)
    {
        var text = GetString(root, "text")?.Trim() ?? "";
        var isFinal = GetBool(root, "is_final");
        var speechFinal = GetBool(root, "speech_final");
        RememberMetadata(root);

        if (!isFinal)
            return new StreamingTranscriptEvent(PendingPreview(text), IsFinal: false);

        if (!speechFinal)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            _pendingChunks.Add((text, GetNumber(root, "start")));
            return new StreamingTranscriptEvent(PendingPreview(""), IsFinal: false);
        }

        var utterance = SettleUtterance(text, GetNumber(root, "start"));
        if (utterance.Length == 0)
            return null;

        _finals.Add(utterance);
        return new StreamingTranscriptEvent(utterance, IsFinal: true);
    }

    private string SettleUtterance(string speechFinalText, double? speechFinalStart)
    {
        if (_pendingChunks.Count == 0)
            return speechFinalText;

        var chunks = JoinNonBlank(_pendingChunks.Select(chunk => chunk.Text));
        var firstChunkStart = _pendingChunks[0].Start;
        _pendingChunks.Clear();

        if (speechFinalText.Length == 0)
            return chunks;

        var restatesChunks =
            speechFinalStart is { } start && firstChunkStart is { } chunkStart
                ? start <= chunkStart + UtteranceStartTolerance
                : speechFinalText.StartsWith(chunks, StringComparison.Ordinal)
                    && (
                        speechFinalText.Length == chunks.Length
                        || IsWordBoundary(speechFinalText[chunks.Length])
                    );
        return restatesChunks ? speechFinalText : $"{chunks} {speechFinalText}";
    }

    private StreamingTranscriptEvent? ApplyDoneEvent(JsonElement root)
    {
        var text = GetString(root, "text")?.Trim() ?? "";
        RememberMetadata(root);
        IsTerminal = true;

        // Chunks the server locked without closing their utterance are still owed to the host.
        var unsettled = JoinNonBlank(_pendingChunks.Select(chunk => chunk.Text));
        _pendingChunks.Clear();
        if (unsettled.Length > 0)
            _finals.Add(unsettled);

        if (!string.IsNullOrWhiteSpace(text))
            _doneText = text;

        var emitted = JoinNonBlank([unsettled, DoneTextBeyondFinals(text)]);
        return emitted.Length == 0 ? null : new StreamingTranscriptEvent(emitted, IsFinal: true);
    }

    private string DoneTextBeyondFinals(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        if (_finals.Count == 0)
        {
            _finals.Add(text);
            return text;
        }

        var joined = string.Join(" ", _finals);
        if (
            !text.StartsWith(joined, StringComparison.Ordinal)
            || text.Length <= joined.Length
            || text[joined.Length] != ' '
        )
        {
            return "";
        }

        var delta = text[(joined.Length + 1)..].Trim();
        if (delta.Length > 0)
            _finals.Add(delta);
        return delta;
    }

    private static bool IsWordBoundary(char next) =>
        char.IsWhiteSpace(next) || next is ',' or '.' or ';' or ':' or '!' or '?';

    private string PendingPreview(string interim) =>
        JoinNonBlank(_pendingChunks.Select(chunk => chunk.Text).Append(interim));

    private static string JoinNonBlank(IEnumerable<string> parts) =>
        string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    private void RememberMetadata(JsonElement root)
    {
        if (
            GetString(root, "language") is { } language
            && !string.IsNullOrWhiteSpace(language)
        )
        {
            _detectedLanguage = language;
        }

        if (
            root.TryGetProperty("duration", out var durationElement)
            && durationElement.ValueKind == JsonValueKind.Number
            && durationElement.TryGetDouble(out var duration)
        )
        {
            _duration = duration;
        }
    }

    private static bool GetBool(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element)
        && element.ValueKind == JsonValueKind.True;

    private static double? GetNumber(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element)
        && element.ValueKind == JsonValueKind.Number
        && element.TryGetDouble(out var value)
            ? value
            : null;

    private static string? GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element)
        && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string? ExtractErrorMessage(JsonElement root)
    {
        // ReSharper disable once InvertIf -- inverting would duplicate the
        // GetString(root, "message") fallback on both exits.
        if (root.TryGetProperty("error", out var error))
        {
            // ReSharper disable once ConvertIfStatementToSwitchStatement -- the Object arm
            // falls through to the shared fallback when "message" is absent, which a switch
            // would only re-express with a `when` clause and a bare `break`.
            if (
                error.ValueKind == JsonValueKind.Object
                && GetString(error, "message") is { } objectMessage
            )
            {
                return objectMessage;
            }

            if (error.ValueKind == JsonValueKind.String)
                return error.GetString();
        }

        return GetString(root, "message");
    }
}
