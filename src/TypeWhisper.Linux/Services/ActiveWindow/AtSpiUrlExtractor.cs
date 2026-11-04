using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>
///     Focused AT-SPI walker that pulls the active URL from a browser's address bar.
///     1. Resolve the focused browser descriptor (AT-SPI walks cost 50–200 ms each).
///     2. Polling caches by <c>(descriptor ID, title)</c>; dictation always walks, because two tabs can share a title.
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
    private const int AtSpiRoleComboBox = 11;
    private const int AtSpiRoleFrame = 23;

    private const int AtSpiRoleWindow = 69;
    private const int AtSpiRoleEditBar = 77;
    private const int AtSpiRoleEntry = 79;
    private const int AtSpiRoleDocumentWeb = 95;
    private const string AtSpiMatchAll = "1";
    private const string AtSpiMatchAny = "2";

    // AT-SPI Collection rules take state and role sets as bitmaps of 32-bit words.
    private static readonly string s_showingVisibleStateWord =
        ((1u << AtSpiStateShowing) | (1u << AtSpiStateVisible)).ToString();

    private static readonly string[] s_urlFieldRoleWords =
    [
        (1 << AtSpiRoleComboBox).ToString(),
        "0",
        ((1 << (AtSpiRoleEditBar - 64)) | (1 << (AtSpiRoleEntry - 64))).ToString(),
        "0",
    ];

    // Bit 31 makes this word negative; RunBusctlCall passes "--" so busctl reads it as a value.
    private static readonly string[] s_documentWebRoleWords =
        ["0", "0", (1 << (AtSpiRoleDocumentWeb - 64)).ToString(), "0"];

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
    private readonly AsyncLocal<CancellationToken> _harvestCallCancellation = new();
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

    internal AtSpiUrlExtractor(
        IProcessRunner processRunner,
        IErrorLogService? errorLog,
        Func<string, string?>? walkOverride
    )
        : this(processRunner, errorLog)
    {
        _walkOverride = walkOverride;
    }

    public string? TryGetBrowserUrl(
        string? focusedProcessName,
        string? focusedTitle,
        bool isPolling = false
    )
    {
        return TryGetBrowserUrl(
            focusedProcessName,
            null,
            focusedTitle,
            isPolling
        );
    }

    /// <summary>
    ///     Drops the cached URL if it is the one a rejected dictation read stored for this
    ///     tab, so polling can't reuse a URL read after a tab switch.
    /// </summary>
    internal void ForgetCachedUrl(
        string? focusedProcessName,
        string? focusedAppId,
        string? focusedTitle,
        string url
    )
    {
        var browser = ResolveBrowserDescriptor(focusedProcessName, focusedAppId, focusedTitle);
        if (browser is null)
        {
            return;
        }

        lock (_cacheLock)
        {
            if (
                !string.Equals(_cachedDescriptorId, browser.Id, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_cachedTitle, focusedTitle, StringComparison.Ordinal)
                || !string.Equals(_cachedUrl, url, StringComparison.Ordinal)
            )
            {
                return;
            }

            _cachedDescriptorId = null;
            _cachedTitle = null;
            _cachedUrl = null;
            _cachedUrlAt = default;
        }
    }

    internal string? TryGetBrowserUrl(
        string? focusedProcessName,
        string? focusedAppId,
        string? focusedTitle,
        bool isPolling = false
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

        // Same-title tabs share a cache key, so only polling uses the cache and miss backoff.
        // Dictation records the URL in history and profile matching, so it always walks.
        if (isPolling)
        {
            lock (_cacheLock)
            {
                var now = DateTime.UtcNow;

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

                if (
                    string.Equals(_missDescriptorId, browser.Id, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(_missTitle, focusedTitle, StringComparison.Ordinal)
                    && now - _missAt < s_missBackoff
                )
                {
                    return null;
                }
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
            url = WalkForUrl(address, browser, focusedTitle, stats, cts.Token);
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
            else if (isPolling)
            {
                // Dictation misses must not throttle the next poll on this (descriptor, title).
                _missDescriptorId = browser.Id;
                _missTitle = focusedTitle;
                _missAt = DateTime.UtcNow;
            }
        }

        // Guarded here too, not just inside LogOnce: building the line is StringBuilder and
        // LINQ work on every walk, and this runs in the dictation path.
        if (s_diagnosticLoggingEnabled)
        {
            LogOnce(
                BuildDiagnosticLine(
                    browser.CanonicalProcessName,
                    focusedTitle,
                    stats,
                    url,
                    budgetExhausted
                )
            );
        }

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
    public string? TryHarvestFocusedContext(
        string? processName,
        string? title,
        string? selfAppName
    )
    {
        if (!_isBusctlAvailable || !_isGdbusAvailable
            || string.IsNullOrWhiteSpace(processName) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        using var cts = new CancellationTokenSource(s_harvestBudget);
        var previousCancellation = _harvestCallCancellation.Value;
        _harvestCallCancellation.Value = cts.Token;
        try
        {
            // Resolve through the current injected runner. A lock/unlock or accessibility-bus
            // restart must not leave a cached missing/stale address attached to future captures.
            var address = GetAtSpiBusAddress();
            cts.Token.ThrowIfCancellationRequested();
            return string.IsNullOrWhiteSpace(address)
                ? null
                : HarvestFocusedContext(address, processName, title, selfAppName, cts.Token);
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
        finally
        {
            _harvestCallCancellation.Value = previousCancellation;
        }
    }

    private string? HarvestFocusedContext(
        string address,
        string? processName,
        string? title,
        string? selfAppName,
        CancellationToken ct
    )
    {
        var remainingVisits = HarvestNodeVisitCap;

        // Process and window title are both required before any text can be harvested.
        if (string.IsNullOrWhiteSpace(processName) || string.IsNullOrWhiteSpace(title))
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
            if (!FocusedNodeBelongsToWindow(address, app, focused.Value, title, ref remainingVisits, ct))
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

    private AccessibleRef? FindFocusedElement(
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

    private AccessibleRef? TryGetCollectionFocusedMatch(string address, AccessibleRef app)
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

    private AccessibleRef? FindFocusedNodeBfs(
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

    private string? ReadFocusedContext(
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
    ///     Missing titles or an unconfirmed frame reject capture before any element text is read.
    ///     A title is not a window identity, so another top-level window of
    ///     <paramref name="app" /> with a related title also rejects: either could be the focused one.
    /// </summary>
    private bool FocusedNodeBelongsToWindow(
        string address,
        AccessibleRef app,
        AccessibleRef focused,
        string? recordedTitle,
        ref int remainingVisits,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(recordedTitle))
        {
            return false;
        }

        var node = focused;
        for (var i = 0; i < HarvestWindowAncestorCap && remainingVisits > 0; i++)
        {
            ct.ThrowIfCancellationRequested();
            remainingVisits--;
            var role = GetAccessibleRole(address, node);
            if (IsPasswordTextRole(role))
            {
                return false;
            }

            if (role is AtSpiRoleFrame or AtSpiRoleWindow)
            {
                var frameTitle = GetAccessibleName(address, node);
                return !string.IsNullOrWhiteSpace(frameTitle)
                       && TitlesRelate(frameTitle, recordedTitle)
                       && !HasOtherWindowWithRelatedTitle(address, app, node, recordedTitle, ref remainingVisits, ct);
            }

            ct.ThrowIfCancellationRequested();
            var parent = GetAccessibleParent(address, node);
            if (parent is null)
            {
                break;
            }

            node = parent.Value;
        }

        return false;
    }

    /// <summary>
    ///     True when another top-level window of <paramref name="app" /> has a title relating to
    ///     <paramref name="recordedTitle" />, or when the visit budget runs out before that is ruled out.
    /// </summary>
    private bool HasOtherWindowWithRelatedTitle(
        string address,
        AccessibleRef app,
        AccessibleRef frame,
        string recordedTitle,
        ref int remainingVisits,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        foreach (var window in GetAccessibleChildren(address, app))
        {
            if (window == frame)
            {
                continue;
            }

            if (remainingVisits-- <= 0)
            {
                return true;
            }

            ct.ThrowIfCancellationRequested();
            if (TitlesRelate(GetAccessibleName(address, window), recordedTitle))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     True when the AT-SPI frame Name is the recorded window title. Toolkits expose the title
    ///     verbatim, so a containing title (“Report private”) is a different same-app window; only
    ///     KWin's duplicate-caption suffix (“Report &lt;2&gt;”) on the recorded title is tolerated.
    /// </summary>
    internal static bool TitlesRelate(string? frameTitle, string? recordedTitle)
    {
        var frame = frameTitle?.Trim();
        var recorded = recordedTitle?.Trim();
        if (string.IsNullOrEmpty(frame) || string.IsNullOrEmpty(recorded))
        {
            return false;
        }

        if (string.Equals(frame, recorded, StringComparison.Ordinal))
        {
            return true;
        }

        var duplicateSuffix = KWinDuplicateCaptionSuffixRegex().Match(recorded);
        return duplicateSuffix.Success
               && string.Equals(frame, recorded[..duplicateSuffix.Index], StringComparison.Ordinal);
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

    private AccessibleRef? GetAccessibleParent(string address, AccessibleRef node)
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

    private string? WalkForUrl(
        string address,
        BrowserDescriptor focusedBrowser,
        string? focusedTitle,
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

            var window = FindFocusedBrowserWindow(address, app, focusedTitle, ct);
            if (window is null)
            {
                continue;
            }

            stats.WindowFound = true;

            var url = FindUrlInVisibleEntries(address, window.Value, stats, ct)
                      ?? FindLikelyBrowserUrlInSubtree(address, window.Value, stats, ct);
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

    // Picks the browser window the compositor reports as focused. The AT-SPI ACTIVE state alone
    // is unreliable: Chrome under XWayland on KWin can leave it unset on the focused window
    // (fresh launch, or focus returning from a native Wayland window) and set on a window that
    // lost focus. The frame named exactly like the focused window title wins; an ACTIVE frame
    // is the fallback for titles the compositor decorates (e.g. KWin's " <2>" suffix).
    private AccessibleRef? FindFocusedBrowserWindow(
        string address,
        AccessibleRef app,
        string? focusedTitle,
        CancellationToken ct
    )
    {
        var queue = new Queue<(AccessibleRef Node, int Depth)>();
        queue.Enqueue((app, 0));
        AccessibleRef? titleMatch = null;
        AccessibleRef? activeWindow = null;

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
            if (role is AtSpiRoleFrame or AtSpiRoleWindow)
            {
                var isActive = ActiveWindowService.HasState(
                    GetAccessibleState(address, node),
                    AtSpiStateActive
                );
                var isTitleMatch =
                    !string.IsNullOrEmpty(focusedTitle)
                    && string.Equals(
                        GetAccessibleName(address, node),
                        focusedTitle,
                        StringComparison.Ordinal
                    );
                if (isActive && (isTitleMatch || string.IsNullOrEmpty(focusedTitle)))
                {
                    return node;
                }

                titleMatch ??= isTitleMatch ? node : null;
                activeWindow ??= isActive ? node : null;
                // A window's subtree is its content, not further top-level windows.
                continue;
            }

            foreach (var child in GetAccessibleChildren(address, node))
            {
                queue.Enqueue((child, depth + 1));
            }
        }

        return titleMatch ?? activeWindow;
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
                // A page's document holds its form fields, never the address bar; skipping
                // the whole subtree keeps URL-like page fields out and saves walk budget.
                // An unreadable role (0) can't be ruled out as a document, so skip it too.
                var role = GetAccessibleRole(address, node);
                if (role is AtSpiRoleDocumentWeb or 0)
                {
                    continue;
                }

                var (score, candidate) = ScoreUrlCandidate(address, node, role, states);
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

    // One Collection.GetMatches call for the window's showing, visible entries, edit bars and
    // combo boxes (Firefox's URL bar). Chromium's omnibox sits ~120 nodes into a breadth-first
    // walk, and at several busctl spawns per node the 2.5 s budget often ran out first. Fields
    // inside a web page's document are dropped so a form field holding a URL can't pose as the
    // address bar. Null when the browser has no Collection support, a page-field query fails,
    // or no URL-like entry remains, so the caller falls back to the full walk.
    private string? FindUrlInVisibleEntries(
        string address,
        AccessibleRef window,
        WalkStats stats,
        CancellationToken ct
    )
    {
        var output = GetVisibleMatches(address, window, s_urlFieldRoleWords);
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var pageFields = FindWebPageFields(address, window);
        if (pageFields is null)
        {
            return null;
        }

        string? bestUrl = null;
        var bestScore = int.MinValue;
        var values = ParseQuotedStrings(output);
        for (var i = 0; i + 1 < values.Count && !ct.IsCancellationRequested; i += 2)
        {
            var node = new AccessibleRef(values[i], values[i + 1]);
            if (pageFields.Contains(node))
            {
                continue;
            }

            var (score, candidate) = ScoreUrlCandidate(
                address,
                node,
                GetAccessibleRole(address, node),
                GetAccessibleState(address, node)
            );
            if (score > bestScore)
            {
                bestScore = score;
                bestUrl = ActiveWindowService.SanitizeCapturedBrowserUrl(candidate);
                stats.BestScore = score;
                stats.BestCandidate = candidate;
            }

            // 250 requires more than an ordinary unfocused entry: e.g. focus,
            // an address label, or the browser-specific edit-bar role.
            if (bestUrl is not null && bestScore >= 250)
            {
                break;
            }
        }

        return bestUrl;
    }

    // URL-like fields inside the window's visible web documents. Every browser exposes its
    // address bar outside the page document, while Firefox's HTML-built toolbar carries the
    // same "tag" attribute as page fields, so document membership is the reliable split.
    // Null when a query fails: unchecked candidates must not be trusted as the address bar.
    private HashSet<AccessibleRef>? FindWebPageFields(string address, AccessibleRef window)
    {
        var documents = GetVisibleMatches(address, window, s_documentWebRoleWords);
        if (documents is null)
        {
            return null;
        }

        var pageFields = new HashSet<AccessibleRef>();
        var documentRefs = ParseQuotedStrings(documents);
        for (var i = 0; i + 1 < documentRefs.Count; i += 2)
        {
            var fields = GetVisibleMatches(
                address,
                new AccessibleRef(documentRefs[i], documentRefs[i + 1]),
                s_urlFieldRoleWords
            );
            if (string.IsNullOrWhiteSpace(fields))
            {
                return null;
            }

            var fieldRefs = ParseQuotedStrings(fields);
            for (var j = 0; j + 1 < fieldRefs.Count; j += 2)
            {
                pageFields.Add(new AccessibleRef(fieldRefs[j], fieldRefs[j + 1]));
            }
        }

        return pageFields;
    }

    // Collection.GetMatches for showing, visible descendants with any of the given roles.
    private string? GetVisibleMatches(string address, AccessibleRef root, string[] roleWords)
    {
        return RunBusctlCall(
            address,
            root.BusName,
            root.ObjectPath,
            "org.a11y.atspi.Collection",
            "GetMatches",
            [
                "(aiia{ss}iaiiasib)uib",
                // states: SHOWING and VISIBLE, match all
                "2", s_showingVisibleStateWord, "0", AtSpiMatchAll,
                // attributes: none, match all
                "0", AtSpiMatchAll,
                // roles: any of the four role words
                "4", .. roleWords, AtSpiMatchAny,
                // interfaces: none, match all; invert: false
                "0", AtSpiMatchAll, "false",
                // Sort canonical, no cap: page fields must not hide a later address bar. Traverse.
                "1", "0", "true",
            ]
        );
    }

    private (int Score, string? Candidate) ScoreUrlCandidate(
        string address,
        AccessibleRef node,
        int role,
        IReadOnlyList<uint> states
    )
    {
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
        return (score, candidate);
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
            "--",
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
                ),
                _harvestCallCancellation.Value
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

    internal static List<string> ParseQuotedStrings(string value)
    {
        return QuotedStringRegex()
            .Matches(value)
            .Select(match => UnescapeBusctlString(match.Groups[1].Value))
            .ToList();
    }

    // busctl C-escapes strings byte by byte: non-ASCII UTF-8 arrives as octal escapes
    // ("b\303\274cher" for "bücher"). Regex.Unescape would map each escaped byte to its own
    // Latin-1 char ("bÃ¼cher"), so collect the raw bytes and decode them as UTF-8.
    internal static string UnescapeBusctlString(string escaped)
    {
        if (!escaped.Contains('\\', StringComparison.Ordinal))
        {
            return escaped;
        }

        var bytes = new List<byte>(escaped.Length);
        var literalStart = 0;
        var i = 0;
        while (i < escaped.Length)
        {
            if (escaped[i] != '\\' || i + 1 == escaped.Length)
            {
                i++;
                continue;
            }

            bytes.AddRange(Encoding.UTF8.GetBytes(escaped[literalStart..i]));
            var next = escaped[i + 1];
            i += 2;
            switch (next)
            {
                case 'a': bytes.Add(0x07); break;
                case 'b': bytes.Add(0x08); break;
                case 'f': bytes.Add(0x0C); break;
                case 'n': bytes.Add(0x0A); break;
                case 'r': bytes.Add(0x0D); break;
                case 't': bytes.Add(0x09); break;
                case 'v': bytes.Add(0x0B); break;
                case >= '0' and <= '7':
                    var octal = next - '0';
                    for (
                        var digits = 1;
                        digits < 3 && i < escaped.Length && escaped[i] is >= '0' and <= '7';
                        digits++
                    )
                    {
                        octal = octal * 8 + (escaped[i++] - '0');
                    }

                    bytes.Add((byte)octal);
                    break;
                default:
                    // \\, \", \' and anything unrecognised decode to the escaped char itself.
                    literalStart = i - 1;
                    continue;
            }

            literalStart = i;
        }

        bytes.AddRange(Encoding.UTF8.GetBytes(escaped[literalStart..]));
        return Encoding.UTF8.GetString(bytes.ToArray());
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

    // KWin's " <N>" suffix on duplicate window captions.
    [GeneratedRegex(@" <\d+>$")]
    private static partial Regex KWinDuplicateCaptionSuffixRegex();

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
