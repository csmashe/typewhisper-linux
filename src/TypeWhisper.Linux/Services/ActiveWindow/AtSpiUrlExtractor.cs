using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
///     Focused AT-SPI walker that pulls the active URL from a browser's address bar.
///     1. Gate on focused process name (AT-SPI walks cost 50–200 ms each).
///     2. Cache by <c>(processName, title)</c> — the URL changes when the title does; cache hits skip the walk.
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

    // Focused-context harvest (Feature 03). Independent of the URL walk: targets the
    // single focused element (+ nearby labels) so Medium/High cleanup can match the
    // spelling of names/identifiers the user is looking at. Every bound is deliberate —
    // the harvest must never add user-perceived latency.
    private const string AtSpiCollectionInterface = "org.a11y.atspi.Collection";

    // AtspiStateType: FOCUSED is 12 (11 is FOCUSABLE — every control is focusable, so matching
    // 11 would harvest the first focusable node, not the one actually in focus).
    private const int AtSpiStateFocused = 12;

    // ATSPI_ROLE_PASSWORD_TEXT — never read (or descend into) a password field.
    private const int AtSpiRolePasswordText = 40;
    private const int HarvestNodeVisitCap = 40;
    private const int HarvestBfsMaxDepth = 12;
    private const int HarvestMaxNearbyLabels = 8;
    private const int HarvestMaxOutputChars = 2500;
    private const int HarvestWindowAncestorCap = 12;

    // Separate, tighter budget than the 2.5 s URL walk: the harvest runs in the
    // background snapshot task and must finish well inside the 4 s stop ceiling.
    private static readonly TimeSpan s_harvestBudget = TimeSpan.FromMilliseconds(1000);

    // The a11y bus address never changes for the process lifetime, so resolving it
    // once avoids re-spawning gdbus on every capture. Guarded by its own lock; the
    // "resolved" flag distinguishes "resolved to null" (unavailable) from "not yet tried".
    private static readonly Lock s_busAddressLock = new();
    private static string? s_cachedBusAddress;
    private static bool s_busAddressResolved;

    // Each busctl invocation is a separate process + D-Bus round-trip (50–200 ms each on
    // a busy system). Firefox's URL bar also sits under invisible structural containers, so
    // the walker descends into unseen subtrees — 2.5 s gives headroom while remaining
    // strictly inside the orchestrator's 4 s deferred-URL timeout.
    private static readonly TimeSpan s_walkBudget = TimeSpan.FromMilliseconds(2500);
    private static readonly bool s_isBusctlAvailable = CheckCommandAvailable("busctl", "--version");

    // gdbus has no --version flag (exits 1 with "Unknown command"). Probe
    // with `help`, which exits 0 and proves the binary is runnable.
    private static readonly bool s_isGdbusAvailable = CheckCommandAvailable("gdbus", "help");

    private static readonly HashSet<string> s_supportedBrowserProcessNames = new(
        StringComparer.OrdinalIgnoreCase
    )
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
        "zen-bin"
    };

    private static readonly TimeSpan s_cacheTtl = TimeSpan.FromSeconds(10);

    // Diagnostic logging is off by default — flip to true to emit one line per unique
    // walk outcome to the Error Log. Kept as `static readonly` (not `const`) so the
    // compiler doesn't eliminate the LogOnce body as dead code.
    // ReSharper disable once ConvertToConstant.Local — intentionally not const (see above).
    private static readonly bool s_diagnosticLoggingEnabled = false;

    private readonly Lock _cacheLock = new();
    private readonly IErrorLogService? _errorLog;
    private string? _cachedProcessName;
    private string? _cachedTitle;
    private string? _cachedUrl;
    private DateTime _cachedUrlAt;
    private string? _lastDiagnosticKey;

    public AtSpiUrlExtractor()
        : this(null)
    {
    }

    public AtSpiUrlExtractor(IErrorLogService? errorLog)
    {
        _errorLog = errorLog;
    }

    public string? TryGetBrowserUrl(string? focusedProcessName, string? focusedTitle)
    {
        var processHint = !string.IsNullOrWhiteSpace(focusedProcessName)
            ? focusedProcessName
            : ActiveWindowService.TryInferBrowserProcessNameFromTitle(focusedTitle);

        if (
            string.IsNullOrWhiteSpace(processHint)
            || !s_supportedBrowserProcessNames.Contains(processHint)
        )
        {
            return null;
        }

        lock (_cacheLock)
        {
            // Cache key is (process, title): title is the only signal for tab/page change.
            // Keying on process alone would return the previous tab's URL after a tab switch.
            // TTL caps stale trust for SPAs where navigation doesn't change the title.
            if (
                _cachedUrl is not null
                && string.Equals(
                    _cachedProcessName,
                    processHint,
                    StringComparison.OrdinalIgnoreCase
                )
                && string.Equals(_cachedTitle, focusedTitle, StringComparison.Ordinal)
                && DateTime.UtcNow - _cachedUrlAt < s_cacheTtl
            )
            {
                return _cachedUrl;
            }
        }

        if (!s_isBusctlAvailable || !s_isGdbusAvailable)
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
        var stats = new WalkStats();
        var url = WalkForUrl(address, processHint, stats, cts.Token);

        lock (_cacheLock)
        {
            // Only cache successes — caching null would suppress retries for 10 s,
            // and updating keys on miss would re-key a still-valid previous URL.
            if (!string.IsNullOrWhiteSpace(url))
            {
                _cachedProcessName = processHint;
                _cachedTitle = focusedTitle;
                _cachedUrl = url;
                _cachedUrlAt = DateTime.UtcNow;
            }
        }

        LogOnce(
            BuildDiagnosticLine(processHint, focusedTitle, stats, url, cts.IsCancellationRequested)
        );
        return url;
    }

    /// <summary>
    ///     Harvests a bounded snippet of the focused element's text (+ nearby labels) so the
    ///     cleanup LLM can match the spelling of proper nouns / identifiers on screen. Opt-in
    ///     and gated by the caller; this method assumes the toggle is already effective.
    ///     Returns null instantly when busctl/gdbus is unavailable or the app exposes no a11y
    ///     tree. Password fields are never read. Hard caps: ~1 s wall-clock, ~40 node visits,
    ///     ~2500-char output.
    /// </summary>
    // kept instance: part of the injected extractor's public API, mirroring TryGetBrowserUrl
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "kept instance: injected as a DI/test seam, consistent with TryGetBrowserUrl")]
    // ReSharper disable once MemberCanBeMadeStatic.Global
    public string? TryHarvestFocusedContext(
        string? processName,
        string? title,
        string? selfAppName
    )
    {
        if (!s_isBusctlAvailable || !s_isGdbusAvailable)
        {
            return null;
        }

        var address = GetCachedAtSpiBusAddress();
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        using var cts = new CancellationTokenSource(s_harvestBudget);
        try
        {
            return HarvestFocusedContext(address, processName, title, selfAppName, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[AtSpiUrlExtractor] Focused-context harvest failed: {ex.Message}");
            return null;
        }
    }

    private static string? HarvestFocusedContext(
        string address,
        string? processName,
        string? title,
        string? selfAppName,
        CancellationToken ct
    )
    {
        var remainingVisits = HarvestNodeVisitCap;

        // Without any window hint (no process AND no title — e.g. Wayland without xdotool) we
        // can't scope the harvest to the recorded window, so we must not read whatever happens
        // to be focused: a focus change could feed an unrelated app's screen into cleanup. Bail.
        if (string.IsNullOrWhiteSpace(processName) && string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator -- body has early-return, ref-counter mutation, and per-app subprocess calls; a LINQ rewrite would obscure the bounded walk
        foreach (
            var app in GetAccessibleChildren(
                address,
                new AccessibleRef(AtSpiRegistryBusName, AtSpiRootPath)
            )
        )
        {
            if (ct.IsCancellationRequested || remainingVisits <= 0)
            {
                break;
            }

            var appName = GetAccessibleName(address, app);
            if (IsSelfApp(appName, selfAppName))
            {
                continue;
            }

            if (!IsFocusTargetApp(appName, processName, title))
            {
                continue;
            }

            var focused = FindFocusedElement(address, app, ref remainingVisits, ct);
            if (focused is null)
            {
                continue;
            }

            // An app node hosts every window of the process (all Firefox windows share one app
            // node), so the focused element could belong to a different window of the same app
            // if focus moved. When we know the recorded window's title, require the focused
            // node's top-level frame title to relate to it before reading — otherwise a focus
            // switch to another same-app window could feed unrelated on-screen text into cleanup.
            if (!FocusedNodeBelongsToWindow(address, focused.Value, title, ref remainingVisits, ct))
            {
                continue;
            }

            var context = ReadFocusedContext(address, focused.Value, ref remainingVisits, ct);
            if (!string.IsNullOrWhiteSpace(context))
            {
                return context;
            }
        }

        return null;
    }

    private static bool IsSelfApp(string? appName, string? selfAppName)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return false;
        }

        if (
            !string.IsNullOrWhiteSpace(selfAppName)
            && appName.Contains(selfAppName, StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        return appName.Contains("typewhisper", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFocusTargetApp(string? appName, string? processHint, string? title)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return false;
        }

        // Exact app-identity match only — deliberately NOT IsMatchingApp: its browser-family
        // aliasing (Edge ↔ Chrome ↔ Brave all "chromium") could harvest a *different* browser's
        // focused window, and screen text is privacy-sensitive. Family bridging is fine for the
        // URL walk (one app on the bus) but wrong when scoping a capture to the recorded window.
        if (
            !string.IsNullOrWhiteSpace(processHint)
            && string.Equals(appName, processHint, StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        // The AT-SPI app Name often differs from the process name but appears in the window
        // title (process "code" ↔ Name "Visual Studio Code" ↔ title "file — Visual Studio Code").
        // Require the Name to be a *trailing* segment of the title — window titles conventionally
        // end with the app name — rather than any substring, so an app merely *mentioned*
        // mid-title (a document/tab named after another app) can't be mistaken for the window.
        return !string.IsNullOrWhiteSpace(title)
               && appName.Length >= 3
               && title.TrimEnd().EndsWith(appName, StringComparison.OrdinalIgnoreCase);
    }

    private static AccessibleRef? FindFocusedElement(
        string address,
        AccessibleRef app,
        ref int remainingVisits,
        CancellationToken ct
    )
    {
        // Fast path: a single Collection.GetMatches round trip returns the focused node
        // directly (traverse=true searches the whole app subtree). Validate the returned
        // node actually reports STATE_FOCUSED so a malformed/garbage result can't leak.
        // ThrowIfCancellationRequested before each subprocess call keeps a hung a11y app from
        // accumulating multiple ~1 s busctl waits past the harvest budget (caught at the top).
        ct.ThrowIfCancellationRequested();
        var interfaces = GetAccessibleInterfaces(address, app);
        var match = interfaces.Contains(AtSpiCollectionInterface, StringComparer.Ordinal)
            ? TryGetCollectionFocusedMatch(address, app)
            : null;
        if (
            match is not null
            && !ct.IsCancellationRequested
            && ActiveWindowService.HasState(
                GetAccessibleState(address, match.Value),
                AtSpiStateFocused
            )
        )
        {
            return match;
        }

        // Fallback: tightly-bounded BFS for the first STATE_FOCUSED node.
        ct.ThrowIfCancellationRequested();
        return FindFocusedNodeBfs(address, app, ref remainingVisits, ct);
    }

    private static AccessibleRef? TryGetCollectionFocusedMatch(string address, AccessibleRef app)
    {
        // MatchRule signature (aiia{ss}iaiiasib): states=[1<<12, 0] (STATE_FOCUSED = bit 12),
        // stateMatch=ANY(2), empty attributes/roles/interfaces, invert=false; then
        // sortby=CANONICAL(0), count=1, traverse=true. Result is a(so) like GetChildren.
        var output = RunBusctlCall(
            address,
            app.BusName,
            app.ObjectPath,
            AtSpiCollectionInterface,
            "GetMatches",
            "(aiia{ss}iaiiasib)uib",
            "2",
            "4096",
            "0",
            "2",
            "0",
            "0",
            "0",
            "0",
            "0",
            "0",
            "false",
            "0",
            "1",
            "true"
        );
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var values = ParseQuotedStrings(output);
        return values.Count >= 2 ? new AccessibleRef(values[0], values[1]) : null;
    }

    private static AccessibleRef? FindFocusedNodeBfs(
        string address,
        AccessibleRef app,
        ref int remainingVisits,
        CancellationToken ct
    )
    {
        var queue = new Queue<(AccessibleRef Node, int Depth)>();
        queue.Enqueue((app, 0));

        while (queue.Count > 0 && remainingVisits > 0)
        {
            if (ct.IsCancellationRequested)
            {
                return null;
            }

            var (node, depth) = queue.Dequeue();
            remainingVisits--;

            var states = GetAccessibleState(address, node);
            if (ActiveWindowService.HasState(states, AtSpiStateFocused))
            {
                return node;
            }

            if (depth >= HarvestBfsMaxDepth || ct.IsCancellationRequested)
            {
                continue;
            }

            foreach (var child in GetAccessibleChildren(address, node))
            {
                queue.Enqueue((child, depth + 1));
            }
        }

        return null;
    }

    private static string? ReadFocusedContext(
        string address,
        AccessibleRef focused,
        ref int remainingVisits,
        CancellationToken ct
    )
    {
        // Cancellation checks before each subprocess call keep the harvest inside its budget
        // even if this app's a11y calls block (each check throws to the top-level catch).
        ct.ThrowIfCancellationRequested();
        var interfaces = GetAccessibleInterfaces(address, focused);
        ct.ThrowIfCancellationRequested();
        var role = GetAccessibleRole(address, focused);
        if (IsPasswordTextRole(role))
        {
            // Never read a password field, and never descend to its labels.
            return null;
        }

        var raw = new List<string?>
        {
            TryGetAccessibleText(address, focused, interfaces, HarvestMaxOutputChars)
            ?? GetAccessibleName(address, focused)
        };

        // Nearby labels: the focused node's parent's showing+visible, text-bearing children.
        ct.ThrowIfCancellationRequested();
        var parent = GetAccessibleParent(address, focused);
        // ReSharper disable once InvertIf -- inverting to a guard would duplicate the trailing CombineFocusedSnippets return; the single-return form is clearer
        if (parent is not null)
        {
            var labels = 0;
            foreach (var sibling in GetAccessibleChildren(address, parent.Value))
            {
                if (
                    ct.IsCancellationRequested
                    || labels >= HarvestMaxNearbyLabels
                    || remainingVisits <= 0
                )
                {
                    break;
                }

                if (sibling.Equals(focused))
                {
                    continue;
                }

                remainingVisits--;
                var states = GetAccessibleState(address, sibling);
                if (
                    !ActiveWindowService.HasState(states, AtSpiStateShowing)
                    || !ActiveWindowService.HasState(states, AtSpiStateVisible)
                )
                {
                    continue;
                }

                // Never read a password field's text/name, even as a "nearby label": a
                // password input sitting next to the focused control would otherwise be
                // captured and forwarded to LLM cleanup. Same guard as the focused node.
                ct.ThrowIfCancellationRequested();
                if (IsPasswordTextRole(GetAccessibleRole(address, sibling)))
                {
                    continue;
                }

                var siblingInterfaces = GetAccessibleInterfaces(address, sibling);
                var siblingText =
                    TryGetAccessibleText(address, sibling, siblingInterfaces, HarvestMaxOutputChars)
                    ?? GetAccessibleName(address, sibling);
                if (string.IsNullOrWhiteSpace(siblingText))
                {
                    continue;
                }

                raw.Add(siblingText);
                labels++;
            }
        }

        return CombineFocusedSnippets(raw, HarvestMaxOutputChars);
    }

    /// <summary>
    ///     Confirms the focused node lives under the recorded window: walk up to the nearest
    ///     frame/window ancestor and require its title to relate to <paramref name="recordedTitle" />.
    ///     Best-effort — accepts (doesn't reject) when the title is unknown or the frame's own
    ///     title can't be read, so a legitimate harvest is never dropped on missing data; only a
    ///     positively-different window title rejects.
    /// </summary>
    private static bool FocusedNodeBelongsToWindow(
        string address,
        AccessibleRef focused,
        string? recordedTitle,
        ref int remainingVisits,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(recordedTitle))
        {
            return true;
        }

        var node = focused;
        for (var i = 0; i < HarvestWindowAncestorCap && remainingVisits > 0; i++)
        {
            ct.ThrowIfCancellationRequested();
            remainingVisits--;
            var role = GetAccessibleRole(address, node);
            if (role is AtSpiRoleFrame or AtSpiRoleWindow)
            {
                var frameTitle = GetAccessibleName(address, node);
                // Unreadable frame title → can't disprove; keep best-effort behavior.
                return string.IsNullOrWhiteSpace(frameTitle) || TitlesRelate(frameTitle, recordedTitle);
            }

            ct.ThrowIfCancellationRequested();
            var parent = GetAccessibleParent(address, node);
            if (parent is null)
            {
                break;
            }

            node = parent.Value;
        }

        return true;
    }

    /// <summary>
    ///     True when two window titles plausibly name the same window: exact/contains-either-way,
    ///     case-insensitive. The compositor snapshot title and the AT-SPI frame Name derive from
    ///     the same window title in GTK/Qt, so this stays lenient to avoid false rejections while
    ///     still rejecting a clearly different window's title.
    /// </summary>
    internal static bool TitlesRelate(string? a, string? b)
    {
        var x = a?.Trim();
        var y = b?.Trim();
        if (string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y))
        {
            return false;
        }

        return x.Contains(y, StringComparison.OrdinalIgnoreCase)
               || y.Contains(x, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Collapses whitespace, drops adjacent duplicate snippets, and hard-caps the joined
    ///     length. Pure so the harvest's text-shaping is unit-testable without a live bus.
    /// </summary>
    internal static string? CombineFocusedSnippets(IReadOnlyList<string?> rawSnippets, int maxChars)
    {
        var collected = new List<string>();
        var total = 0;
        foreach (var raw in rawSnippets)
        {
            if (total >= maxChars)
            {
                break;
            }

            var snippet = CollapseWhitespace(raw);
            if (snippet.Length == 0)
            {
                continue;
            }

            // Drop adjacent repeats (a labelled field often exposes its label twice).
            if (collected.Count > 0 && string.Equals(collected[^1], snippet, StringComparison.Ordinal))
            {
                continue;
            }

            var separatorLength = collected.Count > 0 ? 1 : 0;
            var budget = maxChars - total - separatorLength;
            if (budget <= 0)
            {
                break;
            }

            if (snippet.Length > budget)
            {
                snippet = snippet[..budget];
            }

            collected.Add(snippet);
            total += snippet.Length + separatorLength;
        }

        return collected.Count == 0 ? null : string.Join('\n', collected);
    }

    internal static string CollapseWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    internal static bool IsPasswordTextRole(int role)
    {
        return role == AtSpiRolePasswordText;
    }

    private static string? GetCachedAtSpiBusAddress()
    {
        lock (s_busAddressLock)
        {
            if (s_busAddressResolved)
            {
                return s_cachedBusAddress;
            }

            s_cachedBusAddress = GetAtSpiBusAddress();
            s_busAddressResolved = true;
            return s_cachedBusAddress;
        }
    }

    private static AccessibleRef? GetAccessibleParent(string address, AccessibleRef node)
    {
        var output = RunBusctlGetProperty(
            address,
            node.BusName,
            node.ObjectPath,
            "org.a11y.atspi.Accessible",
            "Parent"
        );
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var values = ParseQuotedStrings(output);
        if (values.Count < 2)
        {
            return null;
        }

        var busName = values[0];
        var path = values[1];
        // A null parent is reported as an empty bus name / the "…/null" sentinel path.
        if (string.IsNullOrEmpty(busName) || path.EndsWith("/null", StringComparison.Ordinal))
        {
            return null;
        }

        return new AccessibleRef(busName, path);
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

    private static string? WalkForUrl(
        string address,
        string processHint,
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

            if (!IsMatchingApp(appName, processHint))
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

    private static bool IsMatchingApp(string? identity, string processHint)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            return false;
        }

        if (string.Equals(identity, processHint, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // AT-SPI app Name often differs from the process name ("Firefox" vs "firefox",
        // "Google Chrome" vs "chrome"). Match within the same browser family so the walker
        // can bridge that gap without accepting a different browser's URL. Without the
        // family gate, when Firefox and Chrome are both on the bus the walker could return
        // Chrome's URL when Firefox is the focused window.
        var identityFamily = ClassifyBrowserFamily(identity);
        var hintFamily = ClassifyBrowserFamily(processHint);
        return identityFamily is not null && hintFamily is not null && identityFamily == hintFamily;
    }

    /// <summary>
    ///     Maps a browser identity or process name to its engine family ("firefox" or "chromium").
    ///     Forks sharing an engine share a family (Firefox/Zen/LibreWolf/Waterfox → "firefox";
    ///     Chrome/Chromium/Brave/Edge/Vivaldi/Opera → "chromium"). Returns null for unknown identities.
    /// </summary>
    private static string? ClassifyBrowserFamily(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var lower = value.ToLowerInvariant();

        if (
            lower.Contains("firefox")
            || lower.Contains("zen")
            || lower.Contains("librewolf")
            || lower.Contains("waterfox")
        )
        {
            return "firefox";
        }

        if (
            lower.Contains("chrome")
            || lower.Contains("chromium")
            || lower.Contains("brave")
            || lower.Contains("edge")
            || lower.Contains("vivaldi")
            || lower.Contains("opera")
        )
        {
            return "chromium";
        }

        return null;
    }

    private static AccessibleRef? FindActiveBrowserWindow(
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

    private static string? FindLikelyBrowserUrlInSubtree(
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

    private static string? GetAtSpiBusAddress()
    {
        var exitCode = RunProcess(
            "gdbus",
            "call --session --dest org.a11y.Bus --object-path /org/a11y/bus --method org.a11y.Bus.GetAddress",
            out var output
        );

        if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var match = TupleValueRegex().Match(output);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string? TryGetAccessibleText(
        string address,
        AccessibleRef node,
        IReadOnlyList<string> interfaces,
        int maxChars = int.MaxValue
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
                return maxChars < valueText.Length ? valueText[..maxChars] : valueText;
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

        // Only request up to maxChars so a focused editor / large text area doesn't stream its
        // whole buffer over busctl just to be truncated afterwards (latency + memory + the
        // bounded-snippet privacy guarantee). The URL walker uses the default (unbounded).
        var end = maxChars < characterCount ? maxChars : characterCount;
        var output = RunBusctlCall(
            address,
            node.BusName,
            node.ObjectPath,
            "org.a11y.atspi.Text",
            "GetText",
            "ii",
            "0",
            end.ToString()
        );

        return ParseFirstQuotedString(output);
    }

    private static List<AccessibleRef> GetAccessibleChildren(
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

    private static List<string> GetAccessibleInterfaces(string address, AccessibleRef node)
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

    private static string? GetAccessibleName(string address, AccessibleRef node)
    {
        return GetBusctlStringProperty(
            address,
            node.BusName,
            node.ObjectPath,
            "org.a11y.atspi.Accessible",
            "Name"
        );
    }

    private static int GetAccessibleRole(string address, AccessibleRef node)
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

    private static IReadOnlyList<uint> GetAccessibleState(string address, AccessibleRef node)
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

    private static string? GetBusctlStringProperty(
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

    private static int GetBusctlUInt32Property(
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

    private static string? RunBusctlCall(
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
            method
        };
        args.AddRange(signatureAndArgs);

        var exitCode = RunProcess("busctl", args, out var output);
        return exitCode == 0 ? output?.Trim() : null;
    }

    private static string? RunBusctlGetProperty(
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

    private static int RunProcess(string fileName, IReadOnlyList<string> args, out string? output)
    {
        output = null;

        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var p = Process.Start(startInfo);
            if (p is null)
            {
                return -1;
            }

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