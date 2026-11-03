extern alias SherpaOnnx;

using Moq;
using SherpaOnnx::TypeWhisper.Plugin.SherpaOnnx;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SherpaOnnxUnloadTests
{
    [Fact]
    public async Task UnloadModelAsync_PreservesSelectionAndRejectsTranscription()
    {
        using var plugin = new SherpaOnnxPlugin();
        plugin.SetHostForTests(CreateHost(Path.GetTempPath()).Object);
        plugin.SelectModel("parakeet-tdt-0.6b");

        await plugin.UnloadModelAsync();

        Assert.Equal("parakeet-tdt-0.6b", plugin.SelectedModelId);
        Assert.Null(plugin.LoadedModelIdForTests);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plugin.TranscribeAsync(CreateWav(), "en", false, null, CancellationToken.None));
    }

    [Fact]
    public async Task UnloadModelAsync_IsIdempotent()
    {
        using var plugin = new SherpaOnnxPlugin();
        plugin.SetHostForTests(CreateHost(Path.GetTempPath()).Object);
        plugin.SelectModel("parakeet-tdt-0.6b");

        await plugin.UnloadModelAsync();
        await plugin.UnloadModelAsync();

        Assert.Equal("parakeet-tdt-0.6b", plugin.SelectedModelId);
        Assert.Null(plugin.LoadedModelIdForTests);
    }

    private static Mock<IPluginHostServices> CreateHost(string assetDirectory)
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.PluginDataDirectory).Returns(assetDirectory);
        host.Setup(h => h.PluginAssetDirectory).Returns(assetDirectory);
        return host;
    }

    private static byte[] CreateWav()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(38);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(2);
        writer.Write((short)0);
        return stream.ToArray();
    }
}
