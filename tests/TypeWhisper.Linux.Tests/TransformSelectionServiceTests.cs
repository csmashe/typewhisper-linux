using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class TransformSelectionServiceTests
{
    [Fact]
    public async Task CaptureSelectionForTransformAsync_UsesTerminalCopyShortcut_ForTerminalProcess()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            SelectionText = "selected text",
        };
        var textInsertion = new TextInsertionService(platform);
        var activeWindow = new FakeActiveWindowService(
            new ActiveWindowSnapshot(
                "gnome-terminal-server",
                "Terminal",
                "wayland-window-1",
                "org.gnome.Terminal",
                "gnome"
            )
        );

        var captured = await TransformSelectionService.CaptureSelectionForTransformAsync(
            textInsertion,
            activeWindow
        );

        Assert.Equal("selected text", captured.SelectedText);
        Assert.True(platform.LastCopyUsedTerminalShortcut);
    }

    [Fact]
    public async Task CaptureSelectionForTransformAsync_UsesPlainCopyShortcut_ForNonTerminalProcess()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            SelectionText = "selected text",
        };
        var textInsertion = new TextInsertionService(platform);
        var activeWindow = new FakeActiveWindowService(
            new ActiveWindowSnapshot(
                "firefox",
                "Docs",
                "wayland-window-1",
                "org.mozilla.firefox",
                "gnome"
            )
        );

        var captured = await TransformSelectionService.CaptureSelectionForTransformAsync(
            textInsertion,
            activeWindow
        );

        Assert.Equal("selected text", captured.SelectedText);
        Assert.False(platform.LastCopyUsedTerminalShortcut);
    }

    [Fact]
    public async Task CaptureSelectionForTransformAsync_UsesSingleSnapshotForIdentityAndTerminalDecision()
    {
        var platform = new FakeTextInsertionPlatform
        {
            Clipboard = "previous",
            SelectionText = "selected text",
        };
        var textInsertion = new TextInsertionService(platform);
        var snapshot = new ActiveWindowSnapshot(
            "konsole",
            "Shell",
            "wayland-window-7",
            "org.kde.konsole",
            "kwin"
        );
        var activeWindow = new FakeActiveWindowService(snapshot);

        var captured = await TransformSelectionService.CaptureSelectionForTransformAsync(
            textInsertion,
            activeWindow
        );

        Assert.Same(snapshot, captured.TargetSnapshot);
        Assert.True(platform.LastCopyUsedTerminalShortcut);
        Assert.Equal(1, activeWindow.SnapshotCalls);
    }

    [Fact]
    public void SameProcessAndApp_DifferentWaylandWindowIds_ReturnsTrue()
    {
        var captured = Snapshot("wayland-window-1", "hyprland");
        var current = Snapshot("wayland-window-2", "hyprland");

        Assert.True(TransformSelectionService.HasSelectionTargetChanged(captured, current));
    }

    [Fact]
    public void SameSourceAndWindowId_ReturnsFalse()
    {
        var captured = Snapshot("123", "xdotool", appId: "Org.Example.App");
        var current = Snapshot("123", "xdotool", appId: "org.example.app");

        Assert.False(TransformSelectionService.HasSelectionTargetChanged(captured, current));
    }

    [Fact]
    public void SameRawIdDifferentSource_ReturnsTrue()
    {
        var captured = Snapshot("123", "xdotool");
        var current = Snapshot("123", "kwin");

        Assert.True(TransformSelectionService.HasSelectionTargetChanged(captured, current));
    }

    [Fact]
    public void SameWindowIdDifferentAppId_ReturnsTrue()
    {
        var captured = Snapshot("123", "kwin", appId: "org.example.Editor");
        var current = Snapshot("123", "kwin", appId: "org.example.Browser");

        Assert.True(TransformSelectionService.HasSelectionTargetChanged(captured, current));
    }

    [Fact]
    public void AppIdAppearsOrDisappears_ReturnsTrue()
    {
        var withoutAppId = Snapshot("123", "kwin", appId: null);
        var withAppId = Snapshot("123", "kwin", appId: "org.example.App");

        Assert.True(
            TransformSelectionService.HasSelectionTargetChanged(withoutAppId, withAppId)
        );
        Assert.True(
            TransformSelectionService.HasSelectionTargetChanged(withAppId, withoutAppId)
        );
    }

    [Fact]
    public void MissingCapturedSnapshot_ReturnsTrue()
    {
        Assert.True(
            TransformSelectionService.HasSelectionTargetChanged(null, Snapshot("123", "kwin"))
        );
    }

    [Fact]
    public void MissingCurrentSnapshot_ReturnsTrue()
    {
        Assert.True(
            TransformSelectionService.HasSelectionTargetChanged(Snapshot("123", "kwin"), null)
        );
    }

    [Fact]
    public void BothWindowIdsMissing_ReturnsTrue()
    {
        var captured = Snapshot(null, "kwin");
        var current = Snapshot(null, "kwin");

        Assert.True(TransformSelectionService.HasSelectionTargetChanged(captured, current));
    }

    [Fact]
    public void WhitespaceOnlyWindowId_ReturnsTrue()
    {
        Assert.True(
            TransformSelectionService.HasSelectionTargetChanged(
                Snapshot("   ", "kwin"),
                Snapshot("123", "kwin")
            )
        );
        Assert.True(
            TransformSelectionService.HasSelectionTargetChanged(
                Snapshot("123", "kwin"),
                Snapshot("   ", "kwin")
            )
        );
    }

    [Fact]
    public void TitleChangesOnSameWindow_ReturnsFalse()
    {
        var captured = Snapshot("123", "kwin", title: "Draft");
        var current = Snapshot("123", "kwin", title: "Published");

        Assert.False(TransformSelectionService.HasSelectionTargetChanged(captured, current));
    }

    [Fact]
    public void BuildTransformPrompt_IncludesSelectedTextAndSpokenCommand()
    {
        var result = TransformSelectionService.BuildTransformPrompt(
            "This sentence is too long.",
            "make it concise"
        );

        Assert.Contains("Output ONLY the edited text", result);
        Assert.Contains("This sentence is too long.", result);
        Assert.Contains("make it concise", result);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("Cancel.")]
    [InlineData("never mind")]
    [InlineData("nevermind!")]
    [InlineData("stop")]
    public void IsCancelCommand_ReturnsTrueForCancelPhrases(string command)
    {
        Assert.True(TransformSelectionService.IsCancelCommand(command));
    }

    [Fact]
    public void IsCancelCommand_ReturnsFalseForNormalEditInstruction()
    {
        Assert.False(TransformSelectionService.IsCancelCommand("make this more concise"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsCancelCommand_ReturnsFalseForEmptyOrWhitespace(string command)
    {
        Assert.False(TransformSelectionService.IsCancelCommand(command));
    }

    [Fact]
    public async Task DeliverAbortedTransformAsync_CopiesToClipboardOnly_WithoutPasteActivateOrType()
    {
        var platform = new FakeTextInsertionPlatform { Clipboard = "previous" };
        var textInsertion = new TextInsertionService(platform);

        var result = await TransformSelectionService.DeliverAbortedTransformAsync(
            textInsertion,
            "transformed text"
        );

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.Equal("transformed text", platform.Clipboard);
        Assert.Equal(0, platform.PasteCalls);
        Assert.Equal(0, platform.ActivateCalls);
        Assert.Equal(0, platform.TypeCalls);
    }

    [Fact]
    public async Task X11ValidatedReplacement_ActivatesCapturedXdotoolWindow()
    {
        var platform = new FakeTextInsertionPlatform { Clipboard = "previous" };
        var textInsertion = new TextInsertionService(platform);

        await TransformSelectionService.DeliverValidatedTransformAsync(
            textInsertion,
            "transformed text",
            Snapshot("123", "xdotool"),
            isWaylandSession: false
        );

        Assert.Equal(1, platform.ActivateCalls);
        Assert.Equal("123", platform.LastActivatedWindowId);
    }

    // KDE on X11: the session isn't Wayland, but the id came from the compositor, so it is not an
    // X11 window id and must never be handed to xdotool.
    [Fact]
    public async Task X11ValidatedReplacement_DoesNotPassCompositorIdAsActivationTarget()
    {
        var platform = new FakeTextInsertionPlatform { Clipboard = "previous" };
        var textInsertion = new TextInsertionService(platform);

        await TransformSelectionService.DeliverValidatedTransformAsync(
            textInsertion,
            "transformed text",
            Snapshot("{6ba7b810-9dad-11d1-80b4-00c04fd430c8}", "kwin"),
            isWaylandSession: false
        );

        Assert.Equal(0, platform.ActivateCalls);
    }

    [Fact]
    public async Task WaylandValidatedReplacement_DoesNotPassNativeIdAsX11ActivationTarget()
    {
        var platform = new FakeTextInsertionPlatform { Clipboard = "previous" };
        var textInsertion = new TextInsertionService(platform);

        await TransformSelectionService.DeliverValidatedTransformAsync(
            textInsertion,
            "transformed text",
            Snapshot("123", "xdotool"),
            isWaylandSession: true
        );

        Assert.Equal(0, platform.ActivateCalls);
    }

    [Fact]
    public async Task VerifyProbeWithoutSnapshot_AbortsToClipboardOnly()
    {
        var platform = new FakeTextInsertionPlatform { Clipboard = "previous" };
        var textInsertion = new TextInsertionService(platform);
        var activeWindow = new FakeActiveWindowService(null);

        var target = await TransformSelectionService.ResolveReplacementTargetAsync(
            activeWindow,
            Snapshot("123", "xdotool"),
            CancellationToken.None
        );
        Assert.Null(target);

        var insertion = await TransformSelectionService.DeliverAbortedTransformAsync(
            textInsertion,
            "transformed text"
        );

        Assert.Equal(InsertionResult.CopiedToClipboard, insertion);
        Assert.Equal("transformed text", platform.Clipboard);
        Assert.Equal(0, platform.ActivateCalls);
        Assert.Equal(0, platform.PasteCalls);
        Assert.Equal(0, platform.TypeCalls);
    }

    [Fact]
    public async Task ExpiredDeadlineAtVerifyProbe_ReportsTimeoutNotFocusChange()
    {
        // The probe swallows per-provider cancellation and reports no snapshot, which the
        // focus check below reads as a focus change — so the deadline has to win first.
        var activeWindow = new FakeActiveWindowService(null);
        var captured = Snapshot("123", "xdotool");
        Assert.True(TransformSelectionService.HasSelectionTargetChanged(captured, null));

        using var processingCts = new CancellationTokenSource();
        await processingCts.CancelAsync();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () =>
                TransformSelectionService.ResolveReplacementTargetAsync(
                    activeWindow,
                    captured,
                    processingCts.Token
                )
        );

        Assert.Equal(
            "Transform selection timed out.",
            TransformSelectionService.ProcessingFailureMessage(
                exception,
                processingCts.IsCancellationRequested
            )
        );
    }

    [Fact]
    public void ProcessingFailure_ProviderCancellationWithLiveDeadline_UsesFailedWarning()
    {
        var message = TransformSelectionService.ProcessingFailureMessage(
            new OperationCanceledException("provider canceled"),
            deadlineExpired: false
        );

        Assert.Equal("Transform selection failed: provider canceled", message);
    }

    [Fact]
    public void ProcessingFailure_PrivateDeadline_UsesTimedOutWarning()
    {
        var message = TransformSelectionService.ProcessingFailureMessage(
            new OperationCanceledException("deadline canceled"),
            deadlineExpired: true
        );

        Assert.Equal("Transform selection timed out.", message);
    }

    [Fact]
    public void ProcessingFailure_DeadlineRacingProviderFault_DeadlineWins()
    {
        var message = TransformSelectionService.ProcessingFailureMessage(
            new HttpRequestException("provider failed"),
            deadlineExpired: true
        );

        Assert.Equal("Transform selection timed out.", message);
    }

    private static ActiveWindowSnapshot Snapshot(
        string? windowId,
        string source,
        string? appId = "org.example.App",
        string? title = "Document"
    )
    {
        return new ActiveWindowSnapshot("example", title, windowId, appId, source);
    }

    private sealed class FakeActiveWindowService(ActiveWindowSnapshot? snapshot)
        : IActiveWindowService
    {
        public int SnapshotCalls { get; private set; }

        // ReSharper disable once ReturnTypeCanBeNotNullable -- implements IActiveWindowService.GetActiveWindowProcessName, whose contract is nullable; the throw-only body is the assertion.
        public string? GetActiveWindowProcessName()
        {
            throw new InvalidOperationException("Separate active-window probes are not allowed.");
        }

        // ReSharper disable once ReturnTypeCanBeNotNullable -- implements IActiveWindowService.GetActiveWindowTitle, whose contract is nullable; the throw-only body is the assertion.
        public string? GetActiveWindowTitle()
        {
            throw new InvalidOperationException("Separate active-window probes are not allowed.");
        }

        public string? GetBrowserUrl(bool allowInteractiveCapture = true)
        {
            return null;
        }

        public Task<ActiveWindowSnapshot?> GetActiveWindowSnapshotAsync(CancellationToken ct)
        {
            SnapshotCalls++;
            return Task.FromResult(snapshot);
        }

        public string? GetBrowserUrlForSnapshot(
            ActiveWindowSnapshot? activeWindowSnapshot,
            bool honorMissBackoff = false
        )
        {
            return null;
        }

        public IReadOnlyList<string> GetRunningAppProcessNames()
        {
            return [];
        }
    }

    private sealed class FakeTextInsertionPlatform : ITextInsertionPlatform
    {
        public string? Clipboard { get; set; }
        public string? SelectionText { get; init; }
        public bool LastCopyUsedTerminalShortcut { get; private set; }
        public int PasteCalls { get; private set; }
        public int ActivateCalls { get; private set; }
        public string? LastActivatedWindowId { get; private set; }
        public int TypeCalls { get; private set; }
        public bool IsClipboardSetAvailable => true;
        public bool IsPasteAvailable => true;
        public bool IsKdePlasma => false;
        public bool PrefersDirectTypingForUnknownTarget => false;
        public InsertionFailureReason LastFailureReason => InsertionFailureReason.None;
        public bool LastTypingDeliveredPartialText => false;

        public Task<string?> TryGetClipboardTextAsync()
        {
            return Task.FromResult(Clipboard);
        }

        public Task<bool> SetClipboardTextAsync(string text)
        {
            Clipboard = text;
            return Task.FromResult(true);
        }

        public Task<bool> ClipboardHasNonTextFormatsAsync()
        {
            return Task.FromResult(false);
        }

        public Task DelayAsync(TimeSpan delay)
        {
            return Task.CompletedTask;
        }

        public string? GetActiveWindowId()
        {
            return null;
        }

        public Task<bool> ActivateWindowAsync(string windowId)
        {
            ActivateCalls++;
            LastActivatedWindowId = windowId;
            return Task.FromResult(false);
        }

        public Task<bool> SendPasteAsync(bool useTerminalShortcut = false)
        {
            PasteCalls++;
            return Task.FromResult(false);
        }

        public Task<bool> TypeTextAsync(string text)
        {
            TypeCalls++;
            return Task.FromResult(false);
        }

        public Task<bool> SendCopyAsync(bool useTerminalShortcut)
        {
            LastCopyUsedTerminalShortcut = useTerminalShortcut;
            Clipboard = SelectionText;
            return Task.FromResult(true);
        }

        public Task<bool> SendEnterAsync()
        {
            return Task.FromResult(false);
        }
    }
}
