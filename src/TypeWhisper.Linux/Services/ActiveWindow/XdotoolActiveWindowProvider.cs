using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
/// X11 / XWayland active-window provider backed by <c>xdotool</c>. Gated on
/// the presence of the <c>xdotool</c> binary on PATH so it stays out of the
/// way on pure-Wayland sessions where xdotool returns errors or stale data.
/// This is the last entry in the orchestrator chain — compositor-native
/// providers win first under Wayland, xdotool covers everything else.
/// </summary>
public sealed class XdotoolActiveWindowProvider : IActiveWindowProvider
{
    public string Name => "xdotool";

    public bool IsApplicable() => DesktopDetector.BinaryExists("xdotool");

    public async Task<ActiveWindowSnapshot?> TryGetActiveWindowAsync(CancellationToken ct)
    {
        try
        {
            var windowId = await RunXdotoolAsync("getactivewindow", ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(windowId))
                return null;

            var title = await RunXdotoolAsync("getactivewindow getwindowname", ct).ConfigureAwait(false);
            var pidText = await RunXdotoolAsync("getactivewindow getwindowpid", ct).ConfigureAwait(false);

            string? processName = null;
            if (!string.IsNullOrWhiteSpace(pidText) && int.TryParse(pidText, out var pid))
            {
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    processName = proc.ProcessName;
                }
                catch
                {
                    processName = null;
                }
            }

            return new ActiveWindowSnapshot(
                ProcessName: processName,
                Title: string.IsNullOrWhiteSpace(title) ? null : title,
                WindowId: windowId,
                AppId: null,
                Source: Name,
                IsTrusted: true);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> RunXdotoolAsync(string args, CancellationToken ct)
    {
        var (exitCode, output) = await ProviderProcessRunner.RunAsync("xdotool", args, ct).ConfigureAwait(false);
        return exitCode == 0 ? output?.Trim() : null;
    }
}
