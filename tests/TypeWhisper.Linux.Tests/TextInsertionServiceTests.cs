using TypeWhisper.Core.Interfaces;
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
            PasteSucceeds = true,
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
            PasteSucceeds = false,
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
            PasteResults = new Queue<bool>([false, false, true]),
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
                    "new text", // verify attempt 2 — serving
                ]
            ),
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
                    "new text", // verify pass 2 after the re-set — serving
                ]
            ),
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
                    "previous", "previous", "previous", "previous", // verify pass 2 after re-set
                ]
            ),
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
            PasteSucceeds = true,
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
            OnPasteSent = () => order.Add("ctrl-v"),
        };
        var confirmation = new FakePasteConfirmationSource
        {
            Result = true,
            OnBeginWatch = () => order.Add("begin-watch"),
        };
        var sut = new TextInsertionService(platform, pasteConfirmation: confirmation);

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Equal(["begin-watch", "ctrl-v"], order);
        Assert.Equal("new text", confirmation.LastExpectedText);
    }

    [Fact]
    public async Task InsertTextAsync_text_changed_during_paste_is_latched_and_confirms_immediately()
    {
        // End-to-end through the real AtSpiPasteConfirmation: the target's text-changed
        // arrives while Ctrl+V is being processed — before the restore step ever awaits
        // the watch. The pre-armed watch must have latched it, so the restore confirms
        // instantly instead of waiting out the timeout and then floor-delaying anyway.
        var targetElement = new AtSpiElementRef(":1.7", "/org/a11y/atspi/accessible/42");
        var client = new FakeAtSpiEventClient
        {
            CurrentFocusedElement = targetElement,
            TextByElement = { [targetElement] = "Prefix new text suffix" },
        };
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true,
            OnPasteSent = () => client.RaiseTextChanged(targetElement),
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
    public async Task InsertTextAsync_unrelated_same_bus_text_change_does_not_confirm_paste()
    {
        var focusedElement = new AtSpiElementRef(
            ":1.7",
            "/org/a11y/atspi/accessible/42"
        );
        var unrelatedElement = new AtSpiElementRef(
            ":1.7",
            "/org/a11y/atspi/accessible/99"
        );
        var client = new FakeAtSpiEventClient
        {
            CurrentFocusedElement = focusedElement,
            TextByElement = { [unrelatedElement] = "Background log count: 17" },
        };
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true,
            OnPasteSent = () => client.RaiseTextChanged(unrelatedElement),
        };
        var sut = new TextInsertionService(
            platform,
            pasteConfirmation: new AtSpiPasteConfirmation(client)
        );

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Contains(TimeSpan.FromMilliseconds(500), platform.Delays);
        Assert.Equal("previous", platform.Clipboard);
        Assert.Equal(unrelatedElement, Assert.Single(client.TextReadRequests).Element);
    }

    [Fact]
    public async Task InsertTextAsync_unknown_bus_unverified_text_change_does_not_confirm_paste()
    {
        // Focus was unknown when the watch armed (_targetBusName null), so the bus filter is
        // skipped and every text-changed proceeds to the content read. An event whose text does
        // NOT contain the pasted string must still stay unconfirmed — the floor delay applies.
        var changedElement = new AtSpiElementRef(
            ":1.7",
            "/org/a11y/atspi/accessible/99"
        );
        var client = new FakeAtSpiEventClient
        {
            CurrentFocusedElement = null,
            TextByElement = { [changedElement] = "Background log count: 17" },
        };
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true,
            OnPasteSent = () => client.RaiseTextChanged(changedElement),
        };
        var sut = new TextInsertionService(
            platform,
            pasteConfirmation: new AtSpiPasteConfirmation(client)
        );

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Contains(TimeSpan.FromMilliseconds(500), platform.Delays);
        Assert.Equal("previous", platform.Clipboard);
        Assert.Equal(changedElement, Assert.Single(client.TextReadRequests).Element);
    }

    [Fact]
    public async Task InsertTextAsync_unreadable_same_bus_text_change_remains_indeterminate()
    {
        var targetElement = new AtSpiElementRef(":1.7", "/org/a11y/atspi/accessible/42");
        var client = new FakeAtSpiEventClient { CurrentFocusedElement = targetElement };
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true,
            OnPasteSent = () => client.RaiseTextChanged(targetElement),
        };
        var sut = new TextInsertionService(
            platform,
            pasteConfirmation: new AtSpiPasteConfirmation(client)
        );

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Contains(TimeSpan.FromMilliseconds(500), platform.Delays);
        Assert.Equal("previous", platform.Clipboard);
        Assert.Equal(targetElement, Assert.Single(client.TextReadRequests).Element);
    }

    [Fact]
    public async Task InsertTextAsync_password_element_text_change_is_never_read()
    {
        // Privacy boundary: even when the paste target's text-changed fires, a password (or
        // role-unreadable) element must never be read to verify delivery. The watch stays
        // indeterminate and the floor delay applies — no TryReadTextAsync ever runs.
        var targetElement = new AtSpiElementRef(":1.7", "/org/a11y/atspi/accessible/42");
        var client = new FakeAtSpiEventClient
        {
            CurrentFocusedElement = targetElement,
            TextByElement = { [targetElement] = "Prefix new text suffix" },
            PasswordRoleByElement = { [targetElement] = true },
        };
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true,
            OnPasteSent = () => client.RaiseTextChanged(targetElement),
        };
        var sut = new TextInsertionService(
            platform,
            pasteConfirmation: new AtSpiPasteConfirmation(client)
        );

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Contains(TimeSpan.FromMilliseconds(500), platform.Delays);
        Assert.Equal("previous", platform.Clipboard);
        Assert.Empty(client.TextReadRequests);
    }

    [Fact]
    public async Task InsertTextAsync_watch_keeps_listening_after_unverified_text_change()
    {
        var targetElement = new AtSpiElementRef(":1.7", "/org/a11y/atspi/accessible/42");
        var unrelatedElement = new AtSpiElementRef(
            ":1.7",
            "/org/a11y/atspi/accessible/99"
        );
        var client = new FakeAtSpiEventClient
        {
            CurrentFocusedElement = targetElement,
            TextByElement =
            {
                [unrelatedElement] = "Background log count: 17",
                [targetElement] = "Prefix new text suffix",
            },
        };
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true,
            OnPasteSent = () =>
            {
                client.RaiseTextChanged(unrelatedElement);
                client.RaiseTextChanged(targetElement);
            },
        };
        var sut = new TextInsertionService(
            platform,
            pasteConfirmation: new AtSpiPasteConfirmation(client)
        );

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.DoesNotContain(TimeSpan.FromMilliseconds(500), platform.Delays);
        Assert.Equal("previous", platform.Clipboard);
        Assert.Equal(
            [unrelatedElement, targetElement],
            client.TextReadRequests.Select(request => request.Element)
        );
    }

    [Fact]
    public async Task InsertTextAsync_auto_enter_waits_for_confirmed_paste_without_floor_delay()
    {
        // A confirmed paste skips the floor delay, but Enter must still wait behind the gate.
        // The OnWait probe pins the moment the gate is entered — Enter must be unsent there.
        var order = new List<string>();
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true,
            OnPasteSent = () => order.Add("paste"),
            OnEnterSent = () => order.Add("enter"),
        };
        var confirmation = new FakePasteConfirmationSource
        {
            Result = true,
            OnWait = () =>
            {
                order.Add("gate");
                Assert.False(platform.EnterSent);
            },
        };
        var sut = new TextInsertionService(platform, pasteConfirmation: confirmation);

        var result = await sut.InsertTextAsync("new text", autoEnter: true);

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.True(platform.EnterSent);
        Assert.Equal(["paste", "gate", "enter"], order);
        Assert.Equal(1, confirmation.LastWatch!.WaitCallCount);
        Assert.DoesNotContain(TimeSpan.FromMilliseconds(500), platform.Delays);
        Assert.DoesNotContain(TimeSpan.FromMilliseconds(600), platform.Delays);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Fact]
    public async Task InsertTextAsync_auto_enter_indeterminate_gate_delays_once_before_enter()
    {
        var order = new List<string>();
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true,
            OnPasteSent = () => order.Add("paste"),
            OnDelay = delay =>
            {
                if (delay == TimeSpan.FromMilliseconds(500))
                {
                    order.Add("floor");
                }
            },
            OnEnterSent = () => order.Add("enter"),
        };
        var confirmation = new FakePasteConfirmationSource { Result = null };
        var sut = new TextInsertionService(platform, pasteConfirmation: confirmation);

        var result = await sut.InsertTextAsync("new text", autoEnter: true);

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.True(platform.EnterSent);
        Assert.Equal(["paste", "floor", "enter"], order);
        Assert.Equal(
            1,
            platform.Delays.Count(delay => delay == TimeSpan.FromMilliseconds(500))
        );
        Assert.NotNull(confirmation.LastWatch);
        Assert.Equal(1, confirmation.LastWatch.WaitCallCount);
        Assert.Equal(TimeSpan.FromSeconds(2), confirmation.LastWatch.LastTimeout);
        Assert.True(confirmation.LastWatch.Disposed);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Fact]
    public async Task InsertTextAsync_auto_enter_without_confirmer_delays_once_even_without_restore()
    {
        // Delivery ordering is independent of clipboard restoration. A null previous
        // clipboard still needs the floor before Enter, even though restore returns early.
        var order = new List<string>();
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = null,
            PasteSucceeds = true,
            OnPasteSent = () => order.Add("paste"),
            OnDelay = delay =>
            {
                if (delay == TimeSpan.FromMilliseconds(500))
                {
                    order.Add("floor");
                }
            },
            OnEnterSent = () => order.Add("enter"),
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("new text", autoEnter: true);

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.True(platform.EnterSent);
        Assert.Equal(["paste", "floor", "enter"], order);
        Assert.Equal(
            1,
            platform.Delays.Count(delay => delay == TimeSpan.FromMilliseconds(500))
        );
        Assert.Equal("new text", platform.Clipboard);
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
            PasteSucceeds = true,
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
            PasteSucceeds = true,
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
            PasteSucceeds = true,
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
            PasteSucceeds = true,
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
                    "user copied meanwhile", // ownership check before restore
                ]
            ),
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
    public async Task InsertTextAsync_skips_restore_when_ownership_read_cannot_prove_our_text()
    {
        // The ownership read comes back null — an image landed on the clipboard, or the read
        // timed out. Either way ownership is unproven, which must not license a restore.
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true,
            ClipboardReadResults = new Queue<string?>(
                [
                    "previous", // snapshot
                    "new text", // verify — serving
                    null, // ownership check — clipboard no longer reads back as text
                ]
            ),
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
            ClipboardHasNonTextFormats = false,
            PasteSucceeds = true,
        };
        var confirmation = new FakePasteConfirmationSource { Result = true };
        var errorLog = new RecordingErrorLogService();
        var sut = new TextInsertionService(platform, errorLog, confirmation);

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.NotNull(confirmation.LastWatch);
        Assert.False(confirmation.LastWatch.WaitCalled);
        Assert.True(confirmation.LastWatch.Disposed);
        Assert.DoesNotContain(TimeSpan.FromMilliseconds(500), platform.Delays);
        Assert.Equal("new text", platform.Clipboard);
        Assert.Empty(errorLog.AddedEntries);
    }

    [Fact]
    public async Task InsertTextAsync_nontext_previous_clipboard_logs_unrestorable_disclosure()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = null,
            ClipboardHasNonTextFormats = true,
            PasteSucceeds = true,
        };
        var errorLog = new RecordingErrorLogService();
        var sut = new TextInsertionService(platform, errorLog);

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Equal("new text", platform.Clipboard);
        var entry = Assert.Single(errorLog.AddedEntries);
        Assert.Equal(ErrorCategory.Insertion, entry.Category);
        Assert.Contains("could not be restored", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InsertTextAsync_richer_previous_clipboard_skips_lossy_restore_and_logs()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            ClipboardHasNonTextFormats = true,
            PasteSucceeds = true,
        };
        var confirmation = new FakePasteConfirmationSource { Result = true };
        var errorLog = new RecordingErrorLogService();
        var sut = new TextInsertionService(platform, errorLog, confirmation);

        var result = await sut.InsertTextAsync("new text");

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.Equal("new text", platform.Clipboard);
        Assert.Equal(1, platform.SetClipboardCount);
        var entry = Assert.Single(errorLog.AddedEntries);
        Assert.Equal(ErrorCategory.Insertion, entry.Category);
        Assert.Contains("was not restored", entry.Message, StringComparison.Ordinal);
        // The message must say what the clipboard holds now, not imply the original survived.
        Assert.Contains("now holds the dictated text", entry.Message, StringComparison.Ordinal);
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
            ActivateSucceeds = false,
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
    public async Task InsertTextAsync_partial_typing_failure_does_not_retry_via_clipboard_paste()
    {
        // Regression: once direct typing has already delivered part of the text
        // under a failed backend, falling through to the clipboard-paste path
        // would paste the COMPLETE text again, duplicating the already-typed
        // prefix. A partial-delivery failure must fail closed instead.
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            TypeSucceeds = false,
            TypeFailureReason = InsertionFailureReason.PartialTypingFailure,
            LastTypingDeliveredPartialText = true,
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("new text", targetProcessName: "codex");

        Assert.Equal(InsertionResult.Failed, result);
        Assert.Equal(0, platform.SetClipboardCount);
        Assert.False(platform.PasteSent);
        Assert.Equal("previous", platform.Clipboard);
        Assert.Equal(InsertionFailureReason.PartialTypingFailure, sut.LastFailureReason);
    }

    [Fact]
    public async Task InsertTextAsync_partial_delivery_with_structural_reason_still_suppresses_clipboard_paste()
    {
        // Regression (audit §3 M2b): a mid-sequence typing failure can record a specific
        // structural reason BEFORE FailPartway runs, so LastFailureReason is not
        // PartialTypingFailure — yet a prefix already landed. The clipboard fallback must
        // stay suppressed on the partial-delivery fact, not on the reason value, and the
        // specific reason must still win (first-specific-wins).
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            TypeSucceeds = false,
            TypeFailureReason = InsertionFailureReason.YdotoolSocketUnreachable,
            LastTypingDeliveredPartialText = true,
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("new text", targetProcessName: "codex");

        Assert.Equal(InsertionResult.Failed, result);
        Assert.Equal(0, platform.SetClipboardCount);
        Assert.False(platform.PasteSent);
        Assert.Equal("previous", platform.Clipboard);
        Assert.Equal(InsertionFailureReason.YdotoolSocketUnreachable, sut.LastFailureReason);
    }

    [Fact]
    public async Task InsertTextAsync_direct_typing_failure_reason_survives_paste_fallback_overwrite()
    {
        // Regression: a specific direct-typing failure reason (e.g. ydotool socket
        // unreachable) must not be downgraded to the generic "no typing tool"
        // reason from the clipboard-paste fallback's own WalkChainAsync call —
        // otherwise the user sees the generic hint instead of "check the ydotool
        // daemon", even though ydotool is installed and the socket is the problem.
        var platform = new FakeTextInsertionPlatform
        {
            TypeSucceeds = false,
            TypeFailureReason = InsertionFailureReason.YdotoolSocketUnreachable,
            PasteSucceeds = false,
            PasteFailureReason = InsertionFailureReason.NoWaylandTypingTool,
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("hello", targetProcessName: "codex");

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.Equal(InsertionFailureReason.YdotoolSocketUnreachable, sut.LastFailureReason);
    }

    [Fact]
    public async Task InsertTextAsync_terminal_multiline_focus_failure_fails_closed_without_typing()
    {
        // Multiline text into a terminal must NOT fall back to direct typing
        // (Shift+Return still submits partial shell commands).
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            ActiveWindowId = "other",
            ActivateSucceeds = false,
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "line1\nline2",
            true,
            "term-window",
            "konsole"
        );

        Assert.Equal(InsertionResult.Failed, result);
        Assert.False(platform.PasteSent);
        Assert.Null(platform.TypedText);
        // The aborted insert restores the user's clipboard rather than leaving dictated text behind.
        Assert.Equal("previous", platform.Clipboard);
    }

    [Fact]
    public async Task InsertTextAsync_terminal_multiline_verify_failure_fails_closed_without_typing()
    {
        // The clipboard never serves our text, so Ctrl+Shift+V would paste nothing/stale.
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true,
            ClipboardReadResults = new Queue<string?>(
                [
                    "previous", // snapshot
                    "previous", "previous", "previous", "previous", // verify pass 1
                    "previous", "previous", "previous", "previous", // verify pass 2 after re-set
                ]
            ),
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "line1\nline2",
            true,
            "term-window",
            "konsole"
        );

        Assert.Equal(InsertionResult.Failed, result);
        Assert.False(platform.PasteSent);
        Assert.Null(platform.TypedText);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Fact]
    public async Task InsertTextAsync_terminal_multiline_fail_closed_keeps_staged_text_when_no_prior_clipboard()
    {
        // The staged dictated text stays on the clipboard as a manual-paste fallback.
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = null,
            ActiveWindowId = "other",
            ActivateSucceeds = false,
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("line1\nline2", true, "term-window", "konsole");

        Assert.Equal(InsertionResult.Failed, result);
        Assert.False(platform.PasteSent);
        Assert.Null(platform.TypedText);
        Assert.Equal("line1\nline2", platform.Clipboard);
        Assert.Equal(1, platform.SetClipboardCount);
    }

    [Fact]
    public async Task InsertTextAsync_terminal_multiline_nontext_clipboard_keeps_staged_text_and_logs()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = null,
            ClipboardHasNonTextFormats = true,
            ActiveWindowId = "other",
            ActivateSucceeds = false,
        };
        var errorLog = new RecordingErrorLogService();
        var sut = new TextInsertionService(platform, errorLog);

        var result = await sut.InsertTextAsync("line1\nline2", true, "term-window", "konsole");

        Assert.Equal(InsertionResult.Failed, result);
        Assert.False(platform.PasteSent);
        Assert.Equal("line1\nline2", platform.Clipboard);
        Assert.Equal(1, platform.SetClipboardCount);
        Assert.Contains(
            errorLog.AddedEntries,
            entry =>
                entry.Category == ErrorCategory.Insertion
                && entry.Message.Contains("could not be restored", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task InsertTextAsync_terminal_multiline_fail_closed_keeps_newer_clipboard_copy()
    {
        // The user copied something new after we staged the dictated text but before the
        // insert failed; the ownership check must leave that newer copy intact.
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            ActiveWindowId = "other",
            ActivateSucceeds = false,
            ClipboardReadResults = new Queue<string?>(
                [
                    "previous", // snapshot before staging
                    "user-copied-this-later", // ownership check during fail-closed restore
                ]
            ),
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync("line1\nline2", true, "term-window", "konsole");

        Assert.Equal(InsertionResult.Failed, result);
        Assert.False(platform.PasteSent);
        // Clipboard was last set to the staged text; the ownership check saw the newer copy
        // and declined to restore, so no further clipboard write occurred.
        Assert.Equal("line1\nline2", platform.Clipboard);
    }

    [Fact]
    public async Task InsertTextAsync_missing_clipboard_tool_returns_specific_result()
    {
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardSetAvailable = false,
            PasteAvailable = true,
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
            PasteAvailable = false,
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
            PasteAvailable = false,
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
            PasteSucceeds = true,
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

    [Fact]
    public async Task InsertTextAsync_explicit_direct_typing_keeps_terminal_single_line_direct()
    {
        var platform = new FakeTextInsertionPlatform { Clipboard = "previous" };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "one line",
            targetProcessName: "kitty",
            strategy: TextInsertionStrategy.DirectTyping
        );

        Assert.Equal(InsertionResult.Typed, result);
        Assert.Equal("one line", platform.TypedText);
        Assert.False(platform.PasteSent);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Theory]
    [InlineData(TextInsertionStrategy.Auto, "line one\nline two")]
    [InlineData(TextInsertionStrategy.DirectTyping, "line one\r\nline two")]
    [InlineData(TextInsertionStrategy.DirectTyping, "line one\rline two")]
    public async Task InsertTextAsync_terminal_multiline_uses_terminal_clipboard_paste(
        TextInsertionStrategy strategy,
        string text
    )
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true,
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            text,
            targetProcessName: "kitty",
            strategy: strategy
        );

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.True(platform.PasteSent);
        Assert.True(platform.LastPasteUsedTerminalShortcut);
        Assert.Null(platform.TypedText);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Fact]
    public async Task InsertTextAsync_non_terminal_multiline_keeps_direct_typing()
    {
        var platform = new FakeTextInsertionPlatform { Clipboard = "previous" };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "line one\nline two",
            targetProcessName: "firefox",
            targetWindowTitle: "Example"
        );

        Assert.Equal(InsertionResult.Typed, result);
        Assert.Equal("line one\nline two", platform.TypedText);
        Assert.False(platform.PasteSent);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Fact]
    public async Task InsertTextAsync_terminal_multiline_without_clipboard_tool_fails_closed()
    {
        var platform = new FakeTextInsertionPlatform
        {
            ClipboardSetAvailable = false,
            PasteAvailable = true,
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "echo unsafe\nsecond command",
            targetProcessName: "kitty",
            strategy: TextInsertionStrategy.DirectTyping
        );

        Assert.Equal(InsertionResult.MissingClipboardTool, result);
        Assert.False(platform.PasteSent);
        Assert.Null(platform.TypedText);
    }

    [Fact]
    public async Task InsertTextAsync_terminal_multiline_when_paste_chord_fails_fails_closed()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = false,
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "echo unsafe\nsecond command",
            targetProcessName: "kitty"
        );

        Assert.Equal(InsertionResult.Failed, result);
        Assert.Equal(3, platform.PasteAttemptCount);
        Assert.True(platform.LastPasteUsedTerminalShortcut);
        Assert.Null(platform.TypedText);
    }

    [Fact]
    public async Task TypeStreamChunkAsync_terminal_multiline_never_reaches_direct_typing()
    {
        var platform = new FakeTextInsertionPlatform();
        var sut = new TextInsertionService(platform);

        var result = await sut.TypeStreamChunkAsync("line one\nline two", "kitty");

        Assert.False(result);
        Assert.Null(platform.TypedText);
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
            PasteSucceeds = true,
        };
        var sut = new TextInsertionService(platform);

        var result = await sut.InsertTextAsync(
            "new text",
            targetProcessName: processName,
            targetWindowTitle: windowTitle
        );

        Assert.Equal(InsertionResult.Pasted, result);
        Assert.True(platform.PasteSent);
        Assert.False(platform.LastPasteUsedTerminalShortcut);
        Assert.Null(platform.TypedText);
        Assert.Equal("previous", platform.Clipboard);
    }

    [Fact]
    public async Task InsertTextAsync_clipboard_paste_strategy_overrides_terminal_direct_typing()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            PasteSucceeds = true,
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
        Assert.True(platform.LastPasteUsedTerminalShortcut);
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
            PrefersDirectTypingForUnknownTarget = true,
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
            PrefersDirectTypingForUnknownTarget = true,
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
            PrefersDirectTypingForUnknownTarget = true,
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
            PasteAvailable = false,
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
            SelectionText = "the selected text",
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
            SelectionText = "the selected text",
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
            SelectionText = null,
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
            CopyLandsOnAttempt = 3,
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
            SelectionText = null,
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
            CopySucceeds = false,
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
            ClipboardHasNonTextFormats = true,
            SelectionText = null,
        };
        var errorLog = new RecordingErrorLogService();
        var sut = new TextInsertionService(platform, errorLog);

        var captured = await sut.CaptureSelectedTextAsync();

        Assert.Equal("", captured);
        Assert.Null(platform.Clipboard);
        var entry = Assert.Single(errorLog.AddedEntries);
        Assert.Equal(ErrorCategory.Insertion, entry.Category);
        Assert.Contains("could not be restored", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureSelectedTextAsync_richer_clipboard_restores_plain_text_and_logs_loss()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            ClipboardHasNonTextFormats = true,
            SelectionText = "the selected text",
        };
        var errorLog = new RecordingErrorLogService();
        var sut = new TextInsertionService(platform, errorLog);

        var captured = await sut.CaptureSelectedTextAsync();

        Assert.Equal("the selected text", captured);
        Assert.Equal("previous", platform.Clipboard);
        var entry = Assert.Single(errorLog.AddedEntries);
        Assert.Equal(ErrorCategory.Insertion, entry.Category);
        Assert.Contains(
            "only its plain-text content was restored",
            entry.Message,
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData("", true, false)]
    [InlineData("text/plain;charset=utf-8\n", true, false)]
    [InlineData("text/plain;charset=utf-8\ntext/html\n", true, true)]
    [InlineData("image/png\n", true, true)]
    [InlineData("TARGETS\nMULTIPLE\nSTRING\nUTF8_STRING\ntext/plain\n", false, false)]
    [InlineData(
        "TARGETS\nMULTIPLE\nSTRING\nUTF8_STRING\ntext/plain\ntext/uri-list\n",
        false,
        true
    )]
    [InlineData(
        "TARGETS\nMULTIPLE\nSTRING\nUTF8_STRING\ntext/plain\nx-special/gnome-copied-files\n",
        false,
        true
    )]
    // ICCCM metadata / side-effect targets carry no content of their own.
    [InlineData("TARGETS\nSTRING\nLENGTH\n", false, false)]
    [InlineData("TARGETS\nSTRING\nDELETE\n", false, false)]
    [InlineData("TARGETS\nSTRING\nINSERT_SELECTION\n", false, false)]
    [InlineData("TARGETS\nSTRING\nINSERT_PROPERTY\n", false, false)]
    [InlineData(
        "TARGETS\nMULTIPLE\nTIMESTAMP\nLENGTH\nDELETE\nINSERT_SELECTION\nINSERT_PROPERTY\nSTRING\nUTF8_STRING\nTEXT\nCOMPOUND_TEXT\n",
        false,
        false
    )]
    // A real rich format alongside them still counts.
    [InlineData("TARGETS\nSTRING\nLENGTH\ntext/html\n", false, true)]
    public void LinuxTextInsertionPlatform_ListingHasNonTextFormats_classifies_targets(
        string listing,
        bool isWayland,
        bool expected
    )
    {
        Assert.Equal(
            expected,
            LinuxTextInsertionPlatform.ListingHasNonTextFormats(listing, isWayland)
        );
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_ClipboardRead_UsesFiveSecondTimeout()
    {
        var runner = new FakeProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, true),
            runner
        );

        await platform.TryGetClipboardTextAsync();

        var call = Assert.Single(runner.Invocations);
        Assert.Equal("wl-paste", call.FileName);
        Assert.Equal(TimeSpan.FromSeconds(5), call.Timeout);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_ClipboardRead_TimeoutReturnsNull()
    {
        var runner = CreateTimedOutProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, true),
            runner
        );

        var result = await platform.TryGetClipboardTextAsync();

        Assert.Null(result);
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_ClipboardWrite_UsesFiveSecondTimeout()
    {
        var runner = new FakeProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, true),
            runner
        );

        var result = await platform.SetClipboardTextAsync("hello");

        Assert.True(result);
        var call = Assert.Single(runner.Invocations);
        Assert.Equal("wl-copy", call.FileName);
        Assert.Equal("hello", call.StandardInput);
        Assert.Equal(TimeSpan.FromSeconds(5), call.Timeout);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_ClipboardWrite_TimeoutReturnsFalse()
    {
        var runner = CreateTimedOutProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, true),
            runner
        );

        var result = await platform.SetClipboardTextAsync("hello");

        Assert.False(result);
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_ClipboardFormatListing_UsesFiveSecondTimeout()
    {
        var runner = new FakeProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, true),
            runner
        );

        await platform.ClipboardHasNonTextFormatsAsync();

        var call = Assert.Single(runner.Invocations);
        Assert.Equal("wl-paste", call.FileName);
        Assert.Equal(["--list-types"], call.Args);
        Assert.Equal(TimeSpan.FromSeconds(5), call.Timeout);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_ClipboardFormatListing_TimeoutReturnsFalse()
    {
        var runner = CreateTimedOutProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, true),
            runner
        );

        var result = await platform.ClipboardHasNonTextFormatsAsync();

        Assert.False(result);
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_InjectorCalls_UseSixtySecondTimeout()
    {
        var runner = new FakeProcessRunner();
        var wtypePlatform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, true, "hyprland", false, false),
            runner
        );
        var ydotoolPlatform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, false, "gnome", true, true),
            runner
        );
        var xdotoolPlatform = new LinuxTextInsertionPlatform(
            SnapshotFor("X11", true, false),
            runner
        );

        Assert.True(await wtypePlatform.TypeTextAsync("hello"));
        Assert.True(await ydotoolPlatform.TypeTextAsync("hello"));
        Assert.True(await xdotoolPlatform.TypeTextAsync("hello"));

        Assert.Equal(["wtype", "ydotool", "xdotool"], runner.Invocations.Select(i => i.FileName));
        Assert.All(
            runner.Invocations,
            invocation => Assert.Equal(TimeSpan.FromSeconds(60), invocation.Timeout)
        );
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_WtypeTimeout_FailsCleanly()
    {
        var runner = CreateTimedOutProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, true, "hyprland", false, false),
            runner
        );

        var result = await platform.TypeTextAsync("hello");

        Assert.False(result);
        var call = Assert.Single(runner.Invocations);
        Assert.Equal("wtype", call.FileName);
        Assert.Equal(TimeSpan.FromSeconds(60), call.Timeout);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_XdotoolTimeout_FailsCleanly()
    {
        var runner = CreateTimedOutProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("X11", true, false),
            runner
        );

        var result = await platform.TypeTextAsync("hello");

        Assert.False(result);
        var call = Assert.Single(runner.Invocations);
        Assert.Equal("xdotool", call.FileName);
        Assert.Equal(TimeSpan.FromSeconds(60), call.Timeout);
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
    public async Task LinuxTextInsertionPlatform_Wtype_TerminalPasteUsesCtrlShiftV()
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, true),
            runner.Run
        );

        var result = await platform.SendPasteAsync(useTerminalShortcut: true);

        Assert.True(result);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("wtype", call.FileName);
        Assert.Equal(
            ["-M", "ctrl", "-M", "shift", "v", "-m", "shift", "-m", "ctrl"],
            call.Arguments
        );
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_Xdotool_TerminalPasteUsesCtrlShiftV()
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("X11", true, false),
            runner.Run
        );

        var result = await platform.SendPasteAsync(useTerminalShortcut: true);

        Assert.True(result);
        Assert.All(runner.Calls, call => Assert.Equal("xdotool", call.FileName));
        Assert.Equal(
            [
                ["keydown", "--clearmodifiers", "Control_L"],
                ["keydown", "--clearmodifiers", "Shift_L"],
                ["key", "v"],
                ["keyup", "Shift_L"],
                ["keyup", "Control_L"],
            ],
            runner.Calls.Select(call => call.Arguments).ToArray()
        );
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_Ydotool_TerminalPasteUsesCtrlShiftV()
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, false, "gnome", true, true),
            runner.Run
        );

        var result = await platform.SendPasteAsync(useTerminalShortcut: true);

        Assert.True(result);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("ydotool", call.FileName);
        Assert.Equal(
            ["key", "--key-delay", "25", "29:1", "42:1", "47:1", "47:0", "42:0", "29:0"],
            call.Arguments
        );
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_WaylandWithOnlyXdotool_HasNoAvailablePasteBackend()
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", true, false),
            runner.Run
        );

        Assert.False(platform.IsPasteAvailable);

        var result = await platform.SendPasteAsync();

        Assert.False(result);
        Assert.Empty(runner.Calls);
        Assert.Equal(
            InsertionFailureReason.NoWaylandTypingTool,
            platform.LastFailureReason
        );
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_WaylandWithOnlyXdotool_AllInputMethodsReturnFalse()
    {
        var runner = new RecordingProcessRunner();
        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", true, false, "unknown", false, false),
            runner.Run
        );

        Assert.False(platform.IsPasteAvailable);
        Assert.False(await platform.SendPasteAsync());
        Assert.False(await platform.SendCopyAsync(false));
        Assert.False(await platform.SendCopyAsync(true));
        Assert.False(await platform.SendEnterAsync());
        Assert.False(await platform.TypeTextAsync("anything"));
        Assert.Empty(runner.Calls);
        Assert.Equal(
            InsertionFailureReason.NoWaylandTypingTool,
            platform.LastFailureReason
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
        // is the actual broken thing. xdotool is deliberately never a
        // Wayland candidate because its success cannot prove delivery
        // to the native focused surface.
        var runner = new ScriptedProcessRunner();
        runner.Queue.Enqueue(("ydotool", 1, string.Empty));
        runner.Queue.Enqueue(
            ("wtype", 1, "Compositor does not support the virtual keyboard protocol")
        );

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

        Assert.False(ok);
        Assert.Equal(
            ["ydotool", "wtype"],
            runner.Calls.Select(c => c.FileName).ToArray()
        );
        Assert.Equal(
            InsertionFailureReason.YdotoolSocketUnreachable,
            platform.LastFailureReason
        );

        // Second dictation: the failed ydotool and wtype are now disabled.
        runner.Calls.Clear();
        var ok2 = await platform.TypeTextAsync("hello");

        Assert.False(ok2);
        Assert.Empty(runner.Calls);
        Assert.Equal(
            InsertionFailureReason.NoWaylandTypingTool,
            platform.LastFailureReason
        );
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
                ["--", "line two"],
            ],
            runner.Calls.Select(c => c.Arguments).ToArray()
        );
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_MultilineTyping_PartialDeliveryStopsChainInsteadOfRetyping()
    {
        // Regression (audit §3 M2): wtype types "line one" successfully, then fails
        // the Shift+Enter before "line two" can be sent. Retrying with the next
        // backend (ydotool) would retype the whole text from the start, duplicating
        // "line one" in the target app. The chain must stop here instead.
        var runner = new ScriptedProcessRunner();
        runner.Queue.Enqueue(("wtype", 0, string.Empty)); // "line one" succeeds
        runner.Queue.Enqueue(("wtype", 1, string.Empty)); // Shift+Enter fails

        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, true, "hyprland", true, true),
            (file, args, _) => runner.Run(file, args),
            runner.RunWithStderr
        );

        var result = await platform.TypeTextAsync("line one\nline two");

        Assert.False(result);
        Assert.Equal(2, runner.Calls.Count);
        Assert.All(runner.Calls, call => Assert.Equal("wtype", call.FileName));
        Assert.Equal(InsertionFailureReason.PartialTypingFailure, platform.LastFailureReason);
        Assert.True(platform.LastTypingDeliveredPartialText);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_MultilineTyping_LeadingNewlineDeliveredBeforeFailureStopsChain()
    {
        // Regression (audit §3 M2, leading-blank-line variant): the first segment is
        // empty (input begins with a newline), so the Shift+Enter — not a text segment —
        // is the first thing to land in the target. When the following segment then fails,
        // that already-delivered newline must count as partial delivery: the chain must
        // stop rather than retype "\nline two" via the next backend and duplicate the
        // newline.
        var runner = new ScriptedProcessRunner();
        runner.Queue.Enqueue(("wtype", 0, string.Empty)); // Shift+Enter lands the leading newline
        runner.Queue.Enqueue(("wtype", 1, string.Empty)); // "line two" fails

        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, true, "hyprland", true, true),
            (file, args, _) => runner.Run(file, args),
            runner.RunWithStderr
        );

        var result = await platform.TypeTextAsync("\nline two");

        Assert.False(result);
        Assert.Equal(2, runner.Calls.Count);
        Assert.All(runner.Calls, call => Assert.Equal("wtype", call.FileName));
        Assert.Equal(InsertionFailureReason.PartialTypingFailure, platform.LastFailureReason);
        Assert.True(platform.LastTypingDeliveredPartialText);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_MultilineTyping_StructuralReasonBeforeAbort_KeepsReasonAndFlagsPartial()
    {
        // Regression (audit §3 M2b): ydotool types "line one", then Shift+Enter exits
        // non-zero — RunYdotoolAsync records YdotoolSocketUnreachable BEFORE FailPartway
        // fires, whose "== None" guard leaves that specific reason in place. A prefix
        // already landed, so LastTypingDeliveredPartialText must still report it and the
        // chain must abort rather than retype the whole text via wtype.
        var runner = new ScriptedProcessRunner();
        runner.Queue.Enqueue(("ydotool", 0, string.Empty)); // "line one" types
        runner.Queue.Enqueue(("ydotool", 1, string.Empty)); // Shift+Enter fails non-zero

        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, true, "gnome", true, true),
            (file, args, _) => runner.Run(file, args),
            runner.RunWithStderr
        );

        var result = await platform.TypeTextAsync("line one\nline two");

        Assert.False(result);
        Assert.Equal(2, runner.Calls.Count);
        Assert.All(runner.Calls, call => Assert.Equal("ydotool", call.FileName));
        Assert.Equal(InsertionFailureReason.YdotoolSocketUnreachable, platform.LastFailureReason);
        Assert.True(platform.LastTypingDeliveredPartialText);
    }

    [Fact]
    public async Task LinuxTextInsertionPlatform_MultilineTyping_NoProgressYetStillFallsBackToNextBackend()
    {
        // A backend that fails before typing anything (dead binary, immediate
        // rejection) is still safe to retry from scratch with the next backend —
        // nothing has been delivered yet to duplicate.
        var runner = new ScriptedProcessRunner();
        runner.Queue.Enqueue(("wtype", 1, string.Empty)); // fails before typing "line one" at all

        var platform = new LinuxTextInsertionPlatform(
            SnapshotFor("Wayland", false, true, "hyprland", true, true),
            (file, args, _) => runner.Run(file, args),
            runner.RunWithStderr
        );

        var result = await platform.TypeTextAsync("line one\nline two");

        Assert.True(result);
        Assert.Equal("wtype", runner.Calls[0].FileName);
        Assert.All(runner.Calls.Skip(1), call => Assert.Equal("ydotool", call.FileName));
        Assert.Equal(InsertionFailureReason.None, platform.LastFailureReason);
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
                ["type", "--key-delay", "2", "--key-hold", "2", "--", "line two"],
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
                ["type", "--clearmodifiers", "--delay", "8", "--", "line two"],
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
                ["--", "second"],
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
                ["--", "b"],
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

    private static FakeProcessRunner CreateTimedOutProcessRunner()
    {
        return new FakeProcessRunner
        {
            Default = new ProcessRunResult(
                true,
                true,
                -1,
                string.Empty,
                string.Empty
            ),
        };
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

    private sealed class RecordingErrorLogService : IErrorLogService
    {
        public List<(string Message, string Category)> AddedEntries { get; } = [];

        public IReadOnlyList<ErrorLogEntry> Entries => [];

        public event Action? EntriesChanged;

        public void AddEntry(string message, string category = ErrorCategory.General)
        {
            AddedEntries.Add((message, category));
            EntriesChanged?.Invoke();
        }

        public void ClearAll()
        {
            AddedEntries.Clear();
            EntriesChanged?.Invoke();
        }

        public string ExportDiagnostics()
        {
            return string.Empty;
        }
    }

    private sealed class FakeTextInsertionPlatform : ITextInsertionPlatform
    {
        public string? Clipboard { get; set; }
        public bool ClipboardHasNonTextFormats { get; init; }
        public string? ActiveWindowId { get; set; }
        public bool ClipboardSetAvailable { get; init; } = true;
        public bool PasteAvailable { get; init; } = true;
        public bool ActivateSucceeds { get; init; } = true;
        public bool PasteSucceeds { get; init; } = true;
        public bool TypeSucceeds { get; init; } = true;
        public InsertionFailureReason TypeFailureReason { get; init; } =
            InsertionFailureReason.None;

        // Models a direct-typing attempt that aborted mid-sequence after already
        // delivering part of the text (real flow: TypeWithNewlinesAsync.FailPartway).
        public bool LastTypingDeliveredPartialText { get; init; }

        public InsertionFailureReason PasteFailureReason { get; init; } =
            InsertionFailureReason.None;
        public Queue<bool>? PasteResults { get; init; }
        public bool PasteSent { get; private set; }
        public int PasteAttemptCount { get; private set; }
        public bool LastPasteUsedTerminalShortcut { get; private set; }
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
        public Action<TimeSpan>? OnDelay { get; init; }
        public Action? OnEnterSent { get; init; }

        public bool IsClipboardSetAvailable => ClipboardSetAvailable;

        public bool IsPasteAvailable => PasteAvailable;

        public bool IsKdePlasma => false;

        public bool PrefersDirectTypingForUnknownTarget { get; init; }

        public InsertionFailureReason LastFailureReason { get; private set; }

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

        public Task<bool> ClipboardHasNonTextFormatsAsync()
        {
            return Task.FromResult(ClipboardHasNonTextFormats);
        }

        public Task DelayAsync(TimeSpan delay)
        {
            Delays.Add(delay);
            OnDelay?.Invoke(delay);
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

        public Task<bool> SendPasteAsync(bool useTerminalShortcut = false)
        {
            PasteSent = true;
            PasteAttemptCount++;
            LastPasteUsedTerminalShortcut = useTerminalShortcut;
            OnPasteSent?.Invoke();
            var succeeded = PasteResults?.Count > 0 ? PasteResults.Dequeue() : PasteSucceeds;
            LastFailureReason = succeeded ? InsertionFailureReason.None : PasteFailureReason;
            return Task.FromResult(succeeded);
        }

        public Task<bool> TypeTextAsync(string text)
        {
            TypedText = text;
            LastFailureReason = TypeSucceeds
                ? InsertionFailureReason.None
                : TypeFailureReason;
            return Task.FromResult(TypeSucceeds);
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
            OnEnterSent?.Invoke();
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

        // Forwarded to the watch so ordering tests can observe entry into the delivery gate.
        public Action? OnWait { get; init; }

        public bool BeginWatchCalled { get; private set; }
        public string? LastExpectedText { get; private set; }
        public FakePasteWatch? LastWatch { get; private set; }

        // ReSharper disable once UnusedAutoPropertyAccessor.Local — configurable surface mirroring
        // the fake's other init properties and IPasteConfirmationSource; no test sets it yet.
        public bool? HasFocusedElement { get; init; }

        public IPasteWatch? BeginWatch(string expectedText)
        {
            BeginWatchCalled = true;
            LastExpectedText = expectedText;
            OnBeginWatch?.Invoke();
            if (SourceNotRunning)
            {
                return null;
            }

            LastWatch = new FakePasteWatch { Result = Result, OnWait = OnWait };
            return LastWatch;
        }
    }

    private sealed class FakePasteWatch : IPasteWatch
    {
        public bool? Result { get; init; }
        public Action? OnWait { get; init; }

        public bool WaitCalled { get; private set; }
        public int WaitCallCount { get; private set; }
        public bool Disposed { get; private set; }
        public TimeSpan LastTimeout { get; private set; }

        public Task<bool?> WaitAsync(TimeSpan timeout, CancellationToken ct)
        {
            OnWait?.Invoke();
            WaitCalled = true;
            WaitCallCount++;
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

        public AtSpiElementRef? CurrentFocusedElement { get; init; }

        public bool IsRunning => true;

        public Dictionary<AtSpiElementRef, string?> TextByElement { get; } = [];
        public List<(AtSpiElementRef Element, int MaxLength)> TextReadRequests { get; } = [];

        // Password-role verdicts per element. Absent entries default to false (positively
        // non-password); set true (or null for unreadable) to exercise the privacy gate.
        public Dictionary<AtSpiElementRef, bool?> PasswordRoleByElement { get; } = [];

        public IReadOnlyList<AtSpiElementRef> GetRecentFocusedElements()
        {
            return [];
        }

        public Task PokeAccessibilityTreesAsync()
        {
            return Task.CompletedTask;
        }

        public Task<AtSpiElementRef?> TryBootstrapFocusAsync()
        {
            return Task.FromResult<AtSpiElementRef?>(null);
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
            TextReadRequests.Add((element, maxLength));
            return Task.FromResult(TextByElement.GetValueOrDefault(element));
        }

        public Task<bool?> IsPasswordFieldAsync(AtSpiElementRef element)
        {
            return Task.FromResult(PasswordRoleByElement.GetValueOrDefault(element, false));
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
