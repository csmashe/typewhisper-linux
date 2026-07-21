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
    AppSettings Load();

    /// <summary>Persists <paramref name="settings" />, updates <see cref="Current" />, and raises <see cref="SettingsChanged" />.</summary>
    void Save(AppSettings settings);

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
