using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TypeWhisper.Core;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// Linux-only UX preferences, stored separate from Core's AppSettings so the
/// fork doesn't have to mutate upstream's data model. Tiny on purpose — holds
/// only the toggles that are specific to Linux-desktop behavior (tray
/// handling, compositor-specific hints, etc.).
/// </summary>
public sealed record LinuxPreferences
{
    /// <summary>
    /// When true, clicking the window's close (X) button hides to the
    /// system tray and keeps the process alive. When false (default),
    /// the X button fully quits the app — safer on desktops without
    /// a working SNI tray.
    /// </summary>
    public bool CloseToTray { get; init; }

    public static LinuxPreferences Default => new();
}

public sealed class LinuxPreferencesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private LinuxPreferences _current = LinuxPreferences.Default;

    public LinuxPreferences Current => _current;
    public event Action<LinuxPreferences>? Changed;

    public LinuxPreferencesService()
    {
        _path = Path.Combine(TypeWhisperEnvironment.BasePath, "linux-preferences.json");
        Load();
    }

    public LinuxPreferences Load()
    {
        if (File.Exists(_path))
        {
            try
            {
                var json = File.ReadAllText(_path);
                _current = JsonSerializer.Deserialize<LinuxPreferences>(json, JsonOptions)
                    ?? LinuxPreferences.Default;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LinuxPreferencesService] Load failed: {ex.Message}");
                _current = LinuxPreferences.Default;
            }
        }
        return _current;
    }

    public void Save(LinuxPreferences next)
    {
        _current = next;
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
