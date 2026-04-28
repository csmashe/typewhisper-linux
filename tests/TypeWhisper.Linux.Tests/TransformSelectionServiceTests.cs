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
}
