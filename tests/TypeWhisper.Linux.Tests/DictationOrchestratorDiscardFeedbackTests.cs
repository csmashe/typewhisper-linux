using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using Xunit;

// The Assert.Collection lambdas in this file assert on each element; ReSharper reads
// xUnit asserts as precondition checks and concludes the element parameter is only
// validated, never used — but asserting on each element is exactly the test's
// purpose, so the inspection is a false positive here.
// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local
namespace TypeWhisper.Linux.Tests;

public sealed class DictationOrchestratorDiscardFeedbackTests
{
    [Theory]
    [InlineData((int)LinuxShortSpeechDecision.DiscardTooShort, "Overlay.TooShort")]
    [InlineData((int)LinuxShortSpeechDecision.DiscardNoSpeech, "Overlay.NoSpeech")]
    public void Short_speech_discard_resolves_message_and_reports_status_before_error_feedback(
        int discardReasonValue,
        string messageKey
    )
    {
        var discardReason = (LinuxShortSpeechDecision)discardReasonValue;
        var recordingContext = NewRecordingContext();
        var message = Loc.Instance[messageKey];
        var calls = new List<OutcomeCall>();

        DictationOrchestrator.ReportShortSpeechDiscardOutcome(
            discardReason,
            recordingContext,
            (context, status) =>
                calls.Add(new OutcomeCall("status", context, status, null, null)),
            (context, feedback, isError, isCanceled) =>
                calls.Add(
                    new OutcomeCall("feedback", context, feedback, isError, isCanceled)
                )
        );

        Assert.Collection(
            calls,
            status =>
            {
                Assert.Equal("status", status.Kind);
                Assert.Same(recordingContext, status.Context);
                Assert.Equal(message, status.Message);
                Assert.Null(status.IsError);
                Assert.Null(status.IsCanceled);
            },
            feedback =>
            {
                Assert.Equal("feedback", feedback.Kind);
                Assert.Same(recordingContext, feedback.Context);
                Assert.Equal(message, feedback.Message);
                Assert.True(feedback.IsError);
                Assert.False(feedback.IsCanceled);
            }
        );
    }

    [Fact]
    public void Discard_reasons_resolve_their_distinct_localized_messages()
    {
        var recordingContext = NewRecordingContext();
        var messages = new Dictionary<LinuxShortSpeechDecision, string>();

        foreach (
            var discardReason in new[]
            {
                LinuxShortSpeechDecision.DiscardTooShort,
                LinuxShortSpeechDecision.DiscardNoSpeech
            }
        )
        {
            DictationOrchestrator.ReportShortSpeechDiscardOutcome(
                discardReason,
                recordingContext,
                (_, message) => messages.Add(discardReason, message),
                (_, _, _, _) => { }
            );
        }

        Assert.Equal(
            Loc.Instance["Overlay.TooShort"],
            messages[LinuxShortSpeechDecision.DiscardTooShort]
        );
        Assert.Equal(
            Loc.Instance["Overlay.NoSpeech"],
            messages[LinuxShortSpeechDecision.DiscardNoSpeech]
        );
        Assert.NotEqual(
            messages[LinuxShortSpeechDecision.DiscardTooShort],
            messages[LinuxShortSpeechDecision.DiscardNoSpeech]
        );
    }

    private static RecordingContext NewRecordingContext()
    {
        return new RecordingContext(
            SessionId: 42,
            RecordingStart: DateTime.UnixEpoch,
            AppProcess: null,
            AppTitle: null,
            AppUrl: null,
            WindowId: null,
            Profile: null,
            RecoveredPartialPreview: string.Empty,
            StreamingFinalText: null,
            StreamingFaulted: false,
            StreamingProviderId: null,
            StreamingModelId: null,
            StreamingLanguageHint: null,
            CancelToken: CancellationToken.None
        );
    }

    private sealed record OutcomeCall(
        string Kind,
        RecordingContext Context,
        string Message,
        bool? IsError,
        bool? IsCanceled
    );
}
