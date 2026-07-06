using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.ActiveWindow;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Iterates the <see cref="IActiveWindowProvider" /> chain (xdotool,
///     Hyprland, Sway, KWin, GNOME Shell) and returns the first non-null
///     snapshot. AT-SPI URL extraction is delegated to
///     <see cref="AtSpiUrlExtractor" />; xclip clipboard capture is the
///     last-resort X11 fallback for browser URLs.
/// </summary>
public sealed class ActiveWindowService : IActiveWindowService
{
    private const int AtSpiStateFocused = 11;
    private const int AtSpiStateEditable = 18;
    private const int AtSpiRoleEditBar = 77;
    private const int AtSpiRoleEntry = 79;

    // Our own AT-SPI app Name, so the focused-context harvest never reads TypeWhisper's
    // own window (the overlay/settings) instead of the app the user is dictating into.
    private const string SelfAtSpiAppName = "TypeWhisper";
    private static readonly TimeSpan s_providerSyncBudget = TimeSpan.FromMilliseconds(150);

    private static readonly bool s_isXdotoolAvailable = CheckXdotoolAvailable();
    private static readonly bool s_isXclipAvailable = CheckCommandAvailable("xclip", "-version");

    private static readonly HashSet<string> s_browserProcessNames = new(
        StringComparer.OrdinalIgnoreCase
    )
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

    private static readonly string[] s_browserAppNameHints =
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

    private readonly AtSpiUrlExtractor _atSpiUrlExtractor;

    private readonly IReadOnlyList<IActiveWindowProvider> _providers;

    public ActiveWindowService(
        IEnumerable<IActiveWindowProvider> providers,
        AtSpiUrlExtractor atSpiUrlExtractor
    )
    {
        _providers = providers.ToList();
        _atSpiUrlExtractor = atSpiUrlExtractor;
    }

    public string? GetActiveWindowProcessName()
    {
        var snapshot = GetActiveWindowSnapshotSync();
        if (snapshot?.ProcessName is { Length: > 0 } name)
        {
            return name;
        }

        return TryInferBrowserProcessNameFromTitle(snapshot?.Title);
    }

    public string? GetActiveWindowTitle()
    {
        return GetActiveWindowSnapshotSync()?.Title;
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
        {
            return atSpiUrl;
        }

        var inferredUrl = TryInferBrowserUrlFromTitle(title);
        if (inferredUrl is not null)
        {
            return inferredUrl;
        }

        if (
            !allowInteractiveCapture
            || !s_isXclipAvailable
            || !s_isXdotoolAvailable
            || !IsSupportedBrowserWindow(processName, title)
        )
        {
            return null;
        }

        // Only use the snapshot's WindowId when it came from xdotool —
        // Wayland provider ids are compositor-specific and xdotool can't address them.
        var windowId = snapshot?.Source == "xdotool" ? snapshot.WindowId : null;
        if (string.IsNullOrWhiteSpace(windowId))
        {
            windowId = RunXdotool("getactivewindow");
        }

        return string.IsNullOrWhiteSpace(windowId) ? null : TryCaptureBrowserUrl(windowId);
    }

    public string? GetFocusedScreenContext(string? processName, string? title)
    {
        // Scoped to the recording's window (passed in by the caller) rather than re-snapshotting
        // the active window here: a focus change between record-start and harvest must not let
        // the harvest read a different app. The harvest still requires a STATE_FOCUSED element
        // inside this app, so it returns null if that window is no longer focused.
        return _atSpiUrlExtractor.TryHarvestFocusedContext(processName, title, SelfAtSpiAppName);
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
                    {
                        names.Add(process.ProcessName);
                    }
                }
                catch
                {
                    /* best effort */
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

    public async Task<ActiveWindowSnapshot?> GetActiveWindowSnapshotAsync(CancellationToken ct)
    {
        foreach (var provider in _providers)
        {
            if (!provider.IsApplicable())
            {
                continue;
            }

            try
            {
                // Each provider gets its own per-provider budget so a slow
                // earlier provider can't starve later fallbacks. The caller
                // token is linked so external cancellation (e.g. shutdown) propagates.
                using var perProviderCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                perProviderCts.CancelAfter(s_providerSyncBudget);
                var snapshot = await provider
                    .TryGetActiveWindowAsync(perProviderCts.Token)
                    .ConfigureAwait(false);
                if (snapshot is not null)
                {
                    return snapshot;
                }
            }
            catch
            {
                /* provider misbehaved — skip to next */
            }
        }

        return null;
    }

    // kept instance: injected as a DI/test seam by callers
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "kept instance: injected as a DI/test seam")]
    // ReSharper disable once MemberCanBeMadeStatic.Global
    public string? GetActiveWindowId()
    {
        if (!s_isXdotoolAvailable)
        {
            return null;
        }

        var windowId = RunXdotool("getactivewindow");
        return string.IsNullOrWhiteSpace(windowId) ? null : windowId;
    }

    // ReSharper disable once UnusedMember.Global  public API surface (window-activation helper paired with GetActiveWindowId); not currently called in-tree
    public static bool TryActivateWindow(string? windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId) || !s_isXdotoolAvailable)
        {
            return false;
        }

        var exitCode = RunProcess("xdotool", $"windowactivate --sync {windowId}", out _);
        return exitCode == 0;
    }

    internal static bool IsSupportedBrowserProcess(string? processName)
    {
        return !string.IsNullOrWhiteSpace(processName) && s_browserProcessNames.Contains(processName);
    }

    internal static bool IsSupportedBrowserWindow(string? processName, string? title)
    {
        return IsSupportedBrowserProcess(processName)
               || TryInferBrowserProcessNameFromTitle(title) is not null;
    }

    internal static string? TryInferBrowserProcessNameFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        return title.Contains("Zen Browser", StringComparison.OrdinalIgnoreCase) ? "zen" : null;
    }

    internal static string? TryInferBrowserUrlFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        // Zen/Firefox Flatpak builds often report no process name (XWayland surface
        // runs under a sandboxed PID). Title-matching avoids needing to focus the
        // address bar, which would be visible and disruptive.
        if (
            title.Contains(" Mail", StringComparison.OrdinalIgnoreCase)
            && title.Contains("Zen Browser", StringComparison.OrdinalIgnoreCase)
        )
        {
            return "https://mail.google.com";
        }

        return null;
    }

    internal static bool IsSupportedBrowserIdentity(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            return false;
        }

        if (IsSupportedBrowserProcess(identity))
        {
            return true;
        }

        return s_browserAppNameHints.Any(hint =>
            identity.Contains(hint, StringComparison.OrdinalIgnoreCase)
        );
    }

    internal static string? SanitizeCapturedBrowserUrl(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || !IsLikelyUrl(trimmed))
        {
            return null;
        }

        return NormalizeUrl(trimmed);
    }

    private static bool IsLikelyUrl(string value)
    {
        if (value.Length is < 3 or > 2048)
        {
            return false;
        }

        if (
            value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        if (value.Contains(' ') || !value.Contains('.'))
        {
            return false;
        }

        var host = value.Split('/')[0];
        return host.Contains('.');
    }

    private static string NormalizeUrl(string value)
    {
        if (
            value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
        )
        {
            return value;
        }

        return "https://" + value;
    }

    internal static bool HasState(IReadOnlyList<uint> stateWords, int state)
    {
        var wordIndex = state / 32;
        var bitOffset = state % 32;
        return wordIndex < stateWords.Count && (stateWords[wordIndex] & (1u << bitOffset)) != 0;
    }

    /// <summary>
    ///     Scores an AT-SPI node as a browser URL candidate. Returns
    ///     <see cref="int.MinValue" /> when the text is not a URL. Weights:
    ///     role (EditBar &gt; Entry), focused, editable, Text interface, HTTP
    ///     scheme, path depth, "address" in accessible name.
    /// </summary>
    internal static int ScoreBrowserUrlCandidate(
        int role,
        IReadOnlyList<uint> states,
        string? name,
        string? candidateText,
        IReadOnlyList<string> interfaces
    )
    {
        var sanitized = SanitizeCapturedBrowserUrl(candidateText);
        if (sanitized is null)
        {
            return int.MinValue;
        }

        var score = 100;
        switch (role)
        {
            case AtSpiRoleEditBar:
                score += 120;
                break;
            case AtSpiRoleEntry:
                score += 80;
                break;
        }

        if (HasState(states, AtSpiStateFocused))
        {
            score += 50;
        }

        if (HasState(states, AtSpiStateEditable))
        {
            score += 15;
        }

        if (interfaces.Contains("org.a11y.atspi.Text", StringComparer.Ordinal))
        {
            score += 10;
        }

        if (sanitized.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        if (sanitized.Contains('/', StringComparison.Ordinal))
        {
            score += 5;
        }

        if (
            !string.IsNullOrWhiteSpace(name)
            && name.Contains("address", StringComparison.OrdinalIgnoreCase)
        )
        {
            score += 40;
        }

        return score;
    }

    private ActiveWindowSnapshot? GetActiveWindowSnapshotSync()
    {
        foreach (var provider in _providers)
        {
            if (!provider.IsApplicable())
            {
                continue;
            }

            using var cts = new CancellationTokenSource(s_providerSyncBudget);
            try
            {
                var snapshot = provider.TryGetActiveWindowAsync(cts.Token).GetAwaiter().GetResult();
                if (snapshot is not null)
                {
                    return snapshot;
                }
            }
            catch
            {
                /* provider misbehaved — skip to next */
            }
        }

        return null;
    }

    private static string? TryCaptureBrowserUrl(string windowId)
    {
        string? previousClipboard = null;

        try
        {
            previousClipboard = TryReadClipboardText();

            // Clear first so a failed copy does not return stale clipboard contents.
            if (!TryWriteClipboardText(string.Empty))
            {
                return null;
            }

            if (!SendBrowserAddressBarCaptureKeys(windowId))
            {
                return null;
            }

            var copied = TryReadClipboardText();
            return SanitizeCapturedBrowserUrl(copied);
        }
        finally
        {
            if (previousClipboard is not null)
            {
                TryWriteClipboardText(previousClipboard);
            }
        }
    }

    private static bool SendBrowserAddressBarCaptureKeys(string windowId)
    {
        // X11 last-resort: synthesize Ctrl+L (focus address bar) + Ctrl+C,
        // read the clipboard, then Escape to restore caret position.
        if (!RunXdotoolKey(windowId, "key --clearmodifiers ctrl+l"))
        {
            return false;
        }

        Thread.Sleep(60);

        if (!RunXdotoolKey(windowId, "key --clearmodifiers ctrl+c"))
        {
            return false;
        }

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

    private static bool CheckXdotoolAvailable()
    {
        return CheckCommandAvailable("xdotool", "--version");
    }

    private static bool CheckCommandAvailable(string command, string args)
    {
        try
        {
            using var p = Process.Start(
                new ProcessStartInfo(command, args)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
                }
            );
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
            using var p = Process.Start(
                new ProcessStartInfo(fileName, args)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
                }
            );
            if (p is null)
            {
                return -1;
            }

            // Drain stdout and stderr concurrently to avoid the classic deadlock
            // where a full stderr pipe blocks the child while we wait for exit.
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(1000))
            {
                try
                {
                    p.Kill(true);
                }
                catch
                {
                    /* best effort */
                }

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
            using var p = Process.Start(
                new ProcessStartInfo(fileName, args)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            );
            if (p is null)
            {
                return -1;
            }

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