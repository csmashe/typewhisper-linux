using TypeWhisper.Plugin.FillerWords;

namespace TypeWhisper.PluginSystem.Tests;

/// <summary>Tests filler word filtering and plugin behavior.</summary>
public sealed class FillerWordFilterTests
{
    [Theory]
    [InlineData("So um I think uh this works", "So I think this works")]
    [InlineData("Well, um, that's it.", "Well, that's it.")]
    [InlineData("Hello,um, world", "Hello, world")]
    [InlineData("Hello!um world", "Hello! world")]
    [InlineData("I agree um; next point.", "I agree; next point.")]
    [InlineData("“Um, hello.”", "“hello.”")]
    [InlineData("(um, yes)", "(yes)")]
    [InlineData("so (um) yes", "so () yes")]
    [InlineData("“Um. Hello”", "“Hello”")]
    [InlineData("He said \"yes\" um and left.", "He said \"yes\" and left.")]
    [InlineData("\"Um, hello\"", "\"hello\"")]
    [InlineData("He said, “Um. Hello.”", "He said, “Hello.”")]
    [InlineData("I agree um uh; next point.", "I agree; next point.")]
    [InlineData("(um uh) fine", "() fine")]
    [InlineData("'Um, hello.'", "'hello.'")]
    [InlineData("don't um stop", "don't stop")]
    [InlineData("'hello um'", "'hello'")]
    [InlineData("Say um \"hello\"", "Say \"hello\"")]
    [InlineData("(Hello, um)", "(Hello)")]
    [InlineData("Er sagte: „Ähm. Hallo.“", "Er sagte: „Hallo.“")]
    [InlineData("Er sagte „ja“ ähm und ging.", "Er sagte „ja“ und ging.")]
    [InlineData("He said, “um\nhello”", "He said, “\nhello”")]
    [InlineData("Um, hello", "hello")]
    [InlineData("hmm let me check", "let me check")]
    [InlineData("Das ist ähm nicht gut", "Das ist nicht gut")]
    public void Remove_StripsLatinFillerWords(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input));

    [Theory]
    [InlineData("The umbrella is uhh open", "The umbrella is open")]
    [InlineData("A hum in the room", "A hum in the room")]
    [InlineData("Rahm and Graham", "Rahm and Graham")]
    public void Remove_KeepsWordsThatMerelyContainAFiller(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input));

    [Theory]
    [InlineData("Uh-huh, that is correct.", "Uh-huh, that is correct.")]
    [InlineData("Uh-uh.", "Uh-uh.")]
    [InlineData("Uh-oh, um, wait", "Uh-oh, wait")]
    [InlineData("Uh huh, that is correct.", "Uh huh, that is correct.")]
    [InlineData("uh oh, um, wait", "uh oh, wait")]
    [InlineData("Um oh, I forgot.", "oh, I forgot.")]
    [InlineData("Uh uh, I disagree.", "Uh uh, I disagree.")]
    [InlineData("Uh-uh uh I disagree.", "Uh-uh I disagree.")]
    [InlineData("Uh‑huh, yes.", "Uh‑huh, yes.")]
    [InlineData("He said 'uh oh'.", "He said 'uh oh'.")]
    [InlineData("He said ‘uh huh’ again", "He said ‘uh huh’ again")]
    [InlineData("hmm huh", "huh")]
    [InlineData("we um'd our way through", "we um'd our way through")]
    public void Remove_KeepsHyphenatedExpressionsBuiltFromFillers(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input));

    [Fact]
    public void Remove_ReturnsInputUnchanged_WhenNothingMatches() =>
        Assert.Equal("A clean sentence.", FillerWordFilter.Remove("A clean sentence."));

    [Fact]
    public void Remove_ReturnsInputUnchanged_WhenWordListIsEmpty() =>
        Assert.Equal("So um I think", FillerWordFilter.Remove("So um I think", []));

    [Fact]
    public void Remove_ReturnsInputUnchanged_WhenTextIsEmpty() =>
        Assert.Equal(string.Empty, FillerWordFilter.Remove(string.Empty));

    [Theory]
    [InlineData(" um hello", " hello")]
    [InlineData("  um hello", "  hello")]
    [InlineData("\tum hello", "\thello")]
    [InlineData(" hello um there", " hello there")]
    public void Remove_PreservesLeadingWhitespaceFromTheOriginal(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input));

    [Fact]
    public void Remove_DropsLeadingWhitespace_WhenEverythingElseWasFiller() =>
        Assert.Equal(string.Empty, FillerWordFilter.Remove(" um "));

    [Fact]
    public void Remove_StripsLeadingWhitespaceIntroducedByRemoval() =>
        Assert.Equal("hello", FillerWordFilter.Remove("um hello"));

    [Fact]
    public void Remove_StripsLineLeadingWhitespaceIntroducedByRemoval() =>
        Assert.Equal("hello\nthere", FillerWordFilter.Remove("hello\num there"));

    [Fact]
    public void Remove_PreservesLineStructure() =>
        Assert.Equal("first line\nsecond line", FillerWordFilter.Remove("first um line\nsecond uh line"));

    [Fact]
    public void Remove_StripsJapaneseFillerWordsWithTheirTrailingComma() =>
        Assert.Equal("これはテストです。", FillerWordFilter.Remove("えっと、これはテストです。"));

    [Fact]
    public void Remove_StripsJapaneseFillerWordsMidSentence() =>
        Assert.Equal("それは、いいですね", FillerWordFilter.Remove("それは、なんかいいですね"));

    [Theory]
    [InlineData("えっと、あのー、今日は晴れです。", "今日は晴れです。")]
    [InlineData("えっと あのー 今日は晴れです。", "今日は晴れです。")]
    [InlineData("それは、えっと、なんか、いいですね", "それは、いいですね")]
    [InlineData("えっと。今日は晴れです。", "今日は晴れです。")]
    [InlineData("うーん。", "")]
    [InlineData("はい、えっと。次です。", "はい。次です。")]
    [InlineData("「えっと、今日は晴れです。」", "「今日は晴れです。」")]
    [InlineData("「えっと。今日は晴れです。」", "「今日は晴れです。」")]
    [InlineData("彼は「えっと。はい」と言った", "彼は「はい」と言った")]
    [InlineData("はい、えっと", "はい")]
    [InlineData("「はい、えっと」", "「はい」")]
    [InlineData("はい：えっと。次です。", "はい。次です。")]
    [InlineData("「はい」えっと、次の質問です。", "「はい」次の質問です。")]
    [InlineData("はい—えっと、次です。", "はい—次です。")]
    [InlineData("えっと…あのー、今日は晴れです。", "今日は晴れです。")]
    [InlineData("えっと：あのー、今日は晴れです。", "今日は晴れです。")]
    [InlineData("えっとあのー今日は晴れです。", "今日は晴れです。")]
    [InlineData("えっと　今日は晴れです。", "今日は晴れです。")]
    [InlineData("それは　なんか　いいですね", "それは　いいですね")]
    public void Remove_StripsConsecutiveJapaneseFillers(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input));

    [Fact]
    public void Remove_KeepsRepeatedMaaDrawl() =>
        Assert.Equal("まあまあいいです", FillerWordFilter.Remove("まあまあいいです"));

    [Fact]
    public void Remove_HandlesMixedScripts() =>
        Assert.Equal("So これはテストです。", FillerWordFilter.Remove("So um えっと、これはテストです。"));

    [Fact]
    public void Remove_KeepsIndentationOnUnaffectedLines() =>
        Assert.Equal("if ready:\n    send()", FillerWordFilter.Remove("um\nif ready:\n    send()"));

    [Theory]
    [InlineData("hello\num\nthere", "hello\nthere")]
    [InlineData("hello\num", "hello")]
    [InlineData("hello\r\n  um uh\r\nthere", "hello\r\nthere")]
    public void Remove_DropsLinesThatHeldOnlyFillers(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input));

    [Theory]
    [InlineData("I agree um. Next topic.", "I agree. Next topic.")]
    [InlineData("Really uh? Yes.", "Really? Yes.")]
    [InlineData("Um. Hello", "Hello")]
    [InlineData("hello um", "hello")]
    [InlineData("hello um.", "hello.")]
    [InlineData("I agree, um", "I agree")]
    [InlineData("I agree um uh. Next topic.", "I agree. Next topic.")]
    [InlineData("Really um, uh? Yes.", "Really? Yes.")]
    [InlineData("Hello. Um. Next.", "Hello. Next.")]
    [InlineData("Hello, um.", "Hello.")]
    [InlineData("Wait, um? Yes.", "Wait? Yes.")]
    [InlineData("Done! Um.", "Done!")]
    [InlineData("Um... let me think", "let me think")]
    [InlineData("Uh?!", "")]
    [InlineData("I agree um... Next topic.", "I agree... Next topic.")]
    [InlineData("So um, uh... okay", "So... okay")]
    [InlineData("Um; let me think.", "let me think.")]
    [InlineData("(um; yes)", "(yes)")]
    [InlineData("I agree um: next point.", "I agree: next point.")]
    [InlineData("He said “yes.” Um. Then left.", "He said “yes.” Then left.")]
    [InlineData("He said—“Um. Hello.”", "He said—“Hello.”")]
    [InlineData("He said:\"Um, hello.\"", "He said:\"hello.\"")]
    [InlineData("I think—um—we should go.", "I think—we should go.")]
    [InlineData("I think — um — we should go.", "I think — we should go.")]
    [InlineData("I think um — we should go.", "I think — we should go.")]
    [InlineData("I think - um - we should go.", "I think - we should go.")]
    [InlineData("Er sagte: »Ähm. Hallo.«", "Er sagte: »Hallo.«")]
    [InlineData("Il a dit : « Um, bonjour. »", "Il a dit : « bonjour. »")]
    public void Remove_KeepsSentencePunctuationThatFollowsAFiller(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input));

    [Theory]
    [InlineData("So um uh I think", "So I think")]
    [InlineData("um uh hello", "hello")]
    [InlineData("hello um uh", "hello")]
    public void Remove_CollapsesConsecutiveFillersToOneSeparator(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input));

    [Fact]
    public void Remove_DistinctWordListsDoNotShareAMatcher()
    {
        Assert.Equal("I this", FillerWordFilter.Remove("I like this", ["you know", "like"]));
        Assert.Equal("I like this", FillerWordFilter.Remove("I like this", ["you know like"]));
    }

    [Fact]
    public void NormalizeWords_SplitsOnNewlinesCommasAndSemicolons()
    {
        var words = FillerWordFilter.NormalizeWords("um, uh;\r\nlike\n");

        Assert.Equal(["like", "uh", "um"], words);
    }

    [Fact]
    public void NormalizeWords_LowerCasesTrimsAndDeduplicates()
    {
        var words = FillerWordFilter.NormalizeWords("  Um \n um\nUH");

        Assert.Equal(["uh", "um"], words);
    }

    [Fact]
    public void NormalizeWords_OrdersLongestFirst()
    {
        var words = FillerWordFilter.NormalizeWords("um\nummm\numm");

        Assert.Equal(["ummm", "umm", "um"], words);
    }

    [Fact]
    public void Remove_PrefersTheLongestMatchingFiller() =>
        Assert.Equal("well then", FillerWordFilter.Remove("well umm then", ["um", "umm"]));

    [Fact]
    public void ParseWordList_ScopesLinesByLanguagePrefix()
    {
        var list = FillerWordFilter.ParseWordList("meh, welp\nde: äh; ähm\nen-GB: erm\nnot a tag: keep");

        // "not a tag: keep" is no language line, so it stays one unscoped entry.
        Assert.Equal(["not a tag: keep", "welp", "meh"], list.WordsFor(null));
        Assert.Equal(["not a tag: keep", "welp", "meh", "ähm", "äh"], list.WordsFor("de-CH"));
        Assert.Equal(["not a tag: keep", "welp", "erm", "meh"], list.WordsFor("en"));
        Assert.Equal(6, list.AllWords.Count);
    }

    [Fact]
    public void DefaultWordsFor_ScopesUmToEnglish()
    {
        Assert.Contains("um", FillerWordFilter.DefaultWordsFor("en-US"));
        Assert.DoesNotContain("um", FillerWordFilter.DefaultWordsFor("de"));
        Assert.Contains("ähm", FillerWordFilter.DefaultWordsFor("de"));
        Assert.Contains("えっと", FillerWordFilter.DefaultWordsFor("ja"));
        Assert.Empty(FillerWordFilter.DefaultWordsFor(null));
    }

    [Fact]
    public void DefaultWordsText_RoundTripsThroughTheParser() =>
        Assert.Equal(
            FillerWordFilter.DefaultFillerWords,
            FillerWordFilter.ParseWordList(FillerWordFilter.DefaultWordsText).AllWords);

    [Fact]
    public void DefaultFillerWords_AreAllNormalized() =>
        Assert.Equal(
            FillerWordFilter.DefaultFillerWords.Count,
            FillerWordFilter.NormalizeWords(FillerWordFilter.DefaultFillerWords).Count);
}
