using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationOrchestratorStartFeedbackTests
{
    private static readonly TimeSpan s_testGuard = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Capture_waits_for_prior_speech_stop_and_selected_sound_in_exact_order()
    {
        var stopCompletion = NewSignal();
        var soundStarted = NewSignal();
        var soundCompletion = NewSignal();
        var order = new List<string>();
        var captureInvoked = false;

        var startup = DictationOrchestrator.StartCaptureAfterFeedbackAsync(
            soundFeedbackEnabled: true,
            stopPriorSpeechAsync: () =>
            {
                order.Add("stop prior speech");
                return stopCompletion.Task;
            },
            playStartSoundAsync: () =>
            {
                order.Add("sound");
                soundStarted.TrySetResult();
                return soundCompletion.Task;
            },
            announceRecordingStartedAsync: () =>
                throw new InvalidOperationException("Speech must not overlap sound."),
            isInputAllowed: () => true,
            startCapture: () =>
            {
                captureInvoked = true;
                order.Add("capture");
                return new object();
            }
        );

        Assert.Equal(["stop prior speech"], order);
        Assert.False(captureInvoked);

        stopCompletion.TrySetResult();
        await soundStarted.Task.WaitAsync(s_testGuard);

        Assert.Equal(["stop prior speech", "sound"], order);
        Assert.False(captureInvoked);

        soundCompletion.TrySetResult();
        _ = await startup.WaitAsync(s_testGuard);

        Assert.Equal(["stop prior speech", "sound", "capture"], order);
        Assert.True(captureInvoked);
    }

    [Fact]
    public async Task Sound_wins_when_both_feedback_modes_are_enabled()
    {
        var speechInvocations = 0;

        _ = await DictationOrchestrator.StartCaptureAfterFeedbackAsync(
            soundFeedbackEnabled: true,
            stopPriorSpeechAsync: () => Task.CompletedTask,
            playStartSoundAsync: () => Task.CompletedTask,
            announceRecordingStartedAsync: () =>
            {
                speechInvocations++;
                return Task.CompletedTask;
            },
            isInputAllowed: () => true,
            startCapture: () => new object()
        );

        Assert.Equal(0, speechInvocations);
    }

    [Fact]
    public async Task Sound_disabled_startup_waits_for_spoken_cue_before_capture()
    {
        var speechStarted = NewSignal();
        var speechCompletion = NewSignal();
        var captureInvoked = false;

        var startup = DictationOrchestrator.StartCaptureAfterFeedbackAsync(
            soundFeedbackEnabled: false,
            stopPriorSpeechAsync: () => Task.CompletedTask,
            playStartSoundAsync: () =>
                throw new InvalidOperationException("Sound is disabled."),
            announceRecordingStartedAsync: () =>
            {
                speechStarted.TrySetResult();
                return speechCompletion.Task;
            },
            isInputAllowed: () => true,
            startCapture: () =>
            {
                captureInvoked = true;
                return new object();
            }
        );

        await speechStarted.Task.WaitAsync(s_testGuard);
        Assert.False(captureInvoked);

        speechCompletion.TrySetResult();
        _ = await startup.WaitAsync(s_testGuard);

        Assert.True(captureInvoked);
    }

    [Fact]
    public async Task Unavailable_feedback_still_stops_prior_speech_then_attempts_capture()
    {
        var order = new List<string>();

        _ = await DictationOrchestrator.StartCaptureAfterFeedbackAsync(
            soundFeedbackEnabled: false,
            stopPriorSpeechAsync: () =>
            {
                order.Add("stop");
                return Task.CompletedTask;
            },
            playStartSoundAsync: () => Task.CompletedTask,
            announceRecordingStartedAsync: () =>
            {
                order.Add("speech no-op");
                return Task.CompletedTask;
            },
            isInputAllowed: () => true,
            startCapture: () =>
            {
                order.Add("capture");
                return new object();
            }
        );

        Assert.Equal(["stop", "speech no-op", "capture"], order);
    }

    [Fact]
    public async Task Failed_feedback_remains_best_effort_and_capture_is_attempted()
    {
        var order = new List<string>();

        _ = await DictationOrchestrator.StartCaptureAfterFeedbackAsync(
            soundFeedbackEnabled: true,
            stopPriorSpeechAsync: () =>
            {
                order.Add("stop");
                return Task.CompletedTask;
            },
            playStartSoundAsync: () =>
            {
                order.Add("sound failure");
                return Task.FromException(new InvalidOperationException("player failed"));
            },
            announceRecordingStartedAsync: () => Task.CompletedTask,
            isInputAllowed: () => true,
            startCapture: () =>
            {
                order.Add("capture");
                return new object();
            }
        );

        Assert.Equal(["stop", "sound failure", "capture"], order);
    }

    [Fact]
    public async Task Session_disallowed_while_cue_is_pending_never_invokes_capture()
    {
        var soundCompletion = NewSignal();
        var inputAllowed = true;
        var captureInvoked = false;

        var startup = DictationOrchestrator.StartCaptureAfterFeedbackAsync(
            soundFeedbackEnabled: true,
            stopPriorSpeechAsync: () => Task.CompletedTask,
            playStartSoundAsync: () => soundCompletion.Task,
            announceRecordingStartedAsync: () => Task.CompletedTask,
            // ReSharper disable once AccessToModifiedClosure -- the test flips inputAllowed after the cue starts to prove the pre-capture re-check wins.
            isInputAllowed: () => inputAllowed,
            startCapture: () =>
            {
                captureInvoked = true;
                return new object();
            }
        );

        inputAllowed = false;
        soundCompletion.TrySetResult();

        Assert.Null(await startup.WaitAsync(s_testGuard));
        Assert.False(captureInvoked);
    }

    [Fact]
    public async Task Capture_result_preserves_the_delegates_exact_reference()
    {
        var captureToken = new object();

        var result = await DictationOrchestrator.StartCaptureAfterFeedbackAsync(
            soundFeedbackEnabled: false,
            stopPriorSpeechAsync: () => Task.CompletedTask,
            playStartSoundAsync: () => Task.CompletedTask,
            announceRecordingStartedAsync: () => Task.CompletedTask,
            isInputAllowed: () => true,
            startCapture: () => captureToken
        );

        Assert.Same(captureToken, result);
    }

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
