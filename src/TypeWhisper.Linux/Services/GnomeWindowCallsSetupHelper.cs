using System.Diagnostics;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// Probes for the "Window Calls" GNOME Shell extension and opens its
/// install page when the user clicks the remediation button in the
/// Profiles section. We don't run any privileged installation step
/// ourselves — extensions.gnome.org requires the user to click "Install"
/// in their browser with the GNOME Browser Integration extension active.
/// </summary>
public sealed class GnomeWindowCallsSetupHelper
{
    private const string ExtensionInstallUrl =
        "https://extensions.gnome.org/extension/4974/window-calls/";

    private const string DBusDest = "org.gnome.Shell";
    private const string DBusPath = "/org/gnome/Shell/Extensions/Windows";

    public bool IsApplicable()
    {
        var raw = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var lower = raw.ToLowerInvariant();
        return lower.Contains("gnome") || lower.Contains("ubuntu");
    }

    public bool IsCurrentlyInstalled()
    {
        if (!DesktopDetector.BinaryExists("gdbus")) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(
                "gdbus",
                $"introspect --session --dest {DBusDest} --object-path {DBusPath}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return false;

            if (!p.WaitForExit(500))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return false;
            }

            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public bool TryOpenInstallPage()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("xdg-open", ExtensionInstallUrl)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            return p is not null;
        }
        catch
        {
            return false;
        }
    }
}
