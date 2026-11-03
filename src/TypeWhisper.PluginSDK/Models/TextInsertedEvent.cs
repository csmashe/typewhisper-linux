// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK.Models;

/// <summary>Raised after text is delivered by typing, paste, or clipboard fallback.</summary>
// ReSharper disable once UnusedType.Global
public sealed record TextInsertedEvent : PluginEvent
{
    /// <summary>The text that was delivered.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public required string Text { get; init; }

    /// <summary>Name of the target application selected for delivery, or null.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public string? AppName { get; init; }
}
