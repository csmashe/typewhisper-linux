using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Insertion;

namespace TypeWhisper.Linux.Services;

public enum InsertionResult
{
    Pasted,
    Typed,
    CopiedToClipboard,
    NoText,
    ActionHandled,
    ActionFailed,
    MissingClipboardTool,
    MissingPasteTool,
    Failed,
    ActionUnavailable
}

/// <summary>
///     Why the insertion fell back from direct paste/type to the clipboard.
///     Drives the wording of the fallback popup so we can tell the user
///     "set up ydotool" instead of the generic "paste with Ctrl+V".
/// </summary>
public enum InsertionFailureReason
{
    None,
    WtypeCompositorUnsupported,
    YdotoolSocketUnreachable,
    NoWaylandTypingTool,
    FocusFailed,
    PasteRetriesExhausted,
    PartialTypingFailure
}

public sealed record TextInsertionRequest(
    string Text,
    bool AutoPaste = true,
    string? TargetWindowId = null,
    string? TargetProcessName = null,
    string? TargetWindowTitle = null,
    bool AutoEnter = false,
    TextInsertionStrategy Strategy = TextInsertionStrategy.Auto
);

/// <summary>
///     Text insertion on Linux. The dispatch logic is a per-compositor
///     ordered backend chain: on GNOME / KDE Wayland we prefer ydotool
///     (since their compositors omit the wtype protocol), on wlroots
///     derivatives wtype keeps its first-tried slot. Every backend attempt
///     updates <see cref="LastFailureReason" /> so the orchestrator can
///     surface a setup hint instead of the generic "paste manually" popup
///     when fallback is the result of a known, fixable misconfiguration.
/// </summary>
public sealed class TextInsertionService
{
    private const int PasteAttemptCount = 3;
    private const string ClipboardNonTextCouldNotRestoreMessage =
        "Clipboard preservation skipped: the previous clipboard offered a non-text format (e.g. an image or file list) that cannot be captured as plain text, so it was replaced and could not be restored.";
    private const string ClipboardRichRestoreSkippedMessage =
        "Clipboard preservation skipped: the previous clipboard also offered a richer, non-text format (e.g. HTML) that would be lost if restored as plain text, so it was left as-is instead of a lossy restore.";
    private const string ClipboardRichRestoreLossyMessage =
        "Clipboard preservation was lossy: the previous clipboard also offered a richer, non-text format (e.g. HTML) that could not be restored; only its plain-text content was restored.";
    private static readonly TimeSpan s_focusDelay = TimeSpan.FromMilliseconds(100);

    // After the paste chord we hold our text on the clipboard this long before restoring the user's
    // previous content. On Wayland the target reads the clipboard asynchronously, so restoring
    // too soon races the paste: the app reads back the restored (old) content and nothing lands.
    // 200 ms was marginal for GTK apps and lost the race outright once accessibility is active
    // (AT-SPI makes the app do extra per-event work, delaying its clipboard read) — e.g. the
    // target-app correction-learning feature. 500 ms comfortably covers GTK4's async paste.
    private static readonly TimeSpan s_clipboardRestoreDelayDefault = TimeSpan.FromMilliseconds(500);

    // KDE Plasma's Klipper races us when restoring the clipboard — the
    // ~600 ms delay matches what OpenWhispr landed after the same race.
    private static readonly TimeSpan s_clipboardRestoreDelayKde = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan s_pasteRetryDelay = TimeSpan.FromMilliseconds(75);

    // Pre-paste readiness: wl-copy forks a child to own the selection, and until that
    // child is actually serving, GTK's async paste read finds nothing (or the user's
    // stale previous clipboard when wl-copy silently died) and inserts nothing. Verify
    // the clipboard serves OUR text before sending the keystroke — happy path is a
    // single ~20-50 ms read; the retry delay only accrues while the serve is late.
    private const int ClipboardVerifyAttempts = 4;
    private static readonly TimeSpan s_clipboardVerifyRetryDelay = TimeSpan.FromMilliseconds(40);

    // How long the event-driven restore waits for a positive "text landed" signal
    // before falling back to the fixed floor delay above.
    private static readonly TimeSpan s_pasteConfirmTimeout = TimeSpan.FromSeconds(2);

    // Env-gated per-paste diagnostics (TW_PASTE_DIAG=1): verify attempts, restore
    // gate (confirmed vs floor) + elapsed, and whether AT-SPI knew a focused element
    // at paste time — the signal that would justify a future pre-paste focus gate.
    private static readonly bool s_pasteDiagEnabled =
        Environment.GetEnvironmentVariable("TW_PASTE_DIAG") == "1";

    private readonly IErrorLogService? _errorLog;
    private readonly IPasteConfirmationSource? _pasteConfirmation;

    private readonly ITextInsertionPlatform _platform;

    // DI-preferred ctor: passes the shared SystemCommandAvailabilityService so the platform
    // subscribes to snapshot refreshes and rebuilds its chain live after ydotool setup —
    // without this the singleton's chain is frozen at startup and ydotool changes need a restart.
    public TextInsertionService(
        IErrorLogService errorLog,
        SystemCommandAvailabilityService commands,
        IPasteConfirmationSource? pasteConfirmation = null
    )
        : this(new LinuxTextInsertionPlatform(commands), errorLog, pasteConfirmation)
    {
    }

    internal TextInsertionService(
        ITextInsertionPlatform platform,
        IErrorLogService? errorLog = null,
        IPasteConfirmationSource? pasteConfirmation = null
    )
    {
        _platform = platform;
        _errorLog = errorLog;
        _pasteConfirmation = pasteConfirmation;
    }

    /// <summary>
    ///     Reason the most recent insertion fell back to the clipboard, or
    ///     <see cref="InsertionFailureReason.None" /> after a successful
    ///     paste/type. Read by <c>DictationOrchestrator</c> immediately
    ///     after each <see cref="InsertTextAsync(TextInsertionRequest)" />
    ///     so the value is single-consumer in practice.
    /// </summary>
    public InsertionFailureReason LastFailureReason { get; private set; } =
        InsertionFailureReason.None;

    // Whether the last direct-typing attempt aborted mid-sequence after already
    // delivering part of the text. Both the clipboard-fallback suppression and the
    // orchestrator's completion message key on this fact rather than on
    // LastFailureReason: a structural reason (e.g. ydotool socket unreachable) can be
    // recorded before the partial-delivery abort, so the reason value alone can't tell
    // whether a prefix already landed. Reset per request.
    public bool LastTypingDeliveredPartialText { get; private set; }

    public async Task<InsertionResult> InsertTextAsync(
        string text,
        bool autoPaste = true,
        string? targetWindowId = null,
        string? targetProcessName = null,
        string? targetWindowTitle = null,
        bool autoEnter = false,
        TextInsertionStrategy strategy = TextInsertionStrategy.Auto
    )
    {
        return await InsertTextAsync(
            new TextInsertionRequest(
                text,
                autoPaste,
                targetWindowId,
                targetProcessName,
                targetWindowTitle,
                autoEnter,
                strategy
            )
        );
    }

