using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationOrchestratorUrlWalkTests
{
    [Fact]
    public void ShouldRematchBrowserUrl_WhenForcedProfileIdProvided_ReturnsFalse()
    {
        var profiles = new[]
        {
            new Profile
            {
                Id = "p1",
                Name = "Web",
                IsEnabled = true,
                UrlPatterns = ["github.com/*"]
            }
        };

        Assert.False(DictationOrchestrator.ShouldRematchBrowserUrl("forced-profile", profiles));
    }

    [Fact]
    public void ShouldRematchBrowserUrl_WhenProfilesNullOrEmpty_ReturnsFalse()
    {
        Assert.False(DictationOrchestrator.ShouldRematchBrowserUrl(null, null));
        Assert.False(DictationOrchestrator.ShouldRematchBrowserUrl(null, Array.Empty<Profile>()));
    }

    [Fact]
    public void ShouldRematchBrowserUrl_WhenNoProfileHasUrlPatterns_ReturnsFalse()
    {
        var profiles = new[]
        {
            new Profile
            {
                Id = "p1",
                Name = "App Only",
                IsEnabled = true,
                ProcessNames = ["code"]
            }
        };

        Assert.False(DictationOrchestrator.ShouldRematchBrowserUrl(null, profiles));
    }

    [Fact]
    public void ShouldRematchBrowserUrl_WhenProfileWithUrlPatternsIsDisabled_ReturnsFalse()
    {
        var profiles = new[]
        {
            new Profile
            {
                Id = "p1",
                Name = "Disabled Web",
                IsEnabled = false,
                UrlPatterns = ["slack.com"]
            }
        };

        Assert.False(DictationOrchestrator.ShouldRematchBrowserUrl(null, profiles));
    }

    [Fact]
    public void ShouldRematchBrowserUrl_WhenEnabledProfileHasUrlPatterns_ReturnsTrue()
    {
        var profiles = new[]
        {
            new Profile
            {
                Id = "p1",
                Name = "Active Web",
                IsEnabled = true,
                UrlPatterns = ["mail.google.com"]
            }
        };

        Assert.True(DictationOrchestrator.ShouldRematchBrowserUrl(null, profiles));
    }

    [Fact]
    public void ResolveStopUrlWaitBudget_Returns400MsWhenActiveProfilesExist_And250MsOtherwise()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(400), DictationOrchestrator.ResolveStopUrlWaitBudget(hasActiveUrlProfiles: true));
        Assert.Equal(TimeSpan.FromMilliseconds(250), DictationOrchestrator.ResolveStopUrlWaitBudget(hasActiveUrlProfiles: false));
    }

    [Fact]
    public async Task StopWaitBudget_BoundsSlowRematchTask()
    {
        var budgetWithoutUrlProfiles = DictationOrchestrator.ResolveStopUrlWaitBudget(hasActiveUrlProfiles: false);
        var budgetWithUrlProfiles = DictationOrchestrator.ResolveStopUrlWaitBudget(hasActiveUrlProfiles: true);

        // A slow URL walk simulating a 2-second AT-SPI tree walk
        var slowTask = Task.Delay(2000);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => slowTask.WaitAsync(budgetWithoutUrlProfiles));
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1500, $"Expected stop wait without URL profiles to be bounded, was {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        await Assert.ThrowsAsync<TimeoutException>(() => slowTask.WaitAsync(budgetWithUrlProfiles));
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1500, $"Expected stop wait with URL profiles to be bounded, was {sw.ElapsedMilliseconds} ms");
    }
}
