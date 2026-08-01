using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class UrlLauncherTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file:///tmp/example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("relative/path")]
    public void Open_rejects_non_http_urls(string? value)
    {
        var runner = new FakeProcessRunner();

        Assert.False(new UrlLauncher(runner).Open(value));
        Assert.Empty(runner.LaunchedUris);
    }

    [Fact]
    public void Open_launches_valid_https_uri_through_supervisor()
    {
        var runner = new FakeProcessRunner();

        Assert.True(new UrlLauncher(runner).Open("https://example.com/a?b=1"));

        Assert.Equal(
            new Uri("https://example.com/a?b=1"),
            Assert.Single(runner.LaunchedUris)
        );
    }
}
