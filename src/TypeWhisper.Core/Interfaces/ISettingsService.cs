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
    ///     result. Unlike <c>Save(Current with { ... })</c>, the read of the latest settings and the write happen
    ///     under the same synchronization, so two concurrent callers mutating disjoint properties cannot lose
    ///     each other's change. Implementations that add real locking to <see cref="Save" /> must apply
    ///     <paramref name="mutate" /> and persist under that same lock.
    /// </summary>
    // ReSharper disable once UnusedMemberInSuper.Global -- default interface method is a fallback for other implementers; the sole in-tree implementer overrides it.
    // ReSharper disable once UnusedMethodReturnValue.Global -- returns the applied settings for caller convenience/chaining; part of the public API contract.
    AppSettings Update(Func<AppSettings, AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        var updated = mutate(Current);
        Save(updated);
        return updated;
    }

    event Action<AppSettings>? SettingsChanged;
}
