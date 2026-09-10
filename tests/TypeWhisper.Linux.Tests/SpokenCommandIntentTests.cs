using TypeWhisper.Linux.Services.SpokenCommand;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SpokenCommandIntentTests
{
    [Theory]
    [InlineData("shorten this")]
    [InlineData("make it formal")]
    [InlineData("fix the grammar in the following")]
    [InlineData("summarize the text")]
    [InlineData("rewrite these")]
    [InlineData("clean up the selection")]
    // Leading transform verb implies an edit even with no pronoun.
    [InlineData("translate to spanish")]
    [InlineData("fix grammar")]
    // Referent pronoun inside the opening tokens signals the selection.
    [InlineData("please make it shorter")]
    [InlineData("write this more formally")]
    // Selection phrases match case-insensitively (STT may capitalize) via the phrase path.
    [InlineData("improve The Text please")]
    // Leading politeness/filler must not hide a transform verb.
    [InlineData("please fix grammar")]
    [InlineData("can you translate to spanish")]
    public void RefersToSelection_TrueWhenCommandTargetsExistingText(string command)
    {
        Assert.True(SpokenCommandIntent.RefersToSelection(command));
    }

    [Theory]
    [InlineData("write a haiku about coffee")]
    [InlineData("draft an email to Bob")]
    [InlineData("write a one sentence summary of quantum computing")]
    // Creation verb; the pronoun "it" lands past the opening tokens, so it does not count.
    [InlineData("write a haiku about coffee and make it rhyme")]
    // Creation verb; the pronoun "this" is the 5th token, outside the referent window, so this
    // routes to create.
    [InlineData("draft an answer to this email")]
    // "this textbook" must not match the selection phrase "this text" via substring.
    [InlineData("write a summary of this textbook")]
    [InlineData("")]
    public void RefersToSelection_FalseForFromScratchCreate(string command)
    {
        Assert.False(SpokenCommandIntent.RefersToSelection(command));
    }

    [Theory]
    [InlineData("write an email to Bob")]
    [InlineData("draft a reply")]
    [InlineData("Compose a tweet about cats")]
    // Leading politeness/filler must not hide the creation verb.
    [InlineData("please write an email to Bob")]
    [InlineData("can you draft a reply")]
    [InlineData("could you please generate a summary")]
    public void OpensWithCreationVerb_TrueForCreationLeadingCommands(string command)
    {
        Assert.True(SpokenCommandIntent.OpensWithCreationVerb(command));
    }

    [Theory]
    [InlineData("shorten this")]
    [InlineData("clean up the email")]
    [InlineData("translate to spanish")]
    // "make"/"reply" lead saved transform action names ("Make Formal", "Reply"), so they must NOT
    // count as creation verbs or an invocation of that action would be misrouted to create.
    [InlineData("make formal")]
    [InlineData("reply to this")]
    [InlineData("")]
    public void OpensWithCreationVerb_FalseForEditsAndEmpty(string command)
    {
        Assert.False(SpokenCommandIntent.OpensWithCreationVerb(command));
    }
}
