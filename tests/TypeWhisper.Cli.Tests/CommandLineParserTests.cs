using TypeWhisper.Cli.Models;
using Xunit;

namespace TypeWhisper.Cli.Tests;

public class CommandLineParserTests
{
    [Fact]
    public void Port_OutOfRange_Errors()
    {
        var options = CliOptions.Parse(["--port", "70000", "status"]);
        Assert.NotNull(options.ErrorMessage);
        Assert.Contains("1 and 65535", options.ErrorMessage);
    }

    [Fact]
    public void Token_FollowedByFlag_FailsCleanly()
    {
        var options = CliOptions.Parse(["--token", "--json", "status"]);
        Assert.NotNull(options.ErrorMessage);
        Assert.Contains("requires a value", options.ErrorMessage);
    }

    [Fact]
    public void ApiTokenAlias_MapsToToken()
    {
        var options = CliOptions.Parse(["--api-token", "abc123", "status"]);
        Assert.Null(options.ErrorMessage);
        Assert.Equal("abc123", options.Token);
        Assert.True(options.TokenWasExplicit);
        Assert.Equal("status", options.Command);
    }

    [Fact]
    public void ExplicitPort_SetsExplicitFlag()
    {
        var options = CliOptions.Parse(["--port", "8080", "status"]);
        Assert.Equal(8080, options.Port);
        Assert.True(options.PortWasExplicit);
    }

    [Fact]
    public void NoExplicitPort_DefaultsAndUnflagged()
    {
        var options = CliOptions.Parse(["status"]);
        Assert.Equal(9876, options.Port);
        Assert.False(options.PortWasExplicit);
    }
}
