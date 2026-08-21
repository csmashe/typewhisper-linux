using System.Text.Json.Serialization;
using TypeWhisper.PluginSDK.Models;

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
    // ReSharper disable once UnusedMember.Global  part of the plugin-registry JSON schema (RegistryPlugin deserialized in PluginRegistryService line 90); data-carrier field
    public PluginCategory[]? Categories
    {
        get => field ?? MapLegacyCategory(Category);
        init;
    }
    // ReSharper disable once UnusedMember.Global  part of the plugin-registry JSON schema (RegistryPlugin deserialized in PluginRegistryService line 90); data-carrier field
    public PluginNetworkAccess? NetworkAccess
    {
        get => field ?? MapLegacyNetworkAccess(IsLocal);
        init;
    }
    // ReSharper disable once UnusedMember.Global  part of the plugin-registry JSON schema (RegistryPlugin deserialized in PluginRegistryService line 90); data-carrier field
    public bool? IsLocal { get; init; }
    public long Size { get; init; }
    public string DownloadUrl { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string Platform { get; init; } = "";
    public string Rid { get; init; } = "";
    public string SdkAbi { get; init; } = "";
    // ReSharper disable once UnusedAutoPropertyAccessor.Global  part of the plugin-registry JSON schema (record deserialized in PluginRegistryService); data-carrier field
    public DateTimeOffset Timestamp { get; init; }
    // ReSharper disable once UnusedMember.Global  part of the plugin-registry JSON schema (RegistryPlugin deserialized in PluginRegistryService line 90); data-carrier field
    public string? IconSystemName { get; init; }
    // ReSharper disable once UnusedMember.Global  part of the plugin-registry JSON schema (RegistryPlugin deserialized in PluginRegistryService line 90); data-carrier field
    public bool RequiresApiKey { get; init; }
    // ReSharper disable once UnusedMember.Global  part of the plugin-registry JSON schema (RegistryPlugin deserialized in PluginRegistryService line 90); data-carrier field
    public Dictionary<string, string>? Descriptions { get; init; }

    private static PluginCategory[]? MapLegacyCategory(string? category)
    {
        var mapped = category?.Trim().ToLowerInvariant() switch
        {
            "transcription" => PluginCategory.Transcription,
            "llm" or "prompt" => PluginCategory.Llm,
            "tts" or "text-to-speech" => PluginCategory.Tts,
            "postprocessing"
                or "post-processing"
                or "postprocessor"
                or "post-processor" => PluginCategory.PostProcessing,
            "action" => PluginCategory.Action,
            "memory" => PluginCategory.Memory,
            "integration" => PluginCategory.Integration,
            "utility" => PluginCategory.Utility,
            _ => (PluginCategory?)null,
        };

        return mapped is { } value ? [value] : null;
    }

    private static PluginNetworkAccess? MapLegacyNetworkAccess(bool? isLocal)
    {
        return isLocal switch
        {
            true => PluginNetworkAccess.Local,
            false => PluginNetworkAccess.Network,
            null => null,
        };
    }
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
