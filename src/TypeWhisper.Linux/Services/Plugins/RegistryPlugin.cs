using System.Text.Json.Serialization;

namespace TypeWhisper.Linux.Services.Plugins;

public sealed record RegistryPlugin
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "0.0.0";
    public string? MinHostVersion { get; init; }
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Category { get; init; }
    public long Size { get; init; }
    public string DownloadUrl { get; init; } = "";
    public string? IconSystemName { get; init; }
    public bool RequiresApiKey { get; init; }
    public Dictionary<string, string>? Descriptions { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginInstallState
{
    NotInstalled,
    Installed,
    UpdateAvailable,
    Bundled
}