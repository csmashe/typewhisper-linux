using System.Diagnostics;
using System.Text.Json;
using TypeWhisper.Core;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Linux-only UX preferences stored separately from Core's AppSettings to
///     avoid mutating the upstream data model. Contains only Linux-desktop toggles
///     (tray handling, compositor hints, etc.).
/// </summary>
public sealed record LinuxPreferences
{
    /// <summary>
    ///     When true, the close (X) button hides to the tray rather than quitting.
    ///     Defaults to false — safer on desktops without a working SNI tray.
    /// </summary>
    public bool CloseToTray { get; init; }

    /// <summary>
    ///     When true (default), checks GitHub for a newer release once per day
    ///     on startup. The manual "Check for Updates" button works regardless.
    /// </summary>
    public bool CheckForUpdatesOnStartup { get; init; } = true;

    /// <summary>
    ///     UTC timestamp of the last successful update check. Used to rate-limit
    ///     the startup check to once per day. Null until the first check runs.
    /// </summary>
    public DateTime? LastUpdateCheckUtc { get; init; }

    /// <summary>
    ///     Latest release version seen by the most recent check (e.g. "0.6.0").
    ///     Allows re-surfacing a known update without a network call.
    /// </summary>
    public string? LastKnownLatestVersion { get; init; }

    /// <summary>
    ///     Release page URL for <see cref="LastKnownLatestVersion" />, cached to
    ///     avoid re-querying — GitHub's <c>/latest</c> endpoint can resolve to a
    ///     republished older tag.
    /// </summary>
    public string? LastKnownLatestUrl { get; init; }

    /// <summary>Version dismissed from the update banner; banner reappears when
    ///     a newer version is published.</summary>
    public string? DismissedUpdateVersion { get; init; }

    public static LinuxPreferences Default => new();
}

public sealed class LinuxPreferencesService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
    };

    private readonly AtomicJsonStore<LinuxPreferences> _store;

    public LinuxPreferencesService()
        : this(Path.Join(TypeWhisperEnvironment.BasePath, "linux-preferences.json")) { }

    internal LinuxPreferencesService(
        string path,
        Action<string, string>? atomicWrite = null
    )
    {
        var options = new AtomicJsonStoreOptions<LinuxPreferences>
        {
            JsonOptions = s_jsonOptions,
            CorruptFilePolicy = AtomicJsonCorruptFilePolicy.PreserveAndReset,
            Diagnostic = diagnostic =>
                Debug.WriteLine(
                    $"[LinuxPreferencesService] {diagnostic.Kind} at "
                    + $"'{diagnostic.Path}': {diagnostic.Exception?.Message}"
                ),
        };
        _store = new AtomicJsonStore<LinuxPreferences>(
            path,
            () => LinuxPreferences.Default,
            options,
            atomicWrite ?? AtomicFileWrite.WriteAllText
        );
        _ = _store.Current;
    }

    public LinuxPreferences Current => _store.Current;

    // ReSharper disable once UnusedMethodReturnValue.Global -- returns Current so callers that reload on demand get the fresh value.
    // ReSharper disable once MemberCanBePrivate.Global -- public reload entry point mirroring ISettingsService.Load().
    // ReSharper disable once UnusedMember.Global -- the constructor primes the store via Current instead, so nothing calls this in-tree today.
    public LinuxPreferences Load()
    {
        return _store.Reload();
    }

    public void Save(LinuxPreferences next)
    {
        Commit(_ => next);
    }

    public LinuxPreferences Update(Func<LinuxPreferences, LinuxPreferences> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        return Commit(mutate);
    }

    private LinuxPreferences Commit(
        Func<LinuxPreferences, LinuxPreferences> update
    )
    {
        var changed = false;
        LinuxPreferences committed;
        try
        {
            committed = _store.Update(
                current =>
                {
                    var next = update(current);
                    changed = !EqualityComparer<LinuxPreferences>.Default.Equals(next, current);
                    return next;
                }
            );
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LinuxPreferencesService] Save failed: {ex.Message}");
            throw;
        }

        if (changed)
        {
            Changed?.Invoke(committed);
        }

        return committed;
    }

    // ReSharper disable once EventNeverSubscribedTo.Global -- public API; raised on preference changes for external/future subscribers.
    public event Action<LinuxPreferences>? Changed;
}
