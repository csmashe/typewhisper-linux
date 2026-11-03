using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Plugin.WhisperCpp;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class WhisperCppLanguageTests
{
    [Fact]
    public void EveryModel_DeclaresAsManyLanguagesAsItsCatalogCount()
    {
        using var plugin = new WhisperCppPlugin();

        foreach (var model in plugin.TranscriptionModels)
        {
            plugin.SelectModel(model.Id);

            if (model.LanguageCount == 1)
            {
                Assert.Empty(plugin.SupportedLanguages);
                continue;
            }

            Assert.Equal(model.LanguageCount, plugin.SupportedLanguages.Count);
            Assert.Equal(
                plugin.SupportedLanguages.Count,
                plugin.SupportedLanguages.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            );
            Assert.Equal(model.Id.StartsWith("large-v3", StringComparison.Ordinal),
                plugin.SupportedLanguages.Contains("yue"));
        }
    }

    [Theory]
    [InlineData("large-v3", "yue", "yue")]
    [InlineData("large-v3-turbo-q5_0", "yue", "yue")]
    [InlineData("small", "de-DE", "de")]
    [InlineData("tiny.en", "de", "de")]
    public void ExplicitSelection_PassesHostValidation(string modelId, string tag, string expected)
    {
        using var plugin = new WhisperCppPlugin();
        plugin.SelectModel(modelId);
        Assert.True(LanguageSelection.TryParse(tag, out var selection));

        Assert.Equal(expected, plugin.ToLegacyLanguage(selection));
    }

    [Fact]
    public void ExplicitCantonese_IsRejectedByModelsBeforeLargeV3()
    {
        using var plugin = new WhisperCppPlugin();
        plugin.SelectModel("medium");
        Assert.True(LanguageSelection.TryParse("yue", out var selection));

        Assert.Throws<TranscriptionLanguageNotSupportedException>(() => plugin.ToLegacyLanguage(selection));
    }

    [Fact]
    public void NoSelectedModel_DeclaresEveryWhisperLanguage()
    {
        using var plugin = new WhisperCppPlugin();

        Assert.Equal(99, plugin.SupportedLanguages.Count);
    }

    [Fact]
    public void EveryWhisperLanguage_HasAPickerName()
    {
        using var plugin = new WhisperCppPlugin();
        plugin.SelectModel("large-v3");

        Assert.All(plugin.SupportedLanguages, code =>
            Assert.NotNull(DictationSectionViewModel.GetLanguageDisplayName(code)));
    }
}
