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
    [InlineData("“Um, hello.”", "“Hello.”")]
    [InlineData("(um, yes)", "(yes)")]
    [InlineData("so (um) yes", "so () yes")]
    [InlineData("“Um. Hello”", "“Hello”")]
    [InlineData("He said \"yes\" um and left.", "He said \"yes\" and left.")]
    [InlineData("\"Um, hello\"", "\"Hello\"")]
    [InlineData("He said, “Um. Hello.”", "He said, “Hello.”")]
    [InlineData("I agree um uh; next point.", "I agree; next point.")]
    [InlineData("(um uh) fine", "() fine")]
    [InlineData("'Um, hello.'", "'Hello.'")]
    [InlineData("don't um stop", "don't stop")]
    [InlineData("'hello um'", "'hello'")]
    [InlineData("Say um \"hello\"", "Say \"hello\"")]
    [InlineData("(Hello, um)", "(Hello)")]
    [InlineData("Er sagte: „Ähm. Hallo.“", "Er sagte: „Hallo.“")]
    [InlineData("Er sagte „ja“ ähm und ging.", "Er sagte „ja“ und ging.")]
    [InlineData("He said, “um\nhello”", "He said, “\nhello”")]
    [InlineData("Um, hello", "Hello")]
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
    [InlineData("Um oh, I forgot.", "Oh, I forgot.")]
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
    [InlineData("Um... let me think", "Let me think")]
    [InlineData("Uh?!", "")]
    [InlineData("I agree um... Next topic.", "I agree... Next topic.")]
    [InlineData("So um, uh... okay", "So... okay")]
    [InlineData("Um; let me think.", "Let me think.")]
    [InlineData("(um; yes)", "(yes)")]
    [InlineData("I agree um: next point.", "I agree: next point.")]
    [InlineData("He said “yes.” Um. Then left.", "He said “yes.” Then left.")]
    [InlineData("He said—“Um. Hello.”", "He said—“Hello.”")]
    [InlineData("He said:\"Um, hello.\"", "He said:\"Hello.\"")]
    [InlineData("I think—um—we should go.", "I think—we should go.")]
    [InlineData("I think — um — we should go.", "I think — we should go.")]
    [InlineData("I think um — we should go.", "I think — we should go.")]
    [InlineData("I think - um - we should go.", "I think - we should go.")]
    [InlineData("Er sagte: »Ähm. Hallo.«", "Er sagte: »Hallo.«")]
    [InlineData("Il a dit : « Um, bonjour. »", "Il a dit : « Bonjour. »")]
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
        Assert.Contains("ehm", FillerWordFilter.DefaultWordsFor("es-MX"));
        Assert.Contains("э-э", FillerWordFilter.DefaultWordsFor("ru"));
        Assert.DoesNotContain("um", FillerWordFilter.DefaultWordsFor("ru"));
        Assert.Empty(FillerWordFilter.DefaultWordsFor(null));
    }

    [Theory]
    [InlineData("es", "Bueno, eh, vamos", "Bueno, vamos")]
    [InlineData("es", "Este libro es mío", "Este libro es mío")]
    [InlineData("ru", "Я, э-э, думаю, что да", "Я, думаю, что да")]
    [InlineData("ru", "Это гм пять миллиметров", "Это пять миллиметров")]
    [InlineData("ru", "Ну, 5 мм хватит", "Ну, 5 мм хватит")]
    public void Remove_StripsLanguageScopedDefaults(string language, string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input, FillerWordFilter.DefaultWordsFor(language)));

    [Theory]
    [InlineData("Gut. Äh, ich komme", "Gut. Ich komme")]
    [InlineData("Done! Uh, next one", "Done! Next one")]
    [InlineData("first line\nUm, second line", "first line\nSecond line")]
    [InlineData("He said “yes.” Um, then left.", "He said “yes.” Then left.")]
    [InlineData("Um, uh, hello", "Hello")]
    [InlineData("Um, あのー、hello", "Hello")]
    [InlineData("Um, あのー。hello", "Hello")]
    [InlineData("Um, あのー uh, hello", "Hello")]
    [InlineData("Uh, émile is here", "Émile is here")]
    [InlineData("Uh, e\u0301mile is here", "E\u0301mile is here")]
    [InlineData("Um, don't stop", "Don't stop")]
    [InlineData("Э, привет", "Привет")]
    [InlineData("{Um, hello}", "{Hello}")]
    [InlineData("（Um, hello）", "（Hello）")]
    [InlineData("„Äh, hallo“", "„Hallo“")]
    [InlineData("Um, \U00010428\U00010429", "\U00010400\U00010429")]
    [InlineData("He said, \"Um. Hello\"", "He said, \"Hello\"")]
    [InlineData("He said \"hi,\" then \"Um, bye\"", "He said \"hi,\" then \"Bye\"")]
    [InlineData("He is 6'2\". He said, \"Um, hello\"", "He is 6'2\". He said, \"Hello\"")]
    public void Remove_HandsASentenceOpeningFillersCapitalToTheNextWord(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input));

    [Theory]
    [InlineData("um, hello there", "hello there")]
    [InlineData("I think Um we go", "I think we go")]
    [InlineData("So, Um, we go", "So, we go")]
    [InlineData("Um, @jdoe said hi", "@jdoe said hi")]
    [InlineData("Um, iPhone sales", "iPhone sales")]
    [InlineData("Um, example.com is down", "example.com is down")]
    [InlineData("Um, 5 people came", "5 people came")]
    [InlineData("Hello. Um\nand then", "Hello.\nand then")]
    [InlineData("Um\nhello", "hello")]
    [InlineData("Um, uh\nhello", "hello")]
    [InlineData("Um,\u00a0\nhello", "\u00a0\nhello")]
    [InlineData("Um,\u2028hello", "\u2028hello")]
    [InlineData("Um, a\U00010400", "a\U00010400")]
    [InlineData("He said \"hello,\" Um, friend", "He said \"hello,\" friend")]
    [InlineData("„Um, あのーhello“", "„あのーhello“")]
    public void Remove_KeepsCasingWhenNoCapitalIsOwed(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input));

    [Fact]
    public void Remove_KeepsCasingAfterAJapanesePhraseTheLatinPassBreaks() =>
        Assert.Equal("あの hello", FillerWordFilter.Remove("Um, あの uh hello", ["um", "uh", "あの uh"]));

    [Theory]
    [InlineData("Il a dit « bonjour » Euh, puis il est parti.", "Il a dit « bonjour » puis il est parti.")]
    [InlineData("Il a dit « Euh, bonjour. »", "Il a dit « Bonjour. »")]
    [InlineData("« Bonjour. » Euh, reprenons.", "« Bonjour. » Reprenons.")]
    [InlineData("Er sagte »Euh, hallo.«", "Er sagte »Hallo.«")]
    public void Remove_PairsGuillemetsToFindSentenceStarts(string input, string expected) =>
        Assert.Equal(expected, FillerWordFilter.Remove(input, ["euh"]));

    [Theory]
    [InlineData("I I I think so", "I think so")]
    [InlineData("ich, ich, ich komme", "ich komme")]
    [InlineData("the the the THE end", "the end")]
    [InlineData("I'm I'm I'm here", "I'm here")]
    [InlineData("we we we we we go", "we go")]
    [InlineData("'I I I think so'", "'I think so'")]
    [InlineData("‘go go go’", "‘go’")]
    [InlineData("cafe\u0301 cafe\u0301 cafe\u0301 noir", "cafe\u0301 noir")]
    public void Collapse_ShortensThreeOrMoreRepetitionsToOne(string input, string expected) =>
        Assert.Equal(expected, RepeatedWordCollapser.Collapse(input));

    [Theory]
    [InlineData("very very good")]
    [InlineData("I I I'm fine")]
    [InlineData("don't don't don'tcha")]
    [InlineData("o'go go go")]
    [InlineData("test test test-case")]
    [InlineData("no. no. no.")]
    [InlineData("go\ngo\ngo")]
    [InlineData("1 1 1")]
    [InlineData("then then thence then")]
    [InlineData("example example example.com")]
    [InlineData("example.com example example")]
    [InlineData("zero point zero zero zero five")]
    [InlineData("call nine nine nine now")]
    [InlineData("null, null, null, sieben")]
    [InlineData("null komma fuenf fuenf fuenf")]
    [InlineData("fünf fünf fünf")]
    [InlineData("fu\u0308nf fu\u0308nf fu\u0308nf")]
    [InlineData("कर कर करो")]
    [InlineData("cafe cafe cafe\u0301")]
    [InlineData("")]
    public void Collapse_KeepsRepetitionsThatAreNotAStutter(string input) =>
        Assert.Equal(input, RepeatedWordCollapser.Collapse(input));

    [Theory]
    [InlineData(null, "I think that this is what we want", "en")]
    [InlineData("", "Ich glaube, dass das nicht gut ist", "de")]
    [InlineData("auto", "Yo creo que esto es muy bueno y también hay más", "es")]
    [InlineData(null, "Я думаю, что это очень хорошо", "ru")]
    [InlineData(null, "えっと、これはテストです", "ja")]
    [InlineData(null, "Je pense que c'est très bien pour nous", "fr")]
    [InlineData(null, "hello there friend", null)]
    [InlineData(null, "I think that A・B・C・D is what we want", "en")]
    [InlineData(null, "", null)]
    [InlineData("de-DE", "I think that this is what we want", "de-DE")]
    [InlineData("en", "Ich glaube, dass das nicht gut ist", "en")]
    public void ResolveLanguage_PrefersTheReportedLanguageAndOtherwiseGuessesStrictly(
        string? reported, string text, string? expected) =>
        Assert.Equal(expected, FillerWordFilter.ResolveLanguage(reported, text));

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
