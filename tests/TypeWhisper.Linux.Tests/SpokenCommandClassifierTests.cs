using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.SpokenCommand;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class SpokenCommandClassifierTests
{
    private static readonly IReadOnlyList<PromptAction> s_actions =
    [
        new()
        {
            Id = "shorten-1",
            Name = "Make shorter",
            SystemPrompt = "Rewrite the text to be more concise."
        }
    ];

    [Fact]
    public void Parse_WellFormedEditWithActionId()
    {
        var decision = SpokenCommandClassifier.Parse(
            """{"kind":"edit","actionId":"shorten-1"}""",
            CommandKind.Create
        );

        Assert.Equal(CommandKind.Edit, decision.Kind);
        Assert.Equal("shorten-1", decision.ActionId);
    }

    [Fact]
    public void Parse_WellFormedCreateWithNullAction()
    {
        var decision = SpokenCommandClassifier.Parse(
            """{"kind":"create","actionId":null}""",
            CommandKind.Edit
        );

        Assert.Equal(CommandKind.Create, decision.Kind);
        Assert.Null(decision.ActionId);
    }

    [Fact]
    public void Parse_ToleratesProseAndCodeFencesAroundJson()
    {
        const string reply = """
                             Sure! Here is the classification:
                             ```json
                             {"kind": "edit", "actionId": "shorten-1"}
                             ```
                             """;

        var decision = SpokenCommandClassifier.Parse(reply, CommandKind.Create);

        Assert.Equal(CommandKind.Edit, decision.Kind);
        Assert.Equal("shorten-1", decision.ActionId);
    }

    [Fact]
    public void Parse_BlankActionIdBecomesNull()
    {
        var decision = SpokenCommandClassifier.Parse(
            """{"kind":"edit","actionId":"  "}""",
            CommandKind.Create
        );

        Assert.Equal(CommandKind.Edit, decision.Kind);
        Assert.Null(decision.ActionId);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{ this is : broken")]
    [InlineData("""{"kind":"unknown"}""")]
    public void Parse_GarbageReturnsProvidedFallbackKind(string reply)
    {
        var toCreate = SpokenCommandClassifier.Parse(reply, CommandKind.Create);
        Assert.Equal(CommandKind.Create, toCreate.Kind);
        Assert.Null(toCreate.ActionId);

        var toEdit = SpokenCommandClassifier.Parse(reply, CommandKind.Edit);
        Assert.Equal(CommandKind.Edit, toEdit.Kind);
        Assert.Null(toEdit.ActionId);
    }

    [Fact]
    public void BuildPrompt_IncludesCommandAndActions()
    {
        var prompt = SpokenCommandClassifier.BuildPrompt("make this shorter", s_actions);

        Assert.Contains("make this shorter", prompt);
        Assert.Contains("shorten-1", prompt);
        Assert.Contains("Make shorter", prompt);
    }

    [Fact]
    public void BuildPrompt_HandlesNoActions()
    {
        var prompt = SpokenCommandClassifier.BuildPrompt("write a poem", []);

        Assert.Contains("write a poem", prompt);
        Assert.Contains("no saved actions", prompt);
    }

    [Fact]
    public void BuildPrompt_HandlesNullActions()
    {
        var prompt = SpokenCommandClassifier.BuildPrompt("write a poem", null);

        Assert.Contains("write a poem", prompt);
        Assert.Contains("no saved actions", prompt);
    }
}
