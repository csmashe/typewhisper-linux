namespace TypeWhisper.Cli.Models;

/// <summary>TCP port, optional token, Unix socket, and protocol version read from <c>api-discovery.json</c>.</summary>
internal sealed record DiscoveryFile(
    int Port,
    string? Token,
    string? SocketPath,
    int? Version = null
);
