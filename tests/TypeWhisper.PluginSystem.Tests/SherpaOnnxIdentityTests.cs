extern alias SherpaOnnx;
using SherpaOnnxPlugin = SherpaOnnx::TypeWhisper.Plugin.SherpaOnnx.SherpaOnnxPlugin;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SherpaOnnxIdentityTests
{
    [Fact]
    public void ProviderDisplayName_IsEnglishLikeEveryOtherProviderName()
    {
        // The model picker and welcome wizard show this name in every UI language.
        using var plugin = new SherpaOnnxPlugin();
        Assert.Equal("Local (sherpa-onnx)", plugin.ProviderDisplayName);
    }
}
