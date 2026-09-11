using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>Covers when dictation may reuse streamed text instead of running the batch call.</summary>
public sealed class DictationOrchestratorStreamingReuseTests
{
    private static readonly LanguageSelection s_german = LanguageSelection.Explicit("de");

    private static bool Reuse(
        IReadOnlyList<string> streamingHints,
        IReadOnlyList<string> hints,
        bool engineSupportsLanguageHints = false,
        string? translationTarget = null,
        bool translate = false,
        string? text = "Hallo Welt",
        bool faulted = false,
        bool engineMatches = true,
        LanguageSelection? selection = null
    ) =>
        DictationOrchestrator.CanReuseStreamingText(
            text,
            faulted,
            engineMatches,
            s_german,
            streamingHints,
            selection ?? s_german,
            hints,
            translate,
            engineSupportsLanguageHints,
            translationTarget
        );

    [Fact]
    public void Reuses_WhenEngineSelectionAndHintsMatch() =>
        Assert.True(Reuse(["de", "en"], ["de", "en"]));

    [Fact]
    public void FallsBackToBatch_WhenHintsChangedButPrimaryDidNot() =>
        Assert.False(Reuse(["de", "en"], ["de", "fr"]));

    [Fact]
    public void HintComparison_IgnoresCase() =>
        Assert.True(Reuse(["de", "EN"], ["DE", "en"]));

    [Fact]
    public void FallsBackToBatch_ForNativeMultiHintStreamWithTranslationTarget() =>
        Assert.False(Reuse(["de", "en"], ["de", "en"], engineSupportsLanguageHints: true, translationTarget: "de"));

    [Fact]
    public void Reuses_NativeMultiHintStream_WithoutTranslationTarget() =>
        Assert.True(Reuse(["de", "en"], ["de", "en"], engineSupportsLanguageHints: true));

    [Fact]
    public void Reuses_WithTranslationTarget_WhenEngineOnlyStreamedThePrimary() =>
        Assert.True(Reuse(["de", "en"], ["de", "en"], translationTarget: "de"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FallsBackToBatch_WithoutStreamedText(string? text) =>
        Assert.False(Reuse(["de"], ["de"], text: text));

    [Fact]
    public void FallsBackToBatch_WhenFaultedOrEngineChangedOrTranslating()
    {
        Assert.False(Reuse(["de"], ["de"], faulted: true));
        Assert.False(Reuse(["de"], ["de"], engineMatches: false));
        Assert.False(Reuse(["de"], ["de"], translate: true));
        Assert.False(Reuse(["de"], ["de"], selection: LanguageSelection.Automatic));
    }

    [Theory]
    [InlineData(true, 2, true)]
    [InlineData(true, 1, false)]
    [InlineData(false, 2, false)]
    public void StreamedSeveralLanguages_RequiresNativeHintsAndMoreThanOne(bool native, int count, bool expected) =>
        Assert.Equal(expected, DictationOrchestrator.StreamedSeveralLanguages(native, count));
}
