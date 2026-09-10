// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK.Models;

/// <summary>
///     Marks an event type as a latest-wins update. While a handler is busy, an
///     event bus may replace an older pending event of the same runtime type with
///     the latest event. An event whose handler has already started is not replaced.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface ICoalescibleEvent
{
    /// <summary>
    ///     True on a terminal frame (for example a stream's final flush). Terminal
    ///     frames are always appended: never replaced by a later same-type event,
    ///     and never used to replace a pending one.
    /// </summary>
    // ReSharper disable once UnusedMemberInSuper.Global
    bool IsTerminalFrame { get; }
}

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
