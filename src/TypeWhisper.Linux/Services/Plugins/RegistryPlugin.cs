using System.Text.Json.Serialization;

namespace TypeWhisper.Linux.Services.Plugins;

public sealed record RegistryPlugin
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "0.0.0";
    // ReSharper disable once UnusedAutoPropertyAccessor.Global  init written by the reflection JSON deserializer (PluginRegistryService.Deserialize<List<RegistryPlugin>>)
    public string? MinHostVersion { get; init; }

    // ReSharper disable once UnusedAutoPropertyAccessor.Global  part of the plugin-registry JSON schema (record deserialized in PluginRegistryService); data-carrier field
    public string Author { get; init; } = "";

    // ReSharper disable once UnusedAutoPropertyAccessor.Global  part of the plugin-registry JSON schema (record deserialized in PluginRegistryService); data-carrier field
    public string Description { get; init; } = "";
    // ReSharper disable once UnusedMember.Global  part of the plugin-registry JSON schema (RegistryPlugin deserialized in PluginRegistryService line 90); data-carrier field
    public string? Category { get; init; }
    public long Size { get; init; }
    public string DownloadUrl { get; init; } = "";
    // ReSharper disable once UnusedMember.Global  part of the plugin-registry JSON schema (RegistryPlugin deserialized in PluginRegistryService line 90); data-carrier field
    public string? IconSystemName { get; init; }
    // ReSharper disable once UnusedMember.Global  part of the plugin-registry JSON schema (RegistryPlugin deserialized in PluginRegistryService line 90); data-carrier field
    public bool RequiresApiKey { get; init; }
    // ReSharper disable once UnusedMember.Global  part of the plugin-registry JSON schema (RegistryPlugin deserialized in PluginRegistryService line 90); data-carrier field
    public Dictionary<string, string>? Descriptions { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginInstallState
{
    NotInstalled,
    Installed,
    UpdateAvailable,

    // ReSharper disable once UnusedMember.Global  member of the JsonStringEnumConverter-serialized install-state vocabulary (PluginInstallState); kept for completeness, not currently produced in-tree
    Bundled,
}