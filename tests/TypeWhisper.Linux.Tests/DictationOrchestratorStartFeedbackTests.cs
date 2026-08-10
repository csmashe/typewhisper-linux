using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationOrchestratorStartFeedbackTests
{
    private static readonly TimeSpan s_testGuard = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Reservation_is_held_through_stop_cue_capture_and_generation_claim()
    {
        var stopCompletion = NewSignal();
        var cueStarted = NewSignal();
        var cueCompletion = NewSignal();
        var order = new List<string>();
        var captureToken = new object();
        var captureInvoked = false;
        var generationClaimed = false;
        var reservation = new TestStartupFeedbackReservation(
            () =>
            {
                order.Add("stop");
                return stopCompletion.Task;
            },
            () => order.Add("dispose")
        );

        var startup = DictationOrchestrator.StartCaptureAfterFeedbackAsync(
            soundFeedbackEnabled: false,
            reserveStartupFeedback: () => reservation,
            playStartSoundAsync: () =>
                throw new InvalidOperationException("Sound is disabled."),
            // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local -- asserting the identity of the lease handed to the cue is what this test exists to prove.
            announceRecordingStartedAsync: lease =>
            {
                Assert.Same(reservation, lease);
                Assert.False(reservation.IsDisposed);
                order.Add("cue");
                cueStarted.TrySetResult();
                return cueCompletion.Task;
            },
            isInputAllowed: () => true,
            startCapture: () =>
            {
                Assert.False(reservation.IsDisposed);
                captureInvoked = true;
                order.Add("capture");
                return captureToken;
            },
            // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local -- asserting the identity of the capture handed to the generation claim is what this test exists to prove.
            onCaptureOpened: capture =>
            {
                Assert.Same(captureToken, capture);
                Assert.False(reservation.IsDisposed);
                generationClaimed = true;
                order.Add("claim");
            }
        );

        Assert.Equal(["stop"], order);
        Assert.False(reservation.IsDisposed);
        Assert.False(captureInvoked);

        stopCompletion.TrySetResult();
        await cueStarted.Task.WaitAsync(s_testGuard);
        Assert.False(reservation.IsDisposed);
        Assert.False(captureInvoked);

        cueCompletion.TrySetResult();
        var result = await startup.WaitAsync(s_testGuard);

        Assert.Same(captureToken, result);
        Assert.True(generationClaimed);
        Assert.True(reservation.IsDisposed);
        Assert.Equal(1, reservation.DisposeCount);
        Assert.Equal(["stop", "cue", "capture", "claim", "dispose"], order);
    }

    [Fact]
    public async Task Null_capture_disposes_reservation_once()
    {
        var reservation = new TestStartupFeedbackReservation();

        var result = await DictationOrchestrator.StartCaptureAfterFeedbackAsync<object>(
            soundFeedbackEnabled: true,
            reserveStartupFeedback: () => reservation,
            playStartSoundAsync: () => Task.CompletedTask,
            announceRecordingStartedAsync: _ => Task.CompletedTask,
            isInputAllowed: () => true,
            startCapture: () => null,
            onCaptureOpened: _ => throw new InvalidOperationException("No capture opened.")
        );

        Assert.Null(result);
        Assert.Equal(1, reservation.DisposeCount);
    }

    [Fact]
    public async Task Thrown_capture_disposes_reservation_once()
    {
        var reservation = new TestStartupFeedbackReservation();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DictationOrchestrator.StartCaptureAfterFeedbackAsync<object>(
                soundFeedbackEnabled: true,
                reserveStartupFeedback: () => reservation,
                playStartSoundAsync: () => Task.CompletedTask,
                announceRecordingStartedAsync: _ => Task.CompletedTask,
                isInputAllowed: () => true,
                startCapture: () => throw new InvalidOperationException("capture failed"),
                onCaptureOpened: _ => { }
            )
        );

        Assert.Equal(1, reservation.DisposeCount);
    }

    [Fact]
    public async Task Thrown_generation_claim_disposes_reservation_once()
    {
        var reservation = new TestStartupFeedbackReservation();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DictationOrchestrator.StartCaptureAfterFeedbackAsync(
                soundFeedbackEnabled: true,
                reserveStartupFeedback: () => reservation,
                playStartSoundAsync: () => Task.CompletedTask,
                announceRecordingStartedAsync: _ => Task.CompletedTask,
                isInputAllowed: () => true,
                startCapture: () => new object(),
                onCaptureOpened: _ => throw new InvalidOperationException("claim failed")
            )
        );

        Assert.Equal(1, reservation.DisposeCount);
    }

    [Fact]
    public async Task Input_rejection_disposes_reservation_once_without_opening_capture()
    {
        var reservation = new TestStartupFeedbackReservation();
        var captureInvoked = false;
        var inputAllowed = true;

        var result = await DictationOrchestrator.StartCaptureAfterFeedbackAsync(
            soundFeedbackEnabled: true,
            reserveStartupFeedback: () => reservation,
            playStartSoundAsync: () =>
            {
                // Session becomes disallowed while the cue is playing: the
                // revalidation immediately before capture must observe it.
                inputAllowed = false;
                return Task.CompletedTask;
            },
            announceRecordingStartedAsync: _ => Task.CompletedTask,
            isInputAllowed: () => inputAllowed,
            startCapture: () =>
            {
                captureInvoked = true;
                return new object();
            },
            onCaptureOpened: _ => { }
        );

        Assert.Null(result);
        Assert.False(captureInvoked);
        Assert.Equal(1, reservation.DisposeCount);
    }

    [Fact]
    public async Task Abort_error_cue_runs_strictly_after_reservation_release()
    {
        var order = new List<string>();
        var reservation = new TestStartupFeedbackReservation(onDispose: () => order.Add("dispose"));

        var result = await DictationOrchestrator.StartCaptureAfterFeedbackAsync<object>(
            soundFeedbackEnabled: false,
            reserveStartupFeedback: () => reservation,
            playStartSoundAsync: () => Task.CompletedTask,
            announceRecordingStartedAsync: _ => Task.CompletedTask,
            isInputAllowed: () => true,
            startCapture: () => null,
            onCaptureOpened: _ => { }
        );
        // The caller emits its start-failure cue only after the helper returns,
        // which the finally-release must precede.
        order.Add("abort-cue");

        Assert.Null(result);
        Assert.Equal(["dispose", "abort-cue"], order);
        Assert.Equal(1, reservation.DisposeCount);
    }

    [Fact]
    public async Task Sound_wins_when_both_feedback_modes_are_enabled()
    {
        var reservation = new TestStartupFeedbackReservation();
        var soundInvocations = 0;
        var speechInvocations = 0;

        _ = await DictationOrchestrator.StartCaptureAfterFeedbackAsync(
            soundFeedbackEnabled: true,
            reserveStartupFeedback: () => reservation,
            playStartSoundAsync: () =>
            {
                soundInvocations++;
                return Task.CompletedTask;
            },
            announceRecordingStartedAsync: _ =>
            {
                speechInvocations++;
                return Task.CompletedTask;
            },
            isInputAllowed: () => true,
            startCapture: () => new object(),
            onCaptureOpened: _ => { }
        );

        Assert.Equal(1, soundInvocations);
        Assert.Equal(0, speechInvocations);
    }

    [Fact]
    public async Task Failed_feedback_is_best_effort_and_reservation_reaches_capture()
    {
        var reservation = new TestStartupFeedbackReservation();
        var captureInvoked = false;

        var result = await DictationOrchestrator.StartCaptureAfterFeedbackAsync(
            soundFeedbackEnabled: true,
            reserveStartupFeedback: () => reservation,
            playStartSoundAsync: () =>
                Task.FromException(new InvalidOperationException("player failed")),
            announceRecordingStartedAsync: _ => Task.CompletedTask,
            isInputAllowed: () => true,
            startCapture: () =>
            {
                Assert.False(reservation.IsDisposed);
                captureInvoked = true;
                return new object();
            },
            onCaptureOpened: _ => Assert.False(reservation.IsDisposed)
        );

        Assert.NotNull(result);
        Assert.True(captureInvoked);
        Assert.Equal(1, reservation.DisposeCount);
    }

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class TestStartupFeedbackReservation(
        Func<Task>? stopPriorPlayback = null,
        Action? onDispose = null
    ) : IStartupFeedbackReservation
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public bool IsDisposed => DisposeCount > 0;

        public Task StopPriorPlaybackAsync()
        {
            return stopPriorPlayback?.Invoke() ?? Task.CompletedTask;
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            onDispose?.Invoke();
        }
    }
}