    public async Task<InsertionResult> InsertTextAsync(TextInsertionRequest request)
    {
        LastFailureReason = InsertionFailureReason.None;
        LastTypingDeliveredPartialText = false;

        var text = request.Text;
        var autoPaste = request.AutoPaste;
        var targetWindowId = request.TargetWindowId;
        var targetProcessName = request.TargetProcessName;
        var targetWindowTitle = request.TargetWindowTitle;
        var autoEnter = request.AutoEnter;
        var strategy = request.Strategy;

        if (string.IsNullOrEmpty(text))
        {
            return autoEnter ? await SendEnterOnlyAsync(targetWindowId) : InsertionResult.NoText;
        }

        if (strategy is TextInsertionStrategy.CopyOnly)
        {
            autoPaste = false;
        }

        var targetIsTerminal = IsTerminalApp(targetProcessName);
        var requiresSafeTerminalPaste =
            autoPaste && targetIsTerminal && ContainsLineBreak(text);
        var pasteShortcut = targetIsTerminal ? "Ctrl+Shift+V" : "Ctrl+V";

        if (autoPaste && !_platform.IsPasteAvailable)
        {
            LastFailureReason = InsertionFailureReason.NoWaylandTypingTool;
            return InsertionResult.MissingPasteTool;
        }

        var shouldTypeDirectly =
            autoPaste
            // Shift+Return is still Return in ordinary terminal protocols, so direct-typing
            // multiline text can submit each partial line as a shell command — overriding
            // even an explicit DirectTyping preference.
            && !requiresSafeTerminalPaste
            && strategy switch
            {
                TextInsertionStrategy.DirectTyping => true,
                TextInsertionStrategy.ClipboardPaste => false,
                _ => ShouldTypeDirectly(targetProcessName, targetWindowTitle)
                     // On Wayland without xdotool, process/title are null; defaulting to paste
                     // hits terminals (readline quoted-insert), vim, and Claude Code's image-paste.
                     // Direct typing via ydotool is universal — BUT only for ASCII: ydotool
                     // synthesizes evdev keycodes via the keyboard layout, so non-ASCII chars
                     // can silently render as the wrong glyph on non-US layouts.
                     || (
                         string.IsNullOrEmpty(targetProcessName)
                         && string.IsNullOrEmpty(targetWindowTitle)
                         && _platform.PrefersDirectTypingForUnknownTarget
                         && IsAsciiSafe(text)
                     )
            };

        if (shouldTypeDirectly)
        {
            var directResult = await TypeTextAsync(text, targetWindowId, autoEnter);
            if (
                strategy is TextInsertionStrategy.DirectTyping
                || directResult is not InsertionResult.Failed
                // Partial delivery already happened under the failed backend; falling
                // through would clipboard-paste the complete text again and duplicate
                // the prefix that's already in the target app. (Keyed on the delivery
                // fact, not LastFailureReason — see the property comment.)
                || LastTypingDeliveredPartialText
            )
            {
                return directResult;
            }
        }

        if (!_platform.IsClipboardSetAvailable)
        {
            return InsertionResult.MissingClipboardTool;
        }

        var previousClipboard = await _platform.TryGetClipboardTextAsync();
        var previousClipboardHasNonTextFormats =
            await _platform.ClipboardHasNonTextFormatsAsync();
        if (!await _platform.SetClipboardTextAsync(text))
        {
            return InsertionResult.Failed;
        }

        if (!autoPaste)
        {
            return InsertionResult.CopiedToClipboard;
        }

        if (!await FocusTargetWindowAsync(targetWindowId))
        {
            LastFailureReason = InsertionFailureReason.FocusFailed;
            LogInsertionFallback(
                "Auto paste fell back to clipboard: target window could not be focused."
            );
            return requiresSafeTerminalPaste
                ? await FailTerminalMultilineAsync(
                    text,
                    previousClipboard,
                    previousClipboardHasNonTextFormats
                )
                : InsertionResult.CopiedToClipboard;
        }

        if (!await VerifyClipboardServesAsync(text))
        {
            LogInsertionFallback(
                "Auto paste fell back to clipboard: the clipboard never served the dictated text, "
                + $"so {pasteShortcut} was not sent (it would have pasted nothing or stale content)."
            );
            return requiresSafeTerminalPaste
                ? await FailTerminalMultilineAsync(
                    text,
                    previousClipboard,
                    previousClipboardHasNonTextFormats
                )
                : InsertionResult.CopiedToClipboard;
        }

        if (s_pasteDiagEnabled)
        {
            var focusKnown = _pasteConfirmation?.HasFocusedElement;
            PasteDiag(
                $"focused element known at {pasteShortcut}: {focusKnown?.ToString() ?? "n/a (AT-SPI not running)"}"
            );
        }

        // Arm the confirmation watch BEFORE the keystroke: the target's text-changed
        // fires while the paste chord is being processed, so a subscription made in the restore
        // step (after the paste) misses it every time and waits out the full timeout.
        var pasteWatch = _pasteConfirmation?.BeginWatch(text);

        // Until the watch is handed to RestorePreviousClipboardAsync (which owns its disposal),
        // any throw from the paste/enter path must still release the AT-SPI subscription.
        var watchHandedOff = false;
        try
        {
            if (!await TrySendPasteAsync(targetIsTerminal))
            {
                // Prefer the platform's diagnostic (e.g. "compositor unsupported")
                // over the generic retries-exhausted reason.
                if (LastFailureReason == InsertionFailureReason.None)
                {
                    LastFailureReason = InsertionFailureReason.PasteRetriesExhausted;
                }

                LogInsertionFallback(
                    $"Auto paste fell back to clipboard: {pasteShortcut} could not be sent after retries."
                );
                return requiresSafeTerminalPaste
                    ? await FailTerminalMultilineAsync(
                        text,
                        previousClipboard,
                        previousClipboardHasNonTextFormats
                    )
                    : InsertionResult.CopiedToClipboard;
            }

            bool? deliveryConfirmed = null;
            if (autoEnter)
            {
                // GTK and Wayland targets read the clipboard asynchronously after Ctrl+V; Enter
                // must not overtake that read and submit an empty or stale field. The gate result
                // is passed to restore so it does not start a second full timeout.
                var stopwatch = s_pasteDiagEnabled ? Stopwatch.StartNew() : null;
                deliveryConfirmed = await AwaitPasteDeliveryAsync(pasteWatch);
                PasteDiag(
                    $"enter gate: {(deliveryConfirmed.Value ? "confirmed" : "floor")} after {stopwatch?.ElapsedMilliseconds ?? 0} ms"
                );

                if (!await _platform.SendEnterAsync())
                {
                    LogInsertionFallback(
                        $"Auto paste sent {pasteShortcut}, but Enter could not be sent."
                    );
                }
            }

            // Awaited inline (not fire-and-forget) so rapid consecutive dictations stay
            // serialized: the next insertion's clipboard snapshot must not race this restore.
            watchHandedOff = true;
            await RestorePreviousClipboardAsync(
                text,
                previousClipboard,
                previousClipboardHasNonTextFormats,
                pasteWatch,
                deliveryConfirmed
            );
            return InsertionResult.Pasted;
        }
        finally
        {
            if (!watchHandedOff)
            {
                pasteWatch?.Dispose();
            }
        }
    }

