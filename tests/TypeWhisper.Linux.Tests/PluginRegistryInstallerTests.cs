using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Plugin.Webhook;
using TypeWhisper.PluginSDK;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class PluginRegistryInstallerTests : IDisposable
{
    private const string PluginId = "com.typewhisper.webhook";
    private const string TransactionDirectoryName = ".typewhisper-plugin-transactions";
    private static readonly string[] s_manifestCategories = ["integration"];

    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly ArchiveHttpHandler _archiveHandler = new();
    private readonly PluginEventBus _eventBus = new();
    private readonly PluginLoader _loader;
    private readonly string _pluginsRoot;
    private readonly Mock<IProfileService> _profiles = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly string _tempRoot;
    private PluginManager? _manager;

    public PluginRegistryInstallerTests()
    {
        _tempRoot = TestPaths.CreateTempDirectory("plugin-registry-installer");
        _pluginsRoot = Path.Join(_tempRoot, "Plugins");
        _loader = new PluginLoader(Path.Join(_tempRoot, "PluginData"));
        _profiles.Setup(profileService => profileService.Profiles).Returns(new List<Profile>());
        _settings.Setup(settingsService => settingsService.Current).Returns(new AppSettings());
    }

    public void Dispose()
    {
        _manager?.Dispose();
        TestPaths.DeleteDirectory(_tempRoot);
    }

    [Fact]
    public async Task InstallPluginAsync_ActuallyBuiltWebhookArchive_InstallsThenUpdates()
    {
        var manager = CreateManager();
        var service = CreateService(manager);
        var versionOneArchive = CreateWebhookArchive("2.0.0-rc.2");
        _archiveHandler.Archive = versionOneArchive;
        var versionOne = RegistryEntry(versionOneArchive, "2.0.0-rc.2");

        await service.InstallPluginAsync(versionOne);

        var installed = Assert.IsType<LoadedPlugin>(manager.GetPlugin(PluginId));
        Assert.Equal("2.0.0-rc.2", installed.Manifest.Version);
        Assert.True(manager.IsEnabled(PluginId));
        Assert.Equal(
            "2.0.0-rc.2",
            ReadInstalledManifestVersion()
        );

        var versionTwoArchive = CreateWebhookArchive("2.0.0-rc.10");
        _archiveHandler.Archive = versionTwoArchive;
        var versionTwo = RegistryEntry(versionTwoArchive, "2.0.0-rc.10");
        Assert.Equal(PluginInstallState.UpdateAvailable, service.GetInstallState(versionTwo));

        await service.InstallPluginAsync(versionTwo);

        Assert.Equal("2.0.0-rc.10", manager.GetPlugin(PluginId)!.Manifest.Version);
        Assert.Equal("2.0.0-rc.10", ReadInstalledManifestVersion());
        Assert.Equal(2, _archiveHandler.RequestCount);
    }

    [Fact]
    public async Task InstallPluginAsync_WrongPlatformEntry_IsRejectedBeforeDownload()
    {
        var archive = CreateWebhookArchive("2.0.0");
        _archiveHandler.Archive = archive;
        var entry = RegistryEntry(archive, "2.0.0") with
        {
            Platform = "windows",
            Rid = "win-x64",
        };
        var service = CreateService(CreateManager());

        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallPluginAsync(entry));

        Assert.Equal(0, _archiveHandler.RequestCount);
        Assert.False(Directory.Exists(Path.Join(_pluginsRoot, PluginId)));
    }

    [Fact]
    public async Task InstallPluginAsync_UndeclaredNativeRuntime_IsRejectedWithoutLiveMutation()
    {
        var archive = CreateWebhookArchive(
            "2.0.0",
            mutateArchive: zip => WriteEntry(
                zip.CreateEntry("runtimes/linux-arm64/native/libunexpected.so"),
                "native"
            )
        );
        _archiveHandler.Archive = archive;
        var service = CreateService(CreateManager());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.InstallPluginAsync(RegistryEntry(archive, "2.0.0"))
        );

        Assert.Contains("undeclared native runtime", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Join(_pluginsRoot, PluginId)));
    }

    [Fact]
    public async Task InstallPluginAsync_TamperedSha256_IsRejectedWithoutExtraction()
    {
        var archive = CreateWebhookArchive("2.0.0");
        _archiveHandler.Archive = archive;
        var entry = RegistryEntry(archive, "2.0.0") with
        {
            Sha256 = new string('0', SHA256.HashSizeInBytes * 2),
        };
        var service = CreateService(CreateManager());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.InstallPluginAsync(entry)
        );

        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Join(_pluginsRoot, PluginId)));
    }

    [Fact]
    public async Task InstallPluginAsync_TraversalEntry_IsRejectedWithoutEscapingStage()
    {
        var archive = CreateWebhookArchive(
            "2.0.0",
            mutateArchive: zip => WriteEntry(zip.CreateEntry("../escaped.txt"), "escaped")
        );
        _archiveHandler.Archive = archive;
        var service = CreateService(CreateManager());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.InstallPluginAsync(RegistryEntry(archive, "2.0.0"))
        );

        Assert.Contains("traversal", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(_pluginsRoot, "escaped.txt")));
        Assert.False(Directory.Exists(Path.Join(_pluginsRoot, PluginId)));
    }

    [Fact]
    public async Task InstallPluginAsync_BundledPluginSdk_IsRejectedWithoutLiveMutation()
    {
        // ReSharper disable once MethodHasAsyncOverload -- synchronous File IO is deliberate in this test step.
        var sdkBytes = File.ReadAllBytes(typeof(ITypeWhisperPlugin).Assembly.Location);
        var archive = CreateWebhookArchive(
            "2.0.0",
            mutateArchive: zip => WriteEntry(
                zip.CreateEntry("TypeWhisper.PluginSDK.dll"),
                sdkBytes
            )
        );
        _archiveHandler.Archive = archive;
        var service = CreateService(CreateManager());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.InstallPluginAsync(RegistryEntry(archive, "2.0.0"))
        );

        Assert.Contains("must not bundle", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Join(_pluginsRoot, PluginId)));
    }

    [Fact]
    public async Task InstallPluginAsync_LoadFailure_RollsBackAndReloadsOldPlugin()
    {
        var manager = CreateManager();
        var service = CreateService(manager);
        var oldArchive = CreateWebhookArchive("1.0.0");
        _archiveHandler.Archive = oldArchive;
        await service.InstallPluginAsync(RegistryEntry(oldArchive, "1.0.0"));
        var oldPlugin = manager.GetPlugin(PluginId)!;

        var rejectedArchive = CreateWebhookArchive("3.0.0", corruptAssembly: true);
        _archiveHandler.Archive = rejectedArchive;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.InstallPluginAsync(RegistryEntry(rejectedArchive, "3.0.0"))
        );

        var restored = Assert.IsType<LoadedPlugin>(manager.GetPlugin(PluginId));
        Assert.Equal("1.0.0", restored.Manifest.Version);
        Assert.NotSame(oldPlugin.Instance, restored.Instance);
        Assert.True(manager.IsEnabled(PluginId));
        Assert.Equal("1.0.0", ReadInstalledManifestVersion());
        Assert.Equal(
            "4D5A",
            Convert.ToHexString(
                // ReSharper disable once MethodHasAsyncOverload -- synchronous File IO is deliberate in this test assertion.
                File.ReadAllBytes(Path.Join(_pluginsRoot, PluginId, "TypeWhisper.Plugin.Webhook.dll"))
                    .AsSpan(0, 2)
            )
        );
    }

    [Fact]
    public async Task InstallPluginAsync_RepeatedLoadFailures_RetainNoRejectedTrees()
    {
        var service = CreateService(CreateManager());
        var rejectedArchive = CreateWebhookArchive("3.0.0", corruptAssembly: true);
        _archiveHandler.Archive = rejectedArchive;
        var entry = RegistryEntry(rejectedArchive, "3.0.0");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => service.InstallPluginAsync(entry));
        }

        Assert.Empty(EnumerateTransactionLeftovers());
        Assert.False(Directory.Exists(Path.Join(_pluginsRoot, PluginId)));
    }

    [Fact]
    public async Task RecoverInterruptedInstallsAsync_PurgesArtifactsLeftByATerminatedInstall()
    {
        var service = CreateService(CreateManager());
        var artifactState = Path.Join(_pluginsRoot, TransactionDirectoryName, PluginId);
        Directory.CreateDirectory(Path.Join(artifactState, "rejected-abandoned"));
        Directory.CreateDirectory(Path.Join(artifactState, "stage-abandoned"));
        // ReSharper disable once MethodHasAsyncOverload -- synchronous File IO is deliberate in this test step.
        File.WriteAllText(Path.Join(artifactState, "download-abandoned.tmp"), "partial");

        await service.RecoverInterruptedInstallsAsync();

        Assert.Empty(EnumerateTransactionLeftovers());
    }

    private string[] EnumerateTransactionLeftovers()
    {
        var artifactState = Path.Join(_pluginsRoot, TransactionDirectoryName, PluginId);
        return Directory.Exists(artifactState)
            ? Directory
                .GetFileSystemEntries(artifactState)
                .Where(entry =>
                    !string.Equals(
                        Path.GetFileName(entry),
                        "transaction.lock",
                        StringComparison.Ordinal
                    )
                )
                .ToArray()
            : [];
    }

    private PluginManager CreateManager()
    {
        _manager = new PluginManager(
            _loader,
            _eventBus,
            _activeWindow.Object,
            _profiles.Object,
            _settings.Object,
            [_pluginsRoot]
        );
        return _manager;
    }

    private PluginRegistryService CreateService(PluginManager manager)
    {
        return new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            new HttpClient(_archiveHandler),
            _pluginsRoot
        )
        {
            HostVersion = "99.0.0",
            RuntimeRid = "linux-x64",
        };
    }

    private string ReadInstalledManifestVersion()
    {
        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Join(_pluginsRoot, PluginId, "manifest.json"))
        );
        return manifest.RootElement.GetProperty("version").GetString()!;
    }

    private byte[] CreateWebhookArchive(
        string version,
        bool corruptAssembly = false,
        Action<ZipArchive>? mutateArchive = null
    )
    {
        var packageRoot = Path.Join(_tempRoot, $"package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(packageRoot);
        var builtAssembly = typeof(WebhookPlugin).Assembly.Location;
        var assemblyDestination = Path.Join(packageRoot, "TypeWhisper.Plugin.Webhook.dll");
        File.Copy(builtAssembly, assemblyDestination);
        if (corruptAssembly)
        {
            File.WriteAllText(Path.Join(packageRoot, "Broken.dll"), "not a managed assembly");
        }

        var depsPath = Path.ChangeExtension(builtAssembly, ".deps.json");
        if (File.Exists(depsPath))
        {
            File.Copy(depsPath, Path.Join(packageRoot, Path.GetFileName(depsPath)));
        }

        var localizationSource = Path.Join(Path.GetDirectoryName(builtAssembly)!, "Localization");
        if (Directory.Exists(localizationSource))
        {
            Directory.CreateDirectory(Path.Join(packageRoot, "Localization"));
            foreach (var localizationFile in Directory.GetFiles(localizationSource))
            {
                File.Copy(
                    localizationFile,
                    Path.Join(packageRoot, "Localization", Path.GetFileName(localizationFile))
                );
            }
        }

        File.WriteAllText(
            Path.Join(packageRoot, "manifest.json"),
            JsonSerializer.Serialize(
                new
                {
                    id = PluginId,
                    name = "Webhook",
                    version,
                    author = "TypeWhisper",
                    description = "Sends event notifications to a webhook URL",
                    networkAccess = "userControlled",
                    categories = s_manifestCategories,
                    assemblyName = corruptAssembly
                        ? "Broken.dll"
                        : "TypeWhisper.Plugin.Webhook.dll",
                    pluginClass = "TypeWhisper.Plugin.Webhook.WebhookPlugin",
                }
            )
        );

        var zipPath = Path.Join(_tempRoot, $"archive-{Guid.NewGuid():N}.zip");
        ZipFile.CreateFromDirectory(packageRoot, zipPath, CompressionLevel.Fastest, false);
        // ReSharper disable once InvertIf -- the block scopes the ZipArchive so it is disposed
        // (and the archive flushed) before the ReadAllBytes below; a guard-clause inversion would
        // hoist the `using` to method scope and read the file while the archive is still open.
        if (mutateArchive is not null)
        {
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update);
            mutateArchive(zip);
        }

        return File.ReadAllBytes(zipPath);
    }

    private static RegistryPlugin RegistryEntry(byte[] archive, string version)
    {
        return new RegistryPlugin
        {
            Id = PluginId,
            Name = "Webhook",
            Version = version,
            Author = "TypeWhisper",
            Description = "Webhook fixture",
            Size = archive.LongLength,
            DownloadUrl = "https://plugins.test/webhook.zip",
            Sha256 = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant(),
            Platform = "linux",
            Rid = "linux-x64",
            SdkAbi = "net10.0",
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    private static void WriteEntry(ZipArchiveEntry entry, string contents)
    {
        using var writer = new StreamWriter(entry.Open());
        writer.Write(contents);
    }

    private static void WriteEntry(ZipArchiveEntry entry, byte[] contents)
    {
        using var stream = entry.Open();
        stream.Write(contents);
    }

    private sealed class ArchiveHttpHandler : HttpMessageHandler
    {
        public byte[] Archive { get; set; } = [];
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Uri.UriSchemeHttps, request.RequestUri!.Scheme);
            RequestCount++;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Archive),
                    RequestMessage = request,
                }
            );
        }
    }
}
