using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Telemetry;
using Xunit;

namespace TypeWhisper.Linux.Tests.Telemetry;

public sealed class DictationTelemetryTrackerTests
{
    [Theory]
    [InlineData(InsertionResult.Pasted, false)]
    [InlineData(InsertionResult.Typed, false)]
    [InlineData(InsertionResult.CopiedToClipboard, false)]
    [InlineData(InsertionResult.NoText, false)]
    [InlineData(InsertionResult.ActionHandled, false)]
    [InlineData(InsertionResult.Failed, true)]
    [InlineData(InsertionResult.ActionFailed, true)]
    [InlineData(InsertionResult.ActionUnavailable, true)]
    [InlineData(InsertionResult.MissingClipboardTool, true)]
    [InlineData(InsertionResult.MissingPasteTool, true)]
    public void Insertion_failure_includes_result_and_thrown_exceptions(InsertionResult insertion, bool expected)
    {
        Assert.Equal(expected, DictationOrchestrator.IsInsertionFailure(insertion, false));
        Assert.True(DictationOrchestrator.IsInsertionFailure(insertion, true));
    }

    [Theory]
    [InlineData("ok", DiagnosticsOutcome.Ok)]
    [InlineData("discarded", DiagnosticsOutcome.Ok)]
    [InlineData("canceled", DiagnosticsOutcome.Cancelled)]
    [InlineData("failed", DiagnosticsOutcome.Failed)]
    [InlineData("insertion_failed", DiagnosticsOutcome.Failed)]
    [InlineData("unknown", DiagnosticsOutcome.Aborted)]
    public void Routes_data_and_finishes_session_and_children_once(string status, DiagnosticsOutcome expected)
    {
        var reporter = new RecordingReporter();
        var tracker = new DictationTelemetryTracker(reporter);
        tracker.Begin(42);
        tracker.Begin(42);
        var session = Assert.Single(reporter.Operations);
        Assert.Equal("dictation.session", session.Name);
        Assert.Equal("dictation", session.Operation);
        tracker.Tag(42, "mode", "Hybrid");
        tracker.Measure(42, "audio.duration_s", 2, "second");
        var child = tracker.Child(42, "post_process");
        var step = child!.StartChild("post_process.step", "Llm");
        tracker.EndCapture(42);
        tracker.EndCapture(42);
        tracker.Finish(42, status);
        tracker.Finish(42, status);
        step!.Dispose();
        child.Dispose();
        Assert.Equal("Hybrid", session.Value.Tags["mode"]);
        Assert.Equal(status, session.Value.Tags["outcome"]);
        Assert.Equal((2d, "second"), session.Value.Measurements["audio.duration_s"]);
        Assert.Equal(expected, Assert.Single(session.Value.Finishes));
        Assert.Equal(DiagnosticsOutcome.Ok, Assert.Single(session.Value.Children[0].Value.Finishes));
        Assert.Equal("audio.capture", session.Value.Children[0].Operation);
        Assert.Equal(expected, Assert.Single(session.Value.Children[1].Value.Finishes));
        Assert.Equal("Llm", Assert.Single(session.Value.Children[1].Value.Children).Description);
        Assert.Equal(expected, Assert.Single(session.Value.Children[1].Value.Children[0].Value.Finishes));
    }

    [Fact]
    public void Missing_sessions_and_null_reporter_are_noops()
    {
        var tracker = new DictationTelemetryTracker(null);
        tracker.Begin(1);
        Assert.Null(tracker.Child(1, "transcribe"));
        tracker.EndCapture(1);
        tracker.Tag(1, "key", "value");
        tracker.Measure(1, "chars", 1, "none");
        tracker.Finish(1, "discarded");
        var reporter = new RecordingReporter();
        tracker = new DictationTelemetryTracker(reporter);
        Assert.Null(tracker.Child(42, "transcribe"));
        tracker.Tag(42, "key", "value");
        tracker.Measure(42, "chars", 1, "none");
        tracker.EndCapture(42);
        tracker.Finish(42, "failed");
        Assert.Empty(reporter.Operations);
    }

    [Fact]
    public void Concurrent_begin_and_finish_are_atomic()
    {
        var reporter = new RecordingReporter();
        var tracker = new DictationTelemetryTracker(reporter);
        Parallel.For(0, 100, _ => tracker.Begin(42));
        Parallel.For(0, 100, _ => tracker.Finish(42, "discarded"));
        var session = Assert.Single(reporter.Operations).Value;
        Assert.Equal(DiagnosticsOutcome.Ok, Assert.Single(session.Finishes));
        Assert.Equal("discarded", session.Tags["outcome"]);
    }
}
