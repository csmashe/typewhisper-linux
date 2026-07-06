extern alias SherpaOnnx;
using SherpaOnnx::TypeWhisper.Plugin.SherpaOnnx;
using TypeWhisper.Plugin.WhisperCpp;
using Provisioner = SherpaOnnx::TypeWhisper.Plugins.Shared.Cuda.CudaRuntimeProvisioner;

namespace TypeWhisper.PluginSystem.Tests;

// M4 Part B: ClearCache deletes a cached GPU runtime tree on disk so the next process
// start re-provisions from scratch — the recovery path for a corrupt cached .so that
// File.Exists checks would otherwise treat as valid forever.
public class CudaRuntimeCacheClearTests
{
    [Fact]
    public async Task CudaRuntimeProvisioner_ClearCache_RemovesEntireCacheRoot()
    {
        var temp = CreateTempDir();
        try
        {
            var cacheRoot = Path.Join(temp, "cuda");
            using var http = new HttpClient();
            var provisioner = new Provisioner(cacheRoot, http);

            // CacheDirectory is cacheRoot/<BundleVersion>; ClearCache deletes its parent
            // (the whole cuda tree, all bundle versions).
            Directory.CreateDirectory(provisioner.CacheDirectory);
            await File.WriteAllTextAsync(Path.Join(provisioner.CacheDirectory, "libcudart.so.12"), "dummy");
            Assert.True(Directory.Exists(cacheRoot));

            await provisioner.ClearCacheAsync(CancellationToken.None);

            Assert.False(Directory.Exists(cacheRoot));
            // Only the cache root is removed, not whatever happens to contain it.
            Assert.True(Directory.Exists(temp));
        }
        finally
        {
            TryDeleteDir(temp);
        }
    }

    [Fact]
    public async Task SherpaCudaRuntimeInstaller_ClearCache_RemovesRuntimeTree()
    {
        var temp = CreateTempDir();
        try
        {
            using var http = new HttpClient();
            var installer = new SherpaCudaRuntimeInstaller(temp, http);

            Directory.CreateDirectory(installer.RuntimeDirectory);
            await File.WriteAllTextAsync(Path.Join(installer.RuntimeDirectory, "libsherpa-onnx-c-api.so"), "dummy");

            // ClearCache deletes the whole sherpa-onnx-cuda tree (every runtime version).
            var runtimeTree = Path.Join(temp, "Runtimes", "sherpa-onnx-cuda");
            Assert.True(Directory.Exists(runtimeTree));

            await installer.ClearCacheAsync(CancellationToken.None);

            Assert.False(Directory.Exists(runtimeTree));
            Assert.True(Directory.Exists(temp));
        }
        finally
        {
            TryDeleteDir(temp);
        }
    }

    [Fact]
    public async Task WhisperCudaRuntimeInstaller_ClearCache_RemovesRuntimeTree()
    {
        var temp = CreateTempDir();
        try
        {
            using var http = new HttpClient();
            var installer = new WhisperCudaRuntimeInstaller(temp, http);

            Directory.CreateDirectory(installer.NativeDirectory);
            await File.WriteAllTextAsync(Path.Join(installer.NativeDirectory, "libwhisper.so"), "dummy");

            // ClearCache deletes the whole whisper-cuda tree (every runtime version).
            var runtimeTree = Path.Join(temp, "Runtimes", "whisper-cuda");
            Assert.True(Directory.Exists(runtimeTree));

            await installer.ClearCacheAsync(CancellationToken.None);

            Assert.False(Directory.Exists(runtimeTree));
            Assert.True(Directory.Exists(temp));
        }
        finally
        {
            TryDeleteDir(temp);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Join(Path.GetTempPath(), "tw-cuda-clear-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}
