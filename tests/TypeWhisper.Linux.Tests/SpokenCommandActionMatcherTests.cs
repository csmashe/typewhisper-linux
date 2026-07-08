using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.SpokenCommand;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SpokenCommandActionMatcherTests
{
    private static readonly IReadOnlyList<PromptAction> s_actions =
    [
        new() { Id = "clean", Name = "Clean up email", SystemPrompt = "..." },
        new() { Id = "auto", Name = "Auto Clean Up Text", SystemPrompt = "..." },
        new() { Id = "formal", Name = "Make Formal", SystemPrompt = "..." }
    ];

    [Theory]
    [InlineData("clean up email", "clean")]
    [InlineData("Clean up this email please", "clean")]
    [InlineData("cleanup email", "clean")]        // STT word-join still matches the multi-word name
    [InlineData("make formal", "formal")]
    [InlineData("make this formal", "formal")]
    public void Match_ReturnsSavedActionWhenCommandNamesIt(string command, string expectedId)
    {
        var matched = SpokenCommandActionMatcher.Match(command, s_actions);

        Assert.NotNull(matched);
        Assert.Equal(expectedId, matched.Id);
    }

    [Theory]
    [InlineData("shorten this")]
    [InlineData("format this as an email")]
    [InlineData("translate to spanish")]
    [InlineData("")]
    public void Match_ReturnsNullForGenericCommands(string command)
    {
        Assert.Null(SpokenCommandActionMatcher.Match(command, s_actions));
    }

    [Fact]
    public void Match_PrefersTheMostSpecificName()
    {
        // "clean up email" satisfies both "Clean up email" and the token set of "Auto Clean Up
        // Text" is NOT fully present, so the longer specific name wins cleanly.
        var matched = SpokenCommandActionMatcher.Match("please clean up email now", s_actions);

        Assert.Equal("clean", matched!.Id);
    }

    [Fact]
    public void Match_ReturnsNullWhenNoActions()
    {
        Assert.Null(SpokenCommandActionMatcher.Match("clean up email", []));
    }

    [Fact]
    public void Match_DoesNotMatchSingleWordNameMerelyMentioned()
    {
        // A create command that only mentions the word must not hijack a single-word "Email" action.
        var actions = new PromptAction[]
        {
            new() { Id = "email", Name = "Email", SystemPrompt = "..." }
        };

        Assert.Null(SpokenCommandActionMatcher.Match("draft an email to Bob", actions));
    }

    [Fact]
    public void Match_MatchesSingleWordNameWhenItLeadsTheCommand()
    {
        var actions = new PromptAction[]
        {
            new() { Id = "email", Name = "Email", SystemPrompt = "..." }
        };

        var matched = SpokenCommandActionMatcher.Match("email this to the team", actions);

        Assert.Equal("email", matched!.Id);
    }

    [Fact]
    public void Match_MatchesSingleWordNameAfterLeadingFiller()
    {
        // A leading politeness filler ("please") must not hide an explicit single-word invocation.
        var actions = new PromptAction[]
        {
            new() { Id = "email", Name = "Email", SystemPrompt = "..." }
        };

        var matched = SpokenCommandActionMatcher.Match("please email this to the team", actions);

        Assert.Equal("email", matched!.Id);
    }
}