    /// <summary>
    ///     Types a chunk of streamed text directly via the platform typing backend — no clipboard,
    ///     no per-chunk focus delay — for streaming a spoken-command result onto the page. The caller
    ///     ensures the target holds focus. Terminal multiline chunks are rejected defensively; the
    ///     orchestrator normally keeps recognized terminals out of streaming entirely so their
    ///     completed result can use the content-aware one-shot policy.
    /// </summary>
    public Task<bool> TypeStreamChunkAsync(string text, string? targetProcessName = null)
    {
        if (IsTerminalApp(targetProcessName) && ContainsLineBreak(text))
        {
            return Task.FromResult(false);
        }

        return _platform.TypeTextAsync(text);
    }

    /// <summary>
    ///     Best-effort re-activates the captured target window before a streaming insertion types
    ///     into it, so output lands in the window the command was issued from rather than whatever
    ///     holds focus after the LLM round-trip. Mirrors the focus step <see cref="InsertTextAsync(TextInsertionRequest)" />
    ///     performs; returns false when the target could not be confirmed focused.
    /// </summary>
    public Task<bool> FocusWindowAsync(string? targetWindowId)
    {
        return FocusTargetWindowAsync(targetWindowId);
    }

    /// <summary>
    ///     Whether the Auto insertion policy would deliver to this target by direct typing based on
    ///     the app alone (terminals, supported browsers, Codex windows) — independent of text content.
    ///     Lets a streaming caller decide whether typing each chunk matches what the one-shot insert
    ///     would do, or whether it must defer to the content-aware one-shot path (clipboard paste for
    ///     GUI targets, ASCII-safety for unknown ones). Because a stream's eventual newline content is
    ///     unknown, streaming callers must additionally exclude terminal processes.
    /// </summary>
    public static bool AppPrefersDirectTyping(string? processName, string? windowTitle)
    {
        return ShouldTypeDirectly(processName, windowTitle);
    }

    // A single synthesized Ctrl+C can be dropped by the compositor or the app; retry a few times.
    private const int CopyProbeAttempts = 3;

    public async Task<string> CaptureSelectedTextAsync(bool targetIsTerminal = false)
    {
        // No PRIMARY-selection fallback: after an edit types over the selection PRIMARY holds a stale
        // leftover, so a failed copy probe means empty. targetIsTerminal switches the probe to
        // Ctrl+Shift+C — a plain Ctrl+C in a terminal is SIGINT, not copy, so it captures nothing and
        // would interrupt whatever is running there.
        var captured = await ProbeSelectionViaCopyAsync(targetIsTerminal);
        return string.IsNullOrWhiteSpace(captured) ? "" : captured;
    }

    private async Task<string> ProbeSelectionViaCopyAsync(bool targetIsTerminal)
    {
        var previousClipboard = await _platform.TryGetClipboardTextAsync();
        var previousClipboardHasNonTextFormats =
            await _platform.ClipboardHasNonTextFormatsAsync();

        // Only prime a sentinel when the clipboard already holds text we can detect against and
        // restore: a null read means it's empty or non-text (an image / file list) we must not
        // clobber. Skipping it there is safe — a no-op copy leaves the read empty anyway. The
        // sentinel only earns its keep against stale text, which a copy with no selection would
        // otherwise leave intact and misread.
        var useSentinel = previousClipboard is not null;
        var sentinel = $"⁣TW-SEL-PROBE-{Guid.NewGuid():N}";
        if (useSentinel)
        {
            await _platform.SetClipboardTextAsync(sentinel);
        }

        var afterCopy = "";
        for (var attempt = 1; attempt <= CopyProbeAttempts; attempt++)
        {
            if (!await _platform.SendCopyAsync(targetIsTerminal))
            {
                // No usable injection backend — retrying won't help.
                break;
            }

            await _platform.DelayAsync(TimeSpan.FromMilliseconds(150));
            afterCopy = await _platform.TryGetClipboardTextAsync() ?? "";

            // With a sentinel, a real selection copied when the clipboard diverges from it.
            // Without one, any non-empty read is the selection.
            var gotSelection = useSentinel
                ? !string.Equals(afterCopy, sentinel, StringComparison.Ordinal)
                : afterCopy.Length > 0;
            if (gotSelection)
            {
                break;
            }
        }

        if (!useSentinel)
        {
            if (previousClipboardHasNonTextFormats)
            {
                LogInsertionFallback(ClipboardNonTextCouldNotRestoreMessage);
            }

            return afterCopy;
        }

        if (previousClipboardHasNonTextFormats)
        {
            LogInsertionFallback(ClipboardRichRestoreLossyMessage);
        }

        await _platform.SetClipboardTextAsync(previousClipboard!);
        return string.Equals(afterCopy, sentinel, StringComparison.Ordinal) ? "" : afterCopy;
    }

    private async Task<bool> FocusTargetWindowAsync(string? targetWindowId)
    {
        if (string.IsNullOrWhiteSpace(targetWindowId)
            || _platform.GetActiveWindowId() == targetWindowId)
        {
            await _platform.DelayAsync(s_focusDelay);
            return true;
        }

        var focusRequested = await _platform.ActivateWindowAsync(targetWindowId);
        await _platform.DelayAsync(s_focusDelay);
        return focusRequested || _platform.GetActiveWindowId() == targetWindowId;
    }

    /// <summary>
    ///     Confirms the clipboard actually serves <paramref name="expected" /> before we
    ///     send the paste chord, with one clipboard re-set + re-verify when the first pass fails
    ///     (wl-copy occasionally dies before its serving child takes over the selection).
    /// </summary>
    private async Task<bool> VerifyClipboardServesAsync(string expected)
    {
        if (await WaitForClipboardToServeAsync(expected))
        {
            return true;
        }

        PasteDiag("clipboard verify exhausted; re-setting clipboard once");
        return await _platform.SetClipboardTextAsync(expected)
               && await WaitForClipboardToServeAsync(expected);
    }

    private async Task<bool> WaitForClipboardToServeAsync(string expected)
    {
        for (var attempt = 0; attempt < ClipboardVerifyAttempts; attempt++)
        {
            if (attempt > 0)
            {
                await _platform.DelayAsync(s_clipboardVerifyRetryDelay);
            }

            // wl-paste may append a trailing newline the write never had — compare
            // content modulo that, matching the ownership check in the restore below.
            var read = await _platform.TryGetClipboardTextAsync();
            if (
                read is null
                || !string.Equals(
                    read.TrimEnd('\n'),
                    expected.TrimEnd('\n'),
                    StringComparison.Ordinal
                )
            )
            {
                continue;
            }

            PasteDiag($"clipboard verified serving on attempt {attempt + 1}");
            return true;
        }

        PasteDiag($"clipboard verify failed after {ClipboardVerifyAttempts} attempts");
        return false;
    }

