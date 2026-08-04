using TypeWhisper.Core.Services.NumberNormalization;

namespace TypeWhisper.Core.Tests.Services;

// fr/zh/ja parsers are intentionally not ported on Linux (locales are en/de/es/ru),
// so the upstream French/Chinese/Japanese cases are omitted here.
public class NumberWordNormalizerTests
{
    [Fact]
    public void Normalize_EnglishSimpleNumber_ReturnsDigits()
    {
        var result = NumberWordNormalizer.Normalize("I have two questions", "en");

        Assert.Equal("I have 2 questions", result);
    }

    [Fact]
    public void Normalize_GermanSimpleNumber_ReturnsDigits()
    {
        var result = NumberWordNormalizer.Normalize("ich habe zwei Fragen", "de");

        Assert.Equal("ich habe 2 Fragen", result);
    }

    [Fact]
    public void Normalize_EnglishCompoundNumber_ReturnsDigits()
    {
        var result = NumberWordNormalizer.Normalize("twenty three files", "en-US");

        Assert.Equal("23 files", result);
    }

    [Fact]
    public void Normalize_GermanCompoundNumber_ReturnsDigits()
    {
        var result = NumberWordNormalizer.Normalize("dreiundzwanzig Dateien", "de_DE");

        Assert.Equal("23 Dateien", result);
    }

    [Fact]
    public void Normalize_EnglishScaleNumber_ReturnsDigits()
    {
        var result = NumberWordNormalizer.Normalize("one thousand two hundred thirty four", "en");

        Assert.Equal("1234", result);
    }

    [Fact]
    public void Normalize_GermanScaleNumber_ReturnsDigits()
    {
        var result = NumberWordNormalizer.Normalize("eintausendzweihundertvierunddreißig", "de");

        Assert.Equal("1234", result);
    }

    [Fact]
    public void Normalize_EnglishNegativeDecimal_ReturnsDotDecimal()
    {
        var result = NumberWordNormalizer.Normalize("minus two point five", "en");

        Assert.Equal("-2.5", result);
    }

    [Fact]
    public void Normalize_EnglishAndSeparator_DoesNotMergeIndependentNumbers()
    {
        Assert.Equal("2 and 3", NumberWordNormalizer.Normalize("two and three", "en"));
        Assert.Equal(
            "between 2 and 3 minutes",
            NumberWordNormalizer.Normalize("between two and three minutes", "en"));
    }

    [Fact]
    public void Normalize_EnglishHundredAndScale_ConsumesAnd()
    {
        Assert.Equal("123", NumberWordNormalizer.Normalize("one hundred and twenty three", "en"));
        Assert.Equal("1005", NumberWordNormalizer.Normalize("one thousand and five", "en"));
    }

    [Fact]
    public void Normalize_GermanNegativeDecimal_ReturnsCommaDecimal()
    {
        var result = NumberWordNormalizer.Normalize("minus zwei komma fünf", "de");

        Assert.Equal("-2,5", result);
    }

    [Fact]
    public void Normalize_SpanishNumbers_ReturnDigits()
    {
        Assert.Equal("tengo 2 preguntas", NumberWordNormalizer.Normalize("tengo dos preguntas", "es"));
        Assert.Equal("23 archivos", NumberWordNormalizer.Normalize("veintitrés archivos", "es"));
        Assert.Equal("23 archivos", NumberWordNormalizer.Normalize("veinte y tres archivos", "es"));
        Assert.Equal("1234", NumberWordNormalizer.Normalize("mil doscientos treinta y cuatro", "es"));
        Assert.Equal("-2,5", NumberWordNormalizer.Normalize("menos dos coma cinco", "es"));
    }

    [Fact]
    public void Normalize_SpanishArticleOne_IsPreservedOutsideClearNumberConstructs()
    {
        Assert.Equal("tengo un problema", NumberWordNormalizer.Normalize("tengo un problema", "es"));
        Assert.Equal("1000000 de filas", NumberWordNormalizer.Normalize("un millón de filas", "es"));
    }

    [Fact]
    public void Normalize_ScaleWordFollowingDigit_IsNotNormalized()
    {
        // The digit already carries the count; treating the trailing scale word as a standalone
        // number would corrupt already-digit text (e.g. "2 mil" -> "2 1000"). The digit and its
        // trailing whitespace share one Other token, so FollowsDigit must scan past that space.
        Assert.Equal("2 mil", NumberWordNormalizer.Normalize("2 mil", "es"));
        Assert.Equal("2 millones", NumberWordNormalizer.Normalize("2 millones", "es"));
        Assert.Equal("2 tausend", NumberWordNormalizer.Normalize("2 tausend", "de"));
        Assert.Equal("2 millionen", NumberWordNormalizer.Normalize("2 millionen", "de"));
    }

    [Fact]
    public void Normalize_SpanishStandaloneUno_ReturnsDigit()
    {
        Assert.Equal("1", NumberWordNormalizer.Normalize("uno", "es"));
        Assert.Equal("tengo 1", NumberWordNormalizer.Normalize("tengo uno", "es"));
    }

    [Fact]
    public void Normalize_SpanishArticleForms_KeepArticleBehavior()
    {
        // "un"/"una" double as the indefinite article and must not convert in ordinary prose,
        // yet still normalize inside clear number constructs (e.g. "un millón").
        Assert.Equal("tengo un coche", NumberWordNormalizer.Normalize("tengo un coche", "es"));
        Assert.Equal("tengo una casa", NumberWordNormalizer.Normalize("tengo una casa", "es"));
        Assert.Equal("1000000 de filas", NumberWordNormalizer.Normalize("un millón de filas", "es"));
    }

    [Fact]
    public void Normalize_SpanishMenosArticleOne_IsNotReadAsNegativeOne()
    {
        // "menos" is also the preposition "except/less", so it is not a number context that
        // licenses the articles "un"/"una"; only the bare numeral "uno" converts after it.
        Assert.Equal("todos menos un estudiante", NumberWordNormalizer.Normalize("todos menos un estudiante", "es"));
        Assert.Equal("todos menos una persona", NumberWordNormalizer.Normalize("todos menos una persona", "es"));
        Assert.Equal("-1000000", NumberWordNormalizer.Normalize("menos un millón", "es"));
    }

    [Fact]
    public void Normalize_BareScaleNoun_IsNotTreatedAsNumber()
    {
        // Singular "millón"/"Million" without a leading count is a noun, not a number.
        Assert.Equal("medio millón de filas", NumberWordNormalizer.Normalize("medio millón de filas", "es"));
        Assert.Equal("eine halbe Million Zeilen", NumberWordNormalizer.Normalize("eine halbe Million Zeilen", "de"));
        Assert.Equal("1000000 Zeilen", NumberWordNormalizer.Normalize("eine Million Zeilen", "de"));
    }

    [Fact]
    public void Normalize_UnsupportedLanguage_IsNoOp()
    {
        var result = NumberWordNormalizer.Normalize("twenty three", "it");

        Assert.Equal("twenty three", result);
    }

    [Fact]
    public void Normalize_AlreadyDigitText_IsNoOp()
    {
        var result = NumberWordNormalizer.Normalize("I have 23 files", "en");

        Assert.Equal("I have 23 files", result);
    }

    [Fact]
    public void Normalize_GermanArticleOne_IsPreservedOutsideClearNumberConstructs()
    {
        Assert.Equal("ich habe ein Problem", NumberWordNormalizer.Normalize("ich habe ein Problem", "de"));
        Assert.Equal("100 Euro", NumberWordNormalizer.Normalize("ein hundert Euro", "de"));
    }
}
