using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

/// <summary>Covers <see cref="ShortUtterancePunctuationService" />: short-utterance punctuation removal and preservation.</summary>
public class ShortUtterancePunctuationServiceTests
{
    [Theory]
    [InlineData("Guten Morgen.", "Guten Morgen")]
    [InlineData("Guten Morgen!", "Guten Morgen")]
    [InlineData("Hallo, Marco.", "Hallo Marco")]
    [InlineData("Hallo Marco?", "Hallo Marco")]
    [InlineData("Hallo, Marco ?", "Hallo Marco")]
    [InlineData("Hallo,", "Hallo")]
    [InlineData("Thanks.", "Thanks")]
    [InlineData("Hello, John!", "Hello John")]
    [InlineData("नमस्ते दुनिया!", "नमस्ते दुनिया")]
    public void NormalizeText_Disabled_RemovesModelPunctuation(string input, string expected)
    {
        Assert.Equal(expected, ShortUtterancePunctuationService.NormalizeText(input, punctuationEnabled: false));
    }

    [Theory]
    [InlineData("Yes, please", "Yes, please")]
    [InlineData("Wait, what?", "Wait, what")]
    public void NormalizeText_Disabled_PreservesNonGreetingComma(string input, string expected)
    {
        Assert.Equal(expected, ShortUtterancePunctuationService.NormalizeText(input, punctuationEnabled: false));
    }

    [Theory]
    [InlineData("¿Cómo estás?", "Cómo estás")]
    [InlineData("¡Hola!", "Hola")]
    [InlineData("¡Hola, Marco!", "Hola Marco")]
    public void NormalizeText_Disabled_RemovesPairedInvertedPunctuation(string input, string expected)
    {
        Assert.Equal(expected, ShortUtterancePunctuationService.NormalizeText(input, punctuationEnabled: false));
    }

    [Theory]
    [InlineData("你好。", "你好")]
    [InlineData("はい。", "はい")]
    public void NormalizeText_Disabled_StripsShortUnspacedScriptUtterance(string input, string expected)
    {
        Assert.Equal(expected, ShortUtterancePunctuationService.NormalizeText(input, punctuationEnabled: false));
    }

    [Theory]
    [InlineData("我们明天上午一起去公园散步。")]
    [InlineData("明日は公園に行きましょう。")]
    [InlineData("안녕하세요 오늘 날씨가 좋네요.")]
    public void NormalizeText_Disabled_PreservesLongerUnspacedScriptSentence(string input)
    {
        Assert.Equal(input, ShortUtterancePunctuationService.NormalizeText(input, punctuationEnabled: false));
    }

    [Fact]
    public void NormalizeText_Disabled_PreservesLongerSentence()
    {
        const string input = "Heute besprechen wir die nächsten Schritte.";
        Assert.Equal(input, ShortUtterancePunctuationService.NormalizeText(input, punctuationEnabled: false));
    }

    [Fact]
    public void NormalizeText_Enabled_PreservesModelPunctuation()
    {
        const string input = "Hallo, Marco.";
        Assert.Equal(input, ShortUtterancePunctuationService.NormalizeText(input, punctuationEnabled: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeText_BlankInput_ReturnsInput(string input)
    {
        Assert.Equal(input, ShortUtterancePunctuationService.NormalizeText(input, punctuationEnabled: false));
    }
}
