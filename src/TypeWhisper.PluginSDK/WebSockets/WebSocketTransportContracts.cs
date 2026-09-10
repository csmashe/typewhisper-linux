using System.Net.WebSockets;

namespace TypeWhisper.PluginSDK.WebSockets;

public enum WebSocketSessionState
{
    Created,
    Connecting,
    Starting,
    Active,
    Finalizing,
    Completed,
    Faulted,
    Disposing,
    Disposed,
}

public sealed record WebSocketConnectionOptions(
    Uri Uri,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyList<string>? SubProtocols = null
);

public readonly record struct WebSocketOutboundMessage(
    ReadOnlyMemory<byte> Payload,
    WebSocketMessageType MessageType
);

public readonly record struct WebSocketReceiveChunk(
    int Count,
    WebSocketMessageType MessageType,
    bool EndOfMessage,
    WebSocketCloseStatus? CloseStatus = null,
    string? CloseDescription = null,
    bool EndOfStream = false
);

public interface IWebSocketTransportFactory
{
    IWebSocketTransport Create();
}

public interface IWebSocketTransport : IAsyncDisposable
{
    WebSocketState State { get; }

    ValueTask ConnectAsync(WebSocketConnectionOptions options, CancellationToken ct);

    ValueTask SendAsync(WebSocketOutboundMessage message, CancellationToken ct);

    ValueTask<WebSocketReceiveChunk> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);

    ValueTask CloseAsync(
        WebSocketCloseStatus status,
        string? description,
        CancellationToken ct
    );

    void Abort();
}

[Flags]
public enum WebSocketSessionSignal
{
    None = 0,
    Ready = 1,
    Terminal = 2,
}

public sealed record WebSocketInboundResult(
    IReadOnlyList<StreamingTranscriptEvent> Transcripts,
    WebSocketSessionSignal Signals = WebSocketSessionSignal.None,
    Exception? Fault = null
)
{
    public static readonly WebSocketInboundResult Empty = new([]);
}

public sealed record WebSocketFinalizePlan(
    IReadOnlyList<WebSocketOutboundMessage> Messages,
    bool AlreadyTerminal = false
);

public sealed record WebSocketReadinessPolicy(bool Required, string SignalName)
{
    public static WebSocketReadinessPolicy Immediate { get; } = new(false, "connection");

    public static WebSocketReadinessPolicy Require(string signalName) =>
        new(true, signalName);
}

public sealed record WebSocketTerminalPolicy(bool Required, string SignalName)
{
    // ReSharper disable once UnusedMember.Global -- SDK counterpart to
    // WebSocketReadinessPolicy.Immediate for adapters whose provider has no terminal
    // signal; every in-tree provider currently documents one, so nothing calls it here.
    public static WebSocketTerminalPolicy None { get; } = new(false, "completion");

    public static WebSocketTerminalPolicy Require(string signalName) =>
        new(true, signalName);
}

// ReSharper disable once ClassNeverInstantiated.Global -- SDK extension point; the pump
// runs KeepAliveLoopAsync against it, but every in-tree adapter returns KeepAlive => null.
public sealed record WebSocketKeepAlivePolicy(
    TimeSpan Interval,
    Func<WebSocketOutboundMessage> CreateMessage
);

public sealed record WebSocketClosePolicy(
    TimeSpan Timeout,
    WebSocketCloseStatus Status = WebSocketCloseStatus.NormalClosure,
    string? Description = null
)
{
    public static WebSocketClosePolicy Default { get; } =
        new(TimeSpan.FromSeconds(2));
}

public sealed record WebSocketSessionPumpOptions(
    int ReceiveBufferSize = 8192,
    int MaximumMessageSize = 1024 * 1024
);

// ReSharper disable UnusedParameter.Global -- `ct` is part of the async adapter contract;
// no in-tree adapter has an awaitable step yet, but out-of-tree ones may.

/// <summary>
///     Threading contract: <see cref="GetConnectionOptionsAsync" />, <see cref="OnConnectedAsync" />,
///     <see cref="EncodeAudioAsync" /> and <see cref="BeginFinalizeAsync" /> are serialized against
///     each other by the pump's send gate, so an adapter never sees two of them at once.
///     <see cref="HandleMessage" /> runs on the receive loop and may execute concurrently with any
///     of them. Implementations must therefore synchronize (lock, <c>volatile</c>, or interlocked)
///     every field that <see cref="HandleMessage" /> shares with a send-side callback.
/// </summary>
public interface IWebSocketSessionAdapter
{
    string ProviderName { get; }
    WebSocketReadinessPolicy Readiness { get; }
    WebSocketTerminalPolicy Terminal { get; }
    WebSocketKeepAlivePolicy? KeepAlive { get; }
    WebSocketClosePolicy ClosePolicy { get; }

    ValueTask<WebSocketConnectionOptions> GetConnectionOptionsAsync(CancellationToken ct);

    ValueTask<IReadOnlyList<WebSocketOutboundMessage>> OnConnectedAsync(
        CancellationToken ct
    );

    ValueTask<IReadOnlyList<WebSocketOutboundMessage>> EncodeAudioAsync(
        ReadOnlyMemory<byte> pcm16Audio,
        CancellationToken ct
    );

    ValueTask<WebSocketFinalizePlan> BeginFinalizeAsync(CancellationToken ct);

    WebSocketInboundResult HandleMessage(
        WebSocketMessageType type,
        ReadOnlyMemory<byte> completePayload
    );
}
// ReSharper restore UnusedParameter.Global
