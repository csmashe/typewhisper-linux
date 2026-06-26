using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
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
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.Equal("new text", platform.Clipboard);
        Assert.True(platform.PasteSent);
        Assert.Equal(3, platform.PasteAttemptCount);
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
        // If they want pristine unicode, they can switch to clipboard
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
        Assert.Equal(new[] { "-M", "ctrl", "v", "-m", "ctrl" }, call.Arguments);
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
                call.Arguments.SequenceEqual(new[] { "keydown", "--clearmodifiers", "Control_L" })
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
            new[] { "type", "--clearmodifiers", "--delay", "8", "--", "hello" },
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
        Assert.Equal(new[] { "--", "--flag value" }, call.Arguments);
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
        Assert.Equal(new[] { "-k", "Return" }, call.Arguments);
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
        Assert.False(await platform.SendCopyAsync());
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
            new[] { "type", "--key-delay", "2", "--key-hold", "2", "--", "hi" },
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
            (file, args, env) => runner.Run(file, args),
            (file, args) => runner.RunWithStderr(file, args)
        );

        var ok = await platform.TypeTextAsync("hi");

        Assert.True(ok);
        // ydotool was tried first, then wtype, then xdotool succeeded.
        Assert.Equal(
            new[] { "ydotool", "wtype", "xdotool" },
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
            (file, args, env) => runner.Run(file, args),
            (file, args) => runner.RunWithStderr(file, args)
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
            (file, args, env) => runner.Run(file, args),
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
                // LEFTSHIFT(42)+ENTER(28) press/release pairs.
                ["key", "42:1", "28:1", "28:0", "42:0"],
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
        Assert.Equal(new[] { "--", "just one line" }, call.Arguments);
    }

    private static LinuxCapabilitySnapshot SnapshotFor(
        string sessionType,
        bool hasXdotool,
        bool hasWtype
    )
    {
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
        public int ExitCode { get; set; }

        public Task<int> Run(string fileName, IReadOnlyList<string> args)
        {
            Calls.Add((fileName, args.ToArray()));
            return Task.FromResult(ExitCode);
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
        public bool ClipboardSetAvailable { get; set; } = true;
        public bool PasteAvailable { get; set; } = true;
        public bool ActivateSucceeds { get; set; } = true;
        public bool PasteSucceeds { get; set; } = true;
        public bool TypeSucceeds { get; } = true;
        public Queue<bool>? PasteResults { get; set; }
        public bool PasteSent { get; private set; }
        public int PasteAttemptCount { get; private set; }
        public bool EnterSent { get; private set; }
        public string? TypedText { get; private set; }

        public bool IsClipboardSetAvailable => ClipboardSetAvailable;

        public bool IsPasteAvailable => PasteAvailable;

        public bool IsKdePlasma { get; set; }

        public bool PrefersDirectTypingForUnknownTarget { get; set; }

        public InsertionFailureReason LastFailureReason { get; } = InsertionFailureReason.None;

        public Task<string?> TryGetClipboardTextAsync()
        {
            return Task.FromResult(Clipboard);
        }

        public Task<bool> SetClipboardTextAsync(string text)
        {
            Clipboard = text;
            return Task.FromResult(true);
        }

        public Task DelayAsync(TimeSpan delay)
        {
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

        public Task<bool> SendPasteAsync()
        {
            PasteSent = true;
            PasteAttemptCount++;
            return Task.FromResult(
                PasteResults?.Count > 0 ? PasteResults.Dequeue() : PasteSucceeds
            );
        }

        public Task<bool> TypeTextAsync(string text)
        {
            TypedText = text;
            return Task.FromResult(TypeSucceeds);
        }

        public Task<bool> SendCopyAsync()
        {
            return Task.FromResult(true);
        }

        public Task<bool> SendEnterAsync()
        {
            EnterSent = true;
            return Task.FromResult(true);
        }
    }
}