using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.SpokenFormatting;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationOrchestratorSpokenFormattingTests
{
    [Theory]
    [InlineData(SpokenFormattingStrategy.NativeOnly, true)]
    [InlineData(SpokenFormattingStrategy.Automatic, true)]
    [InlineData(SpokenFormattingStrategy.FallbackOnly, false)]
    public void CreateSpokenFormatter_RequiresFallbackAndRules(SpokenFormattingStrategy strategy, bool available)
    {
        var service = new SpokenFormattingService(new SpokenFormattingRulesLoader());
        Assert.Null(DictationOrchestrator.CreateSpokenFormatter(service,
            new ResolvedSpokenFormattingStrategy("en", strategy, null, available)));
        Assert.Null(DictationOrchestrator.CreateSpokenFormatter(service, null));
    }

    [Fact]
    public void CreateSpokenFormatter_FullFallbackNormalizesOnlyCommands()
    {
        var formatter = DictationOrchestrator.CreateSpokenFormatter(
            new SpokenFormattingService(new SpokenFormattingRulesLoader()),
            new ResolvedSpokenFormattingStrategy("es", SpokenFormattingStrategy.FallbackOnly, null, true));
        Assert.NotNull(formatter);
        Assert.Equal("hola, mundo", formatter("hola coma mundo"));
        Assert.Equal("hola  , mundo", formatter("hola  , mundo"));
    }
    [Theory]
    [InlineData(true, true, "en", "hello, world")]
    [InlineData(false, true, "de", "hello comma world")]
    [InlineData(true, false, "de", "hello comma world")]
    public void ResolveSpokenFormattingStrategy_UsesTranslatedTranscriptLanguage(
        bool translateRequested, bool engineSupportsTranslation, string expectedLanguage, string expectedText)
    {
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings());
        var loader = new SpokenFormattingRulesLoader();
        var resolver = new SpokenFormattingStrategyResolver(new SpokenFormattingProfileStore(settings.Object), loader);
        var language = DictationOrchestrator.ResolvePostProcessingSourceLanguage(
            "en", "de", translateRequested, engineSupportsTranslation);

        var strategy = DictationOrchestrator.ResolveSpokenFormattingStrategy(
            resolver, "engine", "model", ["de"], language,
            translateRequested && engineSupportsTranslation, SpokenFormattingStrategy.FallbackOnly);

        Assert.Equal(expectedLanguage, strategy.LanguageCode);
        var formatter = DictationOrchestrator.CreateSpokenFormatter(new SpokenFormattingService(loader), strategy);
        Assert.NotNull(formatter);
        Assert.Equal(expectedText, formatter("hello comma world"));
    }

}
