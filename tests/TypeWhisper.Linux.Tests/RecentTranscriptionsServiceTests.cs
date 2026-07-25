using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
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
            ("Copied recent transcription to clipboard.", false),
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
            _service = new RecentTranscriptionsService(
                Mock.Of<IHistoryService>(),
                new RecentTranscriptionStore(),
                () => true,
                () => targetWindowId,
                _ => Task.FromResult(NextSnapshot()),
                InsertAsync,
                DelayAsync,
                isWaylandSession: isWayland
            );
            _service.FeedbackRequested += (message, isError) =>
                Feedback.Add((message, isError));
        }

        public List<TextInsertionRequest> InsertionRequests { get; } = [];
        public List<TimeSpan> Delays { get; } = [];
        public List<(string Message, bool IsError)> Feedback { get; } = [];

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
                request.AutoPaste
                    ? InsertionResult.Pasted
                    : InsertionResult.CopiedToClipboard
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
