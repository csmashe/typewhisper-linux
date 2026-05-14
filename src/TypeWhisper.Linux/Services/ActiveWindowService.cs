using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.ActiveWindow;

namespace TypeWhisper.Linux.Services;

/// <summary>
/// Linux active-window orchestrator. The compositor-specific work lives in
/// the <see cref="IActiveWindowProvider"/> chain (xdotool, Hyprland, Sway,
/// KWin, GNOME Shell) — this class iterates the chain and returns the
/// first non-null snapshot. The synchronous <see cref="GetActiveWindowProcessName"/>
/// and <see cref="GetActiveWindowTitle"/> getters walk the same chain with
/// a tight per-provider budget so legacy callers keep working unchanged.
/// AT-SPI URL extraction is delegated to <see cref="AtSpiUrlExtractor"/>;
/// xclip clipboard capture remains here as the last-resort X11 fallback.
/// </summary>
public sealed class ActiveWindowService : IActiveWindowService
{
    private static readonly TimeSpan ProviderSyncBudget = TimeSpan.FromMilliseconds(150);

    private readonly IReadOnlyList<IActiveWindowProvider> _providers;
    private readonly AtSpiUrlExtractor _atSpiUrlExtractor;

    private static readonly bool IsXdotoolAvailable = CheckXdotoolAvailable();
    private static readonly bool IsXclipAvailable = CheckCommandAvailable("xclip", "-version");

    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome",
        "msedge",
        "brave",
        "opera",
        "vivaldi",
        "chromium",
        "firefox",
        "waterfox",
        "zen",
        "zen-browser",
        "zen-bin"
    };

    private static readonly string[] BrowserAppNameHints =
    [
        "google chrome",
        "chrome",
        "microsoft edge",
        "edge",
        "brave",
        "opera",
        "vivaldi",
        "chromium",
        "firefox",
        "waterfox",
        "zen browser",
        "zen"
    ];

    private const int AtSpiStateFocused = 11;
    private const int AtSpiStateEditable = 18;
    private const int AtSpiRoleEditBar = 77;
    private const int AtSpiRoleEntry = 79;

    public ActiveWindowService(IEnumerable<IActiveWindowProvider> providers, AtSpiUrlExtractor atSpiUrlExtractor)
    {
        _providers = providers.ToList();
        _atSpiUrlExtractor = atSpiUrlExtractor;
    }

    public async Task<ActiveWindowSnapshot?> GetActiveWindowSnapshotAsync(CancellationToken ct)
    {
        foreach (var provider in _providers)
        {
            if (!provider.IsApplicable()) continue;
            try
            {
                // Give each provider its own slice of the caller's budget
                // so a slow earlier provider can't starve later fallbacks.
                // Without this, a single 50 ms caller CTS that's mostly
                // consumed by the first applicable provider leaves every
                // remaining provider with a near-cancelled token and they
                // all early-return null. We still link the caller token
                // so external cancellation (e.g. shutdown) propagates.
                using var perProviderCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                perProviderCts.CancelAfter(ProviderSyncBudget);
                var snapshot = await provider.TryGetActiveWindowAsync(perProviderCts.Token).ConfigureAwait(false);
                if (snapshot is not null) return snapshot;
            }
            catch
            {
                // Providers should never throw, but defensively skip any that do.
            }
        }
        return null;
    }

    public string? GetActiveWindowProcessName()
    {
        var snapshot = GetActiveWindowSnapshotSync();
        if (snapshot?.ProcessName is { Length: > 0 } name)
            return name;

        return TryInferBrowserProcessNameFromTitle(snapshot?.Title);
    }

    public string? GetActiveWindowTitle() => GetActiveWindowSnapshotSync()?.Title;

    private ActiveWindowSnapshot? GetActiveWindowSnapshotSync()
    {
        foreach (var provider in _providers)
        {
            if (!provider.IsApplicable()) continue;

            using var cts = new CancellationTokenSource(ProviderSyncBudget);
            try
            {
                var snapshot = provider.TryGetActiveWindowAsync(cts.Token).GetAwaiter().GetResult();
                if (snapshot is not null) return snapshot;
            }
            catch
            {
                // Skip misbehaving providers — orchestration must never throw.
            }
        }
        return null;
    }

    public string? GetBrowserUrl(bool allowInteractiveCapture = true)
    {
        var snapshot = GetActiveWindowSnapshotSync();
        var title = snapshot?.Title;
        var processName = snapshot?.ProcessName is { Length: > 0 } name
            ? name
            : TryInferBrowserProcessNameFromTitle(title);

        var atSpiUrl = _atSpiUrlExtractor.TryGetBrowserUrl(processName, title);
        if (atSpiUrl is not null)
            return atSpiUrl;

        var inferredUrl = TryInferBrowserUrlFromTitle(title);
        if (inferredUrl is not null)
            return inferredUrl;

        if (!allowInteractiveCapture || !IsXclipAvailable || !IsXdotoolAvailable || !IsSupportedBrowserWindow(processName, title))
            return null;

        // Only reuse the snapshot's WindowId when it came from the X11/xdotool
        // provider — Wayland providers (sway, kwin, gnome-shell, hyprland)
        // expose compositor-specific ids that xdotool can't address.
        var windowId = snapshot?.Source == "xdotool" ? snapshot.WindowId : null;
        if (string.IsNullOrWhiteSpace(windowId))
            windowId = RunXdotool("getactivewindow");
        if (string.IsNullOrWhiteSpace(windowId))
            return null;

        return TryCaptureBrowserUrl(windowId);
    }

    public IReadOnlyList<string> GetRunningAppProcessNames()
    {
        try
        {
            var ownId = Environment.ProcessId;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id != ownId && !string.IsNullOrWhiteSpace(process.MainWindowTitle))
                        names.Add(process.ProcessName);
                }
                catch
                {
                    // Skip processes we can't read.
                }
                finally
                {
                    process.Dispose();
                }
            }

            return names.Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return [];
        }
    }

    public string? GetActiveWindowId()
    {
        if (!IsXdotoolAvailable) return null;
        var windowId = RunXdotool("getactivewindow");
        return string.IsNullOrWhiteSpace(windowId) ? null : windowId;
    }

    public bool TryActivateWindow(string? windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId) || !IsXdotoolAvailable)
            return false;

        var exitCode = RunProcess("xdotool", $"windowactivate --sync {windowId}", out _);
        return exitCode == 0;
    }

    private static string? TryCaptureBrowserUrl(string windowId)
    {
        string? previousClipboard = null;

        try
        {
            previousClipboard = TryReadClipboardText();

            // Clear first so a failed copy does not return stale clipboard contents.
            if (!TryWriteClipboardText(string.Empty))
                return null;

            if (!SendBrowserAddressBarCaptureKeys(windowId))
                return null;

            var copied = TryReadClipboardText();
            return SanitizeCapturedBrowserUrl(copied);
        }
        finally
        {
            if (previousClipboard is not null)
                TryWriteClipboardText(previousClipboard);
        }
    }

    private static bool SendBrowserAddressBarCaptureKeys(string windowId)
    {
        // Linux adaptation: browsers reliably expose Ctrl+L / Ctrl+C on X11,
        // so we can capture the address bar without adding a full AT-SPI stack.
        if (!RunXdotoolKey(windowId, "key --clearmodifiers ctrl+l"))
            return false;

        Thread.Sleep(60);

        if (!RunXdotoolKey(windowId, "key --clearmodifiers ctrl+c"))
            return false;

        Thread.Sleep(80);

        RunXdotoolKey(windowId, "key Escape");
        return true;
    }

    private static bool RunXdotoolKey(string windowId, string args)
    {
        var exitCode = RunProcess("xdotool", $"windowactivate --sync {windowId} {args}", out _);
        return exitCode == 0;
    }

    private static string? TryReadClipboardText()
    {
        var exitCode = RunProcess("xclip", "-selection clipboard -o", out var output);
        return exitCode == 0 ? output : null;
    }

    private static bool TryWriteClipboardText(string text)
    {
        var exitCode = RunProcessWithInput("xclip", "-selection clipboard", text);
        return exitCode == 0;
    }

    internal static bool IsSupportedBrowserProcess(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) && BrowserProcessNames.Contains(processName);

    internal static bool IsSupportedBrowserWindow(string? processName, string? title) =>
        IsSupportedBrowserProcess(processName)
        || TryInferBrowserProcessNameFromTitle(title) is not null;

    internal static string? TryInferBrowserProcessNameFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        if (title.Contains("Zen Browser", StringComparison.OrdinalIgnoreCase))
            return "zen";

        return null;
    }

    internal static string? TryInferBrowserUrlFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        // Zen/Firefox windows do not always expose a process id through
        // xdotool on Flatpak/X11. Gmail titles are still reliable enough to
        // match URL-scoped email profiles without visibly focusing the address
        // bar just to copy the real URL.
        if (title.Contains(" Mail", StringComparison.OrdinalIgnoreCase)
            && title.Contains("Zen Browser", StringComparison.OrdinalIgnoreCase))
        {
            return "https://mail.google.com";
        }

        return null;
    }

    internal static bool IsSupportedBrowserIdentity(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return false;

        if (IsSupportedBrowserProcess(identity))
            return true;

        return BrowserAppNameHints.Any(hint => identity.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    internal static string? SanitizeCapturedBrowserUrl(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || !IsLikelyUrl(trimmed))
            return null;

        return NormalizeUrl(trimmed);
    }

    internal static bool IsLikelyUrl(string value)
    {
        if (value.Length < 3 || value.Length > 2048)
            return false;

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return true;

        if (value.Contains(' ') || !value.Contains('.'))
            return false;

        var host = value.Split('/')[0];
        return host.Contains('.');
    }

    internal static string NormalizeUrl(string value)
    {
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return value;

        return "https://" + value;
    }

    internal static bool HasState(IReadOnlyList<uint> stateWords, int state)
    {
        var wordIndex = state / 32;
        var bitOffset = state % 32;
        return wordIndex < stateWords.Count && (stateWords[wordIndex] & (1u << bitOffset)) != 0;
    }

    internal static int ScoreBrowserUrlCandidate(
        int role,
        IReadOnlyList<uint> states,
        string? name,
        string? candidateText,
        IReadOnlyList<string> interfaces)
    {
        var sanitized = SanitizeCapturedBrowserUrl(candidateText);
        if (sanitized is null)
            return int.MinValue;

        var score = 100;
        if (role == AtSpiRoleEditBar)
            score += 120;
        else if (role == AtSpiRoleEntry)
            score += 80;

        if (HasState(states, AtSpiStateFocused))
            score += 50;
        if (HasState(states, AtSpiStateEditable))
            score += 15;
        if (interfaces.Contains("org.a11y.atspi.Text", StringComparer.Ordinal))
            score += 10;
        if (sanitized.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            score += 10;
        if (sanitized.Contains('/', StringComparison.Ordinal))
            score += 5;
        if (!string.IsNullOrWhiteSpace(name) && name.Contains("address", StringComparison.OrdinalIgnoreCase))
            score += 40;

        return score;
    }

    private static bool CheckXdotoolAvailable() => CheckCommandAvailable("xdotool", "--version");

    private static bool CheckCommandAvailable(string command, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(command, args)
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
        var exitCode = RunProcess("xdotool", args, out var output);
        return exitCode == 0 ? output?.Trim() : null;
    }

    private static int RunProcess(string fileName, string args, out string? output)
    {
        output = null;

        try
        {
            using var p = Process.Start(new ProcessStartInfo(fileName, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return -1;

            // Drain both pipes concurrently — reading only stdout deadlocks if
            // stderr's pipe buffer fills while the child is still running.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(1000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return -1;
            }

            output = stdoutTask.GetAwaiter().GetResult();
            stderrTask.GetAwaiter().GetResult();
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static int RunProcessWithInput(string fileName, string args, string input)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(fileName, args)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return -1;

            p.StandardInput.Write(input);
            p.StandardInput.Close();
            p.WaitForExit(1000);
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
