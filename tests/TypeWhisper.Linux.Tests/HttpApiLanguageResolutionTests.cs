using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Linux.Tests;

/// <summary>Covers how an API transcription request combines its language, its hints and the global settings.</summary>
public sealed class HttpApiLanguageResolutionTests
{
    private static readonly AppSettings s_globalGerman = AppSettings.Default.WithLanguageHints(["de", "fr"]);

    [Fact]
    public void ExplicitLanguage_IsSelectedAndLeadsTheHints()
    {
        var (selection, hints) = HttpApiService.ResolveRequestLanguage("de-de", ["en"], s_globalGerman);

        Assert.Equal("de-DE", selection.LanguageTag);
        Assert.Equal(["de-de", "en"], hints);
    }

    [Fact]
    public void AutoWithHints_KeepsAutomaticDetectionAndTheHints()
    {
        var (selection, hints) = HttpApiService.ResolveRequestLanguage("auto", ["de", "en"], s_globalGerman);

        Assert.True(selection.IsAutomatic);
        Assert.Equal(["de", "en"], hints);
    }

    [Fact]
    public void HintsOnly_KeepTheGlobalSelection()
    {
        var (selection, hints) = HttpApiService.ResolveRequestLanguage(null, ["it", "en"], s_globalGerman);

        Assert.Equal("de", selection.LanguageTag);
        Assert.Equal(["it", "en"], hints);
    }

    [Fact]
    public void HintsOnly_UnderGlobalAuto_StayAutomatic()
    {
        var (selection, hints) = HttpApiService.ResolveRequestLanguage(null, ["de"], AppSettings.Default);

        Assert.True(selection.IsAutomatic);
        Assert.Equal(["de"], hints);
    }

    [Fact]
    public void Nothing_UsesTheGlobalHints()
    {
        var (selection, hints) = HttpApiService.ResolveRequestLanguage(null, [], s_globalGerman);

        Assert.Equal("de", selection.LanguageTag);
        Assert.Equal(["de", "fr"], hints);
    }
}
