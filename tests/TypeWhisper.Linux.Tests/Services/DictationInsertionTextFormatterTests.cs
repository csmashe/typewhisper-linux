using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests.Services;

public class DictationInsertionTextFormatterTests
{
    [Fact]
    public void TextForInsertion_AppendsTrailingSpace_WhenTextEndsWithWord()
    {
        Assert.Equal("hello world ", DictationInsertionTextFormatter.TextForInsertion("hello world"));
    }

    [Fact]
    public void TextForInsertion_AppendsTrailingSpace_WhenTextEndsWithPunctuation()
    {
        Assert.Equal("Done. ", DictationInsertionTextFormatter.TextForInsertion("Done."));
    }

    [Theory]
    [InlineData("hello ")]
    [InlineData("hello\n")]
    [InlineData("hello\t")]
    public void TextForInsertion_LeavesTextUnchanged_WhenAlreadyEndsWithWhitespace(string text)
    {
        Assert.Equal(text, DictationInsertionTextFormatter.TextForInsertion(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void TextForInsertion_ReturnsInput_WhenNullOrEmpty(string? text)
    {
        Assert.Equal(text, DictationInsertionTextFormatter.TextForInsertion(text!));
    }
}
