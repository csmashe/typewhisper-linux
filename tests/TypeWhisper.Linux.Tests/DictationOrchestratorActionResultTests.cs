using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationOrchestratorActionResultTests
{
    [Fact]
    public void FeedbackState_preserves_plugin_terminal_message_and_presentation_fields()
    {
        var result = new ActionPluginExecutionResult(
            true,
            "Linear issue TW-42 created",
            "https://example.com/issues/TW-42",
            "task-due",
            5000
        );

        var state = DictationOrchestrator.FeedbackState(
            new DictationOverlayState
            {
                IsOverlayVisible = true,
                IsRecording = true,
                PartialText = "old preview",
            },
            result.Message,
            isError: false,
            actionResult: result
        );

        Assert.Equal("Linear issue TW-42 created", state.FeedbackText);
        Assert.NotEqual("Action completed.", state.FeedbackText);
        Assert.Equal(result.Url, state.ActionResultUrl);
        Assert.Equal(result.Icon, state.NotificationIconName);
        Assert.Equal(5000, state.FeedbackDurationMilliseconds);
        Assert.True(state.ShowFeedback);
        Assert.False(state.IsOverlayVisible);
        Assert.False(state.IsRecording);
        Assert.Null(state.PartialText);
    }

    [Fact]
    public void Ordinary_feedback_clears_prior_action_presentation_fields()
    {
        var prior = new DictationOverlayState
        {
            ActionResultUrl = "https://example.com/result",
            NotificationIconName = "task-due",
            FeedbackDurationMilliseconds = 5000,
        };

        var state = DictationOrchestrator.FeedbackState(
            prior,
            "Insertion failed.",
            isError: true
        );

        Assert.Null(state.ActionResultUrl);
        Assert.Null(state.NotificationIconName);
        Assert.Null(state.FeedbackDurationMilliseconds);
    }
}
