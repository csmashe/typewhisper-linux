using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class LocalModelStorageServiceTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Join(Path.GetTempPath(), $"tw-storage-test-{Guid.NewGuid():N}");

    public LocalModelStorageServiceTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeLocalModelStoragePath_BlankBecomesNull(string? value)
    {
        Assert.Null(AppSettings.NormalizeLocalModelStoragePath(value));
    }

    [Fact]
    public void NormalizeLocalModelStoragePath_TrimsWhitespace()
    {
        Assert.Equal("/data/models", AppSettings.NormalizeLocalModelStoragePath("  /data/models  "));
    }

    [Fact]
    public void ResolvePluginAssetDirectory_NoCustomPath_UsesPluginDataPath()
    {
        var settings = new AppSettings { LocalModelStoragePath = null };

        var resolved = LocalModelStoragePaths.ResolvePluginAssetDirectory(settings, "com.typewhisper.whisper-cpp");

        Assert.Equal(
            Path.Join(TypeWhisperEnvironment.PluginDataPath, "com.typewhisper.whisper-cpp"),
            resolved);
    }

    [Fact]
    public void ResolvePluginAssetDirectory_CustomPath_NestsUnderPluginDataFolder()
    {
        var custom = Path.Join(_tempRoot, "models");
        var settings = new AppSettings { LocalModelStoragePath = custom };

        var resolved = LocalModelStoragePaths.ResolvePluginAssetDirectory(settings, "com.typewhisper.whisper-cpp");

        Assert.Equal(
            Path.Join(Path.GetFullPath(custom), LocalModelStoragePaths.PluginDataFolderName, "com.typewhisper.whisper-cpp"),
            resolved);
    }

    [Fact]
    public async Task MoveDownloadsAndUsePathAsync_MigratesAssetsAndSavesPath()
    {
        var source = Path.Join(_tempRoot, "source");
        var target = Path.Join(_tempRoot, "target");
        var modelDir = Path.Join(source, LocalModelStoragePaths.PluginDataFolderName, "com.typewhisper.whisper-cpp", "Models");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Join(modelDir, "ggml-base.bin"), "weights");
        File.WriteAllText(Path.Join(source, "root-asset.txt"), "top-level");

        // Start with the source already as the active custom path so the test never
        // touches the real default models directory.
        var settings = new FakeSettingsService(new AppSettings { LocalModelStoragePath = source });
        var unloaded = false;
        var service = new LocalModelStorageService(settings, () => unloaded = true);

        await service.MoveDownloadsAndUsePathAsync(target);

        var movedModel = Path.Join(target, LocalModelStoragePaths.PluginDataFolderName, "com.typewhisper.whisper-cpp", "Models", "ggml-base.bin");
        Assert.True(unloaded);
        Assert.True(File.Exists(movedModel));
        Assert.Equal("weights", File.ReadAllText(movedModel));
        Assert.True(File.Exists(Path.Join(target, "root-asset.txt")));
        Assert.False(File.Exists(Path.Join(source, "root-asset.txt")));
        Assert.False(File.Exists(Path.Join(modelDir, "ggml-base.bin")));
        Assert.Equal(Path.GetFullPath(target), settings.Current.LocalModelStoragePath);
    }

    [Fact]
    public void ResetToDefault_ClearsCustomPath()
    {
        var settings = new FakeSettingsService(new AppSettings { LocalModelStoragePath = "/data/models" });
        var service = new LocalModelStorageService(settings);

        service.ResetToDefault();

        Assert.Null(settings.Current.LocalModelStoragePath);
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public FakeSettingsService(AppSettings initial) => Current = initial;

        public AppSettings Current { get; private set; }

        public AppSettings Load() => Current;

        public void Save(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }

        public event Action<AppSettings>? SettingsChanged;
    }
}
