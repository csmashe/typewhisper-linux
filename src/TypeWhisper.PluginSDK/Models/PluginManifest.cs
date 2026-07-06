// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK.Models;

/// <summary>
///     Describes a plugin's metadata, loaded from plugin.json in the plugin directory.
/// </summary>
// ReSharper disable once UnusedType.Global
public sealed record PluginManifest
{
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

    /// <summary>Plugin category for UI grouping (e.g. "transcription", "llm", "memory", "action", "utility").</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public string? Category { get; init; }

    /// <summary>Whether this is a local (on-device) or cloud-based plugin.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public bool IsLocal { get; init; }

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
}
