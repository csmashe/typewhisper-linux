using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services.ActiveWindow;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     A batch of corrections learned from a single target-app edit, plus the on-screen box of
///     the element they came from (when it could be read). <see cref="SourceExtents" /> lets a
///     feedback surface place its toast beside the corrected element instead of at a fixed spot;
///     it is <c>null</c> when the element doesn't expose extents (native-Wayland apps often can't).
/// </summary>
public sealed record LearnedCorrectionsBatch(
    IReadOnlyList<LearnedDictionaryCorrection> Corrections,
    AtSpiScreenRect? SourceExtents
);

/// <summary>
///     Wispr-Flow-style silent correction learning from the target app. After a
///     dictation inserts text, <see cref="ArmAsync" /> anchors a baseline read of the
///     focused field and opens a bounded tracking window. If the user then types over a
///     word to fix it and moves on (focus leaves the field, or a short idle after edits),
///     the edit is confined to the dictated span, diffed through
///     <see cref="CorrectionSuggestionService" />, and any high-confidence result is
///     silently persisted via <see cref="IDictionaryService.LearnCorrections" /> — no toast,
///     no prompt. Learned entries are reviewable/removable in Settings → Dictionary.
///     <para>
///         The AT-SPI plumbing lives behind <see cref="IAtSpiEventClient" />, so the
///         arm/commit orchestration is unit-testable with a fake.
///     </para>
/// </summary>
public sealed class TargetAppCorrectionLearningService : IDisposable
{
    // Longest insertion we will arm on. Longer than a normal edit is likely a document
    // dump the user won't hand-correct word-by-word, so tracking it wastes a read.
    private const int MaxInsertionLength = 2048;

    // Baseline/final reads are clamped to this; larger than the insertion gate because the
    // field can hold pre-existing surrounding text around the dictated span.
    private const int MaxTrackedTextLength = 8192;

    // How many recently focused same-app elements ArmAsync probes when the newest focused
    // element can't be anchored. Two covers LibreOffice Writer's paragraph/root-pane flap;
    // a couple more absorb apps that flap across additional structural nodes.
    private const int MaxArmCandidates = 4;

    // Defaults for the timing seams below. Kept as instance fields (not static readonly) so
    // unit tests can shrink them via the internal constructor without waiting real seconds.

    // How long we keep tracking a field after insertion before giving up (matches Wispr
    // Flow's "during a dictation session" scoping and bounds resource/privacy exposure).
    private static readonly TimeSpan s_defaultTrackingWindow = TimeSpan.FromSeconds(30);

    // Commit this long after the last edit when focus hasn't left — covers the "fix a word
    // and keep the cursor there" case without waiting for the full tracking window.
    private static readonly TimeSpan s_defaultIdleCommitDelay = TimeSpan.FromSeconds(3);

    // Backoff between baseline read retries while the injected text is still draining into
    // the field (Wayland can return from the injection tool before the app has applied it).
    private static readonly TimeSpan s_defaultBaselineRetryDelay = TimeSpan.FromMilliseconds(150);

    // How long a cold-start arm waits for the first-contact poke sweep before bootstrapping
    // focus. The sweep normally finishes in milliseconds; this only caps the wait when some
    // app on the bus is hung (its calls each time out) — proceeding without its unlock is
    // better than stalling the arm. Not a test seam: fakes complete the sweep synchronously.
    private static readonly TimeSpan s_coldStartPokeWait = TimeSpan.FromSeconds(2);

    private readonly IAtSpiEventClient _client;
    private readonly IDictionaryService _dictionary;
    private readonly IErrorLogService _errorLog;
    private readonly Lock _gate = new();

    // Serializes AT-SPI start/stop so a rapid enable→disable can't interleave. Not disposed:
    // reconciles run fire-and-forget and could be mid-wait at shutdown; SemaphoreSlim needs no
    // disposal unless its AvailableWaitHandle is used (it isn't).
    // ReSharper disable once InconsistentNaming
    private readonly SemaphoreSlim _listenGate = new(1, 1);
    private readonly ISettingsService _settings;

    private readonly TimeSpan _trackingWindow;
    private readonly TimeSpan _idleCommitDelay;
    private readonly TimeSpan _baselineRetryDelay;

    private ArmedState? _armed;

    // Incremented per arm. Timer callbacks capture the generation live at creation and are
    // ignored if they fire after a re-arm/disarm (a disposed timer's callback can still be
    // queued), so a stale idle/timeout can never commit or disarm a newer armed session.
    private int _armGeneration;

    // Incremented at every ArmAsync ENTRY (successful or not) — see the ordering comment in
    // ArmAsync. Distinct from _armGeneration, which only advances on successful installs.
    private int _armEntrySequence;
    private bool _disposed;
    private Timer? _idleTimer;
    private bool _initialized;

    // Skip reasons already surfaced to the error log — once per distinct reason, so the
    // cold-start "no focused element" message isn't swallowed by an earlier "AT-SPI
    // unavailable" entry (or vice versa). Guarded by _gate.
    private readonly HashSet<string> _loggedSkips = new(StringComparer.Ordinal);
    private bool _subscribed;
    private Timer? _timeoutTimer;