    /// <summary>
    ///     Fail-closed exit for terminal multiline auto-paste. The clipboard already holds our
    ///     staged text but no keystroke was sent, so — unlike the paste path — there is no
    ///     in-flight transfer to protect. Restore a faithfully captured plain-text predecessor;
    ///     otherwise retain the staged text as a manual-paste fallback. Ownership-checked like
    ///     the post-paste restore: if the user copied something newer while the insert was
    ///     failing, leave their copy alone rather than clobbering it with the stale snapshot.
    /// </summary>
    private async Task<InsertionResult> FailTerminalMultilineAsync(
        string stagedText,
        string? previousClipboard,
        bool previousClipboardHasNonTextFormats
    )
    {
        var current = await _platform.TryGetClipboardTextAsync();
        var stillOurs =
            current is not null
            && string.Equals(
                current.TrimEnd('\n'),
                stagedText.TrimEnd('\n'),
                StringComparison.Ordinal
            );

        if (!stillOurs)
        {
            return InsertionResult.Failed;
        }

        if (previousClipboard is null || previousClipboardHasNonTextFormats)
        {
            if (previousClipboardHasNonTextFormats)
            {
                LogInsertionFallback(
                    previousClipboard is null
                        ? ClipboardNonTextCouldNotRestoreMessage
                        : ClipboardRichRestoreSkippedMessage
                );
            }

            return InsertionResult.Failed;
        }

        try
        {
            await _platform.SetClipboardTextAsync(previousClipboard);
        }
        catch
        {
            /* best effort restore */
        }

        return InsertionResult.Failed;
    }

    private async Task RestorePreviousClipboardAsync(
        string pastedText,
        string? previousClipboard,
        bool previousClipboardHasNonTextFormats,
        IPasteWatch? watch,
        bool? deliveryConfirmed
    )
    {
        if (previousClipboard is null)
        {
            if (previousClipboardHasNonTextFormats)
            {
                LogInsertionFallback(ClipboardNonTextCouldNotRestoreMessage);
            }

            // Nothing to restore — no restore write can cut off the in-flight paste,
            // so there is nothing to wait for either. Still drop the watch armed
            // before the paste chord: its event subscription must not outlive the insertion.
            watch?.Dispose();
            return;
        }

        using (watch)
        {
            // Event-driven gate: a positive "text landed" signal means the target has read
            // the clipboard, so restoring now cannot cut off the transfer. The watch was
            // armed before the keystroke, so a text-changed that already fired is latched
            // and confirms instantly. Indeterminate (no watch — confirmer absent or AT-SPI
            // idle — or no event within the window) falls back to the fixed floor delay
            // that previously bounded this race on its own. Auto-enter has already paid this
            // gate before sending Enter, so reuse its result instead of re-arming a fresh timeout.
            if (deliveryConfirmed is null)
            {
                var stopwatch = s_pasteDiagEnabled ? Stopwatch.StartNew() : null;
                deliveryConfirmed = await AwaitPasteDeliveryAsync(watch);
                PasteDiag(
                    $"restore gate: {(deliveryConfirmed.Value ? "confirmed" : "floor")} after {stopwatch?.ElapsedMilliseconds ?? 0} ms"
                );
            }
            else
            {
                PasteDiag(
                    $"restore gate: {(deliveryConfirmed.Value ? "confirmed" : "floor")} reused from enter gate"
                );
            }

            // Ownership check: only restore when the clipboard still holds OUR text —
            // content equality, not identity, since Wayland re-serves can differ by a
            // trailing newline. If another app replaced it meanwhile, restoring would
            // clobber the user's newer copy.
            var current = await _platform.TryGetClipboardTextAsync();
            if (
                current is not null
                && !string.Equals(
                    current.TrimEnd('\n'),
                    pastedText.TrimEnd('\n'),
                    StringComparison.Ordinal
                )
            )
            {
                return;
            }

            if (previousClipboardHasNonTextFormats)
            {
                LogInsertionFallback(ClipboardRichRestoreSkippedMessage);
                return;
            }

            try
            {
                await _platform.SetClipboardTextAsync(previousClipboard);
            }
            catch
            {
                /* best effort restore */
            }
        }
    }

    private async Task<bool> AwaitPasteDeliveryAsync(IPasteWatch? watch)
    {
        var confirmed =
            watch is not null
            && await watch.WaitAsync(s_pasteConfirmTimeout, CancellationToken.None) == true;
        if (!confirmed)
        {
            await _platform.DelayAsync(
                _platform.IsKdePlasma
                    ? s_clipboardRestoreDelayKde
                    : s_clipboardRestoreDelayDefault
            );
        }

        return confirmed;
    }

    private async Task<bool> TrySendPasteAsync(bool useTerminalShortcut)
    {
        for (var attempt = 1; attempt <= PasteAttemptCount; attempt++)
        {
            if (await _platform.SendPasteAsync(useTerminalShortcut))
            {
                return true;
            }

            // If the platform identified a structural reason on the
            // first attempt (compositor unsupported, socket missing),
            // retrying won't help — let the caller's reason-aware popup
            // take over immediately.
            var platformReason = _platform.LastFailureReason;
            if (
                platformReason
                is InsertionFailureReason.WtypeCompositorUnsupported
                or InsertionFailureReason.YdotoolSocketUnreachable
                or InsertionFailureReason.NoWaylandTypingTool
            )
            {
                // First-failing structural reason within this request wins — a later,
                // more generic reason from this fallback's own chain walk must not
                // downgrade an earlier specific one (e.g. "ydotool socket unreachable"
                // is a more useful hint than the generic "no typing tool").
                if (LastFailureReason == InsertionFailureReason.None)
                {
                    LastFailureReason = platformReason;
                }

                return false;
            }

            if (attempt < PasteAttemptCount)
            {
                await _platform.DelayAsync(s_pasteRetryDelay);
            }
        }

        return false;
    }

    private async Task<InsertionResult> TypeTextAsync(
        string text,
        string? targetWindowId,
        bool autoEnter
    )
    {
        if (!await FocusTargetWindowAsync(targetWindowId))
        {
            LastFailureReason = InsertionFailureReason.FocusFailed;
            LogInsertionFallback("Direct typing fell back: target window could not be focused.");
            return InsertionResult.Failed;
        }

        if (!await _platform.TypeTextAsync(text))
        {
            if (_platform.LastFailureReason != InsertionFailureReason.None)
            {
                LastFailureReason = _platform.LastFailureReason;
            }

            LastTypingDeliveredPartialText = _platform.LastTypingDeliveredPartialText;
            LogInsertionFallback("Direct typing failed.");
            return InsertionResult.Failed;
        }

        if (autoEnter && !await _platform.SendEnterAsync())
        {
            LogInsertionFallback("Direct typing succeeded, but Enter could not be sent.");
        }

        return InsertionResult.Typed;
    }

    private async Task<InsertionResult> SendEnterOnlyAsync(string? targetWindowId)
    {
        if (!_platform.IsPasteAvailable)
        {
            return InsertionResult.MissingPasteTool;
        }

        if (await FocusTargetWindowAsync(targetWindowId))
        {
            return await _platform.SendEnterAsync()
                ? InsertionResult.ActionHandled
                : InsertionResult.ActionFailed;
        }

        LogInsertionFallback("Enter command failed: target window could not be focused.");
        return InsertionResult.ActionFailed;
    }

