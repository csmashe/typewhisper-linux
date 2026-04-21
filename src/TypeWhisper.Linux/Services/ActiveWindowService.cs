using System.Diagnostics;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// Linux active-window lookup via xdotool (X11 only). On Wayland there's no
/// cross-compositor equivalent — callers should treat nulls as "unknown" and
/// degrade gracefully.
///
/// Browser URL extraction via UIA isn't available on Linux; returning null is
/// the documented behaviour for v1.
/// </summary>
public sealed class ActiveWindowService : IActiveWindowService
{
    private static readonly bool IsXdotoolAvailable = CheckXdotoolAvailable();

    public string? GetActiveWindowProcessName()
    {
        if (!IsXdotoolAvailable) return null;
        var pid = RunXdotool("getactivewindow getwindowpid");
        if (string.IsNullOrWhiteSpace(pid) || !int.TryParse(pid, out var pidInt))
            return null;

        try
        {
            using var proc = Process.GetProcessById(pidInt);
            return proc.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    public string? GetActiveWindowTitle()
    {
        if (!IsXdotoolAvailable) return null;
        var title = RunXdotool("getactivewindow getwindowname");
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    public string? GetBrowserUrl() => null; // Requires Wayland-compatible a11y work; deferred.

    public IReadOnlyList<string> GetRunningAppProcessNames()
    {
        try
        {
            var ownId = Environment.ProcessId;
            return Process.GetProcesses()
                .Where(p =>
                {
                    try { return p.Id != ownId && !string.IsNullOrWhiteSpace(p.MainWindowTitle); }
                    catch { return false; }
                })
                .Select(p =>
                {
                    try { return p.ProcessName; }
                    catch { return null; }
                })
                .Where(n => n is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()!;
        }
        catch
        {
            return [];
        }
    }

    private static bool CheckXdotoolAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("xdotool", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            p?.WaitForExit(1000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? RunXdotool(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("xdotool", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(1000);
            return p.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
