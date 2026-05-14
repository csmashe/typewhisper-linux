using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
/// KDE Plasma / KWin active-window provider. Gated on
/// <c>XDG_CURRENT_DESKTOP</c> containing "KDE" or "Plasma". Prefers
/// <c>kdotool</c> (drop-in xdotool clone for KWin) for window identity;
/// returns null when kdotool is unavailable rather than over-engineering a
/// KWin-scripting fallback. Layer B can extend this with a qdbus-based
/// fallback if real-world coverage demands it.
/// </summary>
public sealed class KWinActiveWindowProvider : IActiveWindowProvider
{
    public string Name => "kwin";

    public bool IsApplicable()
    {
        var raw = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var lower = raw.ToLowerInvariant();
        if (!lower.Contains("kde") && !lower.Contains("plasma")) return false;
        return DesktopDetector.BinaryExists("kdotool");
    }

    public async Task<ActiveWindowSnapshot?> TryGetActiveWindowAsync(CancellationToken ct)
    {
        try
        {
            var (idExit, idOutput) = await ProviderProcessRunner
                .RunAsync("kdotool", "getactivewindow", ct).ConfigureAwait(false);
            var windowId = idExit == 0 ? idOutput?.Trim() : null;
            if (string.IsNullOrWhiteSpace(windowId))
                return null;

            var (classExit, classOutput) = await ProviderProcessRunner
                .RunAsync("kdotool", $"getwindowclassname {windowId}", ct).ConfigureAwait(false);
            var klass = classExit == 0 ? classOutput?.Trim() : null;

            var (nameExit, nameOutput) = await ProviderProcessRunner
                .RunAsync("kdotool", $"getwindowname {windowId}", ct).ConfigureAwait(false);
            var title = nameExit == 0 ? nameOutput?.Trim() : null;

            var processName = !string.IsNullOrWhiteSpace(klass)
                ? ProcessNameNormalizer.Normalize(klass).ToLowerInvariant()
                : null;

            return new ActiveWindowSnapshot(
                ProcessName: string.IsNullOrWhiteSpace(processName) ? null : processName,
                Title: string.IsNullOrWhiteSpace(title) ? null : title,
                WindowId: windowId,
                AppId: string.IsNullOrWhiteSpace(klass) ? null : klass,
                Source: Name,
                IsTrusted: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
