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

    private readonly Task<WebSocketSessionPump> _pumpTask;
    private readonly Lock _subscriberGate = new();
    private Action<StreamingTranscriptEvent>? _pendingSubscribers;
    private bool _subscribersAttached;

    private XaiStreamingSession(Task<WebSocketSessionPump> pumpTask)
    {
        _pumpTask = pumpTask;
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
        CancellationToken ct
    )
    {
        // The readiness deadline covers only the wait for transcript.created. The
        // handshake runs on the caller's token so a slow DNS/TLS connect is not
        // misreported as a missing readiness frame.
        var adapter = new XaiWebSocketAdapter(apiKey, language);
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

    internal static XaiStreamingSession CreateConnectedSessionForTests(WebSocket ws)
    {
        if (ws.State != WebSocketState.Open)
            throw new InvalidOperationException("The test WebSocket must already be open.");

        var pumpTask = WebSocketSessionPump.StartConnectedAsync(
            new XaiWebSocketAdapter("", null),
            new ClientWebSocketTransport(ws),
            CancellationToken.None
        );
        return new XaiStreamingSession(pumpTask);
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
            new XaiWebSocketAdapter("", null),
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

    public static Uri BuildStreamingUri(string? language, bool interimResults)
    {
        var query = new List<string>
        {
            "sample_rate=16000",
            "encoding=pcm",
            $"interim_results={(interimResults ? "true" : "false")}",
        };

        if (
            !string.IsNullOrWhiteSpace(language)
            && !language.Equals("auto", StringComparison.OrdinalIgnoreCase)
        )
        {
            query.Add($"language={Uri.EscapeDataString(language)}");
        }

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
            var pump = await _pumpTask;
            await pump.DisposeAsync();
        }
        catch
        {
            _ = _pumpTask.Exception;
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
    string? language
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
                    interimResults: true
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

internal sealed class XaiTranscriptCollector
{
    private readonly List<string> _finals = [];
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
                : string.Join(" ", _finals).Trim();

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
            return new StreamingTranscriptEvent(text, IsFinal: false);

        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (speechFinal && _finals.Count > 0)
        {
            var joined = string.Join(" ", _finals);
            if (
                text.StartsWith(joined, StringComparison.Ordinal)
                && (
                    text.Length == joined.Length
                    || text[joined.Length] == ' '
                )
            )
            {
                return null;
            }
        }

        _finals.Add(text);
        return new StreamingTranscriptEvent(text, IsFinal: true);
    }

    private StreamingTranscriptEvent? ApplyDoneEvent(JsonElement root)
    {
        var text = GetString(root, "text")?.Trim() ?? "";
        RememberMetadata(root);
        IsTerminal = true;

        if (string.IsNullOrWhiteSpace(text))
            return null;

        _doneText = text;
        if (_finals.Count == 0)
            return new StreamingTranscriptEvent(text, IsFinal: true);

        var joined = string.Join(" ", _finals);
        if (text.Equals(joined, StringComparison.Ordinal))
            return null;

        // ReSharper disable once InvertIf -- the positive form names the case being handled
        // (done text extends the accumulated finals); the De Morgan negation does not.
        if (
            text.StartsWith(joined, StringComparison.Ordinal)
            && text.Length > joined.Length
            && text[joined.Length] == ' '
        )
        {
            var delta = text[(joined.Length + 1)..].Trim();
            if (delta.Length == 0)
                return null;
            _finals.Add(delta);
            return new StreamingTranscriptEvent(delta, IsFinal: true);
        }

        return null;
    }

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
