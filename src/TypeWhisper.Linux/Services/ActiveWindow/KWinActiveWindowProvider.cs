using System.Globalization;
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
public sealed class KWinActiveWindowProvider : IActiveWindowProvider, IActiveWindowActivator
{
    private readonly ProviderProcessRunner _processRunner;

    public KWinActiveWindowProvider()
        : this(new ProcessRunner()) { }

    public KWinActiveWindowProvider(IProcessRunner processRunner)
    {
        _processRunner = new ProviderProcessRunner(processRunner);
    }

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
            var (idExit, idOutput) = await _processRunner
                .RunAsync("kdotool", ["getactivewindow"], ct)
                .ConfigureAwait(false);
            var windowId = idExit == 0 ? idOutput?.Trim() : null;
            if (string.IsNullOrWhiteSpace(windowId))
            {
                return null;
            }

            var (classExit, classOutput) = await _processRunner
                .RunAsync("kdotool", ["getwindowclassname", windowId], ct)
                .ConfigureAwait(false);
            var klass = classExit == 0 ? classOutput?.Trim() : null;

            var (nameExit, nameOutput) = await _processRunner
                .RunAsync("kdotool", ["getwindowname", windowId], ct)
                .ConfigureAwait(false);
            var title = nameExit == 0 ? nameOutput?.Trim() : null;

            // KWin knows the owning PID of both Wayland-native and XWayland clients. Without it
            // the URL and screen-context readers can't tell two same-title instances apart.
            var (pidExit, pidOutput) = await _processRunner
                .RunAsync("kdotool", ["getwindowpid", windowId], ct)
                .ConfigureAwait(false);
            var processId = pidExit == 0 ? ParsePid(pidOutput) : null;

            var processName = !string.IsNullOrWhiteSpace(klass)
                ? ProcessNameNormalizer.Normalize(klass).ToLowerInvariant()
                : null;

            return new ActiveWindowSnapshot(
                string.IsNullOrWhiteSpace(processName) ? null : processName,
                string.IsNullOrWhiteSpace(title) ? null : title,
                windowId,
                string.IsNullOrWhiteSpace(klass) ? null : klass,
                Name,
                processId
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

    public async Task<bool> TryActivateWindowAsync(string windowId, CancellationToken ct)
    {
        var (exitCode, _) = await _processRunner
            .RunAsync("kdotool", ["windowactivate", windowId], ct)
            .ConfigureAwait(false);
        return exitCode == 0;
    }

    private static int? ParsePid(string? output)
    {
        return int.TryParse(
            output?.Trim(),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var pid
        ) && pid > 0
            ? pid
            : null;
    }
}
