// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
///     Publish/subscribe event bus for plugin communication. Each subscription has
///     an independent background delivery queue: its handler is invoked in accepted
///     publish order and never re-entered, while other subscriptions continue
///     independently. A handler exception is isolated to that event and does not stop
///     later delivery.
/// </summary>
/// <remarks>
///     Pending non-terminal <see cref="ICoalescibleEvent" /> instances use latest-wins
///     delivery. A newer non-terminal event replaces an older pending non-terminal
///     event of the same runtime type and takes the newer event's position in the
///     queue. A terminal frame (<see cref="ICoalescibleEvent.IsTerminalFrame" />) is
///     always appended: it never replaces a pending event, and a later same-type event
///     never replaces it, so stream endpoints are delivered with full fidelity.
///     Non-coalescible events are never dropped. Bursts stay bounded because only
///     non-terminal frames coalesce and terminal frames are finite per stream;
///     non-coalescible producers remain responsible for limiting their publish rate.
///
///     Disposing a subscription discards its queued, undelivered events; a handler
///     already in flight is allowed to complete. On host shutdown, the owning bus
///     abandons queued events and stops its workers, waiting for in-flight handlers up
///     to a bounded deadline; handlers still running past the deadline are abandoned so
///     disposal always completes.
/// </remarks>
// ReSharper disable once UnusedType.Global
public interface IPluginEventBus
{
    /// <summary>
    ///     Enqueues an event for all current subscribers of type
    ///     <typeparamref name="T" /> and returns without waiting for handlers.
    /// </summary>
    // ReSharper disable once UnusedMemberInSuper.Global
    void Publish<T>(T pluginEvent)
        where T : PluginEvent;

    /// <summary>
    ///     Subscribes to events of type <typeparamref name="T" />.
    ///     Dispose the returned handle to unsubscribe, discard queued events, and
    ///     allow an in-flight handler invocation to complete.
    /// </summary>
    // ReSharper disable once UnusedMemberInSuper.Global
    IDisposable Subscribe<T>(Func<T, Task> handler)
        where T : PluginEvent;
}