    // Test seam: the most recently scheduled background commit, so unit tests can await
    // completion deterministically instead of polling. Null until the first commit runs.
    // Commits are chained onto this task (see CommitInBackground) so awaiting it covers every
    // scheduled commit, not just the last-started one.
    internal Task? LastCommitTask { get; private set; }

    // Test seam: the most recently scheduled start/stop reconcile. Reconciles serialize on
    // _listenGate, so awaiting the last-assigned task guarantees all prior ones have finished.
    // ReSharper disable once UnusedAutoPropertyAccessor.Global -- getter is read by
    // TargetAppCorrectionLearningServiceTests (cross-project usage the single-project scan can't see).
    internal Task? LastListenTask { get; private set; }

    public TargetAppCorrectionLearningService(
        IAtSpiEventClient client,
        IDictionaryService dictionary,
        ISettingsService settings,
        IErrorLogService errorLog
    )
        : this(
            client,
            dictionary,
            settings,
            errorLog,
            s_defaultTrackingWindow,
            s_defaultIdleCommitDelay,
            s_defaultBaselineRetryDelay
        )
    {
    }

    // Test-only overload: lets unit tests shrink the tracking/idle/retry timers to
    // milliseconds so the arm → commit loop can be exercised deterministically.
    internal TargetAppCorrectionLearningService(
        IAtSpiEventClient client,
        IDictionaryService dictionary,
        ISettingsService settings,
        IErrorLogService errorLog,
        TimeSpan trackingWindow,
        TimeSpan idleCommitDelay,
        TimeSpan baselineRetryDelay
    )
    {
        _client = client;
        _dictionary = dictionary;
        _settings = settings;
        _errorLog = errorLog;
        _trackingWindow = trackingWindow;
        _idleCommitDelay = idleCommitDelay;
        _baselineRetryDelay = baselineRetryDelay;
    }

    /// <summary>
    ///     Raised after a commit that added or updated at least one dictionary entry, carrying
    ///     exactly the list the dictionary returned plus the source element's on-screen box (when
    ///     readable). Fired outside any lock and isolated per handler. A follow-up UI task
    ///     subscribes to surface what was silently learned beside the corrected element.
    /// </summary>
    public event Action<LearnedCorrectionsBatch>? CorrectionsLearned;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _settings.SettingsChanged -= OnSettingsChanged;
        if (_subscribed)
        {
            _client.FocusChanged -= OnFocusChanged;
            _client.TextChanged -= OnTextChanged;
        }

