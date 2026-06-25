// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
///     Publish/subscribe event bus for plugin communication. Handlers are invoked on
///     background threads, so subscribers must not assume UI-thread affinity and must
///     keep work short — a slow handler blocks delivery to the rest of the chain.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface IPluginEventBus
{
    /// <summary>Publishes an event to all subscribers of type <typeparamref name="T" />.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    // ReSharper disable once UnusedParameter.Global
    void Publish<T>(T pluginEvent)
        where T : PluginEvent;

    /// <summary>
    ///     Subscribes to events of type <typeparamref name="T" />.
    ///     Dispose the returned handle to unsubscribe.
    /// </summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    // ReSharper disable once UnusedParameter.Global
    IDisposable Subscribe<T>(Func<T, Task> handler)
        where T : PluginEvent;
}
