using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services.ActiveWindow;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Wispr-Flow-style silent correction learning from the target app. After a
///     dictation inserts text, <see cref="ArmAsync" /> anchors a baseline read of the
///     focused field and opens a bounded tracking window. If the user then types over a
///     word to fix it and moves on (focus leaves the field, or a short idle after edits),
///     the final field text is diffed against the baseline through
///     <see cref="CorrectionSuggestionService" /> and any high-confidence result is
///     silently persisted via <see cref="IDictionaryService.LearnCorrection" /> — no toast,
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
    private bool _disposed;
    private Timer? _idleTimer;
    private bool _initialized;
    private bool _loggedSkip;
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
            _armed = null;
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

        if (!await _client.EnsureStartedAsync().ConfigureAwait(false))
        {
            LogSkipOnce("AT-SPI unavailable; target-app correction learning inactive.");
            return;
        }

        EnsureSubscribed();

        var focused = _client.CurrentFocusedElement;
        if (focused is not { IsValid: true } element)
        {
            Trace.WriteLine("[TargetAppLearning] No focused AT-SPI element to track; skipping.");
            return;
        }

        // Re-check opt-out after EnsureStartedAsync: the user could have disabled the feature
        // while it was connecting. Disable runs StopAsync on a separate gate and does not
        // serialize with this fire-and-forget arm, so we must not do further accessibility
        // reads after opt-out. (Re-checked again immediately before the text read below.)
        if (IsOptedOut())
        {
            Disarm();
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
        // retry attempts don't re-issue role reads.
        var safeToRead = new Dictionary<AtSpiElementRef, bool>();
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
                    Disarm();
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
            Disarm();
            return;
        }

        lock (_gate)
        {
            StopTimers();
            var generation = unchecked(++_armGeneration);
            _armed = new ArmedState(anchoredElement, baseline, generation);
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
        Dictionary<string, string> learned
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
            && string.Equals(known, replacement, StringComparison.Ordinal);

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

            if (state is null || !state.Edited)
            {
                // Inverting to `if (!final) return;` would duplicate the return and split the
                // cleanup; conditional-cleanup-then-return is clearer here.
                // ReSharper disable once InvertIf
                if (final)
                {
                    // Nothing to learn; drop the armed state and stop the timers.
                    _armed = null;
                    StopTimers();
                }

                return;
            }

            if (final)
            {
                // Final commits disarm and stop timers up front; the ArmedState snapshot is
                // then touched only inside the serialized commit chain below.
                _armed = null;
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

            // Re-check opt-out immediately before the text read: a disable between queueing this
            // commit and running it must stop us reading the target app's text (mirrors ArmAsync,
            // which re-checks before every read). Without this, disabling in that gap still lets
            // one more accessibility read (and potentially a learn) slip through.
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

            var suggestions = CorrectionSuggestionService.GenerateSuggestions(
                state.Baseline,
                finalText
            );
            foreach (var suggestion in suggestions)
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
                    // TimesCorrected/UsageCount.
                    if (string.Equals(previous, replacement, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // The user kept typing words after the correction was already complete, so
                    // the diff now appends them to the replacement (e.g. "Kubernetes" then
                    // "Kubernetes now"). Keep the earlier, correct value rather than widen it.
                    if (IsWidening(previous, replacement))
                    {
                        Trace.WriteLine(
                            "[TargetAppLearning] Ignoring widened replacement (kept earlier value)."
                        );
                        continue;
                    }
                }

                _dictionary.LearnCorrection(original, replacement);
                state.LearnedByOriginal[original] = replacement;
                Trace.WriteLine("[TargetAppLearning] Learned a correction from a target-app edit.");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TargetAppLearning] Commit failed: {ex.Message}");
        }
    }

    private void Disarm()
    {
        _ = TakeArmed();
    }

    private ArmedState? TakeArmed()
    {
        lock (_gate)
        {
            var state = _armed;
            _armed = null;
            StopTimers();
            return state;
        }
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
        if (_loggedSkip)
        {
            return;
        }

        _loggedSkip = true;
        _errorLog.AddEntry(message, ErrorCategory.Detection);
    }

    // Ties a timer callback to the owner and the arm generation it was created for.
    private readonly record struct TimerToken(
        TargetAppCorrectionLearningService Owner,
        int Generation
    );

    private sealed class ArmedState(AtSpiElementRef element, string baseline, int generation)
    {
        public AtSpiElementRef Element { get; } = element;
        public string Baseline { get; } = baseline;
        public int Generation { get; } = generation;
        public bool Edited { get; set; }

        // The last replacement persisted for each original during this armed session. Lets a
        // later commit refine an earlier partial (overwrite) while ignoring identical repeats
        // (no count inflation) and widenings (trailing words the user kept typing). Touched
        // only inside the serialized commit chain, so it needs no synchronization of its own.
        public Dictionary<string, string> LearnedByOriginal { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
