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

        var first = dispatcher.TryRun(async () =>
        {
            firstEntered.SetResult();
            await release.Task;
        });
        var second = dispatcher.TryRun(async () =>
        {
            secondEntered.SetResult();
            await release.Task;
        });

        await Task.WhenAll(firstEntered.Task, secondEntered.Task);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        var thirdInvoked = false;
        var third = dispatcher.TryRun(() =>
        {
            thirdInvoked = true;
            return Task.CompletedTask;
        });

        Assert.Null(third);
        Assert.False(thirdInvoked);

        release.SetResult();
        await Task.WhenAll(first, second);

        var reused = dispatcher.TryRun(() => Task.CompletedTask);
        Assert.NotNull(reused);
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

        var failed = dispatcher.TryRun(async () =>
        {
            entered.SetResult();
            await fail.Task;
            throw new InvalidOperationException("Expected dispatcher test failure.");
        });

        await entered.Task;
        fail.SetResult();
        await failed!;

        Assert.True(errorObserved.Task.IsCompletedSuccessfully);
        var observed = await errorObserved.Task;
        Assert.IsType<InvalidOperationException>(observed);

        var releaseFreshHandlers = NewSignal();
        var firstFresh = dispatcher.TryRun(() => releaseFreshHandlers.Task);
        var secondFresh = dispatcher.TryRun(() => releaseFreshHandlers.Task);

        Assert.NotNull(firstFresh);
        Assert.NotNull(secondFresh);
        Assert.Null(dispatcher.TryRun(() => Task.CompletedTask));

        releaseFreshHandlers.SetResult();
        await Task.WhenAll(firstFresh, secondFresh);
    }

    [Fact]
    public async Task Dispose_WhileHandlerInFlight_LeavesTheHandlerAbleToReleaseItsSlot()
    {
        var dispatcher = new HttpApiRequestDispatcher(HttpApiService.MaxConcurrentRequests);
        var entered = NewSignal();
        var release = NewSignal();
        var inFlight = dispatcher.TryRun(async () =>
        {
            entered.SetResult();
            await release.Task;
        });

        Assert.NotNull(inFlight);
        await entered.Task;

        // The drain can't reclaim the busy slot, so disposal backs off rather than pulling the
        // semaphore out from under the handler's finally block.
        dispatcher.Dispose();
        release.SetResult();
        await inFlight;

        Assert.True(inFlight.IsCompletedSuccessfully);
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

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
