using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
///     Loads, persists, and broadcasts changes to the application settings.
/// </summary>
public interface ISettingsService
{
    /// <summary>The current in-memory settings (as last loaded or saved).</summary>
    AppSettings Current { get; }

    /// <summary>Reads settings from disk into <see cref="Current" /> and returns them.</summary>
    // ReSharper disable once UnusedMethodReturnValue.Global -- callers today only want the reload
    // side effect, but returning the loaded settings is part of the published contract.
    AppSettings Load();

    /// <summary>Persists <paramref name="settings" />, updates <see cref="Current" />, and raises <see cref="SettingsChanged" />.</summary>
    void Save(AppSettings settings);

    /// <summary>
    ///     Re-reads settings from disk and writes them straight back, so <see cref="Current" /> and
    ///     <see cref="SettingsChanged" /> reflect files replaced underneath the app (e.g. a restored
    ///     backup). Implementations that add real locking to <see cref="Save" /> must perform the
    ///     read and the write under that same lock, so a concurrent writer cannot be clobbered by a
    ///     stale snapshot; the default body below does not synchronize and is only a fallback for
    ///     implementers without locking (test doubles).
    /// </summary>
    // ReSharper disable once UnusedMemberInSuper.Global -- default interface method is a fallback for other implementers; the sole in-tree implementer overrides it.
    // ReSharper disable once UnusedMember.Global -- same reason: callers reach Reload through the implementer, not this declaration.
    // ReSharper disable once UnusedMethodReturnValue.Global -- returns the reloaded settings for caller convenience; part of the public API contract.
    // ReSharper disable once UnusedMember.Global -- nothing in-tree calls Reload today; it stays part of the ISettingsService contract for external callers.
    AppSettings Reload()
    {
        var loaded = Load();
        Save(loaded);
        return loaded;
    }

    /// <summary>
    ///     Atomically applies <paramref name="mutate" /> to the latest <see cref="Current" /> and persists the
    ///     result. The read of the latest settings and the write must happen under the same synchronization as
    ///     <see cref="Save" />, so two concurrent callers mutating disjoint properties cannot lose each other's
    ///     change — which is why this is abstract rather than a default <c>mutate(Current)</c> + <c>Save</c>
    ///     (that fallback would read and write in separate steps and silently drop a concurrent update).
    /// </summary>
    // ReSharper disable once UnusedMethodReturnValue.Global -- returns the applied settings for caller convenience/chaining; part of the public API contract.
    // ReSharper disable once UnusedMemberInSuper.Global -- callers hold concrete types today, but the interface member is what binds implementations to the atomicity contract above.
    AppSettings Update(Func<AppSettings, AppSettings> mutate);

    event Action<AppSettings>? SettingsChanged;
}
