using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

/// <summary>Covers <see cref="CorrectionSuggestionService" />: which inserted-vs-corrected edits become suggestions and which are deliberately ignored.</summary>
public sealed class CorrectionSuggestionServiceTests
{
    [Fact]
    public void GenerateSuggestions_ReturnsPhraseCorrectionForSmallEdit()
    {
        var result = CorrectionSuggestionService.GenerateSuggestions(
            "I deployed to kubernets today",
            "I deployed to Kubernetes today"
        );

        var suggestion = Assert.Single(result);
        Assert.Equal("kubernets", suggestion.Original);
        Assert.Equal("Kubernetes", suggestion.Replacement);
        Assert.True(suggestion.Confidence > 0);
    }

    [Fact]
    public void GenerateSuggestions_ReturnsMultiWordCorrection()
    {
        var result = CorrectionSuggestionService.GenerateSuggestions(
            "open type whisper settings now",
            "open TypeWhisper settings now"
        );

        var suggestion = Assert.Single(result);
        Assert.Equal("type whisper", suggestion.Original);
        Assert.Equal("TypeWhisper", suggestion.Replacement);
    }

    [Fact]
    public void GenerateSuggestions_IsolatesEditedWordWithinUnchangedSurroundingText()
    {
        // Mirrors the target-app learning flow: baseline is the whole field text right
        // after insertion; final is the same field after the user types over one word.
        // The common prefix/suffix trim must isolate just the corrected word even though
        // most of the surrounding sentence is unchanged.
        var result = CorrectionSuggestionService.GenerateSuggestions(
            "Please email jon about the kubernets migration next week",
            "Please email jon about the Kubernetes migration next week"
        );

        var suggestion = Assert.Single(result);
        Assert.Equal("kubernets", suggestion.Original);
        Assert.Equal("Kubernetes", suggestion.Replacement);
    }

    [Fact]
    public void GenerateSuggestions_DoesNotSuggestLargeRewrite()
    {
        var result = CorrectionSuggestionService.GenerateSuggestions(
            "this is a rough draft for tomorrow",
            "please send a concise status update instead"
        );

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("its working now", "it's working now")]
    [InlineData("the teams project is ready", "the team's project is ready")]
    public void GenerateSuggestions_DoesNotAutoSuggestContractionsOrPossessives(
        string inserted,
        string corrected
    )
    {
        var result = CorrectionSuggestionService.GenerateSuggestions(inserted, corrected);

        Assert.Empty(result);
    }

    [Fact]
    public void GenerateSuggestions_DoesNotSuggestWhenOnlyPunctuationChanged()
    {
        var result = CorrectionSuggestionService.GenerateSuggestions("hello world", "hello, world");

        Assert.Empty(result);
    }

    [Fact]
    public void GenerateSuggestions_DoesNotSuggestWhenChangedTokenEmbedsNewline()
    {
        // Tokenize only splits on spaces, so a word adjacent to a line break gloms into one
        // token ("update\nplease"); a correction embedding a newline is never a sensible
        // word-level fix and must be rejected.
        var result = CorrectionSuggestionService.GenerateSuggestions(
            "send the status update please",
            "send the status update\nplease"
        );

        Assert.Empty(result);
    }
}