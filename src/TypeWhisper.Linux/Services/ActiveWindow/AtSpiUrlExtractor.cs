using System.Text;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
///     Focused AT-SPI walker that pulls the active URL from a browser's address bar.
///     1. Resolve the focused browser descriptor (AT-SPI walks cost 50–200 ms each).
///     2. Cache by <c>(descriptor ID, title)</c> — the URL changes when the title does; cache hits skip the walk.
///     3. Narrow to the matching AT-SPI app and walk only its tree.
///     4. Score showing/visible entries for likely URL candidates; return the best match.
///     Total walk budget: 2.5 s (the orchestrator's deferred-URL timeout is 4 s).
/// </summary>
public sealed partial class AtSpiUrlExtractor
{
    private const string AtSpiRegistryBusName = "org.a11y.atspi.Registry";
    private const string AtSpiRootPath = "/org/a11y/atspi/accessible/root";
    private const int AtSpiStateActive = 1;
    private const int AtSpiStateShowing = 25;
    private const int AtSpiStateVisible = 30;
    private const int AtSpiRoleFrame = 23;

    private const int AtSpiRoleWindow = 69;

    // Each busctl invocation is a separate process + D-Bus round-trip (50–200 ms each on
    // a busy system). Firefox's URL bar also sits under invisible structural containers, so
    // the walker descends into unseen subtrees — 2.5 s gives headroom while remaining
    // strictly inside the orchestrator's 4 s deferred-URL timeout.
    private static readonly TimeSpan s_walkBudget = TimeSpan.FromMilliseconds(2500);
    private static readonly TimeSpan s_cacheTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_missBackoff = TimeSpan.FromSeconds(10);

    // Diagnostic logging is off by default — flip to true to emit one line per unique
    // walk outcome to the Error Log. Kept as `static readonly` (not `const`) so the
    // compiler doesn't eliminate the LogOnce body as dead code.
    // ReSharper disable once ConvertToConstant.Local — intentionally not const (see above).
    private static readonly bool s_diagnosticLoggingEnabled = false;

    private readonly Lock _cacheLock = new();
    private readonly IErrorLogService? _errorLog;
    private readonly bool _isBusctlAvailable;
    private readonly bool _isGdbusAvailable;
    private readonly IProcessRunner _processRunner;
    private string? _cachedDescriptorId;
    private string? _cachedTitle;
    private string? _cachedUrl;
    private DateTime _cachedUrlAt;
    private string? _lastDiagnosticKey;
    private DateTime _missAt;
    private string? _missDescriptorId;
    private string? _missTitle;

    // Test seam standing in for the AT-SPI tree walk, so the cache/miss-backoff state machine
    // can be exercised without busctl, gdbus, or a live a11y bus. Always null in production.
    private readonly Func<string, string?>? _walkOverride;

    public AtSpiUrlExtractor()
        : this(new ProcessRunner())
    {
    }

    public AtSpiUrlExtractor(IErrorLogService? errorLog)
        : this(new ProcessRunner(), errorLog)
    {
    }

    public AtSpiUrlExtractor(
        IProcessRunner processRunner,
        IErrorLogService? errorLog = null
    )
    {
        _processRunner = processRunner;
        _errorLog = errorLog;
        _isBusctlAvailable = CheckCommandAvailable("busctl", ["--version"]);
        // gdbus has no --version flag (exits 1 with "Unknown command"). Probe
        // with `help`, which exits 0 and proves the binary is runnable.
        _isGdbusAvailable = CheckCommandAvailable("gdbus", ["help"]);
    }

    internal AtSpiUrlExtractor(IErrorLogService? errorLog, Func<string, string?>? walkOverride)
        : this(new ProcessRunner(), errorLog)
    {
        _walkOverride = walkOverride;
    }

