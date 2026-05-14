using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
/// Focused AT-SPI walker that pulls the active URL out of a browser's
/// address bar. Mirrors the Windows <c>FindEditWithUrl</c> shape:
///
///   1. Gate on the focused process name — AT-SPI walks against
///      Firefox/Zen are 50–200 ms, so we never start the walk unless the
///      foreground process is one of our known browsers.
///   2. Cache by <c>(processName, title)</c> — the address bar value
///      only changes when the title does. Title-key hits skip the walk
///      entirely.
///   3. Narrow to the matching AT-SPI app — we don't iterate every
///      registered application on the bus, we pick the one whose Name
///      matches the focused process and walk only its tree.
///   4. Find the first showing/visible entry whose text value passes
///      <see cref="ActiveWindowService.IsLikelyUrl"/>, then normalize.
///
/// 500 ms total walk budget. The companion title-inference and xclip
/// paths still live on <see cref="ActiveWindowService"/> — this class
/// is the AT-SPI layer only.
/// </summary>
public sealed class AtSpiUrlExtractor
{
    // AT-SPI is slow on Wayland — each busctl invocation is its own
    // process plus a D-Bus round-trip, and under load process spawning
    // alone can be 50-200ms each. The walker also descends into invisible
    // containers (Firefox keeps the URL bar under structural parents
    // that aren't themselves SHOWING/VISIBLE), so the total node count
    // is larger than just the visible subtree. 2.5s gives plenty of
    // headroom even on a busy system. The orchestrator's deferred-URL
    // timeout (4s) is strictly larger so the await never beats the
    // walker to the punch.
    private static readonly TimeSpan WalkBudget = TimeSpan.FromMilliseconds(2500);
    private static readonly bool IsBusctlAvailable = CheckCommandAvailable("busctl", "--version");
    // gdbus has no --version flag (exits 1 with "Unknown command"). Probe
    // with `help`, which exits 0 and proves the binary is runnable.
    private static readonly bool IsGdbusAvailable = CheckCommandAvailable("gdbus", "help");

