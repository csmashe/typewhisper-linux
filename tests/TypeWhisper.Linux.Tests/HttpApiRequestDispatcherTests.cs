using System.Text.Json;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public class HttpApiRequestDispatcherTests
{
    [Fact]
    public void ProductionConcurrencyBound_IsExactlyTwo()
    {
        Assert.Equal(2, HttpApiService.MaxConcurrentRequests);
    }

    [Fact]
    public async Task TryRun_RejectsThirdInFlightHandlerThenReusesSlot()
    {
        var dispatcher = new HttpApiRequestDispatcher(HttpApiService.MaxConcurrentRequests);
        var firstEntered = NewSignal();
        var secondEntered = NewSignal();
        var release = NewSignal();

        var first = AssertAdmitted(dispatcher.TryRun(async () =>
        {
            firstEntered.SetResult();
            await release.Task;
        }));
        var second = AssertAdmitted(dispatcher.TryRun(async () =>
        {
            secondEntered.SetResult();
            await release.Task;
        }));

        await Task.WhenAll(firstEntered.Task, secondEntered.Task);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        var thirdInvoked = false;
        var third = dispatcher.TryRun(() =>
        {
            thirdInvoked = true;
            return Task.CompletedTask;
        });

        Assert.Equal(HttpApiDispatchStatus.CapacityExceeded, third.Status);
        Assert.Null(third.HandlerTask);
        Assert.False(thirdInvoked);

        release.SetResult();
        await Task.WhenAll(first, second);

        var reused = AssertAdmitted(dispatcher.TryRun(() => Task.CompletedTask));
        await reused;
    }

    [Fact]
    public async Task TryRun_HandlerFailureIsObservedAndDoesNotLeakSlot()
    {
        var errorObserved = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var dispatcher = new HttpApiRequestDispatcher(
            HttpApiService.MaxConcurrentRequests,
            errorObserved.SetResult
        );
        var entered = NewSignal();
        var fail = NewSignal();

        var failed = AssertAdmitted(dispatcher.TryRun(async () =>
        {
            entered.SetResult();
            await fail.Task;
            throw new InvalidOperationException("Expected dispatcher test failure.");
        }));

        await entered.Task;
        fail.SetResult();
        await failed;

        Assert.True(errorObserved.Task.IsCompletedSuccessfully);
        var observed = await errorObserved.Task;
        Assert.IsType<InvalidOperationException>(observed);

        var releaseFreshHandlers = NewSignal();
        var firstFresh = AssertAdmitted(
            dispatcher.TryRun(() => releaseFreshHandlers.Task)
        );
        var secondFresh = AssertAdmitted(
            dispatcher.TryRun(() => releaseFreshHandlers.Task)
        );

        Assert.Equal(
            HttpApiDispatchStatus.CapacityExceeded,
            dispatcher.TryRun(() => Task.CompletedTask).Status
        );

        releaseFreshHandlers.SetResult();
        await Task.WhenAll(firstFresh, secondFresh);
    }

    [Fact]
    public async Task Dispose_WhileHandlerInFlight_LeavesTheHandlerAbleToReleaseItsSlot()
    {
        var dispatcher = new HttpApiRequestDispatcher(HttpApiService.MaxConcurrentRequests);
        var entered = NewSignal();
        var release = NewSignal();
        var inFlight = AssertAdmitted(dispatcher.TryRun(async () =>
        {
            entered.SetResult();
            await release.Task;
        }));

        await entered.Task;

        // Disposal sees the admitted-handler count and backs off without draining, leaving the
        // semaphore available to the handler's finally block.
        dispatcher.Dispose();
        release.SetResult();
        await inFlight;

        Assert.True(inFlight.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CloseAdmission_RejectsNewHandlerWhileParkedHandlerRuns()
    {
        using var dispatcher = new HttpApiRequestDispatcher(1);
        var entered = NewSignal();
        var release = NewSignal();
        var parked = AssertAdmitted(dispatcher.TryRun(async () =>
        {
            entered.SetResult();
            await release.Task;
        }));
        await entered.Task;

        dispatcher.CloseAdmission();
        var rejectedHandlerInvoked = false;
        var rejected = dispatcher.TryRun(() =>
        {
            rejectedHandlerInvoked = true;
            return Task.CompletedTask;
        });

        Assert.Equal(HttpApiDispatchStatus.Closed, rejected.Status);
        Assert.Null(rejected.HandlerTask);
        Assert.False(rejectedHandlerInvoked);

        release.SetResult();
        await parked;
    }

    [Fact]
    public async Task Drain_IncompleteUntilParkedHandlerReleases()
    {
        using var dispatcher = new HttpApiRequestDispatcher(1);
        var entered = NewSignal();
        var release = NewSignal();
        var parked = AssertAdmitted(dispatcher.TryRun(async () =>
        {
            entered.SetResult();
            await release.Task;
        }));
        await entered.Task;
        dispatcher.CloseAdmission();

        var drain = dispatcher.DrainAsync(TimeSpan.FromSeconds(5));
        Assert.False(drain.IsCompleted);

        release.SetResult();
        await parked;
        Assert.True(await drain);
    }

    [Fact]
    public async Task DrainTimeout_LeavesHandlerSafeAndAdmissionClosed()
    {
        using var dispatcher = new HttpApiRequestDispatcher(1);
        var entered = NewSignal();
        var release = NewSignal();
        var parked = AssertAdmitted(dispatcher.TryRun(async () =>
        {
            entered.SetResult();
            await release.Task;
        }));
        await entered.Task;
        dispatcher.CloseAdmission();

        Assert.False(await dispatcher.DrainAsync(TimeSpan.Zero));
        Assert.Equal(
            HttpApiDispatchStatus.Closed,
            dispatcher.TryRun(() => Task.CompletedTask).Status
        );

        release.SetResult();
        await parked;
        Assert.True(parked.IsCompletedSuccessfully);
    }

    [Fact]
    public void OverCapacityResponseMetadata_IsPinned()
    {
        var response = HttpApiService.CreateOverCapacityResponse();

        Assert.Equal(429, response.StatusCode);
        Assert.Equal("1", response.RetryAfter);
        using var body = JsonDocument.Parse(response.Body);
        Assert.Equal(
            "Too many concurrent requests",
            body.RootElement.GetProperty("error").GetString()
        );
    }

    [Fact]
    public void ClosedResponseMetadata_IsPinned()
    {
        var response = HttpApiService.CreateClosedResponse();

        Assert.Equal(503, response.StatusCode);
        Assert.Null(response.RetryAfter);
        using var body = JsonDocument.Parse(response.Body);
        Assert.Equal(
            "Service unavailable",
            body.RootElement.GetProperty("error").GetString()
        );
    }

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static Task AssertAdmitted(HttpApiDispatchResult result)
    {
        Assert.Equal(HttpApiDispatchStatus.Admitted, result.Status);
        return Assert.IsAssignableFrom<Task>(result.HandlerTask);
    }
}
