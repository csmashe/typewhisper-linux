using System.Diagnostics;
using System.Text.Json;
using TypeWhisper.Core;

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
        WriteIndented = true, PropertyNameCaseInsensitive = true
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