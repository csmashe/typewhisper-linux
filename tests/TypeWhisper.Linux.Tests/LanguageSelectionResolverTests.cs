using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class LanguageSelectionResolverTests
{
    [Fact]
    public void Resolve_BlankOverride_UsesNextPrecedenceValue()
    {
        var selection = LanguageSelectionResolver.Resolve("   ", "de-de");

        Assert.False(selection.IsAutomatic);
        Assert.Equal("de-DE", selection.LanguageTag);
    }

    [Fact]
    public void Resolve_ExplicitAutomaticOverride_WinsOverFallback()
    {
        var selection = LanguageSelectionResolver.Resolve(" AUTO ", "de-DE");

        Assert.Same(LanguageSelection.Automatic, selection);
    }

    [Fact]
    public void Resolve_AllBlank_DefaultsToAutomatic()
    {
        Assert.Same(
            LanguageSelection.Automatic,
            LanguageSelectionResolver.Resolve(null, "", "  ")
        );
    }

    [Fact]
    public void Resolve_InvalidWinningValue_ThrowsWithoutTryingLowerPrecedence()
    {
        var exception = Assert.Throws<InvalidLanguageSelectionException>(
            () => LanguageSelectionResolver.Resolve("notalang", "de-DE")
        );

        Assert.Equal("notalang", exception.RawValue);
    }

    [Fact]
    public void ResolveOrAutomatic_InvalidValue_DegradesToAutomatic()
    {
        Assert.Same(
            LanguageSelection.Automatic,
            LanguageSelectionResolver.ResolveOrAutomatic("notalang", "de-DE")
        );
    }

    [Fact]
    public void ResolveOrAutomatic_ValidValue_MatchesResolve()
    {
        Assert.Equal(
            LanguageSelectionResolver.Resolve(null, "de-DE"),
            LanguageSelectionResolver.ResolveOrAutomatic(null, "de-DE")
        );
    }
}
