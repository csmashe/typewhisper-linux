extern alias SherpaOnnx;

using Moq;
using TypeWhisper.Plugin.GemmaLocal;
using TypeWhisper.Plugin.WhisperCpp;
using TypeWhisper.PluginSDK;
using SherpaOnnxPlugin = SherpaOnnx::TypeWhisper.Plugin.SherpaOnnx.SherpaOnnxPlugin;

namespace TypeWhisper.PluginSystem.Tests;

/// <summary>
///     Local model downloads refuse to start when the catalog size cannot fit, before
///     any network request (each probe below would otherwise hit the real host), and
///     never touch model files that are already on disk.
/// </summary>
public sealed class LocalModelDownloadSpaceTests : IDisposable
{
    private const long MiB = 1024 * 1024;
    private const long Reserve = 256 * MiB;
    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "tw-model-space-" + Guid.NewGuid().ToString("N")
    );

    public LocalModelDownloadSpaceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task WhisperDownload_InsufficientSpace_FailsBeforeRequestAndLeavesNoStaging()
    {
        using var plugin = new WhisperCppPlugin();
        await plugin.ActivateAsync(Host().Object);
        var probed = new List<string>();
        plugin.SpaceProbe = path =>
        {
            probed.Add(path);
            return 75 * MiB + Reserve - 1;
        };

        var ex = await DownloadSpaceAssert.ThrowsInsufficientSpaceAsync(() =>
            plugin.DownloadModelAsync("tiny", null, CancellationToken.None)
        );

        Assert.StartsWith("Not enough disk space for the Tiny model: 331.0 MB must be free in ", ex.Message);
        Assert.Equal([Path.Join(_root, "Models")], probed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Join(_root, "Models")));
        Assert.False(plugin.IsModelDownloaded("tiny"));
    }

    [Fact]
    public async Task GemmaDownload_InsufficientSpace_FailsBeforeRequest()
    {
        using var plugin = new GemmaLocalPlugin();
        await plugin.ActivateAsync(Host().Object);
        plugin.SpaceProbe = _ => 0;

        var ex = await DownloadSpaceAssert.ThrowsInsufficientSpaceAsync(() =>
            plugin.DownloadModelAsync("gemma4-e2b-it-q4", null, CancellationToken.None)
        );

        Assert.Contains("the Gemma 4 E2B (Q4_K_M) model", ex.Message);
        // Only the inter-process lock sentinel, which is never deleted.
        Assert.Equal(
            ["gemma-4-E2B-it-Q4_K_M.gguf.lock"],
            Directory.EnumerateFileSystemEntries(Path.Join(_root, "Models", "gemma4-e2b-it-q4"))
                .Select(Path.GetFileName)
        );
    }

    [Fact]
    public async Task SherpaDownload_CountsOnlyMissingFilesAndKeepsDownloadedOnes()
    {
        using var plugin = new SherpaOnnxPlugin();
        plugin.SetHostForTests(Host().Object);
        var modelDir = Path.Join(_root, "Models", "parakeet-tdt-0.6b");
        Directory.CreateDirectory(modelDir);
        var encoder = Path.Join(modelDir, "encoder.int8.onnx");
        await File.WriteAllBytesAsync(encoder, [1, 2, 3]);
        // The decoder, joiner and tokens are 12 + 6 + 1 MB; the 652 MB encoder is present.
        plugin.SpaceProbe = _ => 19 * MiB + Reserve - 1;

        var ex = await DownloadSpaceAssert.ThrowsInsufficientSpaceAsync(() =>
            plugin.DownloadModelAsync("parakeet-tdt-0.6b", null, CancellationToken.None)
        );

        Assert.StartsWith(
            $"Not enough disk space for the Parakeet TDT 0.6B model: 275.0 MB must be free in {modelDir} ",
            ex.Message
        );
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(encoder));
        Assert.Equal([encoder], Directory.GetFiles(modelDir));
    }

    private Mock<IPluginHostServices> Host()
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.PluginDataDirectory).Returns(_root);
        host.Setup(h => h.PluginAssetDirectory).Returns(_root);
        return host;
    }
}
