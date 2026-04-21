using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels.Sections;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ProfilesSectionViewModelTests : IDisposable
{
    private readonly string _tempDir;

    public ProfilesSectionViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TypeWhisper.Linux.Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void Constructor_SeedsGlobalDefaultModelOption()
    {
        var service = CreateProfileService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Combine(_tempDir, "prompt-actions.json"));

        var sut = new ProfilesSectionViewModel(service, pluginManager, promptActions);

        var option = Assert.Single(sut.ModelOptions);
        Assert.Equal("", option.Value);
        Assert.Equal("Use global default", option.Label);
    }

    [Fact]
    public void AddProfile_PersistsConfiguredOverrides()
    {
        var service = CreateProfileService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Combine(_tempDir, "prompt-actions.json"));

        var sut = new ProfilesSectionViewModel(service, pluginManager, promptActions)
        {
            NewName = "Docs",
            NewProcessNames = "firefox, chrome",
            NewUrlPatterns = "docs.google.com, *.github.com",
            NewInputLanguage = "de",
            NewSelectedTask = "translate",
            NewModelId = "plugin:com.typewhisper.sherpa-onnx:parakeet"
        };

        sut.AddProfileCommand.Execute(null);

        var profile = Assert.Single(service.Profiles);
        Assert.Equal("Docs", profile.Name);
        Assert.Equal(["firefox", "chrome"], profile.ProcessNames);
        Assert.Equal(["docs.google.com", "*.github.com"], profile.UrlPatterns);
        Assert.Equal("de", profile.InputLanguage);
        Assert.Equal("translate", profile.SelectedTask);
        Assert.Equal("plugin:com.typewhisper.sherpa-onnx:parakeet", profile.TranscriptionModelOverride);
    }

    private ProfileService CreateProfileService() =>
        new(Path.Combine(_tempDir, "profiles.json"));

    private static PluginManager CreatePluginManager()
    {
        var activeWindow = new Mock<IActiveWindowService>();
        var profiles = new Mock<IProfileService>();
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(s => s.Current).Returns(new AppSettings());
        profiles.SetupGet(p => p.Profiles).Returns([]);

        return new PluginManager(
            new PluginLoader(),
            new PluginEventBus(),
            activeWindow.Object,
            profiles.Object,
            settings.Object);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
        }
    }
}
