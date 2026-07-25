using System.Diagnostics;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.Linux.Services;

public sealed record StartupOperationResult(bool Success, bool IsEnabled, string StatusText);

/// <summary>
///     XDG Autostart integration. Manages ~/.config/autostart/typewhisper.desktop
///     only when TypeWhisper can prove ownership from its contents.
/// </summary>
public static class StartupService
{
    private const string DesktopFileName = "typewhisper.desktop";
    private const string ManagedLine = "X-TypeWhisper-Managed=true";

    private static string AutostartDir
    {
        get
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configHome))
            {
                configHome = Path.Join(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config"
                );
            }

            return Path.Join(configHome, "autostart");
        }
    }

    private static string DesktopFilePath => Path.Join(AutostartDir, DesktopFileName);

    public static bool IsEnabled =>
        File.Exists(DesktopFilePath) && IsOwnedByTypeWhisper(DesktopFilePath);

    public static StartupOperationResult Enable()
    {
        Directory.CreateDirectory(AutostartDir);
        var execPath = ResolveExecutablePath();
        var iconPath = ResolveIconPath();
        var content = BuildDesktopFile(execPath, iconPath, includeManagedMarker: true);

        if (File.Exists(DesktopFilePath) && !IsOwnedByTypeWhisper(DesktopFilePath))
        {
            return RefusedResult();
        }

        File.WriteAllText(DesktopFilePath, content);
        return SuccessResult(isEnabled: true);
    }

    public static StartupOperationResult Disable()
    {
        if (!File.Exists(DesktopFilePath))
        {
            return SuccessResult(isEnabled: false);
        }

        if (!IsOwnedByTypeWhisper(DesktopFilePath))
        {
            return RefusedResult();
        }

        File.Delete(DesktopFilePath);
        return SuccessResult(isEnabled: false);
    }

    internal static string BuildDesktopFile(
        string execPath,
        string iconPath,
        bool includeManagedMarker
    )
    {
        var content =
            "[Desktop Entry]\n"
            + "Type=Application\n"
            + "Name=TypeWhisper\n"
            + "GenericName=Voice-to-text dictation\n"
            + $"Exec=\"{execPath}\" --minimized\n"
            + $"Icon={iconPath}\n"
            + "Terminal=false\n"
            + "Categories=Utility;Accessibility;\n"
            + "X-GNOME-Autostart-enabled=true";
        return includeManagedMarker ? $"{content}\n{ManagedLine}" : content;
    }

    private static bool IsOwnedByTypeWhisper(string target)
    {
        string contents;
        try
        {
            contents = File.ReadAllText(target);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        var lines = contents.Split('\n').Select(line => line.TrimEnd('\r'));
        if (lines.Contains(ManagedLine, StringComparer.Ordinal))
        {
            return true;
        }

        try
        {
            var legacyContent = BuildDesktopFile(
                ResolveExecutablePath(),
                ResolveIconPath(),
                includeManagedMarker: false
            );
            return string.Equals(contents, legacyContent, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static string ResolveExecutablePath()
    {
        return Process.GetCurrentProcess().MainModule?.FileName
               ?? throw new InvalidOperationException("Cannot determine executable path.");
    }

    private static string ResolveIconPath()
    {
        // Prefer an absolute path to the bundled PNG so the entry works even
        // when no icon theme on the system defines "typewhisper". Falls back
        // to the theme name if the PNG is missing for any reason.
        var iconPath = Path.Join(AppContext.BaseDirectory, "Resources", "typewhisper-128.png");
        if (!File.Exists(iconPath))
        {
            iconPath = Path.Join(AppContext.BaseDirectory, "Resources", "typewhisper-64.png");
        }

        return File.Exists(iconPath) ? iconPath : "typewhisper";
    }

    private static StartupOperationResult SuccessResult(bool isEnabled)
    {
        return new StartupOperationResult(
            true,
            isEnabled,
            Loc.Instance["General.AutostartHint"]
        );
    }

    private static StartupOperationResult RefusedResult()
    {
        return new StartupOperationResult(
            false,
            false,
            Loc.Instance.GetString("General.AutostartEntryPreserved", DesktopFilePath)
        );
    }
}
