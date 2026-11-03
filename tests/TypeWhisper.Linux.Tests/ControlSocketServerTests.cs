using System.Text.Json;
using TypeWhisper.Linux.Services.Ipc;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ControlSocketServerTests
{
    private static readonly TimeSpan s_testGuard = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Delayed_accepted_start_returns_starting_before_start_completes()
    {
        const string state = JsonControlProtocol.StateIdle;
        var startEntered = NewSignal();
        var releaseStart = NewSignal();
        var coordinator = CreateCoordinator(() => state);

        try
        {
            var responseTask = coordinator.DispatchStart(() =>
            {
                startEntered.TrySetResult();
                return releaseStart.Task;
            });

            await startEntered.Task.WaitAsync(s_testGuard);
            Assert.True(responseTask.IsCompletedSuccessfully);
            AssertAction(
                await responseTask,
                JsonControlProtocol.StateIdle,
                JsonControlProtocol.StateStarting
            );
            Assert.False(releaseStart.Task.IsCompleted);
        }
        finally
        {
            releaseStart.TrySetResult();
            await AwaitPublishedStartAsync(coordinator);
        }
    }

    [Fact]
    public async Task Completion_correlation_is_published_before_start_delegate_runs()
    {
        var releaseStart = NewSignal();
        var coordinator = CreateCoordinator(() => JsonControlProtocol.StateIdle);
        Task? correlationSeenByStart = null;

        try
        {
            _ = coordinator.DispatchStart(() =>
            {
                correlationSeenByStart = coordinator.GetLastStart().Completion;
                return releaseStart.Task;
            });

            var publishedCorrelation = coordinator.GetLastStart().Completion;
            Assert.NotNull(publishedCorrelation);
            Assert.Same(publishedCorrelation, correlationSeenByStart);
            Assert.False(publishedCorrelation.IsCompleted);

            releaseStart.TrySetResult();
            await publishedCorrelation.WaitAsync(s_testGuard);
            Assert.True(publishedCorrelation.IsCompletedSuccessfully);
        }
        finally
        {
            releaseStart.TrySetResult();
            await AwaitPublishedStartAsync(coordinator);
        }
    }

    [Fact]
    public async Task Status_progresses_from_starting_to_recording_and_clears_after_completion()
    {
        var state = JsonControlProtocol.StateIdle;
        var releaseStart = NewSignal();
        // ReSharper disable once AccessToModifiedClosure -- the test deliberately mutates state to drive SnapshotState transitions
        var coordinator = CreateCoordinator(() => state);

        try
        {
            _ = coordinator.DispatchStart(() => releaseStart.Task);
            var publishedCorrelation = coordinator.GetLastStart().Completion;
            Assert.NotNull(publishedCorrelation);

            Assert.Equal(JsonControlProtocol.StateStarting, coordinator.SnapshotState());

            state = JsonControlProtocol.StateRecording;
            Assert.Equal(JsonControlProtocol.StateRecording, coordinator.SnapshotState());

            releaseStart.TrySetResult();
            await publishedCorrelation.WaitAsync(s_testGuard);
            Assert.Equal(JsonControlProtocol.StateRecording, coordinator.SnapshotState());

            state = JsonControlProtocol.StateIdle;
            var completedResponse = coordinator.DispatchStart(() => Task.CompletedTask);
            Assert.True(completedResponse.IsCompletedSuccessfully);
            AssertAction(
                await completedResponse,
                JsonControlProtocol.StateIdle,
                JsonControlProtocol.StateIdle
            );
            Assert.Equal(JsonControlProtocol.StateIdle, coordinator.SnapshotState());
        }
        finally
        {
            releaseStart.TrySetResult();
            await AwaitPublishedStartAsync(coordinator);
        }
    }

    [Theory]
    [InlineData(JsonControlProtocol.StateTranscribing)]
    [InlineData(JsonControlProtocol.StateInjecting)]
    public async Task Pending_start_returns_real_active_state(string state)
    {
        var releaseStart = NewSignal();
        var coordinator = CreateCoordinator(() => state);

        try
        {
            _ = coordinator.DispatchStart(() => releaseStart.Task);
            var publishedCorrelation = coordinator.GetLastStart().Completion;
            Assert.NotNull(publishedCorrelation);
            Assert.False(publishedCorrelation.IsCompleted);

            Assert.Equal(state, coordinator.SnapshotState());
        }
        finally
        {
            releaseStart.TrySetResult();
            await AwaitPublishedStartAsync(coordinator);
        }
    }

    [Fact]
    public async Task Repeated_start_reuses_in_flight_correlation()
    {
        const string state = JsonControlProtocol.StateIdle;
        var releaseStart = NewSignal();
        var startInvocations = 0;
        var coordinator = CreateCoordinator(() => state);

        try
        {
            var firstResponse = coordinator.DispatchStart(() =>
            {
                startInvocations++;
                return releaseStart.Task;
            });
            var firstCorrelation = coordinator.GetLastStart().Completion;
            Assert.NotNull(firstCorrelation);

            var secondResponse = coordinator.DispatchStart(() =>
            {
                startInvocations++;
                return Task.CompletedTask;
            });

            Assert.True(firstResponse.IsCompletedSuccessfully);
            Assert.True(secondResponse.IsCompletedSuccessfully);
            Assert.Equal(1, startInvocations);
            AssertAction(
                await secondResponse,
                JsonControlProtocol.StateStarting,
                JsonControlProtocol.StateStarting
            );
            Assert.Same(firstCorrelation, coordinator.GetLastStart().Completion);
            Assert.False(firstCorrelation.IsCompleted);

            releaseStart.TrySetResult();
            await firstCorrelation.WaitAsync(s_testGuard);
        }
        finally
        {
            releaseStart.TrySetResult();
            await AwaitPublishedStartAsync(coordinator);
        }
    }

    [Fact]
    public async Task Background_fault_is_observed_and_clears_starting_phase()
    {
        var startCompletion = NewSignal();
        var faultObserved = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var faultCount = 0;
        var coordinator = new ControlSocketStartCoordinator(
            () => JsonControlProtocol.StateIdle,
            ex =>
            {
                // ReSharper disable once AccessToModifiedClosure -- deliberate shared fault counter, read via Volatile.Read in the assertions
                Interlocked.Increment(ref faultCount);
                faultObserved.TrySetResult(ex);
            }
        );
        var expected = new InvalidOperationException("controlled start failure");

        try
        {
            var response = coordinator.DispatchStart(() => startCompletion.Task);
            Assert.True(response.IsCompletedSuccessfully);
            AssertAction(
                await response,
                JsonControlProtocol.StateIdle,
                JsonControlProtocol.StateStarting
            );

            startCompletion.TrySetException(expected);
            var observed = await faultObserved.Task.WaitAsync(s_testGuard);
            var publishedCorrelation = coordinator.GetLastStart().Completion;
            Assert.NotNull(publishedCorrelation);
            await publishedCorrelation.WaitAsync(s_testGuard);

            Assert.Same(expected, observed);
            Assert.Equal(1, Volatile.Read(ref faultCount));
            Assert.Equal(JsonControlProtocol.StateIdle, coordinator.SnapshotState());
        }
        finally
        {
            startCompletion.TrySetException(expected);
            await AwaitPublishedStartAsync(coordinator);
        }
    }

    [Fact]
    public async Task Synchronous_start_throw_returns_internal_error_and_observes_once()
    {
        var faultObserved = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var faultCount = 0;
        var coordinator = new ControlSocketStartCoordinator(
            () => JsonControlProtocol.StateIdle,
            ex =>
            {
                // ReSharper disable once AccessToModifiedClosure -- deliberate shared fault counter, read via Volatile.Read in the assertions
                Interlocked.Increment(ref faultCount);
                faultObserved.TrySetResult(ex);
            }
        );
        var expected = new InvalidOperationException("synchronous start failure");

        var response = coordinator.DispatchStart(() => throw expected);

        Assert.True(response.IsCompletedSuccessfully);
        AssertError(await response, JsonControlProtocol.ErrInternal);

        var observed = await faultObserved.Task.WaitAsync(s_testGuard);
        Assert.Same(expected, observed);
        Assert.Equal(1, Volatile.Read(ref faultCount));

        // The correlation must still settle as an ordering signal even when the start
        // faults before returning a task, and the synthetic phase must clear afterward.
        await AwaitPublishedStartAsync(coordinator);
        Assert.Equal(JsonControlProtocol.StateIdle, coordinator.SnapshotState());
    }

    [Fact]
    public async Task Already_faulted_start_task_returns_internal_error_and_observes_once()
    {
        var faultObserved = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var faultCount = 0;
        var coordinator = new ControlSocketStartCoordinator(
            () => JsonControlProtocol.StateIdle,
            ex =>
            {
                // ReSharper disable once AccessToModifiedClosure -- deliberate shared fault counter, read via Volatile.Read in the assertions
                Interlocked.Increment(ref faultCount);
                faultObserved.TrySetResult(ex);
            }
        );
        var expected = new InvalidOperationException("already-faulted start");

        var response = coordinator.DispatchStart(() => Task.FromException(expected));

        Assert.True(response.IsCompletedSuccessfully);
        AssertError(await response, JsonControlProtocol.ErrInternal);

        var observed = await faultObserved.Task.WaitAsync(s_testGuard);
        Assert.Same(expected, observed);
        Assert.Equal(1, Volatile.Read(ref faultCount));

        await AwaitPublishedStartAsync(coordinator);
        Assert.Equal(JsonControlProtocol.StateIdle, coordinator.SnapshotState());
    }

    private static ControlSocketStartCoordinator CreateCoordinator(Func<string> readState)
    {
        return new ControlSocketStartCoordinator(readState, _ => { });
    }

    private static async Task AwaitPublishedStartAsync(ControlSocketStartCoordinator coordinator)
    {
        var completion = coordinator.GetLastStart().Completion;
        if (completion is not null)
        {
            await completion.WaitAsync(s_testGuard);
        }
    }

    private static void AssertAction(string json, string expectedPrev, string expectedState)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(JsonControlProtocol.CurrentVersion, root.GetProperty("v").GetInt32());
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedPrev, root.GetProperty("prev").GetString());
        Assert.Equal(expectedState, root.GetProperty("state").GetString());
    }

    private static void AssertError(string json, string expectedError)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(JsonControlProtocol.CurrentVersion, root.GetProperty("v").GetInt32());
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedError, root.GetProperty("error").GetString());
    }

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
