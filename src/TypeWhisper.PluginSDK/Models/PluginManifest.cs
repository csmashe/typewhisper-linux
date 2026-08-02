// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK.Models;

/// <summary>
///     Describes a plugin's metadata, loaded from <c>manifest.json</c> in the plugin
///     directory. The required filename is exposed by <see cref="FileName" />.
/// </summary>
// ReSharper disable once UnusedType.Global
public sealed record PluginManifest
{
    /// <summary>The required plugin manifest filename.</summary>
    public const string FileName = "manifest.json";

    /// <summary>Unique plugin identifier (e.g. "com.typewhisper.openai").</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public required string Id { get; init; }

    /// <summary>Human-readable plugin name.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public required string Name { get; init; }

    /// <summary>Semantic version (e.g. "1.0.0").</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public required string Version { get; init; }

    /// <summary>Minimum host version required, or null for any.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public string? MinHostVersion { get; init; }

    /// <summary>Plugin author name.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public string? Author { get; init; }

    /// <summary>Short description of the plugin.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public string? Description { get; init; }

    /// <summary>
    ///     Legacy singular category, superseded by <see cref="Categories" />. Recognized
    ///     values map into <see cref="Categories" /> when the plural field is absent.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public string? Category { get; init; }

    /// <summary>
    ///     Capability categories used for host grouping and routing. Bundled manifests
    ///     must declare a non-empty set.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public PluginCategory[]? Categories
    {
        get => field ?? MapLegacyCategory(Category);
        init;
    }

    /// <summary>
    ///     Declares whether plugin operations remain local or can use the network.
    ///     Bundled manifests must declare this field.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public PluginNetworkAccess? NetworkAccess { get; init; }

    /// <summary>
    ///     Obsolete legacy locality flag retained for external-manifest compatibility.
    ///     New manifests must use <see cref="NetworkAccess" />. Null means omitted.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public bool? IsLocal { get; init; }

    /// <summary>DLL file name containing the plugin type (e.g. "MyPlugin.dll").</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public required string AssemblyName { get; init; }

    /// <summary>Fully-qualified class name implementing ITypeWhisperPlugin.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public required string PluginClass { get; init; }

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
}
