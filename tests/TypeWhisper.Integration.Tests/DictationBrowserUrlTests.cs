using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using Xunit;

namespace TypeWhisper.Integration.Tests;

public sealed class DictationBrowserUrlTests
{
    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(true, false, 1)]
    [Trait("Category", "Integration")]
    public Task StartStop_WalksBrowserOnlyForAutomaticUrlProfiles(
        bool enabled, bool forced, int expectedWalks)
    {
        return BoundedTest.RunAsync(async () =>
        {
            var walks = 0;
            await using var fixture = new OrchestratorCompositionFixture(
                focusedApp: ("firefox", "Example"),
                browserUrlWalk: _ =>
                {
                    Interlocked.Increment(ref walks);
                    return "https://example.com/";
                });
            fixture.Provider.GetRequiredService<IProfileService>().AddProfile(new Profile
            {
                Id = "web", Name = "Web", IsEnabled = enabled,
                UrlPatterns = ["example.com"],
            });
            fixture.Plugin.EnqueueText("hello");

            var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync(forced ? "web" : null));
            fixture.FeedNonSilentAudio();
            var resultTask = fixture.WaitForResultAsync(sessionId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            var result = await BoundedTest.WaitAsync(resultTask);

            Assert.Equal("ready", result.Status);
            Assert.Equal(expectedWalks, Volatile.Read(ref walks));
        });
    }
}
