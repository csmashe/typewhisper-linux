using Moq;
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
