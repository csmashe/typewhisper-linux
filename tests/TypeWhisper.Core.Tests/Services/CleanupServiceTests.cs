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

    [Fact]
    public void Clean_MediumAndHigh_DegradeToLightForNow()
    {
        Assert.Equal("Hello", _sut.Clean("um hello", CleanupLevel.Medium));
        Assert.Equal("Hello", _sut.Clean("um hello", CleanupLevel.High));
    }
}
