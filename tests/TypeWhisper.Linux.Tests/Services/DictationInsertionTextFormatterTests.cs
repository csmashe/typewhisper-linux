using TypeWhisper.Core.Services;
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

    [Fact]
    public void TextForInsertion_WithContext_FitsTheTextInsteadOfPadding()
    {
        Assert.Equal(" next one", DictationInsertionTextFormatter.TextForInsertion(
            "Next one.", new DictationInsertionContext("I said", " here")));
    }

    [Fact]
    public void TextForInsertion_WithContext_PassesTheStandaloneSettingAndLanguage()
    {
        var emptyField = new DictationInsertionContext("", "");
        Assert.Equal("v1.2.3", DictationInsertionTextFormatter.TextForInsertion("v1.2.3.", emptyField));
        Assert.Equal("v1.2.3. ", DictationInsertionTextFormatter.TextForInsertion("v1.2.3.", emptyField, false));
        Assert.Equal(" Häuser", DictationInsertionTextFormatter.TextForInsertion(
            "Häuser.", new DictationInsertionContext("Ich sehe", " dort"), language: "de"));
    }

    [Fact]
    public void TextForInsertion_WithContext_KeepsWhitespaceOnlyDictations()
    {
        Assert.Equal("  ", DictationInsertionTextFormatter.TextForInsertion("  ", new DictationInsertionContext("a", "")));
    }

    [Fact]
    public void TextForInsertion_AtTheEndOfAField_KeepsTheTrailingSpaceForAnUnreadableNextDictation()
    {
        var first = DictationInsertionTextFormatter.TextForInsertion("Hello.", new DictationInsertionContext("Hi.", ""));
        var field = "Hi." + first + DictationInsertionTextFormatter.TextForInsertion("World.");
        Assert.Equal("Hi. Hello. World. ", field);
        var emptyField = new DictationInsertionContext("", "");
        Assert.Equal("Hello. ", DictationInsertionTextFormatter.TextForInsertion("Hello.", emptyField));
        Assert.Equal("name@example.com", DictationInsertionTextFormatter.TextForInsertion("name@example.com.", emptyField));
    }
}
