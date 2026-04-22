using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ActiveWindowServiceTests
{
    [Theory]
    [InlineData("firefox", true)]
    [InlineData("Google Chrome", true)]
    [InlineData("Microsoft Edge", true)]
    [InlineData("org.mozilla.firefox", true)]
    [InlineData("code", false)]
    [InlineData(null, false)]
    public void IsSupportedBrowserIdentity_RecognizesBrowserNames(string? identity, bool expected)
    {
        Assert.Equal(expected, ActiveWindowService.IsSupportedBrowserIdentity(identity));
    }

    [Fact]
    public void HasState_ReadsBitsAcrossWords()
    {
        var states = new uint[] { 0, 1u << 3 };

        Assert.True(ActiveWindowService.HasState(states, 35));
        Assert.False(ActiveWindowService.HasState(states, 11));
    }

    [Theory]
    [InlineData("https://example.com/path", "https://example.com/path")]
    [InlineData("example.com/path", "https://example.com/path")]
    [InlineData("not a url", null)]
    public void SanitizeCapturedBrowserUrl_NormalizesLikelyUrls(string value, string? expected)
    {
        Assert.Equal(expected, ActiveWindowService.SanitizeCapturedBrowserUrl(value));
    }

    [Fact]
    public void ScoreBrowserUrlCandidate_PrefersFocusedEditBarOverGenericEntry()
    {
        var focusedEditBarScore = ActiveWindowService.ScoreBrowserUrlCandidate(
            role: 77,
            states: [1u << 11, 1u << 18],
            name: "Address and search bar",
            candidateText: "https://example.com/path",
            interfaces: ["org.a11y.atspi.Text"]);

        var entryScore = ActiveWindowService.ScoreBrowserUrlCandidate(
            role: 79,
            states: [1u << 18],
            name: "Search",
            candidateText: "example.com",
            interfaces: ["org.a11y.atspi.Text"]);

        Assert.True(focusedEditBarScore > entryScore);
    }
}