    private static readonly HashSet<string> SupportedBrowserProcessNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "firefox",
            "librewolf",
            "waterfox",
            "chrome",
            "chromium",
            "brave",
            "edge",
            "msedge",
            "vivaldi",
            "opera",
            "zen",
            "zen-browser",
            "zen-bin",
        };

    private const string AtSpiRegistryBusName = "org.a11y.atspi.Registry";
    private const string AtSpiRootPath = "/org/a11y/atspi/accessible/root";
    private const int AtSpiStateActive = 1;
    private const int AtSpiStateShowing = 25;
    private const int AtSpiStateVisible = 30;
    private const int AtSpiRoleFrame = 23;
    private const int AtSpiRoleWindow = 69;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    private readonly object _cacheLock = new();
    private readonly IErrorLogService? _errorLog;
    private string? _cachedProcessName;
    private string? _cachedTitle;
    private string? _cachedUrl;
    private DateTime _cachedUrlAt;
    private string? _lastDiagnosticKey;

    public AtSpiUrlExtractor() : this(null) { }

    public AtSpiUrlExtractor(IErrorLogService? errorLog)
    {
        _errorLog = errorLog;
    }

    public string? TryGetBrowserUrl(string? focusedProcessName, string? focusedTitle)
    {
        var processHint = !string.IsNullOrWhiteSpace(focusedProcessName)
            ? focusedProcessName
            : ActiveWindowService.TryInferBrowserProcessNameFromTitle(focusedTitle);

        if (string.IsNullOrWhiteSpace(processHint) || !SupportedBrowserProcessNames.Contains(processHint))
            return null;

        lock (_cacheLock)
        {
            // Key by (process, title). Title is the only signal we have
            // for "different tab / different page" — keying on process
            // alone would happily return the previous tab's URL after a
            // Firefox tab switch (same process, fresh window). Gmail and
            // similar apps that bump the title with a notification count
            // do force a re-walk, but the walker is fast enough on a
            // configured AT-SPI tree that the extra walks are not
            // user-perceptible. The TTL caps how long we trust a stale
            // value for the rare case where the user navigates within
            // the same tab without the title changing (single-page apps).
            if (_cachedUrl is not null
                && string.Equals(_cachedProcessName, processHint, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_cachedTitle, focusedTitle, StringComparison.Ordinal)
                && DateTime.UtcNow - _cachedUrlAt < CacheTtl)
            {
                return _cachedUrl;
            }
        }

        if (!IsBusctlAvailable || !IsGdbusAvailable)
        {
            LogOnce(processHint, focusedTitle, "AT-SPI URL walk skipped: busctl/gdbus not on PATH.");
            return null;
        }

        var address = GetAtSpiBusAddress();
        if (string.IsNullOrWhiteSpace(address))
        {
            LogOnce(processHint, focusedTitle, "AT-SPI URL walk skipped: a11y bus address not resolvable via gdbus.");
            return null;
        }

        using var cts = new CancellationTokenSource(WalkBudget);
        var stats = new WalkStats();
        var url = WalkForUrl(address, processHint, cts.Token, stats);

        lock (_cacheLock)
        {
            _cachedProcessName = processHint;
            _cachedTitle = focusedTitle;
            // Only write the cache on success — caching a null would
            // suppress retries for 10s, which is the wrong tradeoff
            // when the walker is just racing against its own budget.
            if (!string.IsNullOrWhiteSpace(url))
            {
                _cachedUrl = url;
                _cachedUrlAt = DateTime.UtcNow;
            }
        }

        LogOnce(processHint, focusedTitle, BuildDiagnosticLine(processHint, focusedTitle, stats, url, cts.IsCancellationRequested));
        return url;
    }

    // Diagnostic logging is normally off so the Error Log isn't polluted
    // by per-window walk summaries. Flip to true when debugging URL
    // detection: every unique walk outcome (changed apps-seen count,
    // changed candidate score, URL becoming available, walker giving up)
    // emits one line to the General error category. The walk-stats
    // plumbing stays compiled in either way so re-enabling is one edit.
    // Kept as static readonly (not const) so the C# compiler doesn't
    // flag the rest of LogOnce as unreachable code.
    private static readonly bool DiagnosticLoggingEnabled = false;

    private void LogOnce(string processHint, string? title, string message)
    {
        if (!DiagnosticLoggingEnabled) return;
        if (_errorLog is null) return;
        // Dedup by full message content — same window walking to the
        // same outcome should only log once, but a state change (apps-seen
        // count, walker scoring, URL appearing) is interesting and must
        // not be suppressed.
        lock (_cacheLock)
        {
            if (_lastDiagnosticKey == message) return;
            _lastDiagnosticKey = message;
        }
        _errorLog.AddEntry(message, ErrorCategory.General);
    }

    private static string BuildDiagnosticLine(string processHint, string? title, WalkStats stats, string? url, bool walkCancelled)
    {
        var sb = new StringBuilder();
        sb.Append("AT-SPI URL walk: process=").Append(processHint);
        if (!string.IsNullOrWhiteSpace(title))
        {
            sb.Append(" title='");
            sb.Append(title.Length > 60 ? title[..60] + "…" : title);
            sb.Append('\'');
        }
        sb.Append(" apps-seen=").Append(stats.AppsSeen.Count);
        if (stats.MatchedApp is null)
        {
            sb.Append(" matched-app=none seen=[");
            sb.Append(string.Join(",", stats.AppsSeen.Take(8)));
            if (stats.AppsSeen.Count > 8) sb.Append(",…");
            sb.Append(']');
        }
        else
        {
            sb.Append(" matched-app='").Append(stats.MatchedApp).Append('\'');
            sb.Append(" active-window=").Append(stats.WindowFound ? "yes" : "no");
            sb.Append(" nodes-walked=").Append(stats.NodesWalked);
            sb.Append(" best-score=").Append(stats.BestScore == int.MinValue ? "n/a" : stats.BestScore.ToString());
            if (!string.IsNullOrWhiteSpace(stats.BestCandidate))
            {
                sb.Append(" best-candidate='");
                var snippet = stats.BestCandidate.Length > 80 ? stats.BestCandidate[..80] + "…" : stats.BestCandidate;
                sb.Append(snippet);
                sb.Append('\'');
            }
        }
        sb.Append(" walk-cancelled=").Append(walkCancelled);
        sb.Append(" result=");
        sb.Append(string.IsNullOrEmpty(url) ? "null" : url);
        return sb.ToString();
    }

    private static string? WalkForUrl(string address, string processHint, CancellationToken ct, WalkStats stats)
    {
        foreach (var app in GetAccessibleChildren(address, new AccessibleRef(AtSpiRegistryBusName, AtSpiRootPath)))
        {
            if (ct.IsCancellationRequested) return null;

            var appName = GetAccessibleName(address, app);
            if (!string.IsNullOrWhiteSpace(appName))
                stats.AppsSeen.Add(appName);

            if (!IsMatchingApp(appName, processHint))
                continue;

            stats.MatchedApp = appName;

            var window = FindActiveBrowserWindow(address, app, ct);
            if (window is null)
                continue;
            stats.WindowFound = true;

            var url = FindLikelyBrowserUrlInSubtree(address, window.Value, ct, stats);
            if (url is not null)
                return url;
        }

        return null;
    }

    private sealed class WalkStats
    {
        public List<string> AppsSeen { get; } = new();
        public string? MatchedApp { get; set; }
        public bool WindowFound { get; set; }
        public int NodesWalked { get; set; }
        public int BestScore { get; set; } = int.MinValue;
        public string? BestCandidate { get; set; }
    }

    private static bool IsMatchingApp(string? identity, string processHint)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return false;

        if (string.Equals(identity, processHint, StringComparison.OrdinalIgnoreCase))
            return true;

        // AT-SPI app Name often differs from the process name by
        // capitalization or branding ("Firefox" vs "firefox", "Google
        // Chrome" vs "chrome"). Match by browser family so the walker
        // can bridge that gap — but only within the same family.
        // Without the family gate, when both Firefox and Chrome are on
        // the AT-SPI bus, the walker would happily accept whichever
        // app appeared first in the registry and could return Chrome's
        // address bar URL even though Firefox is the focused window.
        var identityFamily = ClassifyBrowserFamily(identity);
        var hintFamily = ClassifyBrowserFamily(processHint);
        return identityFamily is not null
            && hintFamily is not null
            && identityFamily == hintFamily;
    }

    /// <summary>
    /// Buckets a browser identity (AT-SPI app name) or process name
    /// into a coarse "family" used to gate fuzzy AT-SPI matching.
    /// Forks that share an engine share a family — Firefox + Zen +
    /// LibreWolf + Waterfox all bucket to "firefox" because they
    /// expose the same Gecko-shaped accessibility tree; Chrome +
    /// Chromium + Brave + Edge + Vivaldi + Opera bucket to "chromium"
    /// for the same reason. Returns null for anything that isn't a
    /// supported browser identity.
    /// </summary>
    private static string? ClassifyBrowserFamily(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var lower = value.ToLowerInvariant();

        if (lower.Contains("firefox")
            || lower.Contains("zen")
            || lower.Contains("librewolf")
            || lower.Contains("waterfox"))
            return "firefox";

        if (lower.Contains("chrome")
            || lower.Contains("chromium")
            || lower.Contains("brave")
            || lower.Contains("edge")
            || lower.Contains("vivaldi")
            || lower.Contains("opera"))
            return "chromium";

        return null;
    }

    private static AccessibleRef? FindActiveBrowserWindow(string address, AccessibleRef app, CancellationToken ct)
    {
        var queue = new Queue<(AccessibleRef Node, int Depth)>();
        queue.Enqueue((app, 0));

        while (queue.Count > 0)
        {
            if (ct.IsCancellationRequested) return null;

            var (node, depth) = queue.Dequeue();
            if (depth > 3)
                continue;

            var role = GetAccessibleRole(address, node);
            var states = GetAccessibleState(address, node);
            if ((role == AtSpiRoleFrame || role == AtSpiRoleWindow) && ActiveWindowService.HasState(states, AtSpiStateActive))
                return node;

            foreach (var child in GetAccessibleChildren(address, node))
                queue.Enqueue((child, depth + 1));
        }

        return null;
    }

    private static string? FindLikelyBrowserUrlInSubtree(string address, AccessibleRef root, CancellationToken ct, WalkStats stats)
    {
        var queue = new Queue<(AccessibleRef Node, int Depth)>();
        queue.Enqueue((root, 0));

        var seen = 0;
        string? bestUrl = null;
        var bestScore = int.MinValue;
        string? bestRawCandidate = null;

        while (queue.Count > 0 && seen < 500)
        {
            if (ct.IsCancellationRequested) break;

            var (node, depth) = queue.Dequeue();
            seen++;
            if (depth > 8)
                continue;

            // Firefox's accessibility tree has invisible structural
            // containers between the active window and the URL bar
            // (toolbar frames whose own SHOWING/VISIBLE flags aren't
            // set even though their children are visible). Previously
            // we skipped these AND their children — which meant the
            // walker pruned away the entire subtree containing the
            // URL element. Now we score only visible nodes but ALWAYS
            // descend so the URL bar is reachable from above.
            var states = GetAccessibleState(address, node);
            var isShowingVisible =
                ActiveWindowService.HasState(states, AtSpiStateShowing)
                && ActiveWindowService.HasState(states, AtSpiStateVisible);

            if (isShowingVisible)
            {
                var role = GetAccessibleRole(address, node);
                var name = GetAccessibleName(address, node);
                var interfaces = GetAccessibleInterfaces(address, node);
                var candidate = TryGetAccessibleText(address, node, interfaces) ?? name;
                var score = ActiveWindowService.ScoreBrowserUrlCandidate(role, states, name, candidate, interfaces);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestUrl = ActiveWindowService.SanitizeCapturedBrowserUrl(candidate);
                    bestRawCandidate = candidate;
                }
            }

            foreach (var child in GetAccessibleChildren(address, node))
                queue.Enqueue((child, depth + 1));
        }

        stats.NodesWalked = seen;
        stats.BestScore = bestScore;
        stats.BestCandidate = bestRawCandidate;
        return bestUrl;
    }

    private static string? GetAtSpiBusAddress()
    {
        var exitCode = RunProcess(
            "gdbus",
            "call --session --dest org.a11y.Bus --object-path /org/a11y/bus --method org.a11y.Bus.GetAddress",
            out var output);

        if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
            return null;

        var match = Regex.Match(output, @"\('(?<value>.+)'\s*,?\)");
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string? TryGetAccessibleText(string address, AccessibleRef node, IReadOnlyList<string> interfaces)
    {
        if (interfaces.Contains("org.a11y.atspi.Value", StringComparer.Ordinal))
        {
            var valueText = GetBusctlStringProperty(address, node.BusName, node.ObjectPath, "org.a11y.atspi.Value", "Text");
            if (!string.IsNullOrWhiteSpace(valueText))
                return valueText;
        }

        if (!interfaces.Contains("org.a11y.atspi.Text", StringComparer.Ordinal))
            return null;

        var characterCount = GetBusctlUInt32Property(address, node.BusName, node.ObjectPath, "org.a11y.atspi.Text", "CharacterCount");
        if (characterCount <= 0)
            return null;

        var output = RunBusctlCall(
            address,
            node.BusName,
            node.ObjectPath,
            "org.a11y.atspi.Text",
            "GetText",
            "ii",
            "0",
            characterCount.ToString());

        return ParseFirstQuotedString(output);
    }

    private static IReadOnlyList<AccessibleRef> GetAccessibleChildren(string address, AccessibleRef node)
    {
        var output = RunBusctlCall(address, node.BusName, node.ObjectPath, "org.a11y.atspi.Accessible", "GetChildren");
        if (string.IsNullOrWhiteSpace(output))
            return [];

        var values = ParseQuotedStrings(output);
        var children = new List<AccessibleRef>(values.Count / 2);
        for (var i = 0; i + 1 < values.Count; i += 2)
            children.Add(new AccessibleRef(values[i], values[i + 1]));

        return children;
    }

    private static IReadOnlyList<string> GetAccessibleInterfaces(string address, AccessibleRef node)
    {
        var output = RunBusctlCall(address, node.BusName, node.ObjectPath, "org.a11y.atspi.Accessible", "GetInterfaces");
        return string.IsNullOrWhiteSpace(output) ? [] : ParseQuotedStrings(output);
    }

    private static string? GetAccessibleName(string address, AccessibleRef node) =>
        GetBusctlStringProperty(address, node.BusName, node.ObjectPath, "org.a11y.atspi.Accessible", "Name");

    private static int GetAccessibleRole(string address, AccessibleRef node)
    {
        var output = RunBusctlCall(address, node.BusName, node.ObjectPath, "org.a11y.atspi.Accessible", "GetRole");
        return ParseLastInt(output);
    }

    private static IReadOnlyList<uint> GetAccessibleState(string address, AccessibleRef node)
    {
        var output = RunBusctlCall(address, node.BusName, node.ObjectPath, "org.a11y.atspi.Accessible", "GetState");
        if (string.IsNullOrWhiteSpace(output))
            return [];

        var ints = new List<uint>();
        foreach (Match match in Regex.Matches(output, @"\b\d+\b"))
        {
            if (uint.TryParse(match.Value, out var value))
                ints.Add(value);
        }

        return ints.Count > 1 ? ints[1..] : [];
    }

    private static string? GetBusctlStringProperty(string address, string destination, string path, string @interface, string property)
    {
        var output = RunBusctlGetProperty(address, destination, path, @interface, property);
        return ParseFirstQuotedString(output);
    }

    private static int GetBusctlUInt32Property(string address, string destination, string path, string @interface, string property)
    {
        var output = RunBusctlGetProperty(address, destination, path, @interface, property);
        return ParseLastInt(output);
    }

    private static string? RunBusctlCall(
        string address,
        string destination,
        string path,
        string @interface,
        string method,
        params string[] signatureAndArgs)
    {
        var args = new List<string>
        {
            $"--address={address}",
            "call",
            destination,
            path,
            @interface,
            method
        };
        args.AddRange(signatureAndArgs);

        var exitCode = RunProcess("busctl", args, out var output);
        return exitCode == 0 ? output?.Trim() : null;
    }

    private static string? RunBusctlGetProperty(string address, string destination, string path, string @interface, string property)
    {
        var exitCode = RunProcess(
            "busctl",
            [$"--address={address}", "get-property", destination, path, @interface, property],
            out var output);
        return exitCode == 0 ? output?.Trim() : null;
    }

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

    private static int RunProcess(string fileName, IReadOnlyList<string> args, out string? output)
    {
        output = null;

        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            using var p = Process.Start(startInfo);
            if (p is null) return -1;

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

    private static List<string> ParseQuotedStrings(string value) =>
        Regex.Matches(value, "\"((?:[^\"\\\\]|\\\\.)*)\"")
            .Select(match => Regex.Unescape(match.Groups[1].Value))
            .ToList();

    private static string? ParseFirstQuotedString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var values = ParseQuotedStrings(value);
        return values.Count > 0 ? values[0] : null;
    }

    private static int ParseLastInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        var match = Regex.Matches(value, @"\b\d+\b").LastOrDefault();
        return match is not null && int.TryParse(match.Value, out var result) ? result : 0;
    }

    internal readonly record struct AccessibleRef(string BusName, string ObjectPath);
}
