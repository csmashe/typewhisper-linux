using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.SpokenFormatting;

namespace TypeWhisper.Core.Tests.Services;

public class SpokenFormattingServiceTests
{
    private readonly SpokenFormattingRulesLoader _rulesLoader = new();
    private readonly SpokenFormattingService _sut;

    public SpokenFormattingServiceTests()
    {
        _sut = new SpokenFormattingService(_rulesLoader);
    }

    [Theory]
    [InlineData("name tab open quote value close quote", "name\t“value”")]
    [InlineData("name tab open bracket value close bracket", "name\t(value)")]
    [InlineData("name tab comma value", "name\t, value")]
    [InlineData("name\topen quote value close quote", "name\t“value”")]
    [InlineData("name\topen bracket value close bracket", "name\t(value)")]
    [InlineData("name\tcomma value", "name\t, value")]
    [InlineData("name new line comma\n    value", "name\n,\n    value")]
    [InlineData("name\r\ncomma\r\n\t    value", "name\r\n,\r\n\t    value")]
    public void Normalize_PreservesStructuralWhitespaceAroundCommands(string input, string expected)
    {
        Assert.Equal(expected, _sut.Normalize(input, "en"));
    }

    [Theory]
    [InlineData("hello comma new line comma world", "hello,\n, world")]
    [InlineData("a comma tab comma b", "a,\t, b")]
    [InlineData("question new line mark", "question\nmark")]
    [InlineData("done period new paragraph period next", "done.\n\n. next")]
    [InlineData("hello comma , world", "hello, world")]
    [InlineData("(hello period )", "(hello.)")]
    [InlineData("he said “yes period ”", "he said “yes.”")]
    [InlineData("hello comma, world", "hello, world")]
    [InlineData("hello, comma world", "hello, world")]
    [InlineData("end period . Next", "end. Next")]
    public void Normalize_DuplicatePunctuationPreservesExistingMarkSpacing(string input, string expected)
    {
        Assert.Equal(expected, _sut.Normalize(input, "en"));
    }

