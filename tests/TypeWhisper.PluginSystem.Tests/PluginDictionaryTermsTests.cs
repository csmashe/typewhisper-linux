using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class PluginDictionaryTermsTests
{
    [Fact]
    public void Clip_NormalizesAndAppliesPerTermFiltersBeforeTermLimit()
    {
        var result = PluginDictionaryTerms.Clip(
            [" ", "TOOLONG", "A B C", "Beta", "beta", "Gamma"],
            new DictionaryTermsBudget(MaxTerms: 2, MaxCharsPerTerm: 5, MaxWordsPerTerm: 2));

        Assert.Equal(["Beta", "Gamma"], result);
    }

    [Fact]
    public void CreatePrompt_AppliesTotalCharacterBudgetToJoinedPrompt()
    {
        var result = PluginDictionaryTerms.CreatePrompt(
            ["AA", "BB", "CC"],
            new DictionaryTermsBudget(MaxTotalChars: 6));

        Assert.Equal("AA, BB", result);
    }

    [Fact]
    public void CreatePrompt_ReturnsNullWhenBudgetRejectsEveryTerm()
    {
        var result = PluginDictionaryTerms.CreatePrompt(
            ["Alpha", "Beta"],
            new DictionaryTermsBudget(MaxTerms: -1));

        Assert.Null(result);
    }

    [Fact]
    public void Clip_NullBudgetAppliesNoLimits()
    {
        string[] terms = [new('A', 600), "B"];
        Assert.Equal(terms, PluginDictionaryTerms.Clip(terms, null));
    }

    [Fact]
    public void CreatePrompt_UsesConservativeDefaultBudget()
    {
        var result = PluginDictionaryTerms.CreatePrompt([new string('A', 600), "B"]);

        Assert.Equal(new string('A', 600), result);
    }
}