    private static bool ShouldTypeDirectly(string? processName, string? windowTitle)
    {
        return ContainsCodex(processName)
               || ContainsCodex(windowTitle)
               || ShouldTypeBrowserDirectly(processName, windowTitle)
               || IsTerminalApp(processName);

        static bool ContainsCodex(string? value)
        {
            return !string.IsNullOrWhiteSpace(value)
                   && value.Contains("codex", StringComparison.OrdinalIgnoreCase);
        }

        static bool ShouldTypeBrowserDirectly(string? processName, string? title)
        {
            return ActiveWindowService.IsSupportedBrowserWindow(processName, title)
                   && !IsMailBrowserWindow(title);
        }

        static bool IsMailBrowserWindow(string? title)
        {
            return !string.IsNullOrWhiteSpace(title)
                   && (
                       title.Contains(" Mail", StringComparison.OrdinalIgnoreCase)
                       || title.Contains("Gmail", StringComparison.OrdinalIgnoreCase)
                   );
        }
    }

    /// <summary>
    ///     Whether the target process is a terminal emulator. Terminals bind Ctrl+V to
    ///     readline quoted-insert (not paste) and map plain Ctrl+C to SIGINT (copy is
    ///     Ctrl+Shift+C), so both text insertion and selection-capture must be
    ///     terminal-aware. Substring match on "terminal" (not suffix) is intentional:
    ///     GNOME/MATE use a client-server model so the process is "gnome-terminal-server",
    ///     and Linux truncates /proc/pid/comm to 15 bytes, yielding "gnome-terminal-" —
    ///     contains "terminal" but doesn't end with it. The trailing `EndsWith("term")`
    ///     catches xfce4-terminal etc.
    /// </summary>
    public static bool IsTerminalApp(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var process = ProcessNameNormalizer.Normalize(processName);
        if (
            process.Equals("kitty", StringComparison.OrdinalIgnoreCase)
            || process.Equals("gnome-terminal", StringComparison.OrdinalIgnoreCase)
            || process.Equals("konsole", StringComparison.OrdinalIgnoreCase)
            || process.Equals("alacritty", StringComparison.OrdinalIgnoreCase)
            || process.Equals("wezterm", StringComparison.OrdinalIgnoreCase)
            || process.Equals("xterm", StringComparison.OrdinalIgnoreCase)
            || process.Equals("tilix", StringComparison.OrdinalIgnoreCase)
            || process.Equals("ghostty", StringComparison.OrdinalIgnoreCase)
            || process.Equals("foot", StringComparison.OrdinalIgnoreCase)
            || process.Equals("ptyxis", StringComparison.OrdinalIgnoreCase)
            || process.Equals("terminator", StringComparison.OrdinalIgnoreCase)
            || process.Equals("warp", StringComparison.OrdinalIgnoreCase)
            || process.Equals("hyper", StringComparison.OrdinalIgnoreCase)
            || process.Equals("st", StringComparison.OrdinalIgnoreCase)
            || process.Equals("urxvt", StringComparison.OrdinalIgnoreCase)
            || process.Equals("rxvt", StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        return process.Contains("terminal", StringComparison.OrdinalIgnoreCase)
               || process.EndsWith("term", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     True when the text is safe for ydotool's layout-dependent <c>type</c>.
    ///     Tab, newline, and printable ASCII (0x20–0x7E) are synthesizable without
    ///     a layout lookup; anything outside that range may render as the wrong glyph
    ///     on non-US keyboards and is routed through clipboard paste instead.
    /// </summary>
    private static bool IsAsciiSafe(string text)
    {
        // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator -- explicit char scan avoids the LINQ enumerator allocation on the text-insertion path
        foreach (var c in text)
        {
            if (c is '\t' or '\n' or '\r')
            {
                continue;
            }

            if (c < 0x20 || c > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsLineBreak(string text)
    {
        return text.AsSpan().IndexOfAny('\r', '\n') >= 0;
    }

    /// <summary>
    ///     Env-gated (TW_PASTE_DIAG=1) per-paste diagnostic trace. Off by default so the
    ///     hot path stays silent; used to validate the paste-readiness fix in the field.
    /// </summary>
    private static void PasteDiag(string message)
    {
        if (s_pasteDiagEnabled)
        {
            Trace.WriteLine($"[PasteDiag] {message}");
        }
    }

    private void LogInsertionFallback(string message)
    {
        Trace.WriteLine($"[TextInsertionService] {message}");
        try
        {
            _errorLog?.AddEntry(message, ErrorCategory.Insertion);
        }
        catch
        {
            // Diagnostics must never block dictation output.
        }
    }
}

internal interface ITextInsertionPlatform
{
    bool IsClipboardSetAvailable { get; }
    bool IsPasteAvailable { get; }
    bool IsKdePlasma { get; }

    /// <summary>
    ///     True when the platform should default to direct typing for any
    ///     target it cannot identify. On Wayland-without-xdotool we have no
    ///     reliable active-window detection, so <c>targetProcessName</c> is
    ///     almost always null; the paste path then sends a Ctrl+V that
    ///     terminals reject (readline quoted-insert), Claude Code interprets
    ///     as image paste, vim sees as normal-mode garbage, etc. Direct
    ///     typing via ydotool works in all of those.
    /// </summary>
    bool PrefersDirectTypingForUnknownTarget { get; }

    InsertionFailureReason LastFailureReason { get; }

    /// <summary>
    ///     True when the most recent typing attempt aborted mid-sequence after at least
    ///     one segment had already reached the target. The caller must then suppress the
    ///     clipboard fallback — a full re-paste would duplicate the delivered prefix —
    ///     regardless of which failure reason was recorded.
    /// </summary>
    bool LastTypingDeliveredPartialText { get; }

    Task<string?> TryGetClipboardTextAsync();
    Task<bool> SetClipboardTextAsync(string text);

    /// <summary>
    ///     True when the clipboard currently offers a MIME type beyond ordinary plain text
    ///     (an image, a file list, HTML, etc.) — queried before an insertion overwrites the
    ///     clipboard, so a caller can tell whether the value it is about to destroy was
    ///     something a plain-text round trip cannot faithfully preserve or restore.
    /// </summary>
    Task<bool> ClipboardHasNonTextFormatsAsync();

    Task DelayAsync(TimeSpan delay);
    string? GetActiveWindowId();
    Task<bool> ActivateWindowAsync(string windowId);

    /// <summary>
    ///     Synthesizes a paste. When <paramref name="useTerminalShortcut" /> is true,
    ///     sends Ctrl+Shift+V (terminals map plain Ctrl+V to readline quoted-insert);
    ///     otherwise Ctrl+V.
    /// </summary>
    Task<bool> SendPasteAsync(bool useTerminalShortcut = false);
    Task<bool> TypeTextAsync(string text);

    /// <summary>
    ///     Synthesizes a copy. When <paramref name="useTerminalShortcut" /> is true,
    ///     sends Ctrl+Shift+C (terminals map plain Ctrl+C to SIGINT); otherwise Ctrl+C.
    /// </summary>
    Task<bool> SendCopyAsync(bool useTerminalShortcut);
    Task<bool> SendEnterAsync();
}

/// <summary>
///     Wire-level adapter that walks a per-compositor backend chain. The
///     chain is built once at construction; per-attempt failure reasons are
///     surfaced through <see cref="LastFailureReason" /> so the higher layer
///     can stop retrying when the failure is structural (compositor refused
///     wtype, ydotool socket missing) rather than transient.
/// </summary>
internal sealed class LinuxTextInsertionPlatform : ITextInsertionPlatform
{
    private static readonly HashSet<string> s_waylandTextSafeTargets =
        new(StringComparer.OrdinalIgnoreCase) { "STRING", "UTF8_STRING", "TEXT" };

    // X11 TARGETS listings always include protocol/negotiation targets that carry no
    // content of their own alongside the plain-text encodings. None count as non-text content.
    private static readonly HashSet<string> s_x11TextSafeTargets = new(
        [
            "TARGETS",
            "MULTIPLE",
            "SAVE_TARGETS",
            "TIMESTAMP",
            "STRING",
            "UTF8_STRING",
            "TEXT",
            "COMPOUND_TEXT"
        ],
        StringComparer.OrdinalIgnoreCase
    );

    // kept injected as a DI/test seam; not consumed in-tree
    // ReSharper disable once NotAccessedField.Local
    private readonly SystemCommandAvailabilityService? _commands;
    private readonly bool _isWayland;
    private readonly ProcessRunnerWithEnv _processRunner;

    private readonly Func<
        string,
        IReadOnlyList<string>,
        Task<(int exitCode, string stderr)>
    >? _processRunnerWithStderr;

    private List<InputBackend> _chain;
    private HashSet<InputBackend> _disabled = [];
    private bool _abortChainAfterAttempt;

    private LinuxCapabilitySnapshot _snapshot;

    public LinuxTextInsertionPlatform(SystemCommandAvailabilityService commands)
        : this(commands, DefaultProcessRunnerWithEnv, DefaultProcessRunnerWithStderr)
    {
    }

    internal LinuxTextInsertionPlatform(
        SystemCommandAvailabilityService commands,
        ProcessRunnerWithEnv processRunner,
        Func<
            string,
            IReadOnlyList<string>,
            Task<(int exitCode, string stderr)>
        >? processRunnerWithStderr
    )
        : this(commands.GetSnapshot(), processRunner, processRunnerWithStderr)
    {
        _commands = commands;
        // Rebuild chain in place whenever the snapshot refreshes (e.g. after ydotool setup).
        commands.SnapshotChanged += OnSnapshotChanged;
    }

    internal LinuxTextInsertionPlatform(
        LinuxCapabilitySnapshot snapshot,
        Func<string, IReadOnlyList<string>, Task<int>> processRunner
    )
        : this(
            snapshot,
            (file, args, _) => processRunner(file, args),
            // Tests inject the legacy single-return runner that already
            // records wtype's invocation but doesn't surface stderr.
            // Adapt it to the stderr-aware shape so the chain stays on a
            // single mocked code path — otherwise real wtype would be
            // spawned, fail, and the test would never see its argv.
            async (file, args) =>
                (await processRunner(file, args).ConfigureAwait(false), string.Empty)
        )
    {
    }

    internal LinuxTextInsertionPlatform(
        LinuxCapabilitySnapshot snapshot,
        ProcessRunnerWithEnv processRunner,
        Func<
            string,
            IReadOnlyList<string>,
            Task<(int exitCode, string stderr)>
        >? processRunnerWithStderr = null
    )
    {
        _snapshot = snapshot;
        _processRunner = processRunner;
        _processRunnerWithStderr = processRunnerWithStderr;
        _isWayland = snapshot.SessionType == "Wayland";
        _chain = BuildChain(snapshot);
    }

    public bool IsClipboardSetAvailable =>
        _isWayland ? IsCommandAvailable("wl-copy") : IsCommandAvailable("xclip");

    public bool IsPasteAvailable => _chain.Count > 0;

    public bool IsKdePlasma => _snapshot.Compositor == "kde";

    // On Wayland, unknown targets must be typed (not pasted): Ctrl+V is rejected by
    // terminals (readline quoted-insert), misread by vim normal mode, and treated as
    // image-paste by Claude Code. Gate on a Wayland-native typing backend (ydotool or wtype),
    // not on xdotool's presence — xdotool can't see Wayland windows.
    public bool PrefersDirectTypingForUnknownTarget =>
        _isWayland
        && (
            _snapshot.HasYdotoolAvailable
            || _snapshot is { HasWtype: true, CompositorRejectsWtype: false }
        );

    public InsertionFailureReason LastFailureReason { get; private set; } = InsertionFailureReason.None;

    public bool LastTypingDeliveredPartialText { get; private set; }

    public async Task<string?> TryGetClipboardTextAsync()
    {
        var psi = _isWayland
            ? new ProcessStartInfo("wl-paste", "--no-newline")
            : new ProcessStartInfo("xclip", "-selection clipboard -o");
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TextInsertionService] clipboard read failed: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> ClipboardHasNonTextFormatsAsync()
    {
        var psi = _isWayland
            ? new ProcessStartInfo("wl-paste", "--list-types")
            : new ProcessStartInfo("xclip", "-selection clipboard -o -t TARGETS");
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }

            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return p.ExitCode == 0 && ListingHasNonTextFormats(output, _isWayland);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(
                $"[TextInsertionService] clipboard format listing failed: {ex.Message}"
            );
            return false;
        }
    }

    internal static bool ListingHasNonTextFormats(string listing, bool isWayland)
    {
        var textSafe = isWayland ? s_waylandTextSafeTargets : s_x11TextSafeTargets;
        return listing.Split('\n').Any(rawLine =>
        {
            var target = rawLine.Trim();
            return target.Length != 0
                   && !textSafe.Contains(target)
                   && !target.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase);
        });
    }

    public async Task<bool> SetClipboardTextAsync(string text)
    {
        var psi = _isWayland
            ? new ProcessStartInfo("wl-copy")
            : new ProcessStartInfo("xclip", "-selection clipboard");
        psi.RedirectStandardInput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }

            await p.StandardInput.WriteAsync(text);
            p.StandardInput.Close();
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TextInsertionService] clipboard write failed: {ex.Message}");
            return false;
        }
    }

    public Task DelayAsync(TimeSpan delay)
    {
        return Task.Delay(delay);
    }

    public string? GetActiveWindowId()
    {
        // On Wayland, getactivewindow returns the XWayland surface — useless for ydotool/wtype.
        if (_isWayland || !_snapshot.HasXdotool)
        {
            return null;
        }

        var output = RunXdotoolSync("getactivewindow");
        return string.IsNullOrWhiteSpace(output) ? null : output;
    }

    public async Task<bool> ActivateWindowAsync(string windowId)
    {
        // On Wayland we can't focus a window from the client side; the
        // overlay-restore plumbing relies on the compositor having
        // already restored focus by the time we get here.
        if (_isWayland)
        {
            return true;
        }

        if (!_snapshot.HasXdotool)
        {
            return false;
        }

        return await RunWithEnv("xdotool", ["windowactivate", "--sync", windowId], null)
               == 0;
    }

    public async Task<bool> SendPasteAsync(bool useTerminalShortcut = false)
    {
        return await WalkChainAsync(async backend =>
            backend switch
            {
                InputBackend.Wtype => useTerminalShortcut
                    ? await RunWtypeAsync("-M", "ctrl", "-M", "shift", "v", "-m", "shift", "-m", "ctrl")
                    : await RunWtypeAsync("-M", "ctrl", "v", "-m", "ctrl"),
                InputBackend.Xdotool => useTerminalShortcut
                    ? await SendModifiedKeyAsync(["Control_L", "Shift_L"], "v")
                    : await SendModifiedKeyAsync("Control_L", "v"),
                InputBackend.Ydotool => await RunYdotoolAsync(
                    useTerminalShortcut
                        ? YdotoolBackend.TerminalPasteArgs()
                        : YdotoolBackend.PasteArgs()
                ),
                _ => false
            }
        );
    }

    public Task<bool> TypeTextAsync(string text)
    {
        // Map newlines to Shift+Enter rather than a bare Return so that
        // dictated paragraph breaks insert a newline instead of submitting
        // in chat boxes (Slack / Discord / web chat / Claude's box), where
        // Enter sends. The strategy layer must keep terminal multiline text
        // out of this method: terminal protocols still deliver Shift+Return
        // as Return, which can submit partial shell commands. The clipboard-
        // paste path is unaffected (pasted multiline text does not submit).
        return WalkChainAsync(backend => TypeWithNewlinesAsync(backend, text));
    }

    private async Task<bool> TypeWithNewlinesAsync(InputBackend backend, string text)
    {
        // Normalize CRLF / lone CR to LF so each line break is one Shift+Enter.
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!normalized.Contains('\n'))
        {
            return await TypeSegmentAsync(backend, normalized);
        }

        var segments = normalized.Split('\n');
        var delivered = false;
        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0)
            {
                if (!await SendShiftEnterAsync(backend))
                {
                    return FailPartway(delivered);
                }

                // A landed Shift+Enter is itself delivery: it puts a newline in the
                // target even when every segment so far was empty (leading/blank
                // lines). Count it so a later failure fails closed instead of letting
                // the chain retype from scratch and duplicate the newline.
                delivered = true;
            }

