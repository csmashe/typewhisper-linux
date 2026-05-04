using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class TransformSelectionServiceTests
{
    [Fact]
    public void BuildTransformPrompt_IncludesSelectedTextAndSpokenCommand()
    {
        var result = TransformSelectionService.BuildTransformPrompt(
            "This sentence is too long.",
            "make it concise");

        Assert.Contains("Return only the transformed text.", result);
        Assert.Contains("This sentence is too long.", result);
        Assert.Contains("make it concise", result);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("Cancel.")]
    [InlineData("never mind")]
    [InlineData("nevermind!")]
    [InlineData("stop")]
    public void IsCancelCommand_ReturnsTrueForCancelPhrases(string command)
    {
        Assert.True(TransformSelectionService.IsCancelCommand(command));
    }

    [Fact]
    public void IsCancelCommand_ReturnsFalseForNormalEditInstruction()
    {
        Assert.False(TransformSelectionService.IsCancelCommand("make this more concise"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsCancelCommand_ReturnsFalseForEmptyOrWhitespace(string command)
    {
        Assert.False(TransformSelectionService.IsCancelCommand(command));
    }
}
