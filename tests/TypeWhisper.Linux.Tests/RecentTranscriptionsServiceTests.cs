using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class RecentTranscriptionsServiceTests
{
    [Fact]
    public async Task FocusReturnsToCapturedTarget_InvokesNormalInsertionWithCapturedTarget()
    {
        var captured = Snapshot("editor", "Document", "x11-target", "editor");
        var fixture = new Fixture("x11-target", captured, captured);

        var result = await fixture.CaptureAndInsertAsync();

        Assert.Equal(InsertionResult.Pasted, result);
        var request = Assert.Single(fixture.InsertionRequests);
        Assert.True(request.AutoPaste);
        Assert.Equal("x11-target", request.TargetWindowId);
        Assert.Empty(fixture.Delays);
    }

    [Fact]
    public async Task FocusStaysOnDifferentWindow_UsesClipboardOnlyWithoutDirectInsertion()
    {
        var captured = Snapshot("editor", "Document", "wayland-target", "editor");
        var different = Snapshot("typewhisper", "TypeWhisper", "palette", "typewhisper");
        var fixture = new Fixture(null, captured, different);

        var result = await fixture.CaptureAndInsertAsync();

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        var request = Assert.Single(fixture.InsertionRequests);
        Assert.False(request.AutoPaste);
        Assert.Null(request.TargetWindowId);
        Assert.Equal(10, fixture.Delays.Count);
        Assert.DoesNotContain(fixture.InsertionRequests, candidate => candidate.AutoPaste);
        Assert.Equal(
            (
                Loc.Instance["RecentTranscriptions.CopiedToClipboard"],
                false
            ),
            Assert.Single(fixture.Feedback)
        );
    }

    [Fact]
    public async Task FocusMatchArrivesLateWithinBound_InvokesNormalInsertion()
    {
        var captured = Snapshot("editor", "Document", "wayland-target", "editor");
        var different = Snapshot("browser", "Browser", "other-window", "browser");
        var fixture = new Fixture(null, captured, different, different, different, captured);

        var result = await fixture.CaptureAndInsertAsync();

        Assert.Equal(InsertionResult.Pasted, result);
        var request = Assert.Single(fixture.InsertionRequests);
        Assert.True(request.AutoPaste);
        Assert.Null(request.TargetWindowId);
        Assert.Equal(3, fixture.Delays.Count);
    }

    [Fact]
    public async Task FocusLandsOnOwnWindow_ReactivatesCapturedTargetAndInserts()
    {
        var captured = Snapshot("editor", "Document", "kwin-editor", "editor", "kwin");
        var settings = Snapshot("typewhisper", "TypeWhisper", "kwin-main", "typewhisper", "kwin");
        var fixture = new Fixture(isWayland: true, null, captured, settings)
        {
            OnActivate = window => window.WindowId == "kwin-editor" ? captured : null,
        };

        var result = await fixture.CaptureAndInsertAsync();

        Assert.Equal(InsertionResult.Pasted, result);
        var request = Assert.Single(fixture.InsertionRequests);
        Assert.True(request.AutoPaste);
        Assert.Equal("editor", request.TargetProcessName);
        Assert.Same(captured, Assert.Single(fixture.Activations));
    }

    [Fact]
    public async Task ReactivationDoesNotRestoreFocus_KeepsClipboardFallback()
    {
        var captured = Snapshot("editor", "Document", "kwin-editor", "editor", "kwin");
        var settings = Snapshot("typewhisper", "TypeWhisper", "kwin-main", "typewhisper", "kwin");
        var fixture = new Fixture(isWayland: true, null, captured, settings);

        var result = await fixture.CaptureAndInsertAsync();

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.False(Assert.Single(fixture.InsertionRequests).AutoPaste);
        Assert.Equal(10, fixture.Activations.Count);
    }

    [Fact]
    public async Task FocusMovedToAnotherApp_DoesNotReactivateCapturedTarget()
    {
        var captured = Snapshot("editor", "Document", "kwin-editor", "editor", "kwin");
        var browser = Snapshot("browser", "Browser", "kwin-browser", "browser", "kwin");
        var fixture = new Fixture(isWayland: true, null, captured, browser)
        {
            OnActivate = _ => captured,
        };

        var result = await fixture.CaptureAndInsertAsync();

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.Empty(fixture.Activations);
    }

    [Fact]
    public async Task OwnWindowReportedByOtherProvider_DoesNotReactivateCapturedTarget()
    {
        var captured = Snapshot("editor", "Document", "kwin-editor", "editor", "kwin");
        var staleXwayland = Snapshot("typewhisper", "TypeWhisper", "0x1", "typewhisper", "xdotool");
        var fixture = new Fixture(isWayland: true, null, captured, staleXwayland)
        {
            OnActivate = _ => captured,
        };

        var result = await fixture.CaptureAndInsertAsync();

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        Assert.Empty(fixture.Activations);
    }

    [Theory]
    [MemberData(nameof(NullIdentityCases))]
    public async Task NullIdentitySnapshot_UsesX11PathOrClipboardFallback(
        string? targetWindowId,
        bool expectsDirectInsertion
    )
    {
        var fixture = new Fixture(targetWindowId, (ActiveWindowSnapshot?)null);

        var result = await fixture.CaptureAndInsertAsync();

        Assert.Equal(
            expectsDirectInsertion
                ? InsertionResult.Pasted
                : InsertionResult.CopiedToClipboard,
            result
        );
        var request = Assert.Single(fixture.InsertionRequests);
        Assert.Equal(expectsDirectInsertion, request.AutoPaste);
        Assert.Equal(expectsDirectInsertion ? targetWindowId : null, request.TargetWindowId);
        Assert.Empty(fixture.Delays);
    }

    [Fact]
    public async Task X11FocusUnverifiedWithWindowId_InsertsViaWindowIdInsteadOfClipboard()
    {
        var captured = Snapshot("editor", "Document", "x11-target", "editor");
        var different = Snapshot("browser", "Browser", "other-window", "browser");
        var fixture = new Fixture("x11-target", captured, different);

        var result = await fixture.CaptureAndInsertAsync();

        Assert.Equal(InsertionResult.Pasted, result);
        var request = Assert.Single(fixture.InsertionRequests);
        Assert.True(request.AutoPaste);
        Assert.Equal("x11-target", request.TargetWindowId);
    }

    [Fact]
    public async Task WaylandXdotoolIdentity_FallsBackToClipboardOnly()
    {
        var xdotool = Snapshot("editor", "Document", "0x123", null, "xdotool");
        var fixture = new Fixture(isWayland: true, "0x123", xdotool, xdotool);

        var result = await fixture.CaptureAndInsertAsync();

        Assert.Equal(InsertionResult.CopiedToClipboard, result);
        var request = Assert.Single(fixture.InsertionRequests);
        Assert.False(request.AutoPaste);
        Assert.Null(request.TargetWindowId);
        Assert.Empty(fixture.Delays);
    }

    [Fact]
    public async Task WaylandCompositorFocusVerified_InsertsWithoutStaleXdotoolId()
    {
        var captured = Snapshot("editor", "Document", "0xhypr", "editor", "hyprland");
        var fixture = new Fixture(isWayland: true, "0xstale", captured, captured);

        var result = await fixture.CaptureAndInsertAsync();

        Assert.Equal(InsertionResult.Pasted, result);
        var request = Assert.Single(fixture.InsertionRequests);
        Assert.True(request.AutoPaste);
        Assert.Null(request.TargetWindowId);
        Assert.Empty(fixture.Delays);
    }

    [Fact]
    public async Task CopyLastWithEmptyHistory_RaisesLocalizedEmptyHistoryFeedback()
    {
        var fixture = new Fixture(null);

        await fixture.CopyLastTranscriptionToClipboardAsync();

        Assert.Equal(
            (Loc.Instance["Overlay.NoRecentTranscriptions"], false),
            Assert.Single(fixture.Feedback)
        );
        Assert.Empty(fixture.InsertionRequests);
    }

    [Theory]
    [InlineData(InsertionResult.Typed, "RecentTranscriptions.Typed", false)]
    [InlineData(InsertionResult.Pasted, "RecentTranscriptions.Pasted", false)]
    [InlineData(
        InsertionResult.CopiedToClipboard,
        "RecentTranscriptions.CopiedToClipboard",
        false
    )]
    [InlineData(InsertionResult.NoText, "Overlay.NoRecentTranscriptions", false)]
    [InlineData(InsertionResult.Failed, "RecentTranscriptions.InsertionFailed", true)]
    [InlineData(InsertionResult.ActionHandled, "Recorder.StatusDone", false)]
    public async Task InsertionFeedback_UsesLocalizedCatalogMessage(
        InsertionResult insertionResult,
        string localizationKey,
        bool isError
    )
    {
        var fixture = new Fixture(null)
        {
            InsertionResultOverride = insertionResult,
        };

        await fixture.CaptureAndInsertAsync();

        Assert.Equal(
            (Loc.Instance[localizationKey], isError),
            Assert.Single(fixture.Feedback)
        );
    }

    [Fact]
    public async Task MissingClipboardToolFeedback_UsesLocalizedInstallHint()
    {
        var fixture = new Fixture(null)
        {
            InsertionResultOverride = InsertionResult.MissingClipboardTool,
        };
        var clipboardTool =
            Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 }
                ? "wl-clipboard"
                : "xclip";

        await fixture.CaptureAndInsertAsync();

        Assert.Equal(
            (
                Loc.Instance.GetString(
                    "TextInsertion.ClipboardInstallHint",
                    clipboardTool
                ),
                true
            ),
            Assert.Single(fixture.Feedback)
        );
    }

    [Theory]
    [InlineData(
        (int)RecentTranscriptionPasteToolHint.X11,
        "RecentTranscriptions.PasteToolInstallHintX11"
    )]
    [InlineData(
        (int)RecentTranscriptionPasteToolHint.Wayland,
        "RecentTranscriptions.PasteToolInstallHintWayland"
    )]
    [InlineData(
        (int)RecentTranscriptionPasteToolHint.WaylandYdotool,
        "RecentTranscriptions.PasteToolInstallHintWaylandYdotool"
    )]
    public async Task MissingPasteToolFeedback_UsesLocalizedPlatformGuidance(
        int pasteToolHint,
        string localizationKey
    )
    {
        var fixture = new Fixture(null)
        {
            InsertionResultOverride = InsertionResult.MissingPasteTool,
            PasteToolHint = (RecentTranscriptionPasteToolHint)pasteToolHint,
        };

        await fixture.CaptureAndInsertAsync();

        Assert.Equal(
            (
                Loc.Instance[localizationKey],
                true
            ),
            Assert.Single(fixture.Feedback)
        );
    }

    [Fact]
    public async Task PaletteInsertion_PassesTargetProcessTitleAndStrategy()
    {
        var captured = Snapshot("kitty", "shell", "x11-target", "kitty");
        var fixture = new Fixture("x11-target", captured, captured)
        {
            Strategies = { ["kitty"] = TextInsertionStrategy.ClipboardPaste },
        };

        await fixture.CaptureAndInsertAsync();

        var request = Assert.Single(fixture.InsertionRequests);
        Assert.Equal("kitty", request.TargetProcessName);
        Assert.Equal("shell", request.TargetWindowTitle);
        Assert.Equal(TextInsertionStrategy.ClipboardPaste, request.Strategy);
        Assert.False(request.AutoEnter);
    }

    [Fact]
    public async Task PasteLast_Shortcut_InsertsLatestTextIntoTheFocusedWindow()
    {
        var focused = Snapshot("kitty", "shell", "x11-target", "kitty");
        var fixture = new Fixture("x11-target", focused);
        fixture.Record("older text");
        fixture.Record("line one\nline two");

        var outcome = await fixture.PasteLastAsync(LastTranscriptionPasteTrigger.Shortcut);

        Assert.Equal(LastTranscriptionPasteOutcome.Delivered, outcome);
        var request = Assert.Single(fixture.InsertionRequests);
        Assert.Equal("line one\nline two", request.Text);
        Assert.True(request.AutoPaste);
        Assert.Equal("x11-target", request.TargetWindowId);
        // The process name lets insertion recognise the terminal and paste multiline text safely.
        Assert.Equal("kitty", request.TargetProcessName);
        Assert.Empty(fixture.Delays);
        Assert.Equal(
            (Loc.Instance["RecentTranscriptions.Pasted"], false),
            Assert.Single(fixture.Feedback)
        );
    }

    [Fact]
    public async Task PasteLast_ShortcutOnWaylandWithoutWindowInfo_StillUsesNormalInsertion()
    {
        var fixture = new Fixture(true, null, (ActiveWindowSnapshot?)null);
        fixture.Record("hello");

        await fixture.PasteLastAsync(LastTranscriptionPasteTrigger.Shortcut);

        var request = Assert.Single(fixture.InsertionRequests);
        Assert.True(request.AutoPaste);
        Assert.Null(request.TargetWindowId);
        Assert.Null(request.TargetProcessName);
    }

    [Fact]
    public async Task PasteLast_MultilineIntoAnUnknownWindow_CopiesToClipboardOnly()
    {
        var fixture = new Fixture(true, null, (ActiveWindowSnapshot?)null);
        fixture.Record("rm -rf build\nmake");

        await fixture.PasteLastAsync(LastTranscriptionPasteTrigger.Shortcut);

        Assert.False(Assert.Single(fixture.InsertionRequests).AutoPaste);
    }

    [Fact]
    public async Task PasteLast_NoTranscription_ReportsEmptyWithoutInserting()
    {
        var fixture = new Fixture("x11-target", Snapshot("editor", "Doc", "x11-target", "editor"));

        var outcome = await fixture.PasteLastAsync(LastTranscriptionPasteTrigger.Shortcut);

        Assert.Equal(LastTranscriptionPasteOutcome.Empty, outcome);
        Assert.Empty(fixture.InsertionRequests);
        Assert.Equal(
            (Loc.Instance["Overlay.NoRecentTranscriptions"], false),
            Assert.Single(fixture.Feedback)
        );
    }

    [Fact]
    public async Task PasteLast_WhileBusy_ReportsBusyWithoutInserting()
    {
        var fixture = new Fixture("x11-target", Snapshot("editor", "Doc", "x11-target", "editor"));
        fixture.Record("hello");

        var outcome = await fixture.PasteLastAsync(
            LastTranscriptionPasteTrigger.Shortcut,
            () => true
        );

        Assert.Equal(LastTranscriptionPasteOutcome.Busy, outcome);
        Assert.Empty(fixture.InsertionRequests);
        Assert.Equal(
            (Loc.Instance["RecentTranscriptions.PasteLastBusy"], false),
            Assert.Single(fixture.Feedback)
        );
    }

    [Fact]
    public async Task PasteLast_DictationStartsWhileTheTrayMenuCloses_DoesNotInsert()
    {
        var fixture = new Fixture("x11-target", Snapshot("editor", "Doc", "x11-target", "editor"));
        fixture.Record("hello");
        var checks = 0;

        var outcome = await fixture.PasteLastAsync(
            LastTranscriptionPasteTrigger.Tray,
            () => ++checks > 1
        );

        Assert.Equal(LastTranscriptionPasteOutcome.Busy, outcome);
        Assert.Equal(2, checks);
        Assert.Empty(fixture.InsertionRequests);
    }

    [Fact]
    public async Task PasteLast_InputNotAllowed_IsIgnoredSilently()
    {
        var fixture = new Fixture("x11-target", Snapshot("editor", "Doc", "x11-target", "editor"))
        {
            InputAllowed = false,
        };
        fixture.Record("hello");

        var outcome = await fixture.PasteLastAsync(LastTranscriptionPasteTrigger.Shortcut);

        Assert.Equal(LastTranscriptionPasteOutcome.Ignored, outcome);
        Assert.Empty(fixture.InsertionRequests);
        Assert.Empty(fixture.Feedback);
    }

    [Fact]
    public async Task PasteLast_SecondRequestWhileTheFirstIsInserting_IsIgnored()
    {
        var fixture = new Fixture("x11-target", Snapshot("editor", "Doc", "x11-target", "editor"));
        fixture.Record("hello");
        var release = new TaskCompletionSource<InsertionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        fixture.InsertOverride = _ => release.Task;

        var first = fixture.PasteLastAsync(LastTranscriptionPasteTrigger.Shortcut);
        var second = await fixture.PasteLastAsync(LastTranscriptionPasteTrigger.Shortcut);
        release.SetResult(InsertionResult.Pasted);

        Assert.Equal(LastTranscriptionPasteOutcome.Ignored, second);
        Assert.Equal(LastTranscriptionPasteOutcome.Delivered, await first);
        Assert.Single(fixture.InsertionRequests);
    }

    [Fact]
    public async Task PasteLast_Tray_WaitsForTheMenuThenPastesIntoTheAppWindow()
    {
        var fixture = new Fixture("x11-target", Snapshot("editor", "Doc", "x11-target", "editor"));
        fixture.Record("hello");

        await fixture.PasteLastAsync(LastTranscriptionPasteTrigger.Tray);

        Assert.Equal(TimeSpan.FromMilliseconds(250), Assert.Single(fixture.Delays));
        var request = Assert.Single(fixture.InsertionRequests);
        Assert.True(request.AutoPaste);
        Assert.Equal("editor", request.TargetProcessName);
    }

    [Theory]
    [InlineData("plasmashell")]
    [InlineData("gnome-shell")]
    [InlineData("typewhisper")]
    public async Task PasteLast_TrayFocusOnPanelOrTypeWhisper_CopiesToClipboardOnly(string process)
    {
        var fixture = new Fixture("x11-target", Snapshot(process, "Panel", "x11-target", process));
        fixture.Record("hello");

        await fixture.PasteLastAsync(LastTranscriptionPasteTrigger.Tray);

        var request = Assert.Single(fixture.InsertionRequests);
        Assert.False(request.AutoPaste);
        Assert.Null(request.TargetWindowId);
        Assert.Equal(
            (Loc.Instance["RecentTranscriptions.CopiedToClipboard"], false),
            Assert.Single(fixture.Feedback)
        );
    }

    [Fact]
    public async Task PasteLast_TrayWithOnlyAnX11WindowId_CopiesToClipboardOnly()
    {
        var fixture = new Fixture("x11-target", (ActiveWindowSnapshot?)null);
        fixture.Record("hello");

        await fixture.PasteLastAsync(LastTranscriptionPasteTrigger.Tray);

        Assert.False(Assert.Single(fixture.InsertionRequests).AutoPaste);
    }

    [Fact]
    public async Task PasteLast_TrayOnWaylandWithoutWindowInfo_CopiesToClipboardOnly()
    {
        var fixture = new Fixture(true, null, (ActiveWindowSnapshot?)null);
        fixture.Record("hello");

        await fixture.PasteLastAsync(LastTranscriptionPasteTrigger.Tray);

        Assert.False(Assert.Single(fixture.InsertionRequests).AutoPaste);
    }

    [Fact]
    public async Task PasteLast_InsertionFails_ReportsTheFailure()
    {
        var fixture = new Fixture("x11-target", Snapshot("editor", "Doc", "x11-target", "editor"))
        {
            InsertionResultOverride = InsertionResult.Failed,
        };
        fixture.Record("hello");

        await fixture.PasteLastAsync(LastTranscriptionPasteTrigger.Shortcut);

        Assert.Equal(
            (Loc.Instance["RecentTranscriptions.InsertionFailed"], true),
            Assert.Single(fixture.Feedback)
        );
    }

    public static TheoryData<string?, bool> NullIdentityCases =>
        new()
        {
            { "x11-target", true },
            { null, false },
        };

    private static ActiveWindowSnapshot Snapshot(
        string processName,
        string title,
        string windowId,
        string? appId,
        string source = "test"
    )
    {
        return new ActiveWindowSnapshot(processName, title, windowId, appId, source);
    }

    private sealed class Fixture
    {
        private readonly RecentTranscriptionsService _service;
        private readonly Queue<ActiveWindowSnapshot?> _snapshots;
        private readonly RecentTranscriptionStore _store = new();
        private ActiveWindowSnapshot? _lastSnapshot;

        public Fixture(string? targetWindowId, params ActiveWindowSnapshot?[] snapshots)
            : this(false, targetWindowId, snapshots)
        {
        }

        public Fixture(
            bool isWayland,
            string? targetWindowId,
            params ActiveWindowSnapshot?[] snapshots
        )
        {
            _snapshots = new Queue<ActiveWindowSnapshot?>(snapshots);
            var history = new Mock<IHistoryService>();
            history.SetupGet(service => service.Records).Returns([]);
            _service = new RecentTranscriptionsService(
                history.Object,
                _store,
                () => true,
                () => targetWindowId,
                _ => Task.FromResult(NextSnapshot()),
                InsertAsync,
                DelayAsync,
                () => PasteToolHint,
                isWaylandSession: isWayland,
                inputAllowedProvider: () => InputAllowed,
                insertionStrategyProvider: processName =>
                    processName is not null
                    && Strategies.TryGetValue(processName, out var strategy)
                        ? strategy
                        : TextInsertionStrategy.Auto,
                activateWindowAsync: ActivateAsync
            );
            _service.FeedbackRequested += (message, isError) =>
                Feedback.Add((message, isError));
        }

        public InsertionResult? InsertionResultOverride { get; init; }
        public bool InputAllowed { get; init; } = true;
        public Dictionary<string, TextInsertionStrategy> Strategies { get; } = [];
        public Func<TextInsertionRequest, Task<InsertionResult>>? InsertOverride { get; set; }
        public RecentTranscriptionPasteToolHint PasteToolHint { get; init; } =
            RecentTranscriptionPasteToolHint.X11;
        public List<TextInsertionRequest> InsertionRequests { get; } = [];
        public List<TimeSpan> Delays { get; } = [];
        public List<ActiveWindowSnapshot> Activations { get; } = [];

        // Returns the snapshot focus moves to after activating a window, or null to leave it.
        public Func<ActiveWindowSnapshot, ActiveWindowSnapshot?>? OnActivate { get; init; }
        public List<(string Message, bool IsError)> Feedback { get; } = [];

        public Task CopyLastTranscriptionToClipboardAsync()
        {
            return _service.CopyLastTranscriptionToClipboardAsync();
        }

        public void Record(string text)
        {
            _store.RecordTranscription(Guid.NewGuid().ToString(), text, DateTime.UtcNow, null, null);
        }

        public Task<LastTranscriptionPasteOutcome> PasteLastAsync(
            LastTranscriptionPasteTrigger trigger,
            Func<bool>? isBusy = null
        )
        {
            return _service.PasteLastTranscriptionAsync(trigger, isBusy ?? (static () => false));
        }

        public async Task<InsertionResult> CaptureAndInsertAsync()
        {
            var target = await _service.CaptureInsertionTargetAsync();
            return await _service.InsertEntryAsync(
                new RecentTranscriptionEntry(
                    "recent",
                    "transcribed text",
                    DateTime.UtcNow,
                    null,
                    null,
                    RecentTranscriptionSource.Session
                ),
                target
            );
        }

        private ActiveWindowSnapshot? NextSnapshot()
        {
            if (_snapshots.Count > 0)
            {
                _lastSnapshot = _snapshots.Dequeue();
            }

            return _lastSnapshot;
        }

        private Task<InsertionResult> InsertAsync(TextInsertionRequest request)
        {
            InsertionRequests.Add(request);
            if (InsertOverride is not null)
            {
                return InsertOverride(request);
            }

            return Task.FromResult(
                InsertionResultOverride
                ?? (
                    request.AutoPaste
                        ? InsertionResult.Pasted
                        : InsertionResult.CopiedToClipboard
                )
            );
        }

        private Task<bool> ActivateAsync(
            ActiveWindowSnapshot window,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Activations.Add(window);
            if (OnActivate?.Invoke(window) is not { } focused)
            {
                return Task.FromResult(false);
            }

            _snapshots.Enqueue(focused);
            return Task.FromResult(true);
        }

        private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }
}
