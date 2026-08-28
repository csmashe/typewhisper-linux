using System.Net.WebSockets;

namespace TypeWhisper.PluginSDK.WebSockets;

public sealed class ClientWebSocketTransportFactory : IWebSocketTransportFactory
{
    public static ClientWebSocketTransportFactory Instance { get; } = new();

    private ClientWebSocketTransportFactory() { }

    public IWebSocketTransport Create() => new ClientWebSocketTransport();
}

public sealed class ClientWebSocketTransport : IWebSocketTransport
{
    private readonly WebSocket _socket;
    private readonly ClientWebSocket? _clientSocket;
    private int _disposed;

    public ClientWebSocketTransport()
    {
        _clientSocket = new ClientWebSocket();
        _socket = _clientSocket;
    }

    /// <summary>
    ///     Wraps an already-open socket. The transport takes ownership: <see cref="DisposeAsync" />
    ///     disposes <paramref name="connectedSocket" />, so the caller must not dispose it too.
    /// </summary>
    public ClientWebSocketTransport(WebSocket connectedSocket)
    {
        ArgumentNullException.ThrowIfNull(connectedSocket);
        _socket = connectedSocket;
    }

    public WebSocketState State => _socket.State;

    public async ValueTask ConnectAsync(
        WebSocketConnectionOptions options,
        CancellationToken ct
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (_clientSocket is null)
        {
            if (_socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException(
                    "A connected WebSocket transport must already be open."
                );
            }

            return;
        }

        if (options.Headers is not null)
        {
            foreach (var (name, value) in options.Headers)
                _clientSocket.Options.SetRequestHeader(name, value);
        }

        if (options.SubProtocols is not null)
        {
            foreach (var subProtocol in options.SubProtocols)
                _clientSocket.Options.AddSubProtocol(subProtocol);
        }

        await _clientSocket.ConnectAsync(options.Uri, ct);
    }

    public async ValueTask SendAsync(
        WebSocketOutboundMessage message,
        CancellationToken ct
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _socket.SendAsync(message.Payload, message.MessageType, true, ct);
    }

    public async ValueTask<WebSocketReceiveChunk> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken ct
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var result = await _socket.ReceiveAsync(buffer, ct);
        return new WebSocketReceiveChunk(
            result.Count,
            result.MessageType,
            result.EndOfMessage,
            result.MessageType == WebSocketMessageType.Close ? _socket.CloseStatus : null,
            result.MessageType == WebSocketMessageType.Close
                ? _socket.CloseStatusDescription
                : null
        );
    }

    public async ValueTask CloseAsync(
        WebSocketCloseStatus status,
        string? description,
        CancellationToken ct
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _socket.CloseAsync(status, description, ct);
    }

    // Silent no-op rather than the throw the other members use: the pump aborts defensively
    // during teardown, and a disposed socket is already aborted.
    public void Abort()
    {
        if (Volatile.Read(ref _disposed) == 0)
            _socket.Abort();
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _socket.Dispose();

        return ValueTask.CompletedTask;
    }
}
