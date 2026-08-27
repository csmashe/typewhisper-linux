using System.Diagnostics;
using System.Text.Json;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
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
            Path.Join(_tempDir, "c"),
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
    public void DiscoverAndLoad_PluginDirWithOnlyPluginJson_SkipsWithDiagnostic()
    {
        var pluginDir = Path.Join(_tempDir, "com.test.wrongmanifestname");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(
            Path.Join(pluginDir, "plugin.json"),
            JsonSerializer.Serialize(CreateManifest("com.test.wrongmanifestname"))
        );
        using var traceWriter = new StringWriter();
        using var traceListener = new TextWriterTraceListener(traceWriter);
        Trace.Listeners.Add(traceListener);

        try
        {
            var result = _loader.DiscoverAndLoad([_tempDir]);
            Trace.Flush();

            Assert.Empty(result);
            Assert.Contains(
                $"[PluginLoader] No {PluginManifest.FileName} in {pluginDir}, skipping",
                traceWriter.ToString()
            );
        }
        finally
        {
            Trace.Listeners.Remove(traceListener);
        }
    }

    [Fact]
    public void DiscoverAndLoad_InvalidManifestJson_ReturnsEmpty()
    {
        var pluginDir = Path.Join(_tempDir, "com.test.badjson");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(
            Path.Join(pluginDir, PluginManifest.FileName),
            "{ not valid json!!!"
        );

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
            PluginClass = "NonExistent.Plugin",
        };

        File.WriteAllText(
            Path.Join(pluginDir, PluginManifest.FileName),
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
        File.WriteAllText(Path.Join(badPluginDir, PluginManifest.FileName), "null");

        var result = _loader.DiscoverAndLoad([_tempDir, Path.Join(_tempDir, "nonexistent")]);

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAndLoad_ManifestDeserializesToNull_ReturnsEmpty()
    {
        var pluginDir = Path.Join(_tempDir, "com.test.nullmanifest");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Join(pluginDir, PluginManifest.FileName), "null");

        var result = _loader.DiscoverAndLoad([_tempDir]);
        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAndLoad_MinimumAboveHost_RecordsFailureWithoutConstruction()
    {
        var loader = CreateLoader("0.13.0-rc.2");
        var pluginDir = StageConstructorTrackingPlugin("0.13.0");

        var result = loader.DiscoverAndLoad([_tempDir]);

        Assert.Empty(result);
        var failure = Assert.Single(loader.LastLoadFailures);
        Assert.Equal(pluginDir, failure.PluginDirectory);
        Assert.Contains("Requires host version", failure.Message);
        Assert.False(File.Exists(Path.Join(pluginDir, ConstructorMarkerFileName)));
    }

    [Theory]
    [InlineData("0.13.0-rc.2")]
    [InlineData("0.13.0-rc.1")]
    public void DiscoverAndLoad_MinimumAtOrBelowHost_Loads(string minimumHostVersion)
    {
        var loader = CreateLoader("0.13.0-rc.2");
        var pluginDir = StageConstructorTrackingPlugin(minimumHostVersion);

        var result = loader.DiscoverAndLoad([_tempDir]);

        var loaded = Assert.Single(result);
        try
        {
            Assert.Equal("com.test.compatibility", loaded.Manifest.Id);
            Assert.True(File.Exists(Path.Join(pluginDir, ConstructorMarkerFileName)));
            Assert.Empty(loader.LastLoadFailures);
        }
        finally
        {
            loaded.Instance.Dispose();
            loaded.LoadContext.Unload();
        }
    }

    [Fact]
    public void DiscoverAndLoad_MalformedMinimum_RecordsFailureWithoutConstruction()
    {
        var loader = CreateLoader("0.13.0");
        var pluginDir = StageConstructorTrackingPlugin("0.13");

        var result = loader.DiscoverAndLoad([_tempDir]);

        Assert.Empty(result);
        var failure = Assert.Single(loader.LastLoadFailures);
        Assert.Equal(pluginDir, failure.PluginDirectory);
        Assert.Contains("not valid SemVer", failure.Message);
        Assert.False(File.Exists(Path.Join(pluginDir, ConstructorMarkerFileName)));
    }

    [Fact]
    public void DiscoverAndLoad_AbsentMinimum_Loads()
    {
        var loader = CreateLoader("0.13.0-rc.2");
        var pluginDir = StageConstructorTrackingPlugin(null, includeMinimum: false);

        var result = loader.DiscoverAndLoad([_tempDir]);

        var loaded = Assert.Single(result);
        try
        {
            Assert.True(File.Exists(Path.Join(pluginDir, ConstructorMarkerFileName)));
            Assert.Empty(loader.LastLoadFailures);
        }
        finally
        {
            loaded.Instance.Dispose();
            loaded.LoadContext.Unload();
        }
    }

    [Fact]
    public void ResolveMetadata_NewFieldsOverrideContradictoryLegacyValues()
    {
        var manifest = CreateManifest("com.typewhisper.whisper-cpp") with
        {
            Category = "transcription",
            IsLocal = true,
            NetworkAccess = PluginNetworkAccess.UserControlled,
            Categories = [PluginCategory.Integration, PluginCategory.Action],
        };

        var descriptor = PluginLoader.ResolveMetadata(manifest);

        Assert.Equal(PluginNetworkAccess.UserControlled, descriptor.NetworkAccess);
        Assert.Equal(
            new HashSet<PluginCategory>
            {
                PluginCategory.Integration,
                PluginCategory.Action,
            },
            descriptor.Categories
        );
        Assert.False(descriptor.RanLocally);
    }

    [Fact]
    public void ResolveMetadata_LegacyExternalManifestUsesCompatibilityFallback()
    {
        var manifest = CreateManifest("com.typewhisper.whisper-cpp");

        var descriptor = PluginLoader.ResolveMetadata(manifest);

        Assert.Equal(PluginNetworkAccess.Local, descriptor.NetworkAccess);
        Assert.Equal(
            [PluginCategory.Transcription],
            descriptor.Categories
        );
        Assert.True(descriptor.RanLocally);
    }

    [Fact]
    public void ResolveMetadata_LegacyWebhookIsNotPresumedLocal()
    {
        var manifest = CreateManifest("com.typewhisper.webhook");

        var descriptor = PluginLoader.ResolveMetadata(manifest);

        Assert.Equal(PluginNetworkAccess.Network, descriptor.NetworkAccess);
        Assert.False(descriptor.RanLocally);
    }

    [Fact]
    public void ResolveMetadata_ExplicitLegacyFalseOverridesKnownLocalFallback()
    {
        var manifest = CreateManifest("com.typewhisper.whisper-cpp") with
        {
            IsLocal = false,
        };

        var descriptor = PluginLoader.ResolveMetadata(manifest);

        Assert.Equal(PluginNetworkAccess.Network, descriptor.NetworkAccess);
        Assert.False(descriptor.RanLocally);
    }

    [Fact]
    public void ResolveMetadata_UnlabeledExternalManifestFailsClosed()
    {
        var manifest = CreateManifest("com.example.unlabeled") with
        {
            Name = "Unlabeled",
            Description = null,
        };

        var descriptor = PluginLoader.ResolveMetadata(manifest);

        Assert.Equal(PluginNetworkAccess.Network, descriptor.NetworkAccess);
        Assert.Equal([PluginCategory.Unknown], descriptor.Categories);
        Assert.False(descriptor.RanLocally);
    }

    [Fact]
    public void ResolveMetadata_EmptyDeclaredCategoriesIsRejected()
    {
        var manifest = CreateManifest("com.example.empty") with
        {
            NetworkAccess = PluginNetworkAccess.Network,
            Categories = [],
        };

        var error = Assert.Throws<InvalidDataException>(
            () => PluginLoader.ResolveMetadata(manifest)
        );

        Assert.Contains("empty categories", error.Message);
    }

    [Theory]
    [InlineData((PluginNetworkAccess)999, PluginCategory.Utility)]
    [InlineData(PluginNetworkAccess.Network, (PluginCategory)999)]
    [InlineData(PluginNetworkAccess.Network, PluginCategory.Unknown)]
    public void ResolveMetadata_InvalidDeclaredEnumIsRejected(
        PluginNetworkAccess networkAccess,
        PluginCategory category
    )
    {
        var manifest = CreateManifest("com.example.invalid") with
        {
            NetworkAccess = networkAccess,
            Categories = [category],
        };

        Assert.Throws<InvalidDataException>(
            () => PluginLoader.ResolveMetadata(manifest)
        );
    }

    private static PluginManifest CreateManifest(string id)
    {
        return new PluginManifest
        {
            Id = id,
            Name = id,
            Version = "1.0.0",
            AssemblyName = "fake.dll",
            PluginClass = "Fake.Plugin",
        };
    }

    private PluginLoader CreateLoader(string hostVersion)
    {
        return new PluginLoader(Path.Join(_tempDir, "PluginData"))
        {
            HostVersion = hostVersion,
        };
    }

    private string StageConstructorTrackingPlugin(
        string? minimumHostVersion,
        bool includeMinimum = true
    )
    {
        var pluginDir = Path.Join(_tempDir, "com.test.compatibility");
        Directory.CreateDirectory(pluginDir);

        var sourceAssembly = typeof(PluginLoaderTests).Assembly.Location;
        var assemblyName = Path.GetFileName(sourceAssembly);
        File.Copy(sourceAssembly, Path.Join(pluginDir, assemblyName), true);

        var manifest = new Dictionary<string, object?>
        {
            ["id"] = "com.test.compatibility",
            ["name"] = "Compatibility Test Plugin",
            ["version"] = "1.0.0",
            ["assemblyName"] = assemblyName,
            ["pluginClass"] = typeof(ConstructorTrackingPlugin).FullName,
        };
        if (includeMinimum)
        {
            manifest["minHostVersion"] = minimumHostVersion;
        }

        File.WriteAllText(
            Path.Join(pluginDir, PluginManifest.FileName),
            JsonSerializer.Serialize(manifest)
        );
        return pluginDir;
    }

    private sealed class ConstructorTrackingPlugin : ITypeWhisperPlugin
    {
        public ConstructorTrackingPlugin()
        {
            var assemblyDirectory = Path.GetDirectoryName(
                typeof(ConstructorTrackingPlugin).Assembly.Location
            )!;
            File.WriteAllText(
                Path.Join(assemblyDirectory, ConstructorMarkerFileName),
                "constructed"
            );
        }

        public string PluginId => "com.test.compatibility";
        public string PluginName => "Compatibility Test Plugin";
        public string PluginVersion => "1.0.0";

        public Task ActivateAsync(IPluginHostServices host)
        {
            return Task.CompletedTask;
        }

        public Task DeactivateAsync()
        {
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private const string ConstructorMarkerFileName = "constructor.marker";
}