        lock (_gate)
        {
            StopTimers();
            DropArmed();
        }
    }

    /// <summary>
    ///     Wires up settings-change handling and, when the feature is already enabled,
    ///     starts the AT-SPI listener early so it captures the focus of whatever field
    ///     the user dictates into (focus is usually gained before dictation starts).
    ///     Safe to call once from the orchestrator's initialization.
    /// </summary>
    public void Initialize()
    {
        if (_initialized || _disposed)
        {
            return;
        }

        _initialized = true;
        _settings.SettingsChanged += OnSettingsChanged;
        ReconcileListeningInBackground();
    }

    /// <summary>
    ///     Called (fire-and-forget) right after a qualifying dictation insertion. Reads the
    ///     current field text as a baseline and opens the tracking window. No-ops silently
    ///     when the feature is disabled, AT-SPI is unavailable, the focused element is a
    ///     password field, or its text can't be read.
    /// </summary>
    public async Task ArmAsync(string insertedText)
    {
        if (IsOptedOut())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(insertedText))
        {
            return;
        }

        // Arms are fire-and-forget and can complete out of order: a slow arm from an older
        // dictation (retry delays, slow role/text reads) must neither install its stale
        // state over a newer dictation's nor disarm the newer session on its failure paths.
        // Only the most recently entered arm may touch the armed state.
        var armSequence = Interlocked.Increment(ref _armEntrySequence);

        if (!await _client.EnsureStartedAsync().ConfigureAwait(false))
        {
            LogSkipOnce("AT-SPI unavailable; target-app correction learning inactive.");
            DisarmIfCurrent(armSequence);
            return;
        }

        EnsureSubscribed();

        // Re-check opt-out after EnsureStartedAsync: the user could have disabled the feature
        // while it was connecting. Disable runs StopAsync on a separate gate and does not
        // serialize with this fire-and-forget arm, so we must not do further accessibility
        // work after opt-out. (Re-checked again immediately before the text read below.)
        if (IsOptedOut())
        {
            DisarmIfCurrent(armSequence);
            return;
        }

        // Unlock any Chromium/Electron app that joined the a11y bus since the client started
        // (their tree stays a stub until poked — see PokeAccessibilityTreesAsync). Runs after
        // the opt-out re-check above so a disable mid-connect stops this cross-app sweep. Not
        // awaited on the warm path — holding every arm for a rare first-contact case would
        // delay all of them; the cold-start branch below waits for it, bounded, where the
        // unlock actually matters.
        var pokeSweep = _client.PokeAccessibilityTreesAsync();

        var focused = _client.CurrentFocusedElement;
        if (focused is null)
        {
            // Cold start: no focus event has been observed on this connection — the user
            // focused the field BEFORE the client's listener existed (first dictation after
            // launch, or after a bus reset) and AT-SPI never replays it, so waiting cannot
            // recover. Without this branch that first dictation is a silent dud. Give the
            // first-contact sweep a bounded window to unlock lazily-built trees (Qt/KF6,
            // Chromium expose stubs until touched), then actively scan for the FOCUSED
            // element.
            await Task.WhenAny(pokeSweep, Task.Delay(s_coldStartPokeWait)).ConfigureAwait(false);
            if (IsOptedOut())
            {
                DisarmIfCurrent(armSequence);
                return;
            }

            focused = await _client.TryBootstrapFocusAsync().ConfigureAwait(false);
            if (IsOptedOut())
            {
                DisarmIfCurrent(armSequence);
                return;
            }
        }

        if (focused is not { IsValid: true } element)
        {
            // Surfaced to the error log (once per reason): the user-visible symptom is just
            // "nothing was learned", indistinguishable from every other silent skip without it.
            LogSkipOnce(
                "No focused element found on the accessibility bus; correction learning skipped this dictation."
            );
            // Like every other abort below: this arm supersedes the previous one, so drop its
            // state (and its text-changed lease) instead of leaving it tracking a stale field.
            DisarmIfCurrent(armSequence);
            return;
        }

        // The field the dictation actually landed in must be POSITIVELY non-password before
        // anything else happens — a password focused element aborts the whole arm (no
        // same-app sibling fallback: the inserted text IS the secret, and apps can mirror
        // password text into a visible sibling via "show password", which would anchor and
        // leak it), and an indeterminate role fails closed the same way because it could be
        // exactly that password field.
        var focusedPassword = await _client.IsPasswordFieldAsync(element).ConfigureAwait(false);
        if (focusedPassword != false)
        {
            Trace.WriteLine(
                "[TargetAppLearning] Focused element is a password field or its role is unknown; skipping."
            );
            DisarmIfCurrent(armSequence);
            return;
        }

        // Some apps (LibreOffice Writer) flap the AT-SPI focused state between the caret's
        // text widget and a structural pane with no readable text, so the newest focused
        // element is not always the one the dictation landed in. Try it first, then fall back
        // through recently focused elements of the same application; the anchoring check
        // below (the candidate must contain the just-inserted text) is what keeps a stale
        // sibling from being armed.
        var candidates = new List<AtSpiElementRef> { element };
        foreach (var recent in _client.GetRecentFocusedElements())
        {
            if (candidates.Count == MaxArmCandidates)
            {
                break;
            }

            if (
                recent.IsValid
                && string.Equals(recent.BusName, element.BusName, StringComparison.Ordinal)
                && !candidates.Contains(recent)
            )
            {
                candidates.Add(recent);
            }
        }

        // Confirm the field actually contains the text we just inserted before anchoring on
        // it. On Wayland the app may still be draining injected keystrokes when the injection
        // tool returns, so an immediate read can be truncated ("Hello wor" vs the final
        // "Hello world") — anchoring on that would learn garbage like `wor -> world`. Compare
        // with whitespace runs collapsed to single spaces and case-insensitively (tolerates
        // autocapitalize). Retry a couple of times to let the field settle. This also guards
        // against arming on the wrong field when focus moved mid-dictation; and a field longer
        // than the MaxTrackedTextLength read clamp will honestly skip here (the edit past the
        // clamp would be invisible to us anyway).
        string? baseline = null;
        AtSpiElementRef? anchored = null;
        // Fail closed per candidate: only read text from elements positively known to be a
        // non-password role — null (role unreadable) counts as unsafe. Verdicts are cached so
        // retry attempts don't re-issue role reads; the focused element's verdict is already
        // known from the password guard above.
        var safeToRead = new Dictionary<AtSpiElementRef, bool> { [element] = focusedPassword == false };
        for (var attempt = 0; attempt < 3 && anchored is null; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(_baselineRetryDelay).ConfigureAwait(false);
            }

            foreach (var candidate in candidates)
            {
                // Re-check opt-out immediately before every accessibility read: a disable
                // during a role read or a retry delay must stop us reading the target app.
                if (IsOptedOut())
                {
                    DisarmIfCurrent(armSequence);
                    return;
                }

                if (!safeToRead.TryGetValue(candidate, out var safe))
                {
                    safe =
                        await _client.IsPasswordFieldAsync(candidate).ConfigureAwait(false)
                        == false;
                    safeToRead[candidate] = safe;
                }

                if (!safe)
                {
                    continue;
                }

                var read = await _client.TryReadTextAsync(candidate, MaxTrackedTextLength)
                    .ConfigureAwait(false);
                if (read is null || !ContainsCollapsed(read, insertedText))
                {
                    continue;
                }

                baseline = read;
                anchored = candidate;
                break;
            }
        }

        if (anchored is not { } anchoredElement || baseline is null)
        {
            Trace.WriteLine(
                "[TargetAppLearning] No focused element could be anchored to the inserted text; skipping."
            );
            DisarmIfCurrent(armSequence);
            return;
        }

        lock (_gate)
        {
            if (Volatile.Read(ref _armEntrySequence) != armSequence)
            {
                // A newer dictation's arm entered while this one was reading; its state wins.
                return;
            }

            StopTimers();
            // Drop any state a previous arm left behind, disposing its text-events lease before we
            // acquire a fresh one — a re-arm replacing an older armed session must not leak it.
            DropArmed();
            var generation = unchecked(++_armGeneration);
            // Acquire the text-changed lease now that we are committed to installing this state:
            // ArmedState owns it and releases it on every drop path (DropArmed). This is what keeps
            // AT-SPI text events registered only while a window is actually being tracked.
            _armed = new ArmedState(
                anchoredElement,
                baseline,
                insertedText,
                generation,
                _client.AcquireTextChangedEvents()
            );
            _timeoutTimer = new Timer(
                static state =>
                {
                    var token = (TimerToken)state!;
                    token.Owner.OnTimeout(token.Generation);
                },
                new TimerToken(this, generation),
                _trackingWindow,
                Timeout.InfiniteTimeSpan
            );
        }
    }

    // Whitespace-insensitive, case-insensitive containment: splits both strings on any
    // whitespace and rejoins with single spaces so a baseline whose spacing differs from the
    // injected text (tabs, doubled spaces, wrapped newlines) still anchors.
    private static bool ContainsCollapsed(string haystack, string needle)
    {
        return CollapseWhitespace(haystack)
            .Contains(CollapseWhitespace(needle), StringComparison.OrdinalIgnoreCase);
    }

    private static string CollapseWhitespace(string text)
    {
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    // Counts occurrences of needle in haystack under the same case/whitespace-insensitive matching
    // ArmAsync anchors with (collapse runs of whitespace, ignore case), stepping by one so
    // overlapping copies still count. Used to reject an ambiguous baseline before span extraction.
    private static int CountNormalizedOccurrences(string haystack, string needle)
    {
        var collapsedHaystack = CollapseWhitespace(haystack);
        var collapsedNeedle = CollapseWhitespace(needle);
        if (collapsedNeedle.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var index = collapsedHaystack.IndexOf(collapsedNeedle, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            count++;
            index = collapsedHaystack.IndexOf(
                collapsedNeedle,
                index + 1,
                StringComparison.OrdinalIgnoreCase
            );
        }

        return count;
    }

    // A learned pair must share at least this fraction of characters (1 - normalized edit
    // distance) to count as a recognition/spelling fix rather than a change of intent.
    private const double MinCorrectionSimilarity = 0.5;

    // True when <paramref name="next" /> is <paramref name="previous" /> plus one or more
    // appended words (the previous value followed by a word boundary). Distinguishes "the user
    // kept typing new content" (widening — reject) from "the user refined the corrected word
    // itself" (e.g. "Kubernete" -> "Kubernetes" — allowed, no whitespace at the seam).
    private static bool IsWidening(string previous, string next)
    {
        return next.Length > previous.Length
            && next.StartsWith(previous, StringComparison.Ordinal)
            && char.IsWhiteSpace(next[previous.Length]);
    }

    private static bool IsLikelyRecognitionFix(string original, string replacement)
    {
        var a = CollapseWhitespace(original).ToLowerInvariant();
        var b = CollapseWhitespace(replacement).ToLowerInvariant();
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        var similarity = 1.0 - (double)LevenshteinDistance(a, b) / Math.Max(a.Length, b.Length);
        return similarity >= MinCorrectionSimilarity;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost
                );
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    // Separates an edit already learned earlier in this armed session from a genuinely new one. A
    // frozen baseline makes a later commit re-diff the ORIGINAL insertion, so once
    // "kharrington -> Carrington" is known, fixing an adjacent word comes back fused as
    // "Chris kharrington -> Curris Carrington"; this drops the settled part and keeps only the new
    // edit. Absent any settled word the suggestion is returned intact, so a genuine phrase or
    // merge/split fix (e.g. "type whisper" -> "TypeWhisper") is never speculatively broken up.
    private static List<(string Original, string Replacement)> SplitAtLearnedWords(
        CorrectionSuggestion suggestion,
        Dictionary<string, LearnedEntry> learned
    )
    {
        var originals = suggestion.Original.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var replacements = suggestion.Replacement.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Drop anchor words from the START/END of the span. Done for ANY token counts (front pairs
        // with front, back with back), so a settled prior edit next to an otherwise unalignable
        // merge/split fix is removed instead of fusing into it — and any unchanged connector it
        // exposes at the new edge (e.g. "type whisper in" -> "TypeWhisper in") is trimmed too.
        var startO = 0;
        var startR = 0;
        var endO = originals.Length - 1;
        var endR = replacements.Length - 1;
        while (startO <= endO && startR <= endR && IsAnchor(originals[startO], replacements[startR]))
        {
            startO++;
            startR++;
        }

        while (endO >= startO && endR >= startR && IsAnchor(originals[endO], replacements[endR]))
        {
            endO--;
            endR--;
        }

        var remOriginals = originals[startO..(endO + 1)];
        var remReplacements = replacements[startR..(endR + 1)];

        // Only an equal-length multi-word remainder can be aligned position-by-position; a word
        // merge/split (unequal counts) or a single word is returned whole, minus any no-op the edge
        // trim may have left behind.
        if (remOriginals.Length < 2 || remOriginals.Length != remReplacements.Length)
        {
            var original = string.Join(' ', remOriginals);
            var replacement = string.Join(' ', remReplacements);
            return original.Length > 0
                && replacement.Length > 0
                && !string.Equals(original, replacement, StringComparison.Ordinal)
                    ? [(original, replacement)]
                    : [];
        }

        // Split the remainder into segments at any interior already-learned word. Each segment is
        // emitted as ONE atomic correction after trimming unchanged connector words off its ends
        // (interior ones are kept, so "kubernets in cluster" -> "Kubernetes in clusters" stays
        // whole). With no learned word inside, the remainder is a single segment.
        var result = new List<(string, string)>();
        var i = 0;
        while (i < remOriginals.Length)
        {
            if (IsSettled(remOriginals[i], remReplacements[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < remOriginals.Length && !IsSettled(remOriginals[i], remReplacements[i]))
            {
                i++;
            }

            var lo = start;
            var hi = i - 1;
            while (lo <= hi && IsUnchanged(lo))
            {
                lo++;
            }

            while (hi >= lo && IsUnchanged(hi))
            {
                hi--;
            }

            if (lo <= hi)
            {
                result.Add(
                    (
                        string.Join(' ', remOriginals[lo..(hi + 1)]),
                        string.Join(' ', remReplacements[lo..(hi + 1)])
                    )
                );
            }
        }

        return result;

        bool IsSettled(string original, string replacement) =>
            learned.TryGetValue(original, out var known)
            && string.Equals(known.Replacement, replacement, StringComparison.Ordinal);

        // An edge token that is not part of a new edit: either already learned this session, or a
        // truly unchanged connector (Ordinal, so a case-only change still counts as an edit).
        bool IsAnchor(string original, string replacement) =>
            IsSettled(original, replacement)
            || string.Equals(original, replacement, StringComparison.Ordinal);

        // A truly unchanged token: a diff anchor, never part of a correction.
        bool IsUnchanged(int index) =>
            string.Equals(remOriginals[index], remReplacements[index], StringComparison.Ordinal);
    }

    /// <summary>
    ///     Pure gate for whether a dictation insertion should arm target-app learning:
    ///     the feature is on, the text went into the field directly (typed or pasted — not
    ///     a clipboard fallback), it was plain dictation (no action plugin), and it is short
    ///     enough to be a normal edit. Kept static and side-effect-free for unit testing.
    /// </summary>
    public static bool ShouldArm(
        bool featureEnabled,
        InsertionResult insertion,
        bool hasActionPlugin,
        int insertionLength
    )
    {
        if (!featureEnabled || hasActionPlugin)
        {
            return false;
        }

        if (insertion is not (InsertionResult.Typed or InsertionResult.Pasted))
        {
            return false;
        }

        return insertionLength is > 0 and <= MaxInsertionLength;
    }

    // True when the feature has been turned off (or the service torn down). Used as the guard
    // after every await in ArmAsync/CommitAsync so a mid-flight operation never reads target
    // text after opt-out — disable runs on a separate gate and doesn't serialize with them.
    private bool IsOptedOut()
    {
        return _disposed || !_settings.Current.TargetAppCorrectionLearningEnabled;
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        ReconcileListeningInBackground();
    }

    private void ReconcileListeningInBackground()
    {
        LastListenTask = Task.Run(ReconcileListeningAsync);
    }

    // Drives the AT-SPI listener to match the current setting. Serialized on _listenGate and
    // re-reading _settings.Current each time, so a rapid enable→disable can never leave a
    // queued start reconnecting after the stop: whichever reconcile runs last observes the
    // final setting and wins. On opt-out it disarms any tracking window and tears down the
    // a11y-bus connection so the process stops receiving accessibility event traffic.
    private async Task ReconcileListeningAsync()
    {
        await _listenGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_disposed && _settings.Current.TargetAppCorrectionLearningEnabled)
            {
                if (await _client.EnsureStartedAsync().ConfigureAwait(false))
                {
                    EnsureSubscribed();

                    // Unlock already-running Chromium/Electron apps now that the feature is on
                    // (their tree stays a stub until poked — see PokeAccessibilityTreesAsync).
                    // Re-check consent after the connect await: a disable during EnsureStartedAsync
                    // is queued behind _listenGate and runs its teardown next, so we must not
                    // launch the cross-app sweep once the setting has flipped off. Later arms
                    // re-poke any app that joined the bus after this.
                    if (!_disposed && _settings.Current.TargetAppCorrectionLearningEnabled)
                    {
                        _ = _client.PokeAccessibilityTreesAsync();
                    }
                }
                else
                {
                    LogSkipOnce("AT-SPI unavailable; target-app correction learning inactive.");
                }
            }
            else
            {
                Disarm();
                await _client.StopAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TargetAppLearning] Listener reconcile failed: {ex.Message}");
        }
        finally
        {
            _listenGate.Release();
        }
    }

    private void EnsureSubscribed()
    {
        lock (_gate)
        {
            if (_subscribed)
            {
                return;
            }

            _client.FocusChanged += OnFocusChanged;
            _client.TextChanged += OnTextChanged;
            _subscribed = true;
        }
    }

    private void OnTextChanged(AtSpiElementRef element)
    {
        lock (_gate)
        {
            if (_armed is null || !_armed.Element.Equals(element))
            {
                return;
            }

            _armed.Edited = true;
            _idleTimer ??= new Timer(
                static state =>
                {
                    var token = (TimerToken)state!;
                    token.Owner.OnIdle(token.Generation);
                },
                new TimerToken(this, _armed.Generation),
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan
            );
            _idleTimer.Change(_idleCommitDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnFocusChanged(AtSpiElementRef element)
    {
        lock (_gate)
        {
            if (
                _armed is null
                || string.Equals(
                    element.BusName,
                    _armed.Element.BusName,
                    StringComparison.Ordinal
                )
            )
            {
                // Not tracking, or focus stayed inside the same application. Same-app focus
                // changes never end tracking because some apps (LibreOffice Writer) re-assert
                // focus on structural panes between keystrokes while the user is still editing
                // the armed field; a genuine move to another field of the same app is covered
                // by the idle and timeout commits (text-changed matching stays element-exact).
                return;
            }
        }

        // Focus left the application that owns the armed element: a FINAL commit — it learns
        // the edit if there was one and otherwise cleanly disarms.
        CommitInBackground(final: true);
    }

    private void OnIdle(int generation)
    {
        // Idle commits are NON-FINAL: we persist the current diff but stay armed on the same
        // baseline so a later focus-out/timeout can re-diff and overwrite a partial edit the
        // user was still typing when the idle timer fired (LearnCorrection overwrites the
        // Replacement for an Original it already knows — the self-heal mechanism).
        CommitInBackground(final: false, generation);
    }

    private void OnTimeout(int generation)
    {
        // Timeout is a FINAL commit: learn any pending edit that never triggered a focus-out
        // or idle commit and drop the state.
        CommitInBackground(final: true, generation);
    }

    // generation is set for timer-driven commits (idle/timeout) and null for event-driven ones
    // (focus-out). A timer callback can be queued just before StopTimers disposes its timer, so
    // reject it when it belongs to a superseded armed session.
    private void CommitInBackground(bool final, int? generation = null)
    {
        lock (_gate)
        {
            var state = _armed;
            if (generation is not null && state?.Generation != generation)
            {
                return;
            }

            if (state is null)
            {
                return;
            }

            // Idle (non-final) commits only run once a text-changed event actually flagged an edit;
            // without one there is nothing new to read. A FINAL commit (focus-out/timeout) always
            // reads the field even when no event arrived — event registration is best-effort and can
            // lag the arm, so this read is the fallback that still catches an edit no event
            // reported. CommitAsync diffs against the baseline, so a field that truly didn't change
            // simply learns nothing.
            if (!state.Edited && !final)
            {
                return;
            }

            if (final)
            {
                // Final commits disarm and stop timers up front; the ArmedState snapshot is then
                // touched only inside the serialized commit chain below. DropArmed releases the
                // text-events lease — the tracking window is over and CommitAsync does a one-shot
                // read, not event-driven tracking; the captured `state` local keeps the snapshot
                // alive for that read.
                DropArmed();
                StopTimers();
            }

            // Serialize commits: a non-final idle commit can race a final focus-out commit,
            // and both read the field and call LearnCorrection. Chaining onto LastCommitTask
            // guarantees the previous commit finishes first, so awaiting LastCommitTask covers
            // every scheduled commit.
            LastCommitTask = (LastCommitTask ?? Task.CompletedTask)
                .ContinueWith(_ => CommitAsync(state), TaskScheduler.Default)
                .Unwrap();
        }
    }

    private async Task CommitAsync(ArmedState state)
    {
        try
        {
            // A commit may have been queued before the user opted out; never read the field
            // or learn once the feature is disabled (or the service is being torn down).
            if (IsOptedOut())
            {
                return;
            }

            // Re-check the role right before the final read: up to the whole tracking window
            // has passed since arming, and the element can have become a password field in
            // that time (role flip, or the toolkit recycling the object path for a new
            // widget). Fail closed on indeterminate, mirroring the arm-time guard.
            if (await _client.IsPasswordFieldAsync(state.Element).ConfigureAwait(false) != false)
            {
                Trace.WriteLine(
                    "[TargetAppLearning] Element is no longer positively non-password at commit; dropping."
                );
                return;
            }

            // The role read above awaited: a disable during it isn't serialized with this commit,
            // so re-check before reading the field text — never read a target field after opt-out.
            if (IsOptedOut())
            {
                return;
            }

            var finalText = await _client.TryReadTextAsync(state.Element, MaxTrackedTextLength)
                .ConfigureAwait(false);
            if (finalText is null || string.Equals(finalText, state.Baseline, StringComparison.Ordinal))
            {
                return;
            }

            // Confine the diff to the dictated span: find the inserted text inside the baseline
            // and read back only the segment the user edited in place. Diffing the whole field
            // (baseline vs finalText) would learn an unrelated edit elsewhere in the document as
            // a "correction" of the dictation. The extraction is EXACT (Ordinal), while ArmAsync
            // anchors with a whitespace/case-tolerant match — so if the app transformed the
            // inserted text (e.g. autocapitalize) the span won't be found and nothing is learned.
            // That is the safe direction; do not weaken the matching to close that gap.
            var editedSpan = ExtractEditedInsertedText(state.InsertedText, state.Baseline, finalText);
            if (editedSpan is null
                || string.Equals(state.InsertedText, editedSpan, StringComparison.Ordinal))
            {
                return;
            }

            var suggestions = CorrectionSuggestionService.GenerateSuggestions(
                state.InsertedText,
                editedSpan
            );

            var batch = new List<CorrectionSuggestion>();
            foreach (var suggestion in suggestions)
            {
                // De-fuse any edit already learned this session from the genuinely new one (see
                // SplitAtLearnedWords) before applying the gates below.
                foreach (var (original, replacement) in SplitAtLearnedWords(
                             suggestion,
                             state.LearnedByOriginal
                         ))
                {
                    // Silent auto-learn holds a higher bar than the review-first history flow: only
                    // persist when the replacement is a plausible recognition/spelling fix of the
                    // original. CorrectionSuggestionService only rejects majority rewrites once the
                    // total token count exceeds 3, so without this a short "call mom" -> "email dad"
                    // change of intent would be silently learned as a correction.
                    if (!IsLikelyRecognitionFix(original, replacement))
                    {
                        // Redact the raw strings: they can contain sensitive target-app text.
                        Trace.WriteLine(
                            "[TargetAppLearning] Rejected low-similarity edit (likely a change of intent)."
                        );
                        continue;
                    }

                    if (state.LearnedByOriginal.TryGetValue(original, out var previous))
                    {
                        // Identical to what we already learned this session — skip so a non-final
                        // idle commit followed by an identical final commit doesn't inflate
                        // TimesCorrected.
                        if (string.Equals(previous.Replacement, replacement, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        // The user kept typing words after the correction was already complete, so
                        // the diff now appends them to the replacement (e.g. "Kubernetes" then
                        // "Kubernetes now"). Keep the earlier, correct value rather than widen it.
                        if (IsWidening(previous.Replacement, replacement))
                        {
                            Trace.WriteLine(
                                "[TargetAppLearning] Ignoring widened replacement (kept earlier value)."
                            );
                            continue;
                        }
                    }

                    batch.Add(new CorrectionSuggestion(original, replacement));
                }
            }

            if (batch.Count == 0)
            {
                return;
            }

            // Entries this session already created are the only ones the dictionary may overwrite
            // (the idle→final self-heal); every other existing correction is left untouched.
            var replaceableIds = state.LearnedByOriginal.Values
                .Select(e => e.Id)
                .ToHashSet(StringComparer.Ordinal);

            // Final consent gate before persisting: the text read awaited too, so an opt-out that
            // landed while it was pending must stop the write. No await follows, so this closes the
            // window down to nothing.
            if (IsOptedOut())
            {
                return;
            }

            var learned = _dictionary.LearnCorrections(batch, replaceableIds);
            if (learned.Count == 0)
            {
                // The dictionary rejected every pair (unsafe token, existing non-session entry):
                // record nothing as learned so a later commit doesn't treat it as settled.
                return;
            }

            foreach (var entry in learned)
            {
                state.LearnedByOriginal[entry.Original] =
                    new LearnedEntry(entry.Id, entry.Replacement);
            }

            Trace.WriteLine("[TargetAppLearning] Learned corrections from a target-app edit.");

            // Best-effort: fetch the corrected element's on-screen box so the feedback surface can
            // place its toast beside it. A null result (no Component interface, native-Wayland junk,
            // read failure) just falls back to a fixed spot downstream — never blocks the event.
            var extents = await _client.TryGetScreenExtentsAsync(state.Element).ConfigureAwait(false);
            RaiseCorrectionsLearned(new LearnedCorrectionsBatch(learned, extents));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TargetAppLearning] Commit failed: {ex.Message}");
        }
    }

    // Confines the diff to the dictated span: locate the inserted text within the baseline and
    // read back only the segment that changed in place. Requires exactly one occurrence of
    // insertedText in the baseline under the SAME case/whitespace-insensitive matching ArmAsync
    // anchored with (a second copy is ambiguous — we can't tell which was the dictation — so
    // nothing is learned). For that sole occurrence the baseline's prefix must still start the
    // final text and its suffix must still end it; the edited middle is returned.
    private static string? ExtractEditedInsertedText(
        string insertedText,
        string baseline,
        string finalText
    )
    {
        if (insertedText.Length == 0)
        {
            return null;
        }

        // Reject a baseline holding the inserted text more than once under ArmAsync's own
        // case/whitespace-insensitive matching — else a pre-existing copy differing only in
        // case/spacing slips past an Ordinal check (baseline "form Form": editing the first copy to
        // "from" would mis-learn "form" -> "from"). Bailing is the safe direction.
        if (CountNormalizedOccurrences(baseline, insertedText) != 1)
        {
            return null;
        }

        var start = baseline.IndexOf(insertedText, StringComparison.Ordinal);
        if (start < 0)
        {
            // The sole normalized occurrence was transformed (e.g. autocapitalized), so there is no
            // exact span to read back — learn nothing rather than guess.
            return null;
        }

        var prefix = baseline[..start];
        var suffix = baseline[(start + insertedText.Length)..];
        if (finalText.StartsWith(prefix, StringComparison.Ordinal)
            && finalText.EndsWith(suffix, StringComparison.Ordinal)
            && finalText.Length >= prefix.Length + suffix.Length)
        {
            return suffix.Length == 0
                ? finalText[prefix.Length..]
                : finalText[prefix.Length..^suffix.Length];
        }

        return null;
    }

    // Test seam: fires CorrectionsLearned exactly as a commit would (no source extents, as a
    // native-Wayland app would report), so feedback-surface tests can drive the event without
    // staging a full arm→commit flow.
    internal void RaiseCorrectionsLearnedForTest(IReadOnlyList<LearnedDictionaryCorrection> learned)
    {
        RaiseCorrectionsLearned(new LearnedCorrectionsBatch(learned, SourceExtents: null));
    }

    // Isolates each handler so one throwing subscriber can't break the others or the commit.
    private void RaiseCorrectionsLearned(LearnedCorrectionsBatch batch)
    {
        if (CorrectionsLearned is not { } handler)
        {
            return;
        }

        foreach (var subscriber in handler.GetInvocationList().Cast<Action<LearnedCorrectionsBatch>>())
        {
            try
            {
                subscriber(batch);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[TargetAppLearning] CorrectionsLearned handler threw: {ex.Message}");
            }
        }
    }

    private void Disarm()
    {
        lock (_gate)
        {
            DropArmed();
            StopTimers();
        }
    }

    // Failure-path disarm for ArmAsync: only the most recently entered arm may clear the
    // armed state — a stale arm's cleanup must not tear down a newer session it lost to. The
    // sequence check must run under _gate: a newer arm increments the sequence and installs its
    // state under the same lock, so checking outside it lets a preempted stale cleanup pass the
    // check and then dispose the newer arm (and its text-changed lease) once it acquires the gate.
    private void DisarmIfCurrent(int armSequence)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _armEntrySequence) != armSequence)
            {
                return;
            }

            DropArmed();
            StopTimers();
        }
    }

    // Clears _armed and disposes the state it held (releasing its text-changed lease so the
    // AT-SPI registration drops once nothing is armed). The single choke point for discarding an
    // armed state: every path that drops _armed routes through here so the lease can never leak
    // and silently reinstate the permanent text-event flood. Caller must hold _gate.
    private void DropArmed()
    {
        _armed?.Dispose();
        _armed = null;
    }

    // Caller must hold _gate.
    private void StopTimers()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
        _timeoutTimer?.Dispose();
        _timeoutTimer = null;
    }

    private void LogSkipOnce(string message)
    {
        Trace.WriteLine($"[TargetAppLearning] {message}");
        lock (_gate)
        {
            if (!_loggedSkips.Add(message))
            {
                return;
            }
        }

        _errorLog.AddEntry(message, ErrorCategory.Detection);
    }

    // Ties a timer callback to the owner and the arm generation it was created for.
    private readonly record struct TimerToken(
        TargetAppCorrectionLearningService Owner,
        int Generation
    );

    private sealed class ArmedState(
        AtSpiElementRef element,
        string baseline,
        string insertedText,
        int generation,
        IDisposable textEventsLease
    ) : IDisposable
    {
        // Lease keeping AT-SPI text-changed events registered while this window is armed. It is
        // released (deregistering the event with the registry when this was the last holder) when
        // the armed state is dropped — see DropArmed, which disposes every state it discards.
        // ReSharper disable once ReplaceWithPrimaryConstructorParameter -- keep an explicit named
        // field: it documents the held lease and matches how this record projects its other ctor
        // params into named members (Element/Baseline/... below).
        private readonly IDisposable _textEventsLease = textEventsLease;

        public AtSpiElementRef Element { get; } = element;
        public string Baseline { get; } = baseline;

        // The text this dictation inserted, exactly as anchored. Commit confines the diff to
        // this span within the field so an unrelated edit elsewhere in the document is not
        // learned as a correction of the dictation.
        public string InsertedText { get; } = insertedText;
        public int Generation { get; } = generation;
        public bool Edited { get; set; }

        // What we persisted for each original during this armed session: the last replacement
        // (to ignore identical repeats and widenings) plus the dictionary entry id we created
        // (so an idle→final self-heal overwrites the SAME entry rather than adding a new one).
        // Touched only inside the serialized commit chain, so it needs no synchronization.
        public Dictionary<string, LearnedEntry> LearnedByOriginal { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public void Dispose()
        {
            _textEventsLease.Dispose();
        }
    }

    private readonly record struct LearnedEntry(string Id, string Replacement);
}
