// ReSharper disable MethodHasAsyncOverload -- synchronous File.ReadAllBytes is deliberate in these test assertions.
using Moq;
using System.Text.Json;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.Tests;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class PluginHostServicesTests : IDisposable
{
    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly Mock<IPluginEventBus> _eventBus = new();
    private readonly Mock<IProfileService> _profiles = new();
    private readonly string _tempDir;

    public PluginHostServicesTests()
    {
        _profiles.Setup(p => p.Profiles).Returns(new List<Profile>());
        _tempDir = TestPaths.CreateTempDirectory(
            "TypeWhisper.PluginHostServicesTests"
        );
    }

    public void Dispose()
    {
        try
        {
            TestPaths.DeleteDirectory(_tempDir);
        }
        catch
        {
            /* best effort */
        }
    }

    [Fact]
    public void NotifyCapabilitiesChanged_InvokesCallback()
    {
        var callbackInvoked = false;
        var services = CreateServices(() => callbackInvoked = true);

        services.NotifyCapabilitiesChanged();

        Assert.True(callbackInvoked);
    }

    [Fact]
    public void NotifyCapabilitiesChanged_WithNoCallback_DoesNotThrow()
    {
        var services = CreateServices();
        var ex = Record.Exception(services.NotifyCapabilitiesChanged);
        Assert.Null(ex);
    }

    [Fact]
    public void NotifyCapabilitiesChanged_CallbackInvokedMultipleTimes()
    {
        var callCount = 0;
        var services = CreateServices(() => callCount++);

        services.NotifyCapabilitiesChanged();
        services.NotifyCapabilitiesChanged();
        services.NotifyCapabilitiesChanged();

        Assert.Equal(3, callCount);
    }

    [Fact]
    public void Constructor_WithoutCallback_DoesNotThrow()
    {
        var ex = Record.Exception(() => CreateServices());
        Assert.Null(ex);
    }

    [Fact]
    public void Localization_IsAvailable()
    {
        var services = CreateServices();
        Assert.NotNull(services.Localization);
    }

    [Fact]
    public void Localization_ReturnsKeyWhenNoFiles()
    {
        var services = CreateServices();
        Assert.Equal("some.key", services.Localization.GetString("some.key"));
    }

    [Fact]
    public void Localization_AvailableLanguagesEmpty_WhenNoFiles()
    {
        var services = CreateServices();
        Assert.Empty(services.Localization.AvailableLanguages);
    }

    [Fact]
    public void PluginDataDirectory_UsesInjectedRoot()
    {
        var services = CreateServices();

        var directory = services.PluginDataDirectory;

        Assert.Equal(Path.Join(_tempDir, "PluginData", "test-plugin"), directory);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void PluginAssetDirectory_WithSettingsAndNoCustomPath_UsesInjectedRoot()
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.Current).Returns(new AppSettings());
        var services = CreateServices(settings: settings.Object);

        Assert.Equal(services.PluginDataDirectory, services.PluginAssetDirectory);
        Assert.StartsWith(Path.Join(_tempDir, "PluginData"), services.PluginAssetDirectory);
    }

    [Fact]
    public async Task SettingsAndSecrets_RoundTripAcrossServiceInstances()
    {
        var writer = CreateServices();
        writer.SetSetting("language", "en-US");
        await writer.StoreSecretAsync("api-key", "secret-value");

        var reader = CreateServices();

        Assert.Equal("en-US", reader.GetSetting<string>("language"));
        Assert.Equal("secret-value", await reader.LoadSecretAsync("api-key"));
    }

    [Fact]
    public async Task FailedSave_ThrowsWithoutChangingCacheOrSettingsFile()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
        {
            // Root can bypass directory write permissions, so chmod cannot force this failure.
            return;
        }

        var services = CreateServices();
        services.SetSetting("language", "old-value");
        await services.StoreSecretAsync("api-key", "old-secret");

        var pluginDirectory = Path.Join(_tempDir, "PluginData", "test-plugin");
        var settingsPath = Path.Join(pluginDirectory, "settings.json");
        var originalMode = File.GetUnixFileMode(pluginDirectory);
        var originalBytes = File.ReadAllBytes(settingsPath);

        try
        {
            File.SetUnixFileMode(
                pluginDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserExecute
            );

            Assert.ThrowsAny<Exception>(() =>
                services.SetSetting("language", "new-value")
            );
            await Assert.ThrowsAnyAsync<Exception>(() =>
                services.StoreSecretAsync("api-key", "new-secret")
            );

            Assert.Equal("old-value", services.GetSetting<string>("language"));
            Assert.Equal("old-secret", await services.LoadSecretAsync("api-key"));
            Assert.Equal(originalBytes, File.ReadAllBytes(settingsPath));
            Assert.Empty(Directory.EnumerateFiles(pluginDirectory, "*.tmp"));
        }
        finally
        {
            File.SetUnixFileMode(pluginDirectory, originalMode);
        }
    }

    [Fact]
    public void CorruptSettingsFile_IsPreservedBeforeFreshSettingsAreSaved()
    {
        var pluginDirectory = Path.Join(_tempDir, "PluginData", "test-plugin");
        var settingsPath = Path.Join(pluginDirectory, "settings.json");
        Directory.CreateDirectory(pluginDirectory);
        var originalBytes = "{ not valid json"u8.ToArray();
        File.WriteAllBytes(settingsPath, originalBytes);

        var services = CreateServices();

        Assert.Null(services.GetSetting<string>("language"));
        var brokenPath = Assert.Single(
            Directory.EnumerateFiles(pluginDirectory, "settings.json.broken-*")
        );
        Assert.Equal(originalBytes, File.ReadAllBytes(brokenPath));

        services.SetSetting("language", "en-US");

        Assert.True(File.Exists(settingsPath));
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.Equal("en-US", document.RootElement.GetProperty("language").GetString());
    }

    [Fact]
    public void UnreadableSettingsFile_RefusesToOverwriteExistingFile()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
        {
            // Root can read a mode-000 file, so this cannot exercise the unreadable-file path.
            return;
        }

        var pluginDirectory = Path.Join(_tempDir, "PluginData", "test-plugin");
        var settingsPath = Path.Join(pluginDirectory, "settings.json");
        Directory.CreateDirectory(pluginDirectory);
        var originalBytes = "{\"language\":\"old-value\"}"u8.ToArray();
        File.WriteAllBytes(settingsPath, originalBytes);
        var originalMode = File.GetUnixFileMode(settingsPath);

        try
        {
            File.SetUnixFileMode(settingsPath, UnixFileMode.None);
            var services = CreateServices();

            Assert.Null(services.GetSetting<string>("language"));
            Assert.Throws<IOException>(() =>
                services.SetSetting("language", "new-value")
            );
            Assert.True(File.Exists(settingsPath));
        }
        finally
        {
            File.SetUnixFileMode(settingsPath, originalMode);
        }

        Assert.Equal(originalMode, File.GetUnixFileMode(settingsPath));
        Assert.Equal(originalBytes, File.ReadAllBytes(settingsPath));
    }

    [Fact]
    public async Task DeleteSecret_RemovesPersistedSecret()
    {
        var services = CreateServices();
        await services.StoreSecretAsync("api-key", "secret-value");

        await services.DeleteSecretAsync("api-key");

        Assert.Null(await services.LoadSecretAsync("api-key"));
        Assert.Null(await CreateServices().LoadSecretAsync("api-key"));
    }

    [Fact]
    public async Task DeleteSecret_ThrowsWhenFileUnreadableAndLeavesSecretOnDisk()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
        {
            // Root can read a mode-000 file, so this cannot exercise the unreadable-file path.
            return;
        }

        var writer = CreateServices();
        await writer.StoreSecretAsync("api-key", "secret-value");

        var pluginDirectory = Path.Join(_tempDir, "PluginData", "test-plugin");
        var settingsPath = Path.Join(pluginDirectory, "settings.json");
        var originalBytes = File.ReadAllBytes(settingsPath);
        var originalMode = File.GetUnixFileMode(settingsPath);

        try
        {
            File.SetUnixFileMode(settingsPath, UnixFileMode.None);
            var services = CreateServices();

            await Assert.ThrowsAsync<IOException>(() => services.DeleteSecretAsync("api-key"));
        }
        finally
        {
            File.SetUnixFileMode(settingsPath, originalMode);
        }

        Assert.Equal(originalBytes, File.ReadAllBytes(settingsPath));
        Assert.Equal("secret-value", await CreateServices().LoadSecretAsync("api-key"));
    }

    private PluginHostServices CreateServices(
        Action? onCapabilitiesChanged = null,
        ISettingsService? settings = null
    )
    {
        return new PluginHostServices(
            "test-plugin",
            _tempDir,
            _activeWindow.Object,
            _eventBus.Object,
            _profiles.Object,
            settings,
            onCapabilitiesChanged,
            pluginDataRoot: Path.Join(_tempDir, "PluginData")
        );
    }
}
