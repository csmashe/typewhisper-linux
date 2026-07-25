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
                new RecentTranscriptionStore(),
                () => true,
                () => targetWindowId,
                _ => Task.FromResult(NextSnapshot()),
                InsertAsync,
                DelayAsync,
                () => PasteToolHint,
                isWaylandSession: isWayland
            );
            _service.FeedbackRequested += (message, isError) =>
                Feedback.Add((message, isError));
        }

        public InsertionResult? InsertionResultOverride { get; init; }
        public RecentTranscriptionPasteToolHint PasteToolHint { get; init; } =
            RecentTranscriptionPasteToolHint.X11;
        public List<TextInsertionRequest> InsertionRequests { get; } = [];
        public List<TimeSpan> Delays { get; } = [];
        public List<(string Message, bool IsError)> Feedback { get; } = [];

        public Task CopyLastTranscriptionToClipboardAsync()
        {
            return _service.CopyLastTranscriptionToClipboardAsync();
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
            return Task.FromResult(
                InsertionResultOverride
                ?? (
                    request.AutoPaste
                        ? InsertionResult.Pasted
                        : InsertionResult.CopiedToClipboard
                )
            );
        }

        private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }
}
