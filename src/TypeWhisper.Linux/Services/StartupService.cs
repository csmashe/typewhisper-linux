using System.Diagnostics;
using System.IO;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// XDG Autostart integration. Writes ~/.config/autostart/typewhisper.desktop
/// to enable, deletes it to disable. Freedesktop-compliant, works across
/// GNOME / KDE / XFCE / most other desktops.
/// </summary>
public static class StartupService
{
    private const string DesktopFileName = "typewhisper.desktop";

    private static string AutostartDir
    {
        get
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configHome))
                configHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config");
            return Path.Combine(configHome, "autostart");
        }
    }

    private static string DesktopFilePath => Path.Combine(AutostartDir, DesktopFileName);

    public static bool IsEnabled => File.Exists(DesktopFilePath);

    public static void Enable()
    {
        Directory.CreateDirectory(AutostartDir);
        var execPath = Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot determine executable path.");

        // Prefer an absolute path to the bundled PNG so the entry works even
        // when no icon theme on the system defines "typewhisper". Falls back
        // to the theme name if the PNG is missing for any reason.
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "typewhisper-128.png");
        if (!File.Exists(iconPath))
            iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "typewhisper-64.png");
        if (!File.Exists(iconPath))
            iconPath = "typewhisper";

        var content = $"""
            [Desktop Entry]
            Type=Application
            Name=TypeWhisper
            GenericName=Voice-to-text dictation
            Exec="{execPath}" --minimized
            Icon={iconPath}
            Terminal=false
            Categories=Utility;Accessibility;
            X-GNOME-Autostart-enabled=true
            """;
        File.WriteAllText(DesktopFilePath, content);
    }

    public static void Disable()
    {
        if (File.Exists(DesktopFilePath))
            File.Delete(DesktopFilePath);
    }
}
