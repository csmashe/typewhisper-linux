using TypeWhisper.Plugin.WhisperCpp;

namespace TypeWhisper.PluginSystem.Tests;

public partial class WhisperCppPluginTests
{
    // Covers every catalog model.
    [Theory]
    [InlineData("tiny", true)]
    [InlineData("tiny.en", false)]
    [InlineData("tiny-q5_0", true)]
    [InlineData("base", true)]
    [InlineData("base.en", false)]
    [InlineData("base-q5_0", true)]
    [InlineData("small", true)]
    [InlineData("small.en", false)]
    [InlineData("small-q5_0", true)]
    [InlineData("medium", true)]
    [InlineData("medium.en", false)]
    [InlineData("medium-q5_0", true)]
    [InlineData("large-v3-turbo", false)]
    [InlineData("large-v3-turbo-q5_0", false)]
    [InlineData("large-v3", true)]
    [InlineData("large-v3-q5_0", true)]
    public void TranslationRequiresTranslationTrainedWeights(string modelId, bool supported)
    {
        using var plugin = new WhisperCppPlugin();
        plugin.SelectModel(modelId);

        Assert.Equal(supported, plugin.SupportsTranslation);
        Assert.Equal(supported, plugin.TranscriptionModels.Single(model => model.Id == modelId).SupportsTranslation);
    }

    [Fact]
    public void EveryCatalogModelDeclaresTranslation()
    {
        using var plugin = new WhisperCppPlugin();

        Assert.All(plugin.TranscriptionModels, model => Assert.NotNull(model.SupportsTranslation));
    }

    [Fact]
    public void SupportsTranslation_WithoutASelectedModel_IsFalse()
    {
        using var plugin = new WhisperCppPlugin();

        Assert.False(plugin.SupportsTranslation);
    }

    [Theory]
    [InlineData("tiny.en")]
    [InlineData("large-v3-turbo")]
    [InlineData("large-v3-turbo-q5_0")]
    public async Task UnsupportedTranslationFailsBeforeLoadingModel(string modelId)
    {
        using var plugin = new WhisperCppPlugin();
        plugin.SelectModel(modelId);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            plugin.TranscribeAsync(new byte[44], "de", true, null, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            plugin.TranscribeAsync([], "de", true, null, CancellationToken.None));
    }

    [Theory]
    [InlineData("tiny", true)]
    [InlineData("tiny.en", false)]
    [InlineData("large-v3-turbo", false)]
    public async Task SupportedTaskReachesTheLoadCheck(string modelId, bool translate)
    {
        using var plugin = new WhisperCppPlugin();
        plugin.SelectModel(modelId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plugin.TranscribeAsync(new byte[44], null, translate, null, CancellationToken.None));
        Assert.Contains("No model loaded", ex.Message, StringComparison.Ordinal);
    }
}
