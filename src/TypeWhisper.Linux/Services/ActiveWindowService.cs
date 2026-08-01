using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.ActiveWindow;
using TypeWhisper.PluginSDK.Processes;

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
    private static readonly TimeSpan s_providerSyncBudget = TimeSpan.FromMilliseconds(150);

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
        "zen-bin",
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
        "zen",
    ];

    private readonly AtSpiUrlExtractor _atSpiUrlExtractor;
    private readonly bool _isXclipAvailable;
    private readonly bool _isXdotoolAvailable;
    private readonly IProcessRunner _processRunner;

    private readonly IReadOnlyList<IActiveWindowProvider> _providers;

    public ActiveWindowService(
        IEnumerable<IActiveWindowProvider> providers,
        AtSpiUrlExtractor atSpiUrlExtractor,
        IProcessRunner? processRunner = null
    )
    {
        _providers = providers.ToList();
        _atSpiUrlExtractor = atSpiUrlExtractor;
        _processRunner = processRunner ?? new ProcessRunner();
        _isXdotoolAvailable = CheckXdotoolAvailable();
        _isXclipAvailable = CheckCommandAvailable("xclip", ["-version"]);
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
        var nonInteractiveUrl = GetBrowserUrlForSnapshot(snapshot);
        if (nonInteractiveUrl is not null)
        {
            return nonInteractiveUrl;
        }

        var title = snapshot?.Title;
        var processName = snapshot?.ProcessName is { Length: > 0 } name
            ? name
            : TryInferBrowserProcessNameFromTitle(title);

        if (
            !allowInteractiveCapture
            || !_isXclipAvailable
            || !_isXdotoolAvailable
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
            windowId = RunXdotool(["getactivewindow"]);
        }

        return string.IsNullOrWhiteSpace(windowId) ? null : TryCaptureBrowserUrl(windowId);
    }

    public string? GetBrowserUrlForSnapshot(
        ActiveWindowSnapshot? snapshot,
        bool honorMissBackoff = false
    )
    {
        var title = snapshot?.Title;
        var processName = snapshot?.ProcessName is { Length: > 0 } name
            ? name
            : TryInferBrowserProcessNameFromTitle(title);

        var atSpiUrl = _atSpiUrlExtractor.TryGetBrowserUrl(processName, title, honorMissBackoff);
        return atSpiUrl ?? TryInferBrowserUrlFromTitle(title);
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
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "kept instance: injected as a DI/test seam")]
    // ReSharper disable once MemberCanBeMadeStatic.Global
    public string? GetActiveWindowId()
    {
        if (!_isXdotoolAvailable)
        {
            return null;
        }

        var windowId = RunXdotool(["getactivewindow"]);
        return string.IsNullOrWhiteSpace(windowId) ? null : windowId;
    }

    // ReSharper disable once UnusedMember.Global  public API surface (window-activation helper paired with GetActiveWindowId); not currently called in-tree
    public bool TryActivateWindow(string? windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId) || !_isXdotoolAvailable)
        {
            return false;
        }

        var exitCode = RunProcess(
            "xdotool",
            ["windowactivate", "--sync", windowId],
            out _
        );
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

    private string? TryCaptureBrowserUrl(string windowId)
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

    private bool SendBrowserAddressBarCaptureKeys(string windowId)
    {
        // X11 last-resort: synthesize Ctrl+L (focus address bar) + Ctrl+C,
        // read the clipboard, then Escape to restore caret position.
        if (!RunXdotoolKey(windowId, ["key", "--clearmodifiers", "ctrl+l"]))
        {
            return false;
        }

        Thread.Sleep(60);

        if (!RunXdotoolKey(windowId, ["key", "--clearmodifiers", "ctrl+c"]))
        {
            return false;
        }

        Thread.Sleep(80);

        RunXdotoolKey(windowId, ["key", "Escape"]);
        return true;
    }

    private bool RunXdotoolKey(
        string windowId,
        IReadOnlyList<string> arguments
    )
    {
        var args = new List<string> { "windowactivate", "--sync", windowId };
        args.AddRange(arguments);
        var exitCode = RunProcess("xdotool", args, out _);
        return exitCode == 0;
    }

    private string? TryReadClipboardText()
    {
        var exitCode = RunProcess(
            "xclip",
            ["-selection", "clipboard", "-o"],
            out var output
        );
        return exitCode == 0 ? output : null;
    }

    private bool TryWriteClipboardText(string text)
    {
        var exitCode = RunProcessWithInput(
            "xclip",
            ["-selection", "clipboard"],
            text
        );
        return exitCode == 0;
    }

    private bool CheckXdotoolAvailable()
    {
        return CheckCommandAvailable("xdotool", ["--version"]);
    }

    private bool CheckCommandAvailable(
        string command,
        IReadOnlyList<string> args
    )
    {
        try
        {
            var result = _processRunner.RunProbe(
                new ProcessCommand(command, args),
                new ProcessOneShotOptions(
                    Timeout: TimeSpan.FromSeconds(1),
                    StandardOutput: ProcessCaptureMode.Discard,
                    StandardError: ProcessCaptureMode.Discard
                )
            );
            return result.Succeeded;
        }
        catch
        {
            return false;
        }
    }

    private string? RunXdotool(IReadOnlyList<string> args)
    {
        var exitCode = RunProcess("xdotool", args, out var output);
        return exitCode == 0 ? output?.Trim() : null;
    }

    private int RunProcess(
        string fileName,
        IReadOnlyList<string> args,
        out string? output
    )
    {
        output = null;

        try
        {
            var result = _processRunner.RunProbe(
                new ProcessCommand(fileName, args),
                new ProcessOneShotOptions(
                    Timeout: TimeSpan.FromSeconds(1),
                    StandardError: ProcessCaptureMode.Discard
                )
            );
            output = result.Status == ProcessRunStatus.Exited
                ? result.StandardOutputText
                : null;
            return result.Status == ProcessRunStatus.Exited
                ? result.ExitCode ?? -1
                : -1;
        }
        catch
        {
            return -1;
        }
    }

    private int RunProcessWithInput(
        string fileName,
        IReadOnlyList<string> args,
        string input
    )
    {
        try
        {
            var result = _processRunner.RunProbe(
                new ProcessCommand(fileName, args),
                new ProcessOneShotOptions(
                    Timeout: TimeSpan.FromSeconds(1),
                    StandardInput: new Utf8ProcessInput(input),
                    StandardOutput: ProcessCaptureMode.Discard,
                    StandardError: ProcessCaptureMode.Discard,
                    // xclip leaves a selection-serving daemon holding our pipes; waiting for EOF
                    // would burn the whole timeout and report every clipboard write as failed.
                    PostExitPipePolicy: ProcessPostExitPipePolicy.AbandonAfterGrace
                )
            );
            return result.Status == ProcessRunStatus.Exited
                ? result.ExitCode ?? -1
                : -1;
        }
        catch
        {
            return -1;
        }
    }
}
