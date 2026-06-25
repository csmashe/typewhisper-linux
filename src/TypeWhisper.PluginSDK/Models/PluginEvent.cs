// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK.Models;

/// <summary>
///     Base class for all plugin events published via the event bus.
/// </summary>
// ReSharper disable once UnusedType.Global
public abstract record PluginEvent
{
    /// <summary>UTC timestamp when the event was created.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Global
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
