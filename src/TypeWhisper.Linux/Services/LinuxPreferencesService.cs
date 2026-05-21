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

    public static LinuxPreferences Default => new();
}

public sealed class LinuxPreferencesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
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

    public event Action<LinuxPreferences>? Changed;

    public LinuxPreferences Load()
    {
        if (File.Exists(_path))
        {
            try
            {
                var json = File.ReadAllText(_path);
                Current =
                    JsonSerializer.Deserialize<LinuxPreferences>(json, JsonOptions)
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
            File.WriteAllText(_path, JsonSerializer.Serialize(next, JsonOptions));
            Changed?.Invoke(next);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LinuxPreferencesService] Save failed: {ex.Message}");
        }
    }
}