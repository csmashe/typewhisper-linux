using System.Diagnostics;
using System.Text.Json;
using TypeWhisper.Core;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Linux-only UX preferences, stored separate from Core's AppSettings so the
///     fork doesn't have to mutate upstream's data model. Tiny on purpose — holds
///     only the toggles that are specific to Linux-desktop behavior (tray
///     handling, compositor-specific hints, etc.).
/// </summary>
public sealed record LinuxPreferences
{
    /// <summary>
    ///     When true, clicking the window's close (X) button hides the window
    ///     to the tray icon — it leaves the dock/taskbar and the process keeps
    ///     running, with the tray menu as the only entry point. When false
    ///     (default), the X button fully quits the app — safer on desktops
    ///     without a working SNI tray.
    /// </summary>
    public bool CloseToTray { get; init; }

    /// <summary>
    ///     When true (default), the app checks GitHub for a newer release once
    ///     per day on startup and surfaces a non-obtrusive banner if one is
    ///     found. The manual "Check for Updates" button in About works
    ///     regardless of this setting.
    /// </summary>
    public bool CheckForUpdatesOnStartup { get; init; } = true;

    /// <summary>
    ///     UTC timestamp of the last successful update check. Used to rate-limit
    ///     the startup check to once per day. Null until the first check runs.
    /// </summary>
    public DateTime? LastUpdateCheckUtc { get; init; }

    /// <summary>
    ///     The latest release version seen by the most recent successful check
    ///     (e.g. "0.6.0"). Lets the startup path re-surface a known update
    ///     without hitting the network when a check isn't yet due.
    /// </summary>
    public string? LastKnownLatestVersion { get; init; }

    /// <summary>
    ///     The release page URL for <see cref="LastKnownLatestVersion"/>, cached
    ///     so the startup path can point Download at the correct release without
    ///     re-querying (and without falling back to GitHub's /latest endpoint,
    ///     which can resolve to a republished older tag).
    /// </summary>
    public string? LastKnownLatestUrl { get; init; }

    /// <summary>
    ///     The version the user dismissed from the update banner. The banner
    ///     stays hidden for this exact version but reappears when a newer one
    ///     is published.
    /// </summary>
    public string? DismissedUpdateVersion { get; init; }

    public static LinuxPreferences Default => new();
}

public sealed class LinuxPreferencesService
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;

    public LinuxPreferencesService()
    {
        _path = Path.Combine(TypeWhisperEnvironment.BasePath, "linux-preferences.json");
        Load();
    }

    public LinuxPreferences Current { get; private set; } = LinuxPreferences.Default;

    public LinuxPreferences Load()
    {
        if (File.Exists(_path))
        {
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
        }

        return Current;
    }

    public void Save(LinuxPreferences next)
    {
        Current = next;
        try
        {
            Directory.CreateDirectory(TypeWhisperEnvironment.BasePath);
            File.WriteAllText(_path, JsonSerializer.Serialize(next, s_jsonOptions));
            Changed?.Invoke(next);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LinuxPreferencesService] Save failed: {ex.Message}");
        }
    }

    public event Action<LinuxPreferences>? Changed;
}