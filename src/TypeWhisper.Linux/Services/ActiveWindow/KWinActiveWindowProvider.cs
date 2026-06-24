using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
///     KDE Plasma / KWin active-window provider, gated on <c>XDG_CURRENT_DESKTOP</c>
///     containing "KDE" or "Plasma". Uses <c>kdotool</c> (drop-in xdotool clone for
///     KWin); returns null when kdotool is absent. No KWin scripting fallback — it
///     requires a writable scripts directory plus a DBus round-trip per query, making
///     "install kdotool" the better remediation story.
/// </summary>
public sealed class KWinActiveWindowProvider : IActiveWindowProvider
{
    public string Name => "kwin";

    public bool IsApplicable()
    {
        var raw = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var lower = raw.ToLowerInvariant();
        if (!lower.Contains("kde") && !lower.Contains("plasma"))
        {
            return false;
        }

        return DesktopDetector.BinaryExists("kdotool");
    }

    public async Task<ActiveWindowSnapshot?> TryGetActiveWindowAsync(CancellationToken ct)
    {
        try
        {
            var (idExit, idOutput) = await ProviderProcessRunner
                .RunAsync("kdotool", "getactivewindow", ct)
                .ConfigureAwait(false);
            var windowId = idExit == 0 ? idOutput?.Trim() : null;
            if (string.IsNullOrWhiteSpace(windowId))
            {
                return null;
            }

            var (classExit, classOutput) = await ProviderProcessRunner
                .RunAsync("kdotool", new[] { "getwindowclassname", windowId }, ct)
                .ConfigureAwait(false);
            var klass = classExit == 0 ? classOutput?.Trim() : null;

            var (nameExit, nameOutput) = await ProviderProcessRunner
                .RunAsync("kdotool", new[] { "getwindowname", windowId }, ct)
                .ConfigureAwait(false);
            var title = nameExit == 0 ? nameOutput?.Trim() : null;

            var processName = !string.IsNullOrWhiteSpace(klass)
                ? ProcessNameNormalizer.Normalize(klass).ToLowerInvariant()
                : null;

            return new ActiveWindowSnapshot(
                string.IsNullOrWhiteSpace(processName) ? null : processName,
                string.IsNullOrWhiteSpace(title) ? null : title,
                windowId,
                string.IsNullOrWhiteSpace(klass) ? null : klass,
                Name
            );
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