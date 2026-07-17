using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Tests;

namespace TypeWhisper.Core.Tests.Services;

/// <summary>Covers <see cref="LocalModelStorageService" /> and path resolution: custom-path normalization, asset migration, and reset.</summary>
public sealed class LocalModelStorageServiceTests : IDisposable
{
    private readonly string _tempRoot = TestPaths.CreateTempDirectory(
        "TypeWhisper.LocalModelStorageServiceTests"
    );

    public void Dispose()
    {
        try
        {
            TestPaths.DeleteDirectory(_tempRoot);
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
    public void ResolvePluginAssetDirectory_NoCustomPath_UsesProvidedPluginDataPath()
    {
        var settings = new AppSettings { LocalModelStoragePath = null };
        var pluginDataPath = Path.Join(_tempRoot, "PluginData");

        var resolved = LocalModelStoragePaths.ResolvePluginAssetDirectory(
            settings,
            "com.typewhisper.whisper-cpp",
            pluginDataPath
        );

        Assert.Equal(
            Path.Join(pluginDataPath, "com.typewhisper.whisper-cpp"),
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
        await File.WriteAllTextAsync(Path.Join(modelDir, "ggml-base.bin"), "weights");
        await File.WriteAllTextAsync(Path.Join(source, "root-asset.txt"), "top-level");

        // Start with the source already as the active custom path so the test never
        // touches the real default models directory.
        var settings = new FakeSettingsService(new AppSettings { LocalModelStoragePath = source });
        var unloaded = false;
        var service = new LocalModelStorageService(settings, () => unloaded = true);

        await service.MoveDownloadsAndUsePathAsync(target);

        var movedModel = Path.Join(target, LocalModelStoragePaths.PluginDataFolderName, "com.typewhisper.whisper-cpp", "Models", "ggml-base.bin");
        Assert.True(unloaded);
        Assert.True(File.Exists(movedModel));
        Assert.Equal("weights", await File.ReadAllTextAsync(movedModel));
        Assert.True(File.Exists(Path.Join(target, "root-asset.txt")));
        Assert.False(File.Exists(Path.Join(source, "root-asset.txt")));
        Assert.False(File.Exists(Path.Join(modelDir, "ggml-base.bin")));
        Assert.Equal(Path.GetFullPath(target), settings.Current.LocalModelStoragePath);
    }

    [Fact]
    public async Task MoveDownloadsAndUsePathAsync_SaveFails_LeavesSourceIntact()
    {
        var source = Path.Join(_tempRoot, "source-save-failure");
        var target = Path.Join(_tempRoot, "target-save-failure");
        var modelDir = Path.Join(source, LocalModelStoragePaths.PluginDataFolderName, "com.typewhisper.whisper-cpp", "Models");
        Directory.CreateDirectory(modelDir);
        var sourceModel = Path.Join(modelDir, "ggml-base.bin");
        await File.WriteAllTextAsync(sourceModel, "weights");

        // Source is already the active custom path so the general (non-default) branch runs.
        var settings = new FakeSettingsService(new AppSettings { LocalModelStoragePath = source })
        {
            ThrowOnSave = new IOException("save failed")
        };
        var service = new LocalModelStorageService(settings);

        await Assert.ThrowsAsync<IOException>(() => service.MoveDownloadsAndUsePathAsync(target));

        // Save failed before the best-effort cleanup, so source and persisted path are untouched.
        Assert.True(File.Exists(sourceModel));
        Assert.Equal(source, settings.Current.LocalModelStoragePath);
    }

    [Fact]
    public async Task MoveDownloadsAndUsePathAsync_FailurePartwayThroughCopy_LeavesSourceIntact()
    {
        var source = Path.Join(_tempRoot, "source-copy-failure");
        var target = Path.Join(_tempRoot, "target-copy-failure");
        var sourceModel = Path.Join(source, "first-model.bin");
        var sourcePluginAsset = Path.Join(
            source,
            LocalModelStoragePaths.PluginDataFolderName,
            "com.typewhisper.whisper-cpp",
            "Models",
            "second-model.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePluginAsset)!);
        await File.WriteAllTextAsync(sourceModel, "first");
        await File.WriteAllTextAsync(sourcePluginAsset, "second");

        // Pre-create a directory exactly where the second model's file must land: the same-directory
        // staging copy succeeds, but the final File.Move onto an existing directory throws, leaving a
        // partial staging file behind unless the copy helper cleans it up.
        var blockingTarget = Path.Join(
            target,
            LocalModelStoragePaths.PluginDataFolderName,
            "com.typewhisper.whisper-cpp",
            "Models",
            "second-model.bin");
        Directory.CreateDirectory(blockingTarget);

        var settings = new FakeSettingsService(new AppSettings { LocalModelStoragePath = source });
        var service = new LocalModelStorageService(settings);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.MoveDownloadsAndUsePathAsync(target));

        Assert.True(File.Exists(Path.Join(target, "first-model.bin")));
        Assert.True(File.Exists(sourceModel));
        Assert.True(File.Exists(sourcePluginAsset));
        Assert.Equal(source, settings.Current.LocalModelStoragePath);
        Assert.Empty(Directory.EnumerateFiles(target, "*.tw-migrate-*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MoveDownloadsAndUsePathAsync_DefaultLayout_MigratesSiblingPluginAssetsAndSavesPath()
    {
        var defaultRoot = Path.Join(_tempRoot, "default");
        var defaultModelRoot = Path.Join(defaultRoot, "Models");
        var defaultPluginDataRoot = Path.Join(defaultRoot, "PluginData");
        var target = Path.Join(_tempRoot, "target-from-default");
        var builtInModel = Path.Join(defaultModelRoot, "built-in-model.bin");
        var whisperModel = Path.Join(
            defaultPluginDataRoot,
            "com.typewhisper.whisper-cpp",
            "Models",
            "ggml-base.bin");
        var whisperRuntime = Path.Join(
            defaultPluginDataRoot,
            "com.typewhisper.whisper-cpp",
            "Runtimes",
            "whisper-cuda",
            "1.8.1",
            "libwhisper.so");
        var sherpaModel = Path.Join(
            defaultPluginDataRoot,
            "com.typewhisper.sherpa-onnx",
            "Models",
            "encoder.onnx");
        var sherpaRuntime = Path.Join(
            defaultPluginDataRoot,
            "com.typewhisper.sherpa-onnx",
            "Runtimes",
            "sherpa-onnx.dll");
        Directory.CreateDirectory(defaultModelRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(whisperModel)!);
        Directory.CreateDirectory(Path.GetDirectoryName(whisperRuntime)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sherpaModel)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sherpaRuntime)!);
        await File.WriteAllTextAsync(builtInModel, "built-in");
        await File.WriteAllTextAsync(whisperModel, "whisper");
        await File.WriteAllTextAsync(whisperRuntime, "whisper-runtime");
        await File.WriteAllTextAsync(sherpaModel, "sherpa-model");
        await File.WriteAllTextAsync(sherpaRuntime, "sherpa-runtime");

        var settings = new FakeSettingsService(new AppSettings { LocalModelStoragePath = null });
        var unloaded = false;
        var service = new LocalModelStorageService(
            settings,
            () => unloaded = true,
            defaultModelStoragePath: defaultModelRoot,
            defaultPluginDataPath: defaultPluginDataRoot);

        Assert.Equal(defaultModelRoot, service.ResolvedModelStoragePath);

        await service.MoveDownloadsAndUsePathAsync(target);

        Assert.True(unloaded);
        Assert.Equal(
            "built-in",
            await File.ReadAllTextAsync(Path.Join(target, "built-in-model.bin")));
        Assert.Equal(
            "whisper",
            await File.ReadAllTextAsync(Path.Join(
                target,
                LocalModelStoragePaths.PluginDataFolderName,
                "com.typewhisper.whisper-cpp",
                "Models",
                "ggml-base.bin")));
        Assert.Equal(
            "whisper-runtime",
            await File.ReadAllTextAsync(Path.Join(
                target,
                LocalModelStoragePaths.PluginDataFolderName,
                "com.typewhisper.whisper-cpp",
                "Runtimes",
                "whisper-cuda",
                "1.8.1",
                "libwhisper.so")));
        Assert.Equal(
            "sherpa-model",
            await File.ReadAllTextAsync(Path.Join(
                target,
                LocalModelStoragePaths.PluginDataFolderName,
                "com.typewhisper.sherpa-onnx",
                "Models",
                "encoder.onnx")));
        Assert.Equal(
            "sherpa-runtime",
            await File.ReadAllTextAsync(Path.Join(
                target,
                LocalModelStoragePaths.PluginDataFolderName,
                "com.typewhisper.sherpa-onnx",
                "Runtimes",
                "sherpa-onnx.dll")));
        Assert.False(File.Exists(builtInModel));
        Assert.False(File.Exists(whisperModel));
        Assert.False(File.Exists(whisperRuntime));
        Assert.False(File.Exists(sherpaModel));
        Assert.False(File.Exists(sherpaRuntime));
        Assert.Equal(Path.GetFullPath(target), settings.Current.LocalModelStoragePath);
    }

    [Fact]
    public async Task MoveDownloadsAndUsePathAsync_DefaultLayout_TargetIsDefaultModelsRoot_MigratesSiblingPluginAssets()
    {
        var defaultRoot = Path.Join(_tempRoot, "default-target-is-models-root");
        var defaultModelRoot = Path.Join(defaultRoot, "Models");
        var defaultPluginDataRoot = Path.Join(defaultRoot, "PluginData");
        var whisperModel = Path.Join(
            defaultPluginDataRoot,
            "com.typewhisper.whisper-cpp",
            "Models",
            "ggml-base.bin");
        Directory.CreateDirectory(defaultModelRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(whisperModel)!);
        await File.WriteAllTextAsync(whisperModel, "whisper");

        var settings = new FakeSettingsService(new AppSettings { LocalModelStoragePath = null });
        var unloaded = false;
        var service = new LocalModelStorageService(
            settings,
            () => unloaded = true,
            defaultModelStoragePath: defaultModelRoot,
            defaultPluginDataPath: defaultPluginDataRoot);

        // Exercises the PathsEqual short-circuit branch.
        await service.MoveDownloadsAndUsePathAsync(defaultModelRoot);

        Assert.True(unloaded);
        Assert.Equal(
            "whisper",
            await File.ReadAllTextAsync(Path.Join(
                defaultModelRoot,
                LocalModelStoragePaths.PluginDataFolderName,
                "com.typewhisper.whisper-cpp",
                "Models",
                "ggml-base.bin")));
        Assert.False(File.Exists(whisperModel));
        Assert.Equal(Path.GetFullPath(defaultModelRoot), settings.Current.LocalModelStoragePath);
    }

    [Fact]
    public async Task MoveDownloadsAndUsePathAsync_DefaultLayout_LeavesUnknownPluginFolderAlone()
    {
        var defaultRoot = Path.Join(_tempRoot, "default-with-unknown-plugin");
        var defaultModelRoot = Path.Join(defaultRoot, "Models");
        var defaultPluginDataRoot = Path.Join(defaultRoot, "PluginData");
        var target = Path.Join(_tempRoot, "target-with-unknown-plugin");
        var unknownPluginSettings = Path.Join(
            defaultPluginDataRoot,
            "com.example.unknown",
            "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(unknownPluginSettings)!);
        await File.WriteAllTextAsync(unknownPluginSettings, "{}");

        var settings = new FakeSettingsService(new AppSettings { LocalModelStoragePath = null });
        var service = new LocalModelStorageService(
            settings,
            defaultModelStoragePath: defaultModelRoot,
            defaultPluginDataPath: defaultPluginDataRoot);

        await service.MoveDownloadsAndUsePathAsync(target);

        Assert.True(File.Exists(unknownPluginSettings));
        Assert.False(File.Exists(Path.Join(
            target,
            LocalModelStoragePaths.PluginDataFolderName,
            "com.example.unknown",
            "settings.json")));
    }

    [Fact]
    public async Task MoveDownloadsAndUsePathAsync_TargetNestedUnderPluginAssetSource_Throws()
    {
        var defaultRoot = Path.Join(_tempRoot, "default-for-nested-target");
        var defaultModelRoot = Path.Join(defaultRoot, "Models");
        var defaultPluginDataRoot = Path.Join(defaultRoot, "PluginData");
        var target = Path.Join(defaultPluginDataRoot, "nested-target");
        var settings = new FakeSettingsService(new AppSettings { LocalModelStoragePath = null });
        var service = new LocalModelStorageService(
            settings,
            defaultModelStoragePath: defaultModelRoot,
            defaultPluginDataPath: defaultPluginDataRoot);

        var ex = await Assert.ThrowsAsync<LocalModelStorageUnavailableException>(() =>
            service.MoveDownloadsAndUsePathAsync(target));

        Assert.Equal(LocalModelStorageUnavailableReason.NestedUnderCurrentFolder, ex.Reason);
        Assert.Equal(Path.GetFullPath(target), ex.Path);
        Assert.Equal(defaultPluginDataRoot, ex.CurrentPath);
        Assert.Null(settings.Current.LocalModelStoragePath);
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

        public Exception? ThrowOnSave;

        public AppSettings Load() => Current;

        public void Save(AppSettings settings)
        {
            if (ThrowOnSave is not null)
                throw ThrowOnSave;

            Current = settings;
            SettingsChanged?.Invoke(settings);
        }

        public event Action<AppSettings>? SettingsChanged;
    }
}
