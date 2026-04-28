using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class CleanupServiceTests
{
    private readonly CleanupService _sut = new();

    [Fact]
    public void Clean_None_ReturnsOriginalText()
    {
        var text = "um hello   world";

        var result = _sut.Clean(text, CleanupLevel.None);

        Assert.Equal(text, result);
    }

    [Theory]
    [InlineData("um hello world", "Hello world")]
    [InlineData("uh, hello world", "Hello world")]
    [InlineData("hello, you know, world", "Hello, world")]
    [InlineData("er hello ah world", "Hello world")]
    public void Clean_Light_RemovesStandaloneFillers(string input, string expected)
    {
        var result = _sut.Clean(input, CleanupLevel.Light);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Clean_Light_DoesNotDamageWordsContainingFillerText()
    {
        var result = _sut.Clean("umbrella error ahead", CleanupLevel.Light);

        Assert.Equal("Umbrella error ahead", result);
    }

    [Fact]
    public void Clean_Light_NormalizesWhitespaceAndPreservesSentencePunctuation()
    {
        var result = _sut.Clean("  hello   world  .  ", CleanupLevel.Light);

        Assert.Equal("Hello world.", result);
    }

    [Theory]
    [InlineData("send it to John actually Jane", "Send it to Jane")]
    [InlineData("set the color to red I mean blue", "Set the color to blue")]
    public void Clean_Light_AppliesConservativeOneWordBacktrackCorrections(string input, string expected)
    {
        var result = _sut.Clean(input, CleanupLevel.Light);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Clean_Light_AppliesScratchThatReplacementWhenReplacementLooksLikeNewDictation()
    {
        var result = _sut.Clean("send the first draft scratch that please send the final draft", CleanupLevel.Light);

        Assert.Equal("Please send the final draft", result);
    }

    [Theory]
    [InlineData("please scratch that surface carefully", "Please scratch that surface carefully")]
    [InlineData("we meet Tuesday actually Wednesday then review notes", "We meet Tuesday actually Wednesday then review notes")]
    public void Clean_Light_LeavesAmbiguousBacktrackTextAlone(string input, string expected)
    {
        var result = _sut.Clean(input, CleanupLevel.Light);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Clean_Light_FormatsClearSpokenNumberedList()
    {
        var result = _sut.Clean("one apples two bananas three oranges", CleanupLevel.Light);

        Assert.Equal("1. Apples\n2. bananas\n3. oranges", result);
    }

    [Fact]
    public void Clean_Light_DoesNotFormatOutOfOrderNumberedList()
    {
        var result = _sut.Clean("one apples three oranges", CleanupLevel.Light);

        Assert.Equal("One apples three oranges", result);
    }

    [Fact]
    public void Clean_MediumAndHigh_DegradeToLightForNow()
    {
        Assert.Equal("Hello", _sut.Clean("um hello", CleanupLevel.Medium));
        Assert.Equal("Hello", _sut.Clean("um hello", CleanupLevel.High));
    }

    [Fact]
    public void GetLlmSystemPrompt_ReturnsMediumPrompt()
    {
        var result = CleanupService.GetLlmSystemPrompt(CleanupLevel.Medium);

        Assert.Contains("Improve readability", result);
        Assert.Contains("Do not add new information", result);
    }

    [Fact]
    public void GetLlmSystemPrompt_ReturnsHighPrompt()
    {
        var result = CleanupService.GetLlmSystemPrompt(CleanupLevel.High);

        Assert.Contains("Rewrite as concise polished prose", result);
        Assert.Contains("Do not add new information", result);
    }
}