    [Theory]
    [InlineData("de", "Hallo Komma Welt", "Hallo, Welt")]
    [InlineData("de", "Wie geht es dir Fragezeichen", "Wie geht es dir?")]
    [InlineData("de", "Titel Doppelpunkt Beispiel", "Titel: Beispiel")]
    [InlineData("de", "Hallo offene Klammer Test geschlossene Klammer", "Hallo (Test)")]
    [InlineData("en", "Hello comma world", "Hello, world")]
    [InlineData("en", "How are you question mark", "How are you?")]
    [InlineData("en", "Title colon example", "Title: example")]
    [InlineData("en", "Hello open bracket test close bracket", "Hello (test)")]
    public void Normalize_ReplacesVisiblePunctuationCommands(
        string language,
        string input,
        string expected)
    {
        var result = _sut.Normalize(input, language);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("de", "Hallo neue Zeile Welt", "Hallo\nWelt")]
    [InlineData("de", "Hallo neuer Absatz Welt", "Hallo\n\nWelt")]
    [InlineData("de", "Name Tabulator Wert", "Name\tWert")]
    [InlineData("en", "Hello new line world", "Hello\nworld")]
    [InlineData("en", "Hello new paragraph world", "Hello\n\nworld")]
    [InlineData("en", "Name tab value", "Name\tvalue")]
    public void Normalize_ReplacesStructuralCommands(
        string language,
        string input,
        string expected)
    {
        var result = _sut.Normalize(input, language);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Neue Zeile.", "\n")]
    [InlineData("New paragraph!", "\n\n")]
    public void Normalize_StructuralCommand_RemovesDirectlyAttachedAsrPunctuation(string input, string expected)
    {
        var language = input.StartsWith("New", StringComparison.Ordinal) ? "en" : "de";

        var result = _sut.Normalize(input, language);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("de", "Hallo, neue Zeile, Welt.", "Hallo\nWelt.")]
    [InlineData("de", "Erster Absatz, neuer Absatz, zweiter Absatz.", "Erster Absatz\n\nzweiter Absatz.")]
    [InlineData("de", "Name, Tabulator, Wert.", "Name\tWert.")]
    [InlineData("en", "Hello, new line, world.", "Hello\nworld.")]
    public void Normalize_StructuralCommand_RemovesPairedAsrSeparatorCommas(
        string language,
        string input,
        string expected)
    {
        var result = _sut.Normalize(input, language);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Normalize_StructuralCommand_PreservesMeaningfulPunctuationBeforeCommand()
    {
        Assert.Equal(
            "Wie geht es dir?\nGut.",
            _sut.Normalize(
                "Wie geht es dir? neue Zeile, Gut.",
                "de"));

        Assert.Equal(
            "Hallo,\nWelt.",
            _sut.Normalize(
                "Hallo, neue Zeile. Welt.",
                "de"));
    }

    [Fact]
    public void Normalize_StructuralCommand_PreservesExplicitSpokenPunctuation()
    {
        var result = _sut.Normalize(
            "Hallo neue Zeile Punkt",
            "de");

        Assert.Equal("Hallo\n.", result);
    }

    [Fact]
    public void Normalize_CommandOnlyParagraph_PreservesTrailingLineBreaks()
    {
        var result = _sut.Normalize(
            "neuer Absatz",
            "de");

        Assert.Equal("\n\n", result);
    }

    [Fact]
    public void Normalize_WithoutVisibleCommand_ReturnsExactInput()
    {
        const string input = "  Native output, with  spacing.\r\n";

        var result = _sut.Normalize(input, "en");

        Assert.Same(input, result);
    }

    [Fact]
    public void Normalize_FallbackWithoutVisibleCommand_ReturnsExactInput()
    {
        string[] inputs = ["Hello  ,  world", "if ready:\n    run()", "../src", "a ?? b", "wait...",
            "  native  text \t :;!! ..\r\n    indented  ", "« hola »", "„ hallo “"];
        foreach (var input in inputs)
            Assert.Same(input, _sut.Normalize(input, "en"));
    }

    [Theory]
    [InlineData("Hallo Komma, Welt", "Hallo, Welt")]
    [InlineData("Hello question mark?", "Hello?")]
    public void Normalize_DuplicateNativePunctuation_IsSuppressed(string input, string expected)
    {
        var language = input.StartsWith("Hello", StringComparison.Ordinal) ? "en" : "de";

        var result = _sut.Normalize(input, language);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Normalize_UnsupportedLanguage_ReturnsExactInput()
    {
        const string input = "Bonjour virgule monde";

        var result = _sut.Normalize(input, "fr-FR");

        Assert.Same(input, result);
    }

    [Theory]
    [InlineData("de-DE", "de")]
    [InlineData("en_US", "en")]
    [InlineData("  DE  ", "de")]
    [InlineData("auto", null)]
    [InlineData("", null)]
    public void LanguageNormalizer_ReturnsPrimarySupportedCode(string input, string? expected)
    {
        Assert.Equal(expected, SpokenFormattingLanguageNormalizer.Normalize(input));
    }

    [Fact]
    public void RulesLoader_LoadsGermanAndEnglishVerificationScenarios()
    {
        Assert.NotEmpty(_rulesLoader.RuleSetFor("de-DE")!.VerificationScenarios);
        Assert.NotEmpty(_rulesLoader.RuleSetFor("en-US")!.VerificationScenarios);
        Assert.Null(_rulesLoader.RuleSetFor("fr"));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ru")]
    public void Normalize_AppliesEveryEmbeddedRule(string language)
    {
        var rules = _rulesLoader.RuleSetFor(language)!.Rules;

        foreach (var rule in rules)
        {
            Assert.Equal(
                rule.Replacement,
                _sut.Normalize(rule.Phrase, language));
        }
    }

    [Fact]
    public void Normalize_IsCaseInsensitiveAndUsesUnicodeWordBoundaries()
    {
        Assert.Equal(
            "Hallo, WELT",
            _sut.Normalize("Hallo KOMMA WELT", "de"));
        Assert.Equal(
            "äkommaß",
            _sut.Normalize("äkommaß", "de"));
        Assert.Equal(
            "a\u0301komma",
            _sut.Normalize("a\u0301komma", "de"));
        Assert.Equal(
            "(,)",
            _sut.Normalize("(Komma)", "de"));
    }

    [Theory]
    [InlineData("Ende neue Zeile", "Ende\n")]
    [InlineData("Ende neuer Absatz", "Ende\n\n")]
    [InlineData("Ende Tabulator", "Ende\t")]
    public void Normalize_PreservesTrailingStructuralWhitespace(string input, string expected)
    {
        Assert.Equal(
            expected,
            _sut.Normalize(input, "de"));
    }

    [Fact]
    public void Normalize_AppliesLocalBracketSpacingAndSuppressesRepeatedCommands()
    {
        Assert.Equal(
            "Hallo (Test).",
            _sut.Normalize(
                "Hallo offene Klammer Test geschlossene Klammer Punkt",
                "de"));
        Assert.Equal(
            "Hello, world",
            _sut.Normalize("Hello, comma world", "en"));
        Assert.Equal(
            "Hallo.",
            _sut.Normalize("Hallo Punkt Punkt", "de"));
        Assert.Equal(
            "Wait...",
            _sut.Normalize("Wait...", "en"));
    }

    [Fact]
    public void RulesLoader_ReturnsTheSameCachedRuleSet()
    {
        Assert.Same(_rulesLoader.RuleSetFor("de"), _rulesLoader.RuleSetFor("de-DE"));
        Assert.Same(_rulesLoader.RuleSetFor("en"), _rulesLoader.RuleSetFor("en_US"));
        Assert.Same(_rulesLoader.RuleSetFor("de"), new SpokenFormattingRulesLoader().RuleSetFor("de"));
    }
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("ru")]
    public void Normalize_AllVerificationScenariosRoundTrip(string language)
    {
        foreach (var scenario in _rulesLoader.RuleSetFor(language)!.VerificationScenarios)
            Assert.Equal(scenario.Expected, _sut.Normalize(scenario.Spoken, language));
    }

    [Theory]
    [InlineData("en", "she said open quote hello close quote", "she said “hello”")]
    [InlineData("en", "wow exclamation point", "wow!")]
    [InlineData("de", "sie sagt anführungszeichen auf hallo anführungszeichen zu", "sie sagt „hallo“")]
    [InlineData("es", "abrir comillas hola cerrar comillas", "«hola»")]
    [InlineData("ru", "открыть кавычки привет закрыть кавычки", "«привет»")]
    [InlineData("es", "« hola »", "« hola »")]
    [InlineData("ru", "« привет »", "« привет »")]
    public void Normalize_QuotesAndGuillemets(string language, string input, string expected)
    {
        Assert.Equal(expected, _sut.Normalize(input, language));
    }
    [Theory]
    [InlineData("first new line     indented", "first\nindented")]
    [InlineData("hello new line\n    indented", "hello\n\n    indented")]
    [InlineData("hello new line \r\n\t    indented", "hello\n\r\n\t    indented")]
    [InlineData("../src  a ?? b  wait... comma next", "../src  a ?? b  wait..., next")]
    [InlineData("if ready:\n    run() new line next", "if ready:\n    run()\nnext")]
    [InlineData("hello, comma world", "hello, world")]
    [InlineData("hello comma, world", "hello, world")]
    [InlineData("hello、 comma world", "hello、 world")]
    [InlineData("hello comma、 world", "hello、 world")]
    [InlineData("hello comma \n    world", "hello,\n    world")]
    [InlineData("hello comma   ", "hello,")]
    [InlineData("hello comma comma world", "hello, world")]
    [InlineData("say open parenthesis hello", "say (hello")]
    [InlineData("hello close parenthesis world", "hello) world")]
    [InlineData("hello close quote .", "hello”.")]
    [InlineData("( open quote hello", "(“hello")]
    [InlineData("she said open quote hello close quote comma then left", "she said “hello”, then left")]
    [InlineData("hello close quote ]", "hello”]")]
    [InlineData("« open quote hello", "«“hello")]
    public void Normalize_OnlyChangesSpacingAndPunctuationAtCommands(string input, string expected)
    {
        Assert.Equal(expected, _sut.Normalize(input, "en"));
    }

    [Theory]
    [InlineData("en", "say  open quote  hello  close quote  world", "say “hello” world")]
    [InlineData("de", "sage  anführungszeichen auf  hallo  anführungszeichen zu  welt", "sage „hallo“ welt")]
    [InlineData("es", "di  abrir comillas  hola  cerrar comillas  mundo", "di «hola» mundo")]
    [InlineData("ru", "скажи  открыть кавычки  привет  закрыть кавычки  мир", "скажи «привет» мир")]
    [InlineData("en", "\n  open quote hello close quote", "\n“hello”")]
    public void Normalize_QuotePlacementControlsLocalSpacing(string language, string input, string expected)
    {
        Assert.Equal(expected, _sut.Normalize(input, language));
    }

    [Theory]
    [InlineData("(", SpokenFormattingRulePlacement.Open)]
    [InlineData("[", SpokenFormattingRulePlacement.Open)]
    [InlineData("{", SpokenFormattingRulePlacement.Open)]
    [InlineData("“", SpokenFormattingRulePlacement.Open)]
    [InlineData("„", SpokenFormattingRulePlacement.Open)]
    [InlineData("«", SpokenFormattingRulePlacement.Open)]
    [InlineData("‹", SpokenFormattingRulePlacement.Open)]
    [InlineData(")", SpokenFormattingRulePlacement.Close)]
    [InlineData("]", SpokenFormattingRulePlacement.Close)]
    [InlineData("}", SpokenFormattingRulePlacement.Close)]
    [InlineData("”", SpokenFormattingRulePlacement.Close)]
    [InlineData("»", SpokenFormattingRulePlacement.Close)]
    [InlineData("›", SpokenFormattingRulePlacement.Close)]
    [InlineData(".", SpokenFormattingRulePlacement.None)]
    public void RulePlacement_IsInferredWhenAbsent(string replacement, SpokenFormattingRulePlacement expected)
    {
        Assert.Equal(expected, new SpokenFormattingRule { Replacement = replacement }.Placement);
    }

    [Fact]
    public void RulePlacement_GermanClosingQuoteOverridesInference()
    {
        var rule = Assert.Single(_rulesLoader.RuleSetFor("de")!.Rules,
            rule => rule.Phrase == "anführungszeichen zu");
        Assert.Equal("“", rule.Replacement);
        Assert.Equal(SpokenFormattingRulePlacement.Close, rule.Placement);
    }

}
