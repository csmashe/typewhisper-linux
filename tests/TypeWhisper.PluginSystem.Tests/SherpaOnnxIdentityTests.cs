extern alias SherpaOnnx;
using TypeWhisper.Linux.Services.Plugins;
using SherpaOnnxPlugin = SherpaOnnx::TypeWhisper.Plugin.SherpaOnnx.SherpaOnnxPlugin;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SherpaOnnxIdentityTests
{
    [Fact]
    public void ProviderDisplayName_WithoutACatalog_IsEnglish()
    {
        using var plugin = new SherpaOnnxPlugin();
        Assert.Equal("Local (sherpa-onnx)", plugin.ProviderDisplayName);
    }

    [Fact]
    public void ProviderDisplayName_FollowsTheInterfaceLanguageAndKeepsTheEngineName()
    {
        using var plugin = new SherpaOnnxPlugin();
        plugin.SetLocalization(new PluginLocalization(
            Path.Join(PluginCatalogTestData.PluginsDirectory, "TypeWhisper.Plugin.SherpaOnnx"), "de"));

        Assert.Equal("Lokal (sherpa-onnx)", plugin.ProviderDisplayName);
    }
}