    public string? TryGetBrowserUrl(
        string? focusedProcessName,
        string? focusedTitle,
        bool honorMissBackoff = false
    )
    {
        return TryGetBrowserUrl(
            focusedProcessName,
            null,
            focusedTitle,
            honorMissBackoff
        );
    }

    internal string? TryGetBrowserUrl(
        string? focusedProcessName,
        string? focusedAppId,
        string? focusedTitle,
        bool honorMissBackoff = false
    )
    {
        var browser = ResolveBrowserDescriptor(
            focusedProcessName,
            focusedAppId,
            focusedTitle
        );

        if (browser is null)
        {
            return null;
        }

        lock (_cacheLock)
        {
            var now = DateTime.UtcNow;

            // Cache key is (descriptor ID, title): title is the only signal for tab/page change.
            // Keying on process alone would return the previous tab's URL after a tab switch.
            // TTL caps stale trust for SPAs where navigation doesn't change the title.
            if (
                _cachedUrl is not null
                && string.Equals(
                    _cachedDescriptorId,
                    browser.Id,
                    StringComparison.OrdinalIgnoreCase
                )
                && string.Equals(_cachedTitle, focusedTitle, StringComparison.Ordinal)
                && now - _cachedUrlAt < s_cacheTtl
            )
            {
                return _cachedUrl;
            }

            // Miss backoff throttles high-frequency polling only; dictation passes false
            // so a poll miss on the same (descriptor, title) never suppresses its own walk.
            if (
                honorMissBackoff
                && string.Equals(_missDescriptorId, browser.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_missTitle, focusedTitle, StringComparison.Ordinal)
                && now - _missAt < s_missBackoff
            )
            {
                return null;
            }
        }

        string? url;
        var stats = new WalkStats();
        var budgetExhausted = false;

        if (_walkOverride is not null)
        {
            url = _walkOverride(browser.CanonicalProcessName);
        }
        else
        {
            if (!_isBusctlAvailable || !_isGdbusAvailable)
            {
                LogOnce("AT-SPI URL walk skipped: busctl/gdbus not on PATH.");
                return null;
            }

            var address = GetAtSpiBusAddress();
            if (string.IsNullOrWhiteSpace(address))
            {
                LogOnce("AT-SPI URL walk skipped: a11y bus address not resolvable via gdbus.");
                return null;
            }

            using var cts = new CancellationTokenSource(s_walkBudget);
            url = WalkForUrl(address, browser, stats, cts.Token);
            budgetExhausted = cts.IsCancellationRequested;
        }

        lock (_cacheLock)
        {
            // Only cache successes — caching null here would re-key a still-valid previous
            // URL. Misses are tracked separately so a title change retries immediately.
            if (!string.IsNullOrWhiteSpace(url))
            {
                _cachedDescriptorId = browser.Id;
                _cachedTitle = focusedTitle;
                _cachedUrl = url;
                _cachedUrlAt = DateTime.UtcNow;
                _missDescriptorId = null;
                _missTitle = null;
                _missAt = default;
            }
            else if (honorMissBackoff)
            {
                // Dictation misses must not throttle the next poll on this (descriptor, title).
                _missDescriptorId = browser.Id;
                _missTitle = focusedTitle;
                _missAt = DateTime.UtcNow;
            }
        }

        LogOnce(
            BuildDiagnosticLine(
                browser.CanonicalProcessName,
                focusedTitle,
                stats,
                url,
                budgetExhausted
            )
        );
        return url;
    }

    private void LogOnce(string message)
    {
        if (!s_diagnosticLoggingEnabled)
        {
            return;
        }

        if (_errorLog is null)
        {
            return;
        }

        // Dedup by full message content — same window walking to the
        // same outcome should only log once, but a state change (apps-seen
        // count, walker scoring, URL appearing) is interesting and must
        // not be suppressed.
        lock (_cacheLock)
        {
            if (_lastDiagnosticKey == message)
            {
                return;
            }

            _lastDiagnosticKey = message;
        }

        _errorLog.AddEntry(message, ErrorCategory.Detection);
    }

