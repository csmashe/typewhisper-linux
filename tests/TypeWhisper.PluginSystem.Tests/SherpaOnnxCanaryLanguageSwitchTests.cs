extern alias SherpaOnnx;

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Moq;
using OfflineRecognizer = SherpaOnnx.OfflineRecognizer;
using SherpaOnnx::TypeWhisper.Plugin.SherpaOnnx;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SherpaOnnxCanaryLanguageSwitchTests : IDisposable
{
    private const string CanaryModelId = "canary-180m-flash";

    private readonly string _root = Path.Join(
        Path.GetTempPath(),
        "tw-canary-switch-" + Guid.NewGuid().ToString("N")
    );

    public SherpaOnnxCanaryLanguageSwitchTests() => WriteCanaryModelFiles();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task FailedLanguageSwitch_KeepsTheWorkingRecognizer()
    {
        var plugin = CreatePlugin();
        var loaded = CreateStubRecognizer();
        var attempts = 0;
        bool? oldDisposedDuringCreate = null;
        plugin.SetCanaryRecognizerFactoryForTests((_, src, _, _) =>
        {
            if (src == "en")
                return loaded;
            attempts++;
            oldDisposedDuringCreate ??= IsDisposed(loaded);
            throw new InvalidOperationException("CUDA out of memory");
        });
        await plugin.LoadModelAsync(CanaryModelId, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plugin.TranscribeAsync(CreateWav(), "de", false, null, CancellationToken.None)
        );

        Assert.False(oldDisposedDuringCreate);
        Assert.False(IsDisposed(loaded));
        Assert.Equal(CanaryModelId, plugin.LoadedModelIdForTests);
        // The failed language was not recorded, so the next "de" request tries again.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            plugin.TranscribeAsync(CreateWav(), "de", false, null, CancellationToken.None)
        );
        Assert.Equal(2, attempts);
    }

    // Not disposed: that frees the stub through the native library, which the test host can't load.
    private SherpaOnnxPlugin CreatePlugin()
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.PluginDataDirectory).Returns(_root);
        host.Setup(h => h.PluginAssetDirectory).Returns(_root);
        var plugin = new SherpaOnnxPlugin();
        plugin.SetHostForTests(host.Object);
        plugin.SelectModel(CanaryModelId);
        return plugin;
    }

    // A recognizer with a null native handle.
    private static OfflineRecognizer CreateStubRecognizer()
    {
        var recognizer = (OfflineRecognizer)RuntimeHelpers.GetUninitializedObject(
            typeof(OfflineRecognizer)
        );
#pragma warning disable CA1816 // Its finalizer would call the native library, which the test host can't load.
        GC.SuppressFinalize(recognizer);
#pragma warning restore CA1816
        return recognizer;
    }

    // Dispose replaces the default handle with one wrapping the recognizer itself.
    private static bool IsDisposed(OfflineRecognizer recognizer)
    {
        var field = typeof(OfflineRecognizer).GetField(
            "_handle",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        return ((HandleRef)field.GetValue(recognizer)!).Wrapper is not null;
    }

    private void WriteCanaryModelFiles()
    {
        var directory = Path.Join(_root, "Models", CanaryModelId);
        Directory.CreateDirectory(directory);
        foreach (var fileName in new[] { "encoder.int8.onnx", "decoder.int8.onnx" })
            File.WriteAllBytes(Path.Join(directory, fileName), [0x08, 0x09, 0x3a, 0x02, 0x12, 0x00]);
        File.WriteAllText(Path.Join(directory, "tokens.txt"), "<unk> 0\n");
    }

    private static byte[] CreateWav(int sampleCount = 16_000)
    {
        var dataSize = sampleCount * sizeof(short);
        using var stream = new MemoryStream(44 + dataSize);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16_000);
        writer.Write(32_000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        writer.Write(new byte[dataSize]);
        writer.Flush();
        return stream.ToArray();
    }
}
