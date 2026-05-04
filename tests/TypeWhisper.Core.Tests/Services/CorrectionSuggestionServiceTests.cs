using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class CorrectionSuggestionServiceTests
{
    private readonly CorrectionSuggestionService _sut = new();

    [Fact]
    public void GenerateSuggestions_ReturnsPhraseCorrectionForSmallEdit()
    {
        var result = _sut.GenerateSuggestions(
            "I deployed to kubernets today",
            "I deployed to Kubernetes today");

        var suggestion = Assert.Single(result);
        Assert.Equal("kubernets", suggestion.Original);
        Assert.Equal("Kubernetes", suggestion.Replacement);
        Assert.True(suggestion.Confidence > 0);
    }

    [Fact]
    public void GenerateSuggestions_ReturnsMultiWordCorrection()
    {
        var result = _sut.GenerateSuggestions(
            "open type whisper settings now",
            "open TypeWhisper settings now");

        var suggestion = Assert.Single(result);
        Assert.Equal("type whisper", suggestion.Original);
        Assert.Equal("TypeWhisper", suggestion.Replacement);
    }

    [Fact]
    public void GenerateSuggestions_DoesNotSuggestLargeRewrite()
    {
        var result = _sut.GenerateSuggestions(
            "this is a rough draft for tomorrow",
            "please send a concise status update instead");

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("its working now", "it's working now")]
    [InlineData("the teams project is ready", "the team's project is ready")]
    public void GenerateSuggestions_DoesNotAutoSuggestContractionsOrPossessives(string inserted, string corrected)
    {
        var result = _sut.GenerateSuggestions(inserted, corrected);

        Assert.Empty(result);
    }

    [Fact]
    public void GenerateSuggestions_DoesNotSuggestWhenOnlyPunctuationChanged()
    {
        var result = _sut.GenerateSuggestions(
            "hello world",
            "hello, world");

        Assert.Empty(result);
    }
}
