namespace TypeWhisper.Cli.Models;

/// <summary>TCP port, optional token, and Unix socket read from <c>api-discovery.json</c>.</summary>
internal sealed record DiscoveryFile(int Port, string? Token, string? SocketPath);
