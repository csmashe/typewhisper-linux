using System.Text.Json;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Tests;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class PluginLoaderTests : IDisposable
{
    private readonly PluginLoader _loader;
    private readonly string _tempDir;

    public PluginLoaderTests()
    {
        _tempDir = TestPaths.CreateTempDirectory(
            "TypeWhisper.PluginLoaderTests"
        );
        _loader = new PluginLoader(Path.Join(_tempDir, "PluginData"));
    }

    public void Dispose()
    {
        try
        {
            TestPaths.DeleteDirectory(_tempDir);
        }
        catch
        {
            // Best-effort cleanup in tests
        }
    }

    [Fact]
    public void DiscoverAndLoad_EmptyDirectory_ReturnsEmpty()
    {
        var result = _loader.DiscoverAndLoad([_tempDir]);
        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAndLoad_NonExistentDirectory_ReturnsEmpty()
    {
        var nonExistent = Path.Join(_tempDir, "does_not_exist");
        var result = _loader.DiscoverAndLoad([nonExistent]);
        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAndLoad_MultipleNonExistentDirectories_ReturnsEmpty()
    {
        var result = _loader.DiscoverAndLoad([
            Path.Join(_tempDir, "a"),
            Path.Join(_tempDir, "b"),
            Path.Join(_tempDir, "c")
        ]);
        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAndLoad_PluginDirWithoutManifest_ReturnsEmpty()
    {
        var pluginDir = Path.Join(_tempDir, "com.test.nomanifest");
        Directory.CreateDirectory(pluginDir);

        var result = _loader.DiscoverAndLoad([_tempDir]);
        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAndLoad_InvalidManifestJson_ReturnsEmpty()
    {
        var pluginDir = Path.Join(_tempDir, "com.test.badjson");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Join(pluginDir, "manifest.json"), "{ not valid json!!!");

        var result = _loader.DiscoverAndLoad([_tempDir]);
        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAndLoad_ManifestWithMissingAssembly_ReturnsEmpty()
    {
        var pluginDir = Path.Join(_tempDir, "com.test.noasm");
        Directory.CreateDirectory(pluginDir);

        var manifest = new PluginManifest
        {
            Id = "com.test.noasm",
            Name = "No Assembly",
            Version = "1.0.0",
            AssemblyName = "NonExistent.dll",
            PluginClass = "NonExistent.Plugin"
        };

        File.WriteAllText(
            Path.Join(pluginDir, "manifest.json"),
            JsonSerializer.Serialize(manifest)
        );

        var result = _loader.DiscoverAndLoad([_tempDir]);
        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAndLoad_EmptySearchDirectories_ReturnsEmpty()
    {
        var result = _loader.DiscoverAndLoad([]);
        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAndLoad_MixedValidAndInvalidDirs_SkipsBadOnes()
    {
        var badPluginDir = Path.Join(_tempDir, "com.test.bad");
        Directory.CreateDirectory(badPluginDir);
        File.WriteAllText(Path.Join(badPluginDir, "manifest.json"), "null");

        var result = _loader.DiscoverAndLoad([_tempDir, Path.Join(_tempDir, "nonexistent")]);

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAndLoad_ManifestDeserializesToNull_ReturnsEmpty()
    {
        var pluginDir = Path.Join(_tempDir, "com.test.nullmanifest");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Join(pluginDir, "manifest.json"), "null");

        var result = _loader.DiscoverAndLoad([_tempDir]);
        Assert.Empty(result);
    }
}
