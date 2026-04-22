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
        var activeWindow = CreateActiveWindowService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Combine(_tempDir, "prompt-actions.json"));

        var sut = new ProfilesSectionViewModel(service, activeWindow.Object, pluginManager, promptActions);

        var option = Assert.Single(sut.ModelOptions);
        Assert.Null(option.Value);
        Assert.Equal("Use global default", option.Label);
    }

    [Fact]
    public void SaveProfile_PersistsConfiguredOverrides()
    {
        var service = CreateProfileService();
        var activeWindow = CreateActiveWindowService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Combine(_tempDir, "prompt-actions.json"));

        var sut = new ProfilesSectionViewModel(service, activeWindow.Object, pluginManager, promptActions);
        sut.AddProfileCommand.Execute(null);

        sut.EditName = "Docs";
        sut.ProcessNameInput = "firefox";
        sut.AddProcessNameChipCommand.Execute(null);
        sut.ProcessNameInput = "chrome";
        sut.AddProcessNameChipCommand.Execute(null);
        sut.UrlPatternInput = "docs.google.com";
        sut.AddUrlPatternChipCommand.Execute(null);
        sut.UrlPatternInput = "*.github.com";
        sut.AddUrlPatternChipCommand.Execute(null);
        sut.EditLanguage = "de";
        sut.EditTask = "translate";
        sut.EditTranslationTarget = "en";
        sut.EditWhisperModeOverride = true;
        sut.EditModelId = "plugin:com.typewhisper.sherpa-onnx:parakeet";
        sut.SaveProfileCommand.Execute(null);

        var profile = Assert.Single(service.Profiles);
        Assert.Equal("Docs", profile.Name);
        Assert.Equal(["firefox", "chrome"], profile.ProcessNames);
        Assert.Equal(["docs.google.com", "*.github.com"], profile.UrlPatterns);
        Assert.Equal("de", profile.InputLanguage);
        Assert.Equal("translate", profile.SelectedTask);
        Assert.Equal("en", profile.TranslationTarget);
        Assert.True(profile.WhisperModeOverride);
        Assert.Equal("plugin:com.typewhisper.sherpa-onnx:parakeet", profile.TranscriptionModelOverride);
    }

    [Fact]
    public void Constructor_TracksMatchedProfileAndCurrentProcess()
    {
        var service = CreateProfileService();
        service.AddProfile(new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Firefox",
            IsEnabled = true,
            Priority = 10,
            ProcessNames = ["firefox"],
            UrlPatterns = []
        });

        var activeWindow = CreateActiveWindowService();
        activeWindow.Setup(service => service.GetActiveWindowProcessName()).Returns("firefox");
        activeWindow.Setup(service => service.GetActiveWindowTitle()).Returns("Docs");
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Combine(_tempDir, "prompt-actions.json"));

        var sut = new ProfilesSectionViewModel(service, activeWindow.Object, pluginManager, promptActions);

        Assert.Equal("firefox", sut.CurrentProcessName);
        Assert.True(sut.HasMatchedProfile);
        Assert.Equal("Firefox", sut.MatchedProfileName);
        Assert.Equal("Matches Firefox", sut.MatchStatusText);
    }

    [Fact]
    public void AddCurrentProcessRule_AddsFocusedProcessToSelectedProfileDraft()
    {
        var service = CreateProfileService();
        var activeWindow = CreateActiveWindowService();
        activeWindow.Setup(service => service.GetActiveWindowProcessName()).Returns("firefox");
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Combine(_tempDir, "prompt-actions.json"));

        var sut = new ProfilesSectionViewModel(service, activeWindow.Object, pluginManager, promptActions);
        sut.AddProfileCommand.Execute(null);

        sut.AddCurrentProcessRuleCommand.Execute(null);

        Assert.Equal(["firefox"], sut.ProcessNameChips);
        Assert.Equal("1 app rule(s), 0 URL rule(s)", sut.SelectedProfileSummary);
    }

    private ProfileService CreateProfileService() =>
        new(Path.Combine(_tempDir, "profiles.json"));

    private static Mock<IActiveWindowService> CreateActiveWindowService()
    {
        var activeWindow = new Mock<IActiveWindowService>();
        activeWindow.Setup(service => service.GetActiveWindowProcessName()).Returns((string?)null);
        activeWindow.Setup(service => service.GetActiveWindowTitle()).Returns((string?)null);
        activeWindow.Setup(service => service.GetBrowserUrl()).Returns((string?)null);
        activeWindow.Setup(service => service.GetRunningAppProcessNames()).Returns([]);
        return activeWindow;
    }

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
