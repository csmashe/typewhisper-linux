using TypeWhisper.Cli.Models;
using Xunit;

namespace TypeWhisper.Cli.Tests;

public class CommandLineParserTests
{
    [Fact]
    public void Port_IsAnUnknownOption()
    {
        var options = CliOptions.Parse(["--port", "8080", "status"]);

        Assert.NotNull(options.ErrorMessage);
        Assert.Equal("Unknown option '--port'.", options.ErrorMessage);
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

}