            var segment = segments[i];
            if (segment.Length == 0)
            {
                continue;
            }

            if (!await TypeSegmentAsync(backend, segment))
            {
                return FailPartway(delivered);
            }

            delivered = true;
        }

        return true;

        // A failure after at least one segment already reached the target means
        // retrying — with this backend or the next — would retype from the start
        // and duplicate what's already there (or resubmit a partial shell command
        // in a terminal). Stop the chain instead of risking a silent duplicate.
        bool FailPartway(bool hasDelivered)
        {
            if (!hasDelivered)
            {
                return false;
            }

            _abortChainAfterAttempt = true;
            LastTypingDeliveredPartialText = true;
            if (LastFailureReason == InsertionFailureReason.None)
            {
                LastFailureReason = InsertionFailureReason.PartialTypingFailure;
            }

            return false;
        }
    }

    private async Task<bool> TypeSegmentAsync(InputBackend backend, string segment)
    {
        return backend switch
        {
            InputBackend.Wtype => await RunWtypeAsync("--", segment),
            InputBackend.Xdotool => await RunWithEnv(
                "xdotool",
                ["type", "--clearmodifiers", "--delay", "8", "--", segment],
                null
            ) == 0,
            InputBackend.Ydotool => await RunYdotoolAsync(YdotoolBackend.TypeArgs(segment)),
            _ => false
        };
    }

    private async Task<bool> SendShiftEnterAsync(InputBackend backend)
    {
        return backend switch
        {
            InputBackend.Wtype => await RunWtypeAsync("-M", "shift", "-k", "Return", "-m", "shift"),
            InputBackend.Xdotool => await RunWithEnv(
                "xdotool",
                ["key", "--clearmodifiers", "shift+Return"],
                null
            ) == 0,
            InputBackend.Ydotool => await RunYdotoolAsync(YdotoolBackend.ShiftEnterArgs()),
            _ => false
        };
    }

    public async Task<bool> SendCopyAsync(bool useTerminalShortcut)
    {
        return await WalkChainAsync(async backend =>
            backend switch
            {
                InputBackend.Wtype => useTerminalShortcut
                    ? await RunWtypeAsync("-M", "ctrl", "-M", "shift", "c", "-m", "shift", "-m", "ctrl")
                    : await RunWtypeAsync("-M", "ctrl", "c", "-m", "ctrl"),
                InputBackend.Xdotool => useTerminalShortcut
                    ? await SendModifiedKeyAsync(["Control_L", "Shift_L"], "c")
                    : await SendModifiedKeyAsync("Control_L", "c"),
                InputBackend.Ydotool => await RunYdotoolAsync(
                    useTerminalShortcut ? YdotoolBackend.TerminalCopyArgs() : YdotoolBackend.CopyArgs()
                ),
                _ => false
            }
        );
    }

    public async Task<bool> SendEnterAsync()
    {
        return await WalkChainAsync(async backend =>
            backend switch
            {
                InputBackend.Wtype => await RunWtypeAsync("-k", "Return"),
                InputBackend.Xdotool => await RunWithEnv(
                    "xdotool",
                    ["key", "--clearmodifiers", "Return"],
                    null
                ) == 0,
                InputBackend.Ydotool => await RunYdotoolAsync(YdotoolBackend.EnterArgs()),
                _ => false
            }
        );
    }

    /// <summary>
    ///     Re-reads the capability snapshot and rebuilds the backend chain
    ///     in place. Called from the SnapshotChanged subscription so that
    ///     the live singleton picks up newly-installed tools (ydotool
    ///     daemon, wtype, etc.) without an app restart.
    /// </summary>
    internal void ApplyRefreshedSnapshot(LinuxCapabilitySnapshot snapshot)
    {
        var newChain = BuildChain(snapshot);
        var newDisabled = new HashSet<InputBackend>();
        _snapshot = snapshot;
        _chain = newChain;
        _disabled = newDisabled;
        LastFailureReason = InsertionFailureReason.None;
    }

    private void OnSnapshotChanged(object? sender, LinuxCapabilitySnapshot snapshot)
    {
        ApplyRefreshedSnapshot(snapshot);
    }

    private async Task<bool> WalkChainAsync(Func<InputBackend, Task<bool>> attempt)
    {
        var chain = _chain;
        var disabled = _disabled;
        LastFailureReason = InsertionFailureReason.None;
        _abortChainAfterAttempt = false;
        LastTypingDeliveredPartialText = false;
        if (chain.Count == 0)
        {
            LastFailureReason = InsertionFailureReason.NoWaylandTypingTool;
            return false;
        }

        var anyAttempted = false;
        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator -- body awaits and mutates state; the explicit disabled-backend guard is clearer than a LINQ Where
        foreach (var backend in chain)
        {
            if (disabled.Contains(backend))
            {
                continue;
            }

            anyAttempted = true;
            if (await attempt(backend))
            {
                return true;
            }

            if (_abortChainAfterAttempt)
            {
                break;
            }
        }

        if (!anyAttempted)
        {
            LastFailureReason = InsertionFailureReason.NoWaylandTypingTool;
        }

        return false;
    }

    /// <summary>
    ///     Build the ordered list of backends to try. Ordering is the heart
    ///     of this phase: GNOME / KDE Wayland get ydotool first because
    ///     wtype is doomed there; wlroots compositors (Hyprland / Sway /
    ///     unknown wlroots-shaped sessions) keep wtype as the canonical
    ///     fast path; X11 stays xdotool-only.
    /// </summary>
    private static List<InputBackend> BuildChain(LinuxCapabilitySnapshot snapshot)
    {
        var chain = new List<InputBackend>();

        if (snapshot.SessionType == "Wayland")
        {
            var ydotoolUsable = snapshot is { HasYdotool: true, HasYdotoolSocket: true };
            if (snapshot.CompositorRejectsWtype)
            {
                if (ydotoolUsable)
                {
                    chain.Add(InputBackend.Ydotool);
                }

                if (snapshot.HasWtype)
                {
                    chain.Add(InputBackend.Wtype);
                }
            }
            else
            {
                if (snapshot.HasWtype)
                {
                    chain.Add(InputBackend.Wtype);
                }

                if (ydotoolUsable)
                {
                    chain.Add(InputBackend.Ydotool);
                }
            }

            // xdotool is never added on Wayland: XTEST reaches only XWayland
            // surfaces and can exit 0 even when the native-Wayland target received
            // nothing — and nothing here can tell whether the focused surface
            // is XWayland.
        }
        else if (snapshot.HasXdotool)
        {
            chain.Add(InputBackend.Xdotool);
        }

        return chain;
    }

    private Task<bool> SendModifiedKeyAsync(string modifier, string key)
    {
        return SendModifiedKeyAsync([modifier], key);
    }

    // Holds every modifier down (in order), taps the key, then releases the
    // modifiers in reverse — so Ctrl+Shift+C etc. arrive as a real chord. Only
    // modifiers that actually went down are released, so a failed keydown never
    // leaves a stuck modifier.
    private async Task<bool> SendModifiedKeyAsync(IReadOnlyList<string> modifiers, string key)
    {
        var pressed = new List<string>(modifiers.Count);
        var allDown = true;
        foreach (var modifier in modifiers)
        {
            if (await RunWithEnv("xdotool", ["keydown", "--clearmodifiers", modifier], null) == 0)
            {
                pressed.Add(modifier);
            }
            else
            {
                allDown = false;
                break;
            }
        }

        var keySent = false;
        try
        {
            if (allDown)
            {
                keySent = await RunWithEnv("xdotool", ["key", key], null) == 0;
            }
        }
        finally
        {
            for (var i = pressed.Count - 1; i >= 0; i--)
            {
                await RunWithEnv("xdotool", ["keyup", pressed[i]], null);
            }
        }

        return allDown && keySent;
    }

    private static bool IsCommandAvailable(string command)
    {
        return SystemCommandAvailabilityService.IsCommandAvailable(command);
    }

    private static string? RunXdotoolSync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("xdotool", arguments)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
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

                return null;
            }

            var output = stdoutTask.GetAwaiter().GetResult();
            stderrTask.GetAwaiter().GetResult();
            return p.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TextInsertionService] xdotool failed: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> RunWtypeAsync(params string[] args)
    {
        // Capture stderr to detect compositor rejection and disable wtype permanently —
        // without this every dictation on GNOME/KDE Wayland wastes ~225 ms on a doomed backend.
        if (_processRunnerWithStderr is null)
        {
            return await RunWithEnv("wtype", args, null) == 0;
        }

        var (exitCode, stderr) = await _processRunnerWithStderr("wtype", args)
            .ConfigureAwait(false);
        if (exitCode == 0 || !IsWtypeCompositorRejection(stderr))
        {
            return exitCode == 0;
        }

        _disabled.Add(InputBackend.Wtype);
        // First-failing backend's reason wins — keep an earlier specific diagnostic.
        if (LastFailureReason == InsertionFailureReason.None)
        {
            LastFailureReason = InsertionFailureReason.WtypeCompositorUnsupported;
        }

        // Reached only on the compositor-rejection path, where exitCode is always non-zero.
        return false;
    }

    private static bool IsWtypeCompositorRejection(string stderr)
    {
        return !string.IsNullOrEmpty(stderr)
               && (
                   stderr.Contains("Compositor does not support", StringComparison.OrdinalIgnoreCase)
                   || stderr.Contains("virtual keyboard", StringComparison.OrdinalIgnoreCase)
               );
    }

    private async Task<bool> RunYdotoolAsync(IReadOnlyList<string> args)
    {
        var env = YdotoolBackend.BuildEnv(_snapshot.YdotoolSocketPath);
        if (env is null)
        {
            if (LastFailureReason == InsertionFailureReason.None)
            {
                LastFailureReason = InsertionFailureReason.YdotoolSocketUnreachable;
            }

            _disabled.Add(InputBackend.Ydotool);
            return false;
        }

        var exit = await RunWithEnv(YdotoolBackend.ExecutableName, args, env);
        if (exit == 0)
        {
            return true;
        }

        // Almost always EACCES on /dev/uinput (uaccess didn't apply, not in input group)
        // or a wedged socket. Mark as sticky — disable for this process lifetime so the
        // chain skips ydotool rather than spawning it on every subsequent dictation.
        if (LastFailureReason == InsertionFailureReason.None)
        {
            LastFailureReason = InsertionFailureReason.YdotoolSocketUnreachable;
        }

        _disabled.Add(InputBackend.Ydotool);
        return false;
    }

    private Task<int> RunWithEnv(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env
    )
    {
        return _processRunner(fileName, args, env);
    }

    private static async Task<int> DefaultProcessRunnerWithEnv(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env
    )
    {
        try
        {
            var psi = new ProcessStartInfo(fileName) { RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            if (env is not null)
            {
                foreach (var (key, value) in env)
                {
                    psi.Environment[key] = value;
                }
            }

            using var p = Process.Start(psi);
            if (p is null)
            {
                return -1;
            }

            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TextInsertionService] {fileName} failed: {ex.Message}");
            return -1;
        }
    }

    private static async Task<(int exitCode, string stderr)> DefaultProcessRunnerWithStderr(
        string fileName,
        IReadOnlyList<string> args
    )
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var p = Process.Start(psi);
            if (p is null)
            {
                return (-1, string.Empty);
            }

            var stderrTask = p.StandardError.ReadToEndAsync();
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            var stderr = await stderrTask.ConfigureAwait(false);
            await stdoutTask.ConfigureAwait(false);
            return (p.ExitCode, stderr);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TextInsertionService] {fileName} failed: {ex.Message}");
            return (-1, string.Empty);
        }
    }

    private enum InputBackend
    {
        // ReSharper disable once UnusedMember.Local -- zero-value sentinel so default(InputBackend) is not a real backend
        None,
        Xdotool,
        Wtype,
        Ydotool
    }

    internal delegate Task<int> ProcessRunnerWithEnv(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env
    );
}
