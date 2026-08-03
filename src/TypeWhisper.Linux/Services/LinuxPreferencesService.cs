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

    private readonly Action<string, string> _atomicWrite;
    private readonly Lock _gate = new();
    private readonly string _path;

    public LinuxPreferencesService()
        : this(Path.Join(TypeWhisperEnvironment.BasePath, "linux-preferences.json")) { }

    internal LinuxPreferencesService(
        string path,
        Action<string, string>? atomicWrite = null
    )
    {
        _path = path;
        _atomicWrite = atomicWrite ?? AtomicFileWrite.WriteAllText;
        Load();
    }

    public LinuxPreferences Current { get; private set; } = LinuxPreferences.Default;

    // ReSharper disable once UnusedMethodReturnValue.Global -- returns Current so callers that reload on demand get the fresh value.
    // ReSharper disable once MemberCanBePrivate.Global -- public reload entry point mirroring ISettingsService.Load(); only the constructor calls it in-tree.
    public LinuxPreferences Load()
    {
        // Serialize with Save/Update so a reload can't clobber (or be clobbered by) a
        // concurrent write's Current assignment.
        lock (_gate)
        {
            return LoadLocked();
        }
    }

    private LinuxPreferences LoadLocked()
    {
        if (!File.Exists(_path))
        {
            return Current;
        }

        try
        {
            var json = File.ReadAllText(_path);
            Current =
                JsonSerializer.Deserialize<LinuxPreferences>(json, s_jsonOptions)
                ?? LinuxPreferences.Default;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LinuxPreferencesService] Load failed: {ex.Message}");
            Current = LinuxPreferences.Default;
        }

        return Current;
    }

    public void Save(LinuxPreferences next)
    {
        lock (_gate)
        {
            SaveLocked(next);
        }
    }

    public LinuxPreferences Update(Func<LinuxPreferences, LinuxPreferences> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            var updated = mutate(Current);
            SaveLocked(updated);
            return updated;
        }
    }

    private void SaveLocked(LinuxPreferences next)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(next, s_jsonOptions);
            _atomicWrite(_path, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LinuxPreferencesService] Save failed: {ex.Message}");
            throw;
        }

        Current = next;
        Changed?.Invoke(next);
    }

    // ReSharper disable once EventNeverSubscribedTo.Global -- public API; raised on preference changes for external/future subscribers.
    public event Action<LinuxPreferences>? Changed;
}
