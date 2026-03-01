using System.Text.Json.Serialization;

namespace TypeWhisper.Windows.Services.Plugins;

/// <summary>
/// A plugin entry from the remote plugin registry.
/// </summary>
public sealed record RegistryPlugin(
    string Id,
    string Name,
    string Version,
    string? MinHostVersion,
    string Author,
    string Description,
    string? Category,
    long Size,
    string DownloadUrl,
    string? IconSystemName,
    bool RequiresApiKey,
    Dictionary<string, string>? Descriptions);

/// <summary>
/// Installation state of a registry plugin relative to the local system.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginInstallState
{
    NotInstalled,
    Installed,
    UpdateAvailable,
    Bundled
}
