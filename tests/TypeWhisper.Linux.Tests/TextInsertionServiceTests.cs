using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using TypeWhisper.Linux.Services.Insertion;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class TextInsertionServiceTests
{
    [Fact]
    public async Task InsertTextAsync_successful_auto_paste_restores_previous_clipboard()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Equal("previous", platform.Clipboard);
        Assert.True(platform.PasteSent);
        Assert.Equal(1, platform.PasteAttemptCount);
    }

    [Fact]
    public async Task InsertTextAsync_retries_failed_paste_before_fallback()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = false
        };
        var confirmation = new FakePasteConfirmationSource { Result = true };
        var sut = new TextInsertionService(platform, pasteConfirmation: confirmation);

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.Equal("new text", platform.Clipboard);
        Assert.True(platform.PasteSent);
        Assert.Equal(3, platform.PasteAttemptCount);
        // The pre-armed watch is dropped unconsulted when the paste never went out.
        Assert.NotNull(confirmation.LastWatch);
        Assert.False(confirmation.LastWatch.WaitCalled);
        Assert.True(confirmation.LastWatch.Disposed);
    }

    [Fact]
    public async Task InsertTextAsync_successful_retry_restores_previous_clipboard()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteResults = new Queue<bool>([false, false, true])
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Equal("previous", platform.Clipboard);
        Assert.Equal(3, platform.PasteAttemptCount);
    }

    [Fact]
    public async Task InsertTextAsync_verifies_clipboard_serves_before_paste_and_retries_read()
    {
        // wl-copy's serving child isn't up yet: the first verify read still returns the
        // OLD clipboard. The bounded verify loop must retry the read (not the write) and
        // proceed once the clipboard serves the new text.
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true,
            ClipboardReadResults = new Queue<string?>(
                [
                    "previous", // snapshot of the user's clipboard
                    "previous", // verify attempt 1 — wl-copy not serving yet
                    "new text" // verify attempt 2 — serving
                ]
            )
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Equal(1, platform.PasteAttemptCount);
        // One inter-attempt verify delay ran; no second clipboard write was needed
        // before the paste (initial set + post-paste restore only).
        Assert.Contains(TimeSpan.FromMilliseconds(40), platform.Delays);
        Assert.Equal(2, platform.SetClipboardCount);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Fact]
    public async Task InsertTextAsync_verify_failure_resets_clipboard_once_then_proceeds()
    {
        // The whole first verify pass fails (wl-copy died before serving); the one
        // re-set + re-verify recovers and the paste still goes out.
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true,
            ClipboardReadResults = new Queue<string?>(
                [
                    "previous", // snapshot
                    "previous", "previous", "previous", "previous", // verify pass 1 — all stale
                    "new text" // verify pass 2 after the re-set — serving
                ]
            )
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Equal(1, platform.PasteAttemptCount);
        // initial set + one verify re-set + post-paste restore
        Assert.Equal(3, platform.SetClipboardCount);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Fact]
    public async Task InsertTextAsync_verify_never_serves_skips_paste_and_falls_back_to_clipboard()
    {
        // A silently broken wl-copy means Ctrl+V would paste the user's stale previous
        // clipboard. The verify gate must swallow the paste entirely and report the
        // clipboard fallback instead.
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true,
            ClipboardReadResults = new Queue<string?>(
                [
                    "previous", // snapshot
                    "previous", "previous", "previous", "previous", // verify pass 1
                    "previous", "previous", "previous", "previous" // verify pass 2 after re-set
                ]
            )
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.False(platform.PasteSent);
        // initial set + the single re-set retry — no restore write after the fallback
        Assert.Equal(2, platform.SetClipboardCount);
    }

    [Fact]
    public async Task InsertTextAsync_confirmed_paste_restores_immediately_without_floor_delay()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true
        };
        var confirmation = new FakePasteConfirmationSource { Result = true };
        var sut = new TextInsertionService(platform, pasteConfirmation: confirmation);

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.NotNull(confirmation.LastWatch);
        Assert.True(confirmation.LastWatch.WaitCalled);
        Assert.True(confirmation.LastWatch.Disposed);
        Assert.Equal("previous", platform.Clipboard);
        // Positive confirmation must skip the fixed restore floor entirely.
        Assert.DoesNotContain(TimeSpan.FromMilliseconds(500), platform.Delays);
        Assert.DoesNotContain(TimeSpan.FromMilliseconds(600), platform.Delays);
    }

    [Fact]
    public async Task InsertTextAsync_arms_paste_watch_before_sending_ctrl_v()
    {
        // Regression guard for the timing bug: the confirmer used to subscribe inside
        // the restore step — AFTER Ctrl+V — so the paste's text-changed had already
        // fired unobserved and every restore burned the full confirmation timeout.
        var order = new List<string>();
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true,
            OnPasteSent = () => order.Add("ctrl-v")
        };
        var confirmation = new FakePasteConfirmationSource
        {
            Result = true,
            OnBeginWatch = () => order.Add("begin-watch")
        };
        var sut = new TextInsertionService(platform, pasteConfirmation: confirmation);

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Equal(["begin-watch", "ctrl-v"], order);
    }

    [Fact]
    public async Task InsertTextAsync_text_changed_during_paste_is_latched_and_confirms_immediately()
    {
        // End-to-end through the real AtSpiPasteConfirmation: the target's text-changed
        // arrives while Ctrl+V is being processed — before the restore step ever awaits
        // the watch. The pre-armed watch must have latched it, so the restore confirms
        // instantly instead of waiting out the timeout and then floor-delaying anyway.
        var client = new FakeAtSpiEventClient();
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true,
            OnPasteSent = () =>
                client.RaiseTextChanged(new AtSpiElementRef(":1.7", "/org/a11y/atspi/accessible/42"))
        };
        var sut = new TextInsertionService(
            platform,
            pasteConfirmation: new AtSpiPasteConfirmation(client)
        );

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Equal("previous", platform.Clipboard);
        Assert.DoesNotContain(TimeSpan.FromMilliseconds(500), platform.Delays);
        Assert.DoesNotContain(TimeSpan.FromMilliseconds(600), platform.Delays);
        // The disposed watch left no dangling subscription on the client.
        Assert.False(client.HasTextChangedSubscribers);
        // ...and released the text-changed registration lease it held for the paste window.
        Assert.Equal(1, client.AcquireCount);
        Assert.Equal(0, client.ActiveAcquisitions);
    }

    [Fact]
    public async Task InsertTextAsync_paste_watch_acquires_and_releases_text_changed_lease()
    {
        // The paste watch holds a text-changed registration lease for the paste window (so a
        // paste with no armed field still observes text-changed) and must release it on Dispose,
        // exactly once — a leaked lease would reinstate the standing a11y flood.
        var client = new FakeAtSpiEventClient();
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true
        };
        var sut = new TextInsertionService(
            platform,
            pasteConfirmation: new AtSpiPasteConfirmation(client)
        );

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Equal(1, client.AcquireCount);
        Assert.Equal(0, client.ActiveAcquisitions);
    }

    [Fact]
    public async Task InsertTextAsync_without_confirmer_uses_floor_delay_then_restores()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Contains(TimeSpan.FromMilliseconds(500), platform.Delays);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Fact]
    public async Task InsertTextAsync_indeterminate_confirmation_uses_floor_delay_then_restores()
    {
        // The confirmer is wired but AT-SPI is idle (feature off) — BeginWatch returns
        // null, and the restore must behave exactly like the pre-existing fixed-delay path.
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true
        };
        var confirmation = new FakePasteConfirmationSource { SourceNotRunning = true };
        var sut = new TextInsertionService(platform, pasteConfirmation: confirmation);

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.True(confirmation.BeginWatchCalled);
        Assert.Null(confirmation.LastWatch);
        Assert.Contains(TimeSpan.FromMilliseconds(500), platform.Delays);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Fact]
    public async Task InsertTextAsync_watch_timeout_uses_floor_delay_then_restores()
    {
        // AT-SPI is running but the target never emitted text-changed within the
        // confirmation window — indeterminate, so the floor delay still applies.
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true
        };
        var confirmation = new FakePasteConfirmationSource { Result = null };
        var sut = new TextInsertionService(platform, pasteConfirmation: confirmation);

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.NotNull(confirmation.LastWatch);
        Assert.True(confirmation.LastWatch.WaitCalled);
        Assert.Equal(TimeSpan.FromSeconds(2), confirmation.LastWatch.LastTimeout);
        Assert.True(confirmation.LastWatch.Disposed);
        Assert.Contains(TimeSpan.FromMilliseconds(500), platform.Delays);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Fact]
    public async Task InsertTextAsync_skips_restore_when_clipboard_no_longer_holds_our_text()
    {
        // Between Ctrl+V and the restore the user copied something themselves —
        // restoring the old snapshot now would clobber their newer copy.
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true,
            ClipboardReadResults = new Queue<string?>(
                [
                    "previous", // snapshot
                    "new text", // verify — serving
                    "user copied meanwhile" // ownership check before restore
                ]
            )
        };
        var confirmation = new FakePasteConfirmationSource { Result = true };
        var sut = new TextInsertionService(platform, pasteConfirmation: confirmation);

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        // No restore write happened: only the initial set.
        Assert.Equal(1, platform.SetClipboardCount);
        Assert.Equal("new text", platform.Clipboard);
    }

    [Fact]
    public async Task InsertTextAsync_null_previous_clipboard_skips_wait_and_restore()
    {
        // Nothing to restore means no restore write can cut off the in-flight paste —
        // the service must return without awaiting the watch or delaying, but must
        // still dispose the pre-armed watch so its subscription doesn't leak.
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = null,
            PasteSucceeds = true
        };
        var confirmation = new FakePasteConfirmationSource { Result = true };
        var sut = new TextInsertionService(platform, pasteConfirmation: confirmation);

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.NotNull(confirmation.LastWatch);
        Assert.False(confirmation.LastWatch.WaitCalled);
        Assert.True(confirmation.LastWatch.Disposed);
        Assert.DoesNotContain(TimeSpan.FromMilliseconds(500), platform.Delays);
        Assert.Equal("new text", platform.Clipboard);
    }

    [Fact]
    public async Task InsertTextAsync_copy_only_sets_clipboard_without_restore()
    {
        var platform = new FakeTextInsertionPlatform { Clipboard = "previous" };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("new text", false);

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.Equal("new text", platform.Clipboard);
        Assert.False(platform.PasteSent);
    }

    [Fact]
    public async Task InsertTextAsync_focus_failure_falls_back_to_clipboard()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            ActiveWindowId = "other",
            ActivateSucceeds = false
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "new text",
            true,
            "target"
        );

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.Equal("new text", platform.Clipboard);
        Assert.False(platform.PasteSent);
    }

    [Fact]
    public async Task InsertTextAsync_missing_clipboard_tool_returns_specific_result()
    {
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardSetAvailable = false,
            PasteAvailable = true
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.MissingClipboardTool, result);
        Assert.False(platform.PasteSent);
    }

    [Fact]
    public async Task InsertTextAsync_missing_paste_tool_returns_specific_result_when_auto_paste_enabled()
    {
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardSetAvailable = true,
            PasteAvailable = false
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.MissingPasteTool, result);
        Assert.False(platform.PasteSent);
    }

    [Fact]
    public async Task InsertTextAsync_missing_paste_tool_allows_copy_only()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            ClipboardSetAvailable = true,
            PasteAvailable = false
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("new text", false);

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.Equal("new text", platform.Clipboard);
        Assert.False(platform.PasteSent);
    }

    [Fact]
    public async Task InsertTextAsync_codex_window_uses_direct_typing()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "new text",
            targetWindowTitle: "Codex"
        );

        Assert.Equal(InsertionResult.Typed, result);
        Assert.Equal("new text", platform.TypedText);
        Assert.False(platform.PasteSent);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Fact]
    public async Task InsertTextAsync_codex_process_uses_direct_typing()
    {
        var platform = new FakeTextInsertionPlatform();
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "new text",
            targetProcessName: "codex"
        );

        Assert.Equal(InsertionResult.Typed, result);
        Assert.Equal("new text", platform.TypedText);
        Assert.False(platform.PasteSent);
    }

    [Theory]
    [InlineData("kitty")]
    // gnome-terminal / mate-terminal are client-server: the window-owning
    // process is "*-terminal-server", and /proc/<pid>/comm (what
    // Process.ProcessName reads) is truncated to 15 bytes — so we see these
    // mangled forms in the wild and must still type, not paste.
    [InlineData("gnome-terminal-server")]
    [InlineData("gnome-terminal-")]
    [InlineData("mate-terminal-s")]
    public async Task InsertTextAsync_terminal_process_uses_direct_typing(string processName)
    {
        var platform = new FakeTextInsertionPlatform();
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "new text",
            targetProcessName: processName,
            targetWindowTitle: "typewhisper-linux"
        );

        Assert.Equal(InsertionResult.Typed, result);
        Assert.Equal("new text", platform.TypedText);
        Assert.False(platform.PasteSent);
    }

    [Theory]
    [InlineData("firefox", "Example")]
    [InlineData("zen", "Teamwork — Zen Browser")]
    [InlineData(null, "Teamwork — Zen Browser")]
    public async Task InsertTextAsync_browser_target_uses_direct_typing(
        string? processName,
        string windowTitle
    )
    {
        var platform = new FakeTextInsertionPlatform { Clipboard = "previous" };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "new text",
            targetProcessName: processName,
            targetWindowTitle: windowTitle
        );

        Assert.Equal(InsertionResult.Typed, result);
        Assert.Equal("new text", platform.TypedText);
        Assert.False(platform.PasteSent);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Theory]
    [InlineData("zen", "Inbox (3,013) - chris@example.com - Mail — Zen Browser")]
    [InlineData("firefox", "Gmail - Inbox")]
    public async Task InsertTextAsync_mail_browser_target_uses_clipboard_paste(
        string? processName,
        string windowTitle
    )
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "new text",
            targetProcessName: processName,
            targetWindowTitle: windowTitle
        );

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.True(platform.PasteSent);
        Assert.Null(platform.TypedText);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Fact]
    public async Task InsertTextAsync_clipboard_paste_strategy_overrides_terminal_direct_typing()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "new text",
            targetProcessName: "kitty",
            targetWindowTitle: "typewhisper-linux",
            strategy: TextInsertionStrategy.ClipboardPaste
        );

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.True(platform.PasteSent);
        Assert.Null(platform.TypedText);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Fact]
    public async Task InsertTextAsync_direct_typing_strategy_types_for_non_terminal_app()
    {
        var platform = new FakeTextInsertionPlatform { Clipboard = "previous" };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "new text",
            targetProcessName: "firefox",
            strategy: TextInsertionStrategy.DirectTyping
        );

        Assert.Equal(InsertionResult.Typed, result);
        Assert.Equal("new text", platform.TypedText);
        Assert.False(platform.PasteSent);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Fact]
    public async Task InsertTextAsync_unknown_target_with_ascii_text_types_directly()
    {
        // Wayland-without-xdotool: PrefersDirectTypingForUnknownTarget=true.
        // Pure-ASCII text is layout-safe for ydotool synthesis, so the
        // unknown-target path should direct-type to avoid the
        // terminal/Claude-Code Ctrl+V paste failure modes.
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true,
            PrefersDirectTypingForUnknownTarget = true
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "hello world"
        );

        Assert.Equal(InsertionResult.Typed, result);
        Assert.Equal("hello world", platform.TypedText);
        Assert.False(platform.PasteSent);
    }

    [Theory]
    [InlineData("smart “quotes”")] // smart quotes
    [InlineData("em — dash")] // em dash
    [InlineData("café")] // accented letter (é)
    [InlineData("price €42")] // currency (€)
    [InlineData("emoji \U0001F600 face")] // emoji
    public async Task InsertTextAsync_unknown_target_with_non_ascii_text_falls_back_to_clipboard_paste(
        string text
    )
    {
        // Codex adversarial review finding: ydotool's `type` synthesizes
        // evdev keycodes through the user's keyboard layout, so non-ASCII
        // chars (smart quotes, em-dashes, accented letters, currency
        // symbols, emoji) can silently render as the wrong glyph on
        // non-US layouts. For unknown targets on Wayland we must fall
        // back to clipboard paste rather than risk silent corruption.
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true,
            PrefersDirectTypingForUnknownTarget = true
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            text
        );

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.True(platform.PasteSent);
        Assert.Null(platform.TypedText);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Fact]
    public async Task InsertTextAsync_unknown_target_ascii_safe_check_allows_tab_and_newline()
    {
        // Whitespace control chars (\t \n \r) are layout-independent —
        // ydotool synthesizes them via dedicated keycodes, no layout
        // lookup. Keep them in the direct-typing path so that dictated
        // multi-line text (common for notes / chat / code) still types
        // into terminals and Claude Code.
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PrefersDirectTypingForUnknownTarget = true
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "line one\nline\ttwo"
        );

        Assert.Equal(InsertionResult.Typed, result);
        Assert.Equal("line one\nline\ttwo", platform.TypedText);
    }

    [Fact]
    public async Task InsertTextAsync_known_terminal_with_non_ascii_still_direct_types()
    {
        // The ASCII-safe gate only applies to the *unknown-target*
        // fallback. When the user has explicitly registered a terminal
        // (or one of the known direct-typing apps), respect that — they
        // know paste won't work in their app, and a layout-mangled
        // character is still a better fix than silently doing nothing.
        // If they want pristine Unicode, they can switch to clipboard
        // strategy in their settings.
        var platform = new FakeTextInsertionPlatform { Clipboard = "previous" };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "café",
            targetProcessName: "ghostty",
            targetWindowTitle: null
        );

        Assert.Equal(InsertionResult.Typed, result);
        Assert.Equal("café", platform.TypedText);
        Assert.False(platform.PasteSent);
    }

    [Fact]
    public async Task InsertTextAsync_copy_only_strategy_ignores_auto_paste()
    {
        var platform = new FakeTextInsertionPlatform { Clipboard = "previous" };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "new text",
            strategy: TextInsertionStrategy.CopyOnly
        );

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.Equal("new text", platform.Clipboard);
        Assert.False(platform.PasteSent);
        Assert.Null(platform.TypedText);
    }

    [Fact]
    public async Task InsertTextAsync_empty_text_with_auto_enter_requires_paste_tool()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteAvailable = false
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("", autoEnter: true);

        Assert.Equal(InsertionResult.MissingPasteTool, result);
        Assert.False(platform.EnterSent);
        Assert.False(platform.PasteSent);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Fact]
    public async Task CaptureSelectedTextAsync_returns_selection_and_restores_clipboard()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            SelectionText = "the selected text"
        };
        var sut = new TextInsertionService(platform);

        var captured = await sut.CaptureSelectedTextAsync();

        Assert.Equal("the selected text", captured);
        Assert.Equal("previous", platform.Clipboard);
        // GUI targets copy with plain Ctrl+C.
        Assert.False(platform.LastCopyUsedTerminalShortcut);
    }

    [Fact]
    public async Task CaptureSelectedTextAsync_uses_terminal_copy_shortcut_for_terminal_targets()
    {
        // Terminals map plain Ctrl+C to SIGINT, so the probe must use Ctrl+Shift+C there.
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            SelectionText = "the selected text"
        };
        var sut = new TextInsertionService(platform);

        var captured = await sut.CaptureSelectedTextAsync(targetIsTerminal: true);

        Assert.Equal("the selected text", captured);
        Assert.True(platform.LastCopyUsedTerminalShortcut);
    }

    [Theory]
    [InlineData("ghostty")]
    [InlineData("gnome-terminal-")]
    [InlineData("konsole")]
    [InlineData("xfce4-terminal")]
    public void IsTerminalApp_detects_terminals(string process)
    {
        Assert.True(TextInsertionService.IsTerminalApp(process));
    }

    [Theory]
    [InlineData("firefox")]
    [InlineData("code")]
    [InlineData(null)]
    public void IsTerminalApp_rejects_non_terminals(string? process)
    {
        Assert.False(TextInsertionService.IsTerminalApp(process));
    }

    [Fact]
    public async Task CaptureSelectedTextAsync_returns_empty_when_copy_leaves_clipboard_unchanged()
    {
        // No selection: Ctrl+C is a no-op, so the pre-existing clipboard content must NOT be
        // mistaken for a selection. The stale clipboard is also restored.
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "stale clipboard content",
            SelectionText = null
        };
        var sut = new TextInsertionService(platform);

        var captured = await sut.CaptureSelectedTextAsync();

        Assert.Equal("", captured);
        Assert.Equal("stale clipboard content", platform.Clipboard);
    }

    [Fact]
    public async Task CaptureSelectedTextAsync_retries_copy_until_selection_lands()
    {
        // The first two synthesized copies are dropped (ydotool race); the third lands. The
        // retry loop must ride that out rather than reporting nothing selected.
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            SelectionText = "the selected text",
            CopyLandsOnAttempt = 3
        };
        var sut = new TextInsertionService(platform);

        var captured = await sut.CaptureSelectedTextAsync();

        Assert.Equal("the selected text", captured);
        Assert.Equal("previous", platform.Clipboard);
        Assert.Equal(3, platform.CopyAttemptCount);
    }

    [Fact]
    public async Task CaptureSelectedTextAsync_returns_empty_when_copy_never_lands()
    {
        // With the ydotool key-delay making Ctrl+C reliable, a probe that never lands genuinely
        // means nothing is selected — capture must report empty rather than reviving a stale
        // PRIMARY selection (which had made a follow-up command act on a stale fragment).
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            SelectionText = null
        };
        var sut = new TextInsertionService(platform);

        var captured = await sut.CaptureSelectedTextAsync();

        Assert.Equal("", captured);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Fact]
    public async Task CaptureSelectedTextAsync_returns_empty_when_copy_fails()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            CopySucceeds = false
        };
        var sut = new TextInsertionService(platform);

        var captured = await sut.CaptureSelectedTextAsync();

        Assert.Equal("", captured);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Fact]
    public async Task CaptureSelectedTextAsync_preserves_nontext_clipboard_when_no_selection()
    {
        // A null clipboard read models non-text data (image/file list). With nothing selected
        // the probe must not prime a sentinel over it or clear it — the clipboard stays intact.
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = null,
            SelectionText = null
        };
        var sut = new TextInsertionService(platform);

        var captured = await sut.CaptureSelectedTextAsync();

        Assert.Equal("", captured);
        Assert.Null(platform.Clipboard);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_WaylandWithWtype_PasteCallsWtype()
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, true),
            runner.Run
        );

        Assert.True(platform.IsPasteAvailable);

        var result = await platform.SendPasteAsync();

        Assert.True(result);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("wtype", call.FileName);
        Assert.Equal(["-M", "ctrl", "v", "-m", "ctrl"], call.Arguments);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_WaylandWithoutWtype_FallsBackToXdotool()
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", true, false),
            runner.Run
        );

        Assert.True(platform.IsPasteAvailable);

        var result = await platform.SendPasteAsync();

        Assert.True(result);
        Assert.All(runner.Calls, call => Assert.Equal("xdotool", call.FileName));
        Assert.Contains(
            runner.Calls,
            call =>
                call.Arguments.SequenceEqual(["keydown", "--clearmodifiers", "Control_L"])
        );
    }

    [Theory]
    // Regression: xdotool present on Wayland must NOT disable direct typing.
    // ydotool is usable (daemon socket up), so an unknown target is typed.
    [InlineData("Wayland", true, false, "gnome", true, true, true)]
    // ydotool usable, no xdotool — still types.
    [InlineData("Wayland", false, false, "gnome", true, true, true)]
    // wlroots with wtype and no ydotool — wtype is honoured, so types.
    [InlineData("Wayland", false, true, "wlroots", false, false, true)]
    // GNOME rejects wtype and ydotool isn't usable — no native backend, paste.
    [InlineData("Wayland", true, true, "gnome", false, false, false)]
    // Only xdotool on Wayland — XWayland-only, cannot type natively, paste.
    [InlineData("Wayland", true, false, "gnome", false, false, false)]
    // X11 never takes the unknown-target typing path.
    [InlineData("X11", true, false, "unknown", false, false, false)]
    public void LinuxTextInsertionPlatform_PrefersDirectTypingForUnknownTarget_GatesOnNativeBackend(
        string sessionType,
        bool hasXdotool,
        bool hasWtype,
        string compositor,
        bool hasYdotool,
        bool hasYdotoolSocket,
        bool expected
    )
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor(sessionType, hasXdotool, hasWtype, compositor, hasYdotool, hasYdotoolSocket),
            runner.Run
        );

        Assert.Equal(expected, platform.PrefersDirectTypingForUnknownTarget);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_X11WithXdotool_UsesXdotool()
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("X11", true, false),
            runner.Run
        );

        Assert.True(platform.IsPasteAvailable);

        var typed = await platform.TypeTextAsync("hello");

        Assert.True(typed);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("xdotool", call.FileName);
        Assert.Equal(
            ["type", "--clearmodifiers", "--delay", "8", "--", "hello"],
            call.Arguments
        );
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_WaylandTypeTextPassesDoubleDashSeparator()
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, true),
            runner.Run
        );

        var result = await platform.TypeTextAsync("--flag value");

        Assert.True(result);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("wtype", call.FileName);
        Assert.Equal(["--", "--flag value"], call.Arguments);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_WaylandSendEnterUsesWtypeKeyArgs()
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, true),
            runner.Run
        );

        var result = await platform.SendEnterAsync();

        Assert.True(result);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("wtype", call.FileName);
        Assert.Equal(["-k", "Return"], call.Arguments);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_WaylandActivateWindowReturnsTrueWithoutInvocation()
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, true),
            runner.Run
        );

        var activated = await platform.ActivateWindowAsync("123");

        Assert.True(activated);
        Assert.Empty(runner.Calls);
        Assert.Null(platform.GetActiveWindowId());
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_NoBackend_AllInputMethodsReturnFalse()
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("X11", false, false),
            runner.Run
        );

        Assert.False(platform.IsPasteAvailable);
        Assert.False(await platform.SendPasteAsync());
        Assert.False(await platform.SendCopyAsync(false));
        Assert.False(await platform.SendCopyAsync(true));
        Assert.False(await platform.SendEnterAsync());
        Assert.False(await platform.TypeTextAsync("anything"));
        Assert.False(await platform.ActivateWindowAsync("123"));
        Assert.Null(platform.GetActiveWindowId());
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_GnomeWayland_PrefersYdotoolOverWtype()
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor(
                "Wayland",
                true,
                true,
                "gnome",
                true,
                true
            ),
            runner.Run
        );

        var typed = await platform.TypeTextAsync("hi");

        Assert.True(typed);
        var call = Assert.Single(runner.Calls);
        // GNOME rejects wtype, so the chain leads with ydotool — wtype
        // should not even be attempted on the happy path.
        Assert.Equal("ydotool", call.FileName);
        // Speed flags --key-delay 2 --key-hold 2 are part of TypeArgs to
        // bring ydotool's ~40 ms/char default down to ~4 ms/char.
        Assert.Equal(
            ["type", "--key-delay", "2", "--key-hold", "2", "--", "hi"],
            call.Arguments
        );
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_KdeWayland_PrefersYdotoolOverWtype()
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor(
                "Wayland",
                false,
                true,
                "kde",
                true,
                true
            ),
            runner.Run
        );

        var result = await platform.SendPasteAsync();

        Assert.True(result);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("ydotool", call.FileName);
    }

    [Fact]
    public void LinuxTextInsertionPlatform_KdeWayland_ReportsKdePlasma()
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor(
                "Wayland",
                false,
                true,
                "kde",
                true,
                true
            ),
            runner.Run
        );

        Assert.True(platform.IsKdePlasma);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_HyprlandWayland_PrefersWtypeOverYdotool()
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor(
                "Wayland",
                false,
                true,
                "hyprland",
                true,
                true
            ),
            runner.Run
        );

        var typed = await platform.TypeTextAsync("hi");

        Assert.True(typed);
        var call = Assert.Single(runner.Calls);
        // wlroots compositors keep wtype as the canonical fast path.
        Assert.Equal("wtype", call.FileName);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_GnomeWaylandWithoutYdotoolSocket_FallsThroughChain()
    {
        // ydotool binary installed but socket missing — ydotool is
        // un-runnable, so the chain must skip it and try wtype next.
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor(
                "Wayland",
                false,
                true,
                "gnome",
                true,
                false
            ),
            runner.Run
        );

        var typed = await platform.TypeTextAsync("hi");

        Assert.True(typed);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("wtype", call.FileName);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_YdotoolFailure_KeepsReasonAndDisablesBackend()
    {
        // Regression: when ydotool exits non-zero (stale socket, EACCES
        // on /dev/uinput, etc.) the platform must record a
        // ydotool-specific reason. A following wtype attempt with
        // compositor-rejection must NOT overwrite that reason —
        // otherwise the user sees "Set up ydotool" advice when ydotool
        // is the actual broken thing. The chain falls through to
        // xdotool (XWayland) as the final attempt; we use that to
        // observe the full walk happened.
        var runner = new ScriptedProcessRunner();
        runner.Queue.Enqueue(("ydotool", 1, string.Empty));
        runner.Queue.Enqueue(
            ("wtype", 1, "Compositor does not support the virtual keyboard protocol")
        );
        runner.Queue.Enqueue(("xdotool", 0, string.Empty));

        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor(
                "Wayland",
                true,
                true,
                "gnome",
                true,
                true
            ),
            (file, args, _) => runner.Run(file, args),
            runner.RunWithStderr
        );

        var ok = await platform.TypeTextAsync("hi");

        Assert.True(ok);
        // ydotool was tried first, then wtype, then xdotool succeeded.
        Assert.Equal(
            ["ydotool", "wtype", "xdotool"],
            runner.Calls.Select(c => c.FileName).ToArray()
        );

        // Second dictation: both ydotool and wtype should be skipped
        // — only xdotool should be attempted.
        runner.Calls.Clear();
        runner.Queue.Enqueue(("xdotool", 0, string.Empty));
        var ok2 = await platform.TypeTextAsync("hello");

        Assert.True(ok2);
        Assert.Equal("xdotool", Assert.Single(runner.Calls).FileName);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_YdotoolFailure_ReasonNotOverwrittenByWtype()
    {
        // Tightly scoped: just verify wtype's reason-setter respects a
        // prior reason. ydotool fails (no xdotool fallback) → wtype
        // tries and rejects → reason must remain YdotoolSocketUnreachable.
        var runner = new ScriptedProcessRunner();
        runner.Queue.Enqueue(("ydotool", 1, string.Empty));
        runner.Queue.Enqueue(
            ("wtype", 1, "Compositor does not support the virtual keyboard protocol")
        );

        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor(
                "Wayland",
                false,
                true,
                "gnome",
                true,
                true
            ),
            (file, args, _) => runner.Run(file, args),
            runner.RunWithStderr
        );

        var ok = await platform.TypeTextAsync("hi");

        Assert.False(ok);
        Assert.Equal(InsertionFailureReason.YdotoolSocketUnreachable, platform.LastFailureReason);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_ApplyRefreshedSnapshot_RebuildsChainOnSameInstance()
    {
        // Regression for the Codex-flagged race: TextInsertionService is a
        // DI singleton that constructs its platform once at startup. If
        // YdotoolSetupHelper.SetUpAsync installs ydotool after the user
        // clicks the one-click setup button, the live platform must pick
        // up the new backend on the *same* instance — otherwise the UI
        // reports "ydotool is ready" but auto-paste keeps falling back
        // until the next app restart.
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor(
                "Wayland",
                false,
                true,
                "gnome",
                false,
                false
            ),
            runner.Run
        );

        // Pre-refresh: GNOME with no ydotool falls back to wtype.
        Assert.True(await platform.TypeTextAsync("before"));
        Assert.Equal("wtype", Assert.Single(runner.Calls).FileName);
        runner.Calls.Clear();

        platform.ApplyRefreshedSnapshot(
            SnapshotFor(
                "Wayland",
                false,
                true,
                "gnome",
                true,
                true
            )
        );

        // Post-refresh: GNOME now prefers ydotool — same instance.
        Assert.True(await platform.TypeTextAsync("after"));
        Assert.Equal("ydotool", Assert.Single(runner.Calls).FileName);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_SnapshotChangedSubscription_TriggersChainRebuild()
    {
        // End-to-end check that the SystemCommandAvailabilityService
        // event wiring works: subscribing via the DI ctor must update
        // the live chain when RefreshSnapshot fires the event.
        var commands = new SystemCommandAvailabilityService();
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            commands,
            (file, args, _) => runner.Run(file, args),
            async (file, args) => (await runner.Run(file, args).ConfigureAwait(false), string.Empty)
        );

        platform.ApplyRefreshedSnapshot(
            SnapshotFor(
                "Wayland",
                false,
                true,
                "gnome",
                false,
                false
            )
        );
        Assert.True(await platform.TypeTextAsync("before"));
        Assert.Equal("wtype", Assert.Single(runner.Calls).FileName);
        runner.Calls.Clear();

        // Fire the event directly: this models what RefreshSnapshot
        // does after YdotoolSetupHelper installs the daemon.
        var refreshed = SnapshotFor(
            "Wayland",
            false,
            true,
            "gnome",
            true,
            true
        );
        commands.RaiseSnapshotChangedForTests(refreshed);

        Assert.True(await platform.TypeTextAsync("after"));
        Assert.Equal("ydotool", Assert.Single(runner.Calls).FileName);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_Wtype_TypesNewlineAsShiftEnter()
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, true, "hyprland", false, false),
            runner.Run
        );

        var result = await platform.TypeTextAsync("line one\nline two");

        Assert.True(result);
        Assert.All(runner.Calls, call => Assert.Equal("wtype", call.FileName));
        Assert.Equal(
            [
                ["--", "line one"],
                ["-M", "shift", "-k", "Return", "-m", "shift"],
                ["--", "line two"]
            ],
            runner.Calls.Select(c => c.Arguments).ToArray()
        );
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_Ydotool_TypesNewlineAsShiftEnter()
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, false, "gnome", true, true),
            runner.Run
        );

        var result = await platform.TypeTextAsync("line one\nline two");

        Assert.True(result);
        Assert.All(runner.Calls, call => Assert.Equal("ydotool", call.FileName));
        Assert.Equal(
            [
                ["type", "--key-delay", "2", "--key-hold", "2", "--", "line one"],
                // LEFTSHIFT(42)+ENTER(28) press/release pairs, with an inter-event delay so the
                // Shift modifier reliably registers before Enter.
                ["key", "--key-delay", "25", "42:1", "28:1", "28:0", "42:0"],
                ["type", "--key-delay", "2", "--key-hold", "2", "--", "line two"]
            ],
            runner.Calls.Select(c => c.Arguments).ToArray()
        );
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_Xdotool_TypesNewlineAsShiftEnter()
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("X11", true, false),
            runner.Run
        );

        var result = await platform.TypeTextAsync("line one\nline two");

        Assert.True(result);
        Assert.All(runner.Calls, call => Assert.Equal("xdotool", call.FileName));
        Assert.Equal(
            [
                ["type", "--clearmodifiers", "--delay", "8", "--", "line one"],
                ["key", "--clearmodifiers", "shift+Return"],
                ["type", "--clearmodifiers", "--delay", "8", "--", "line two"]
            ],
            runner.Calls.Select(c => c.Arguments).ToArray()
        );
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_ParagraphBreak_EmitsTwoShiftEntersAndSkipsEmptySegment()
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, true, "hyprland", false, false),
            runner.Run
        );

        // "\n\n" yields segments ["first", "", "second"] — the empty middle
        // segment is skipped but both line breaks still emit a Shift+Enter.
        var result = await platform.TypeTextAsync("first\n\nsecond");

        Assert.True(result);
        Assert.Equal(
            [
                ["--", "first"],
                ["-M", "shift", "-k", "Return", "-m", "shift"],
                ["-M", "shift", "-k", "Return", "-m", "shift"],
                ["--", "second"]
            ],
            runner.Calls.Select(c => c.Arguments).ToArray()
        );
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_CrlfNewline_NormalizedToSingleShiftEnter()
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, true, "hyprland", false, false),
            runner.Run
        );

        var result = await platform.TypeTextAsync("a\r\nb");

        Assert.True(result);
        Assert.Equal(
            [
                ["--", "a"],
                ["-M", "shift", "-k", "Return", "-m", "shift"],
                ["--", "b"]
            ],
            runner.Calls.Select(c => c.Arguments).ToArray()
        );
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_NoNewline_TypesInSingleCall()
    {
        // Regression: the common (no-newline) path must remain a single
        // type() invocation — no Shift+Enter machinery, no extra calls.
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, true, "hyprland", false, false),
            runner.Run
        );

        var result = await platform.TypeTextAsync("just one line");

        Assert.True(result);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("wtype", call.FileName);
        Assert.Equal(["--", "just one line"], call.Arguments);
    }

    private static LinuxCapabilitySnapshot SnapshotFor(
        string sessionType,
        bool hasXdotool,
        bool hasWtype
    )
    {
        // ReSharper disable once IntroduceOptionalParameters.Local — keeping the
        // 3-arg X11 convenience overload separate reads clearer than three optional
        // positional params on the 6-arg form; merging would also turn the existing
        // "…, false, false" 6-arg call sites into redundant-argument warnings.
        return SnapshotFor(
            sessionType,
            hasXdotool,
            hasWtype,
            "unknown",
            false,
            false
        );
    }

    private static LinuxCapabilitySnapshot SnapshotFor(
        string sessionType,
        bool hasXdotool,
        bool hasWtype,
        string compositor,
        bool hasYdotool,
        bool hasYdotoolSocket
    )
    {
        return new LinuxCapabilitySnapshot(
            sessionType,
            true,
            sessionType == "Wayland" ? "wl-clipboard" : "xclip",
            hasXdotool,
            hasWtype,
            false,
            false,
            null,
            false,
            false,
            false,
            false,
            false,
            compositor,
            hasYdotool,
            hasYdotoolSocket,
            hasYdotoolSocket ? "/run/user/1000/.ydotool_socket" : null
        );
    }

    private sealed class RecordingProcessRunner
    {
        public List<(string FileName, string[] Arguments)> Calls { get; } = [];

        public Task<int> Run(string fileName, IReadOnlyList<string> args)
        {
            Calls.Add((fileName, args.ToArray()));
            return Task.FromResult(0);
        }
    }

    /// <summary>
    ///     Process runner that returns scripted (exit, stderr) tuples from a
    ///     queue, in order. Lets failure-surfacing tests model a sequence of
    ///     per-backend outcomes inside a single insertion attempt.
    /// </summary>
    private sealed class ScriptedProcessRunner
    {
        public List<(string FileName, string[] Arguments)> Calls { get; } = [];
        public Queue<(string Expected, int ExitCode, string Stderr)> Queue { get; } = new();

        public Task<int> Run(string fileName, IReadOnlyList<string> args)
        {
            Calls.Add((fileName, args.ToArray()));
            var next =
                Queue.Count > 0
                    ? Queue.Dequeue()
                    : (Expected: fileName, ExitCode: 0, Stderr: string.Empty);
            return Task.FromResult(next.ExitCode);
        }

        public Task<(int exitCode, string stderr)> RunWithStderr(
            string fileName,
            IReadOnlyList<string> args
        )
        {
            Calls.Add((fileName, args.ToArray()));
            var next =
                Queue.Count > 0
                    ? Queue.Dequeue()
                    : (Expected: fileName, ExitCode: 0, Stderr: string.Empty);
            return Task.FromResult((next.ExitCode, next.Stderr));
        }
    }

    private sealed class FakeTextInsertionPlatform : ITextInsertionPlatform
    {
        public string? Clipboard { get; set; }
        public string? ActiveWindowId { get; set; }
        public bool ClipboardSetAvailable { get; init; } = true;
        public bool PasteAvailable { get; init; } = true;
        public bool ActivateSucceeds { get; init; } = true;
        public bool PasteSucceeds { get; init; } = true;
        public Queue<bool>? PasteResults { get; init; }
        public bool PasteSent { get; private set; }
        public int PasteAttemptCount { get; private set; }
        public bool EnterSent { get; private set; }
        public string? TypedText { get; private set; }

        // When set, a Ctrl+C copy lands this text on the clipboard (models a real selection).
        // When null, SendCopyAsync leaves the clipboard untouched (nothing selected / copy ignored).
        public string? SelectionText { get; init; }
        public bool CopySucceeds { get; init; } = true;

        // When non-null, each TryGetClipboardTextAsync dequeues the next scripted read
        // (models wl-paste racing wl-copy: reads that lag behind what was just set).
        // An exhausted queue falls back to the live Clipboard value.
        public Queue<string?>? ClipboardReadResults { get; init; }
        public int SetClipboardCount { get; private set; }

        // Every DelayAsync is recorded so tests can assert which waits ran
        // (e.g. the 500 ms restore floor must be skipped on a confirmed paste).
        public List<TimeSpan> Delays { get; } = [];

        public bool IsClipboardSetAvailable => ClipboardSetAvailable;

        public bool IsPasteAvailable => PasteAvailable;

        public bool IsKdePlasma => false;

        public bool PrefersDirectTypingForUnknownTarget { get; init; }

        public InsertionFailureReason LastFailureReason => InsertionFailureReason.None;

        public Task<string?> TryGetClipboardTextAsync()
        {
            return Task.FromResult(
                ClipboardReadResults is { Count: > 0 } ? ClipboardReadResults.Dequeue() : Clipboard
            );
        }

        public Task<bool> SetClipboardTextAsync(string text)
        {
            SetClipboardCount++;
            Clipboard = text;
            return Task.FromResult(true);
        }

        public Task DelayAsync(TimeSpan delay)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }

        public string? GetActiveWindowId()
        {
            return ActiveWindowId;
        }

        public Task<bool> ActivateWindowAsync(string windowId)
        {
            if (ActivateSucceeds)
            {
                ActiveWindowId = windowId;
            }

            return Task.FromResult(ActivateSucceeds);
        }

        // Invoked on every SendPasteAsync — lets tests record ordering relative to the
        // paste keystroke (the confirmation watch must be armed before it) or raise an
        // AT-SPI event "during" the paste.
        public Action? OnPasteSent { get; init; }

        public Task<bool> SendPasteAsync()
        {
            PasteSent = true;
            PasteAttemptCount++;
            OnPasteSent?.Invoke();
            return Task.FromResult(
                PasteResults?.Count > 0 ? PasteResults.Dequeue() : PasteSucceeds
            );
        }

        public Task<bool> TypeTextAsync(string text)
        {
            TypedText = text;
            return Task.FromResult(true);
        }

        // Models a compositor/app dropping the first N synthesized copies before one lands —
        // the ydotool race the retry loop is meant to ride out. 0 = every attempt lands.
        public int CopyLandsOnAttempt { get; init; }
        public int CopyAttemptCount { get; private set; }

        public bool LastCopyUsedTerminalShortcut { get; private set; }

        public Task<bool> SendCopyAsync(bool useTerminalShortcut = false)
        {
            LastCopyUsedTerminalShortcut = useTerminalShortcut;
            CopyAttemptCount++;
            if (CopySucceeds && SelectionText is not null && CopyAttemptCount >= CopyLandsOnAttempt)
            {
                Clipboard = SelectionText;
            }

            return Task.FromResult(CopySucceeds);
        }

        public Task<bool> SendEnterAsync()
        {
            EnterSent = true;
            return Task.FromResult(true);
        }
    }

    private sealed class FakePasteConfirmationSource : IPasteConfirmationSource
    {
        // Scripted outcome of the vended watch: true = insertion observed; null =
        // indeterminate (window elapsed). Never false — mirrors the contract.
        public bool? Result { get; init; }

        // When true, BeginWatch returns null — models the AT-SPI client not running.
        public bool SourceNotRunning { get; init; }

        // Invoked from BeginWatch so ordering tests can record when arming happened
        // relative to the platform's paste call.
        public Action? OnBeginWatch { get; init; }

        public bool BeginWatchCalled { get; private set; }
        public FakePasteWatch? LastWatch { get; private set; }

        // ReSharper disable once UnusedAutoPropertyAccessor.Local — configurable surface mirroring
        // the fake's other init properties and IPasteConfirmationSource; no test sets it yet.
        public bool? HasFocusedElement { get; init; }

        public IPasteWatch? BeginWatch()
        {
            BeginWatchCalled = true;
            OnBeginWatch?.Invoke();
            if (SourceNotRunning)
            {
                return null;
            }

            LastWatch = new FakePasteWatch { Result = Result };
            return LastWatch;
        }
    }

    private sealed class FakePasteWatch : IPasteWatch
    {
        public bool? Result { get; init; }
        public bool WaitCalled { get; private set; }
        public bool Disposed { get; private set; }
        public TimeSpan LastTimeout { get; private set; }

        public Task<bool?> WaitAsync(TimeSpan timeout, CancellationToken ct)
        {
            WaitCalled = true;
            LastTimeout = timeout;
            return Task.FromResult(Result);
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    /// <summary>
    ///     Minimal AT-SPI client fake for driving the real <see cref="AtSpiPasteConfirmation" />:
    ///     always reports running and lets a test raise <see cref="TextChanged" /> at a
    ///     chosen moment (e.g. mid-paste, before the restore step awaits the watch).
    /// </summary>
    private sealed class FakeAtSpiEventClient : IAtSpiEventClient
    {
        // Interface-required; the paste confirmer never subscribes to focus changes.
        public event Action<AtSpiElementRef>? FocusChanged
        {
            add { }
            remove { }
        }

        public event Action<AtSpiElementRef>? TextChanged;

        public AtSpiElementRef? CurrentFocusedElement => null;

        public bool IsRunning => true;

        public IReadOnlyList<AtSpiElementRef> GetRecentFocusedElements()
        {
            return [];
        }

        public Task PokeAccessibilityTreesAsync()
        {
            return Task.CompletedTask;
        }

        public bool HasTextChangedSubscribers => TextChanged is not null;

        // Total AcquireTextChangedEvents calls and how many leases remain undisposed. The paste
        // watch must acquire in its constructor and release in Dispose, so ActiveAcquisitions
        // returns to 0 once the watch is disposed.
        public int AcquireCount { get; private set; }
        public int ActiveAcquisitions { get; private set; }

        public IDisposable AcquireTextChangedEvents()
        {
            AcquireCount++;
            ActiveAcquisitions++;
            return new Lease(this);
        }

        public Task<bool> EnsureStartedAsync()
        {
            return Task.FromResult(true);
        }

        public Task StopAsync()
        {
            return Task.CompletedTask;
        }

        public Task<string?> TryReadTextAsync(AtSpiElementRef element, int maxLength)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<bool?> IsPasswordFieldAsync(AtSpiElementRef element)
        {
            return Task.FromResult<bool?>(null);
        }

        public Task<AtSpiScreenRect?> TryGetScreenExtentsAsync(AtSpiElementRef element)
        {
            return Task.FromResult<AtSpiScreenRect?>(null);
        }

        public void RaiseTextChanged(AtSpiElementRef element)
        {
            TextChanged?.Invoke(element);
        }

        // Idempotent, mirroring the real handle.
        private sealed class Lease(FakeAtSpiEventClient owner) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                owner.ActiveAcquisitions--;
            }
        }
    }
}