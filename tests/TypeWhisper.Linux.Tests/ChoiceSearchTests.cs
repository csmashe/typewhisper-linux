using System.Globalization;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Controls;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ChoiceSearchTests
{
    private static readonly IReadOnlyList<SpokenLanguageOption> s_languages =
        DictationSectionViewModel.CreateLanguageChoices(["de", "en", "es", "fr", "ja", "ru", "uk", "ar", "hy", "pt", "tr"]);

    [Fact]
    public void EmptyQuery_KeepsEveryChoiceInOrder() =>
        Assert.Equal(s_languages, ChoiceSearch.Filter(s_languages, "  ").Cast<SpokenLanguageOption>());

    [Theory]
    [InlineData("ger", "de")] // English name of Deutsch
    [InlineData("DE", "de")] // code
    [InlineData("japan", "ja")] // English name of 日本語
    [InlineData("deu", "de")] // shown native name
    [InlineData("espanol", "es")] // accents ignored
    public void FindsALanguageByItsShownNameCodeOrEnglishName(string query, string code)
    {
        using var _ = new UiCulture("en");
        var matches = ChoiceSearch.Filter(s_languages, query).Cast<SpokenLanguageOption>().ToList();
        Assert.Equal(code, matches.First().Code);
    }

    [Fact]
    public void FindsALanguageByItsNameInTheInterfaceLanguage()
    {
        using var _ = new UiCulture("de");
        // Neither "French" nor "Français" starts with "Franz"; the German "Französisch" does.
        var matches = ChoiceSearch.Filter(s_languages, "Franz").Cast<SpokenLanguageOption>().ToList();
        Assert.Equal("fr", Assert.Single(matches).Code);
    }

    [Fact]
    public void RanksNamePrefixThenCodeThenWordStartThenInsideAWord()
    {
        string[] items = ["Armenian", "Ukrainian (en)", "Enlarged", "Old English"];
        Assert.Equal(["Enlarged", "Ukrainian (en)", "Old English", "Armenian"], ChoiceSearch.Filter(items, "en").Cast<string>());
    }

    [Fact]
    public void CodePrefixComesAfterANameAndBeforeAWordStart()
    {
        Choice[] items = [new("Old Bulgarian", "cu"), new("Belarusian", "bul"), new("Bulgarian", "bg")];
        Assert.Equal(["Bulgarian", "Belarusian", "Old Bulgarian"],
            ChoiceSearch.Filter(items, "bul").Cast<Choice>().Select(choice => choice.Label));
    }

    [Fact]
    public void FindsATranslationTargetByItsEnglishName()
    {
        using var _ = new UiCulture("en");
        TranslationTargetOption[] targets = [new("", "No translation"), new("de", "Deutsch"), new("ja", "日本語")];
        Assert.Equal("de", ((TranslationTargetOption)ChoiceSearch.Filter(targets, "ger").Single()).Code);
        Assert.Equal("ja", ((TranslationTargetOption)ChoiceSearch.Filter(targets, "ja").Single()).Code);
    }

    [Fact]
    public void NoMatch_ReturnsNothing() =>
        Assert.Empty(ChoiceSearch.Filter(s_languages, "zzzz"));

    [Fact]
    public void UnknownLanguageCode_HasNoAliases() =>
        Assert.Empty(ChoiceSearch.LanguageAliases("qq-not-a-language"));

    private sealed record Choice(string Label, string Code) : ISearchableChoice
    {
        public string SearchLabel => Label;
        public string SearchCode => Code;
        public IEnumerable<string> SearchAliases => [];
    }

    private sealed class UiCulture : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentUICulture;

        public UiCulture(string name) => CultureInfo.CurrentUICulture = new CultureInfo(name);

        public void Dispose() => CultureInfo.CurrentUICulture = _previous;
    }
}