    private static string BuildDiagnosticLine(
        string processHint,
        string? title,
        WalkStats stats,
        string? url,
        bool walkCancelled
    )
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
            if (stats.AppsSeen.Count > 8)
            {
                sb.Append(",…");
            }

            sb.Append(']');
        }
        else
        {
            sb.Append(" matched-app='").Append(stats.MatchedApp).Append('\'');
            sb.Append(" active-window=").Append(stats.WindowFound ? "yes" : "no");
            sb.Append(" nodes-walked=").Append(stats.NodesWalked);
            sb.Append(" best-score=")
                .Append(stats.BestScore == int.MinValue ? "n/a" : stats.BestScore.ToString());
            if (!string.IsNullOrWhiteSpace(stats.BestCandidate))
            {
                sb.Append(" best-candidate='");
                var snippet =
                    stats.BestCandidate.Length > 80
                        ? stats.BestCandidate[..80] + "…"
                        : stats.BestCandidate;
                sb.Append(snippet);
                sb.Append('\'');
            }
        }

        sb.Append(" walk-cancelled=").Append(walkCancelled);
        sb.Append(" result=");
        sb.Append(string.IsNullOrEmpty(url) ? "null" : url);
        return sb.ToString();
    }

    private string? WalkForUrl(
        string address,
        BrowserDescriptor focusedBrowser,
        WalkStats stats,
        CancellationToken ct
    )
    {
        foreach (
            var app in GetAccessibleChildren(
                address,
                new AccessibleRef(AtSpiRegistryBusName, AtSpiRootPath)
            )
        )
        {
            if (ct.IsCancellationRequested)
            {
                return null;
            }

            var appName = GetAccessibleName(address, app);
            if (!string.IsNullOrWhiteSpace(appName))
            {
                stats.AppsSeen.Add(appName);
            }

            if (!IsMatchingApp(appName, focusedBrowser))
            {
                continue;
            }

            stats.MatchedApp = appName;

            var window = FindActiveBrowserWindow(address, app, ct);
            if (window is null)
            {
                continue;
            }

            stats.WindowFound = true;

            var url = FindLikelyBrowserUrlInSubtree(address, window.Value, stats, ct);
            if (url is not null)
            {
                return url;
            }
        }

        return null;
    }

    internal static BrowserDescriptor? ResolveBrowserDescriptor(
        string? focusedProcessName,
        string? focusedAppId,
        string? focusedTitle
    )
    {
        return BrowserDescriptorCatalog.Resolve(
            focusedProcessName,
            focusedAppId,
            focusedTitle,
            BrowserCapabilities.AtSpiExtraction
        );
    }

    internal static bool IsMatchingApp(string? identity, BrowserDescriptor focusedBrowser)
    {
        var applicationBrowser = BrowserDescriptorCatalog.ResolveWindowIdentity(
            identity,
            BrowserCapabilities.AtSpiExtraction
        );
        return applicationBrowser?.Id == focusedBrowser.Id;
    }

    private AccessibleRef? FindActiveBrowserWindow(
        string address,
        AccessibleRef app,
        CancellationToken ct
    )
    {
        var queue = new Queue<(AccessibleRef Node, int Depth)>();
        queue.Enqueue((app, 0));

        while (queue.Count > 0)
        {
            if (ct.IsCancellationRequested)
            {
                return null;
            }

            var (node, depth) = queue.Dequeue();
            if (depth > 3)
            {
                continue;
            }

            var role = GetAccessibleRole(address, node);
            var states = GetAccessibleState(address, node);
            if (
                role is AtSpiRoleFrame or AtSpiRoleWindow
                && ActiveWindowService.HasState(states, AtSpiStateActive)
            )
            {
                return node;
            }

            foreach (var child in GetAccessibleChildren(address, node))
            {
                queue.Enqueue((child, depth + 1));
            }
        }

        return null;
    }

    private string? FindLikelyBrowserUrlInSubtree(
        string address,
        AccessibleRef root,
        WalkStats stats,
        CancellationToken ct
    )
    {
        var queue = new Queue<(AccessibleRef Node, int Depth)>();
        queue.Enqueue((root, 0));

        var seen = 0;
        string? bestUrl = null;
        var bestScore = int.MinValue;
        string? bestRawCandidate = null;

        while (queue.Count > 0 && seen < 500)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            var (node, depth) = queue.Dequeue();
            seen++;
            if (depth > 8)
            {
                continue;
            }

            // Firefox has invisible structural containers above the URL bar (toolbar frames
            // whose own SHOWING/VISIBLE flags aren't set). Score only visible nodes but
            // ALWAYS descend so the URL bar is reachable through those invisible parents.
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
                var score = ActiveWindowService.ScoreBrowserUrlCandidate(
                    role,
                    states,
                    name,
                    candidate,
                    interfaces
                );
                if (score > bestScore)
                {
                    bestScore = score;
                    bestUrl = ActiveWindowService.SanitizeCapturedBrowserUrl(candidate);
                    bestRawCandidate = candidate;
                }
            }

            foreach (var child in GetAccessibleChildren(address, node))
            {
                queue.Enqueue((child, depth + 1));
            }
        }

        stats.NodesWalked = seen;
        stats.BestScore = bestScore;
        stats.BestCandidate = bestRawCandidate;
        return bestUrl;
    }

    private string? GetAtSpiBusAddress()
    {
        var exitCode = RunProcess(
            "gdbus",
            [
                "call",
                "--session",
                "--dest",
                "org.a11y.Bus",
                "--object-path",
                "/org/a11y/bus",
                "--method",
                "org.a11y.Bus.GetAddress",
            ],
            out var output
        );

        if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var match = TupleValueRegex().Match(output);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private string? TryGetAccessibleText(
        string address,
        AccessibleRef node,
        IReadOnlyList<string> interfaces
    )
    {
        if (interfaces.Contains("org.a11y.atspi.Value", StringComparer.Ordinal))
        {
            var valueText = GetBusctlStringProperty(
                address,
                node.BusName,
                node.ObjectPath,
                "org.a11y.atspi.Value",
                "Text"
            );
            if (!string.IsNullOrWhiteSpace(valueText))
            {
                return valueText;
            }
        }

        if (!interfaces.Contains("org.a11y.atspi.Text", StringComparer.Ordinal))
        {
            return null;
        }

        var characterCount = GetBusctlUInt32Property(
            address,
            node.BusName,
            node.ObjectPath,
            "org.a11y.atspi.Text",
            "CharacterCount"
        );
        if (characterCount <= 0)
        {
            return null;
        }

        var output = RunBusctlCall(
            address,
            node.BusName,
            node.ObjectPath,
            "org.a11y.atspi.Text",
            "GetText",
            "ii",
            "0",
            characterCount.ToString()
        );

        return ParseFirstQuotedString(output);
    }

    private List<AccessibleRef> GetAccessibleChildren(
        string address,
        AccessibleRef node
    )
    {
        var output = RunBusctlCall(
            address,
            node.BusName,
            node.ObjectPath,
            "org.a11y.atspi.Accessible",
            "GetChildren"
        );
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var values = ParseQuotedStrings(output);
        var children = new List<AccessibleRef>(values.Count / 2);
        for (var i = 0; i + 1 < values.Count; i += 2)
        {
            children.Add(new AccessibleRef(values[i], values[i + 1]));
        }

        return children;
    }

    private List<string> GetAccessibleInterfaces(string address, AccessibleRef node)
    {
        var output = RunBusctlCall(
            address,
            node.BusName,
            node.ObjectPath,
            "org.a11y.atspi.Accessible",
            "GetInterfaces"
        );
        return string.IsNullOrWhiteSpace(output) ? [] : ParseQuotedStrings(output);
    }

    private string? GetAccessibleName(string address, AccessibleRef node)
    {
        return GetBusctlStringProperty(
            address,
            node.BusName,
            node.ObjectPath,
            "org.a11y.atspi.Accessible",
            "Name"
        );
    }

    private int GetAccessibleRole(string address, AccessibleRef node)
    {
        var output = RunBusctlCall(
            address,
            node.BusName,
            node.ObjectPath,
            "org.a11y.atspi.Accessible",
            "GetRole"
        );
        return ParseLastInt(output);
    }

    private IReadOnlyList<uint> GetAccessibleState(string address, AccessibleRef node)
    {
        var output = RunBusctlCall(
            address,
            node.BusName,
            node.ObjectPath,
            "org.a11y.atspi.Accessible",
            "GetState"
        );
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var ints = new List<uint>();
        foreach (Match match in DigitRunRegex().Matches(output))
        {
            if (uint.TryParse(match.Value, out var value))
            {
                ints.Add(value);
            }
        }

        return ints.Count > 1 ? ints[1..] : [];
    }

    private string? GetBusctlStringProperty(
        string address,
        string destination,
        string path,
        string @interface,
        string property
    )
    {
        var output = RunBusctlGetProperty(address, destination, path, @interface, property);
        return ParseFirstQuotedString(output);
    }

    private int GetBusctlUInt32Property(
        string address,
        string destination,
        string path,
        string @interface,
        string property
    )
    {
        var output = RunBusctlGetProperty(address, destination, path, @interface, property);
        return ParseLastInt(output);
    }

    private string? RunBusctlCall(
        string address,
        string destination,
        string path,
        string @interface,
        string method,
        params string[] signatureAndArgs
    )
    {
        var args = new List<string>
        {
            $"--address={address}",
            "call",
            destination,
            path,
            @interface,
            method,
        };
        args.AddRange(signatureAndArgs);

        var exitCode = RunProcess("busctl", args, out var output);
        return exitCode == 0 ? output?.Trim() : null;
    }

    private string? RunBusctlGetProperty(
        string address,
        string destination,
        string path,
        string @interface,
        string property
    )
    {
        var exitCode = RunProcess(
            "busctl",
            [$"--address={address}", "get-property", destination, path, @interface, property],
            out var output
        );
        return exitCode == 0 ? output?.Trim() : null;
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

    private static List<string> ParseQuotedStrings(string value)
    {
        return QuotedStringRegex()
            .Matches(value)
            .Select(match => Regex.Unescape(match.Groups[1].Value))
            .ToList();
    }

    private static string? ParseFirstQuotedString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var values = ParseQuotedStrings(value);
        return values.Count > 0 ? values[0] : null;
    }

    private static int ParseLastInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var match = DigitRunRegex().Matches(value).LastOrDefault();
        return match is not null && int.TryParse(match.Value, out var result) ? result : 0;
    }

    // Single-value gdbus tuple: ('value',).
    [GeneratedRegex(@"\('(?<value>.+)'\s*,?\)")]
    private static partial Regex TupleValueRegex();

    // Runs of digits (used to pull integer tokens out of gdbus/atspi output).
    [GeneratedRegex(@"\b\d+\b")]
    private static partial Regex DigitRunRegex();

    // Double-quoted string with backslash escapes; group 1 is the (still-escaped) body.
    [GeneratedRegex("\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex QuotedStringRegex();

    private sealed class WalkStats
    {
        public List<string> AppsSeen { get; } = [];
        public string? MatchedApp { get; set; }
        public bool WindowFound { get; set; }
        public int NodesWalked { get; set; }
        public int BestScore { get; set; } = int.MinValue;
        public string? BestCandidate { get; set; }
    }

    internal readonly record struct AccessibleRef(string BusName, string ObjectPath);
}
