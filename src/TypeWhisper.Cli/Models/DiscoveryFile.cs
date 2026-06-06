namespace TypeWhisper.Cli.Models;

/// <summary>Port and optional token read from the app's <c>api-discovery.json</c>.</summary>
internal sealed record DiscoveryFile(int Port, string? Token);
