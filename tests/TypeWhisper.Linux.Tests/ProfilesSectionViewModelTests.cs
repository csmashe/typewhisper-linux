using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ProfilesSectionViewModelTests : IDisposable
{
    private readonly string _tempDir = TestPaths.CreateTempDirectory(
        "TypeWhisper.ProfilesSectionViewModelTests"
    );

    public void Dispose()
    {
        try
        {
            TestPaths.DeleteDirectory(_tempDir);
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
        }
    }

    [Fact]
    public void Constructor_SeedsGlobalDefaultModelOption()
    {
        var service = CreateProfileService();
        var activeWindow = CreateActiveWindowService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));

        var sut = new ProfilesSectionViewModel(
            service,
            activeWindow.Object,
            pluginManager,
            promptActions,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper()
        );

        var option = Assert.Single(sut.ModelOptions);
        Assert.Null(option.Value);
        Assert.Equal("Use global default", option.Label);
    }

    [Fact]
    public void Constructor_DoesNotInspectActiveWindow()
    {
        var service = CreateProfileService();
        var activeWindow = CreateActiveWindowService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));

        _ = new ProfilesSectionViewModel(
            service,
            activeWindow.Object,
            pluginManager,
            promptActions,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper()
        );

        activeWindow.VerifyNoOtherCalls();
    }

    [Fact]
    public void SaveProfile_PersistsConfiguredOverrides()
    {
        var service = CreateProfileService();
        var activeWindow = CreateActiveWindowService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));

        var sut = new ProfilesSectionViewModel(
            service,
            activeWindow.Object,
            pluginManager,
            promptActions,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper()
        );
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
        sut.EditStylePreset = ProfileStylePreset.Developer;
        sut.EditCleanupLevelOverride = CleanupLevel.Light;
        sut.EditDeveloperFormattingOverride = false;
        sut.SaveProfileCommand.Execute(null);

        var profile = Assert.Single(service.Profiles);
        Assert.Equal("Docs", profile.Name);
        Assert.Equal(["firefox", "chrome"], profile.ProcessNames);
        Assert.Equal(["docs.google.com", "*.github.com"], profile.UrlPatterns);
        Assert.Equal("de", profile.InputLanguage);
        Assert.Equal("translate", profile.SelectedTask);
        Assert.Equal("en", profile.TranslationTarget);
        Assert.True(profile.WhisperModeOverride);
        Assert.Equal(
            "plugin:com.typewhisper.sherpa-onnx:parakeet",
            profile.TranscriptionModelOverride
        );
        Assert.Equal(ProfileStylePreset.Developer, profile.StylePreset);
        Assert.Equal(CleanupLevel.Light, profile.CleanupLevelOverride);
        Assert.False(profile.DeveloperFormattingOverride);
    }

    [Fact]
    public async Task ActivateLiveContext_AppliesOneSnapshotAndTracksMatchedProfile()
    {
        var service = CreateProfileService();
        service.AddProfile(
            new Profile
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Firefox",
                IsEnabled = true,
                Priority = 10,
                ProcessNames = ["firefox"],
                UrlPatterns = []
            }
        );

        var activeWindow = CreateActiveWindowService();
        var snapshot = new ActiveWindowSnapshot("firefox", "Docs", "42", null, "test");
        activeWindow
            .Setup(s => s.GetActiveWindowSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        activeWindow
            .Setup(s => s.GetBrowserUrlForSnapshot(snapshot, true))
            .Returns("https://docs.example.com/page");
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));

        var sut = new ProfilesSectionViewModel(
            service,
            activeWindow.Object,
            pluginManager,
            promptActions,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper()
        );

        try
        {
            sut.ActivateLiveContext();
            await sut.CurrentWindowUpdateTask;

            Assert.Equal("firefox", sut.CurrentProcessName);
            Assert.Equal("Docs", sut.CurrentWindowTitle);
            Assert.Equal("https://docs.example.com/page", sut.CurrentUrl);
            Assert.True(sut.HasMatchedProfile);
            Assert.Equal("Firefox", sut.MatchedProfileName);
            Assert.Equal("Matches Firefox", sut.MatchStatusText);
            activeWindow.Verify(
                s => s.GetActiveWindowSnapshotAsync(It.IsAny<CancellationToken>()),
                Times.Once
            );
            activeWindow.Verify(s => s.GetBrowserUrlForSnapshot(snapshot, true), Times.Once);
        }
        finally
        {
            sut.DeactivateLiveContext();
        }
    }

    [Fact]
    public async Task DeactivateLiveContext_DiscardsCompletingInFlightUpdate()
    {
        var service = CreateProfileService();
        var activeWindow = CreateActiveWindowService();
        var snapshotSource = new TaskCompletionSource<ActiveWindowSnapshot?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var callStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        activeWindow
            .Setup(s => s.GetActiveWindowSnapshotAsync(It.IsAny<CancellationToken>()))
            .Returns(
                (CancellationToken _) =>
                {
                    callStarted.TrySetResult(true);
                    return snapshotSource.Task;
                }
            );
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        var sut = new ProfilesSectionViewModel(
            service,
            activeWindow.Object,
            pluginManager,
            promptActions,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper()
        );

        sut.ActivateLiveContext();
        var update = sut.CurrentWindowUpdateTask;
        await callStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sut.DeactivateLiveContext();
        snapshotSource.SetResult(
            new ActiveWindowSnapshot("firefox", "Late title", "42", null, "test")
        );
        await update.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("-", sut.CurrentProcessName);
        Assert.Equal("-", sut.CurrentWindowTitle);
        Assert.Equal("-", sut.CurrentUrl);
        activeWindow.Verify(
            s => s.GetBrowserUrlForSnapshot(It.IsAny<ActiveWindowSnapshot?>(), It.IsAny<bool>()),
            Times.Never
        );
    }

    [Fact]
    public async Task UpdateCurrentWindowAsync_IsSingleFlight()
    {
        var service = CreateProfileService();
        var activeWindow = CreateActiveWindowService();
        var snapshotSource = new TaskCompletionSource<ActiveWindowSnapshot?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var callStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        activeWindow
            .Setup(s => s.GetActiveWindowSnapshotAsync(It.IsAny<CancellationToken>()))
            .Returns(
                (CancellationToken _) =>
                {
                    callStarted.TrySetResult(true);
                    return snapshotSource.Task;
                }
            );
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        var sut = new ProfilesSectionViewModel(
            service,
            activeWindow.Object,
            pluginManager,
            promptActions,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper()
        );

        try
        {
            sut.ActivateLiveContext();
            var firstUpdate = sut.CurrentWindowUpdateTask;
            await callStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await sut.UpdateCurrentWindowAsync();

            activeWindow.Verify(
                s => s.GetActiveWindowSnapshotAsync(It.IsAny<CancellationToken>()),
                Times.Once
            );
            snapshotSource.SetResult(null);
            await firstUpdate.WaitAsync(TimeSpan.FromSeconds(5));
            activeWindow.Verify(
                s => s.GetBrowserUrlForSnapshot(It.IsAny<ActiveWindowSnapshot?>(), It.IsAny<bool>()),
                Times.Once
            );
        }
        finally
        {
            sut.DeactivateLiveContext();
        }
    }

    [Fact]
    public async Task LiveContextActivation_IsReferenceCounted()
    {
        var service = CreateProfileService();
        var activeWindow = CreateActiveWindowService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        var sut = new ProfilesSectionViewModel(
            service,
            activeWindow.Object,
            pluginManager,
            promptActions,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper()
        );

        sut.ActivateLiveContext();
        var update = sut.CurrentWindowUpdateTask;
        sut.ActivateLiveContext();
        sut.DeactivateLiveContext();

        Assert.True(sut.IsLiveContextActive);

        sut.DeactivateLiveContext();
        await update.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(sut.IsLiveContextActive);
    }

    [Fact]
    public void RefreshPromptActionOptions_ExcludesManualOnlyActions()
    {
        var service = CreateProfileService();
        var activeWindow = CreateActiveWindowService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        promptActions.AddAction(
            new PromptAction
            {
                Id = "auto",
                Name = "Auto",
                SystemPrompt = "a"
            }
        );
        promptActions.AddAction(
            new PromptAction
            {
                Id = "manual",
                Name = "Manual",
                SystemPrompt = "m",
                IsManualOnly = true
            }
        );

        var sut = new ProfilesSectionViewModel(
            service,
            activeWindow.Object,
            pluginManager,
            promptActions,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper()
        );

        // First entry is the "No prompt action" placeholder; the manual-only
        // action must be missing from the rest.
        Assert.DoesNotContain(sut.PromptActionOptions, option => option.Value == "manual");
        Assert.Contains(sut.PromptActionOptions, option => option.Value == "auto");
    }

    [Fact]
    public async Task AddCurrentProcessRule_AddsFocusedProcessToSelectedProfileDraft()
    {
        var service = CreateProfileService();
        var activeWindow = CreateActiveWindowService();
        activeWindow
            .Setup(s => s.GetActiveWindowSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActiveWindowSnapshot("firefox", "Docs", "42", null, "test"));
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));

        var sut = new ProfilesSectionViewModel(
            service,
            activeWindow.Object,
            pluginManager,
            promptActions,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper()
        );

        try
        {
            sut.ActivateLiveContext();
            await sut.CurrentWindowUpdateTask;
            sut.AddProfileCommand.Execute(null);

            sut.AddCurrentProcessRuleCommand.Execute(null);

            Assert.Equal(["firefox"], sut.ProcessNameChips);
            Assert.Equal("1 app rule(s), 0 URL rule(s)", sut.SelectedProfileSummary);
        }
        finally
        {
            sut.DeactivateLiveContext();
        }
    }

    private ProfileService CreateProfileService()
    {
        return new ProfileService(Path.Join(_tempDir, "profiles.json"));
    }

    private static Mock<IActiveWindowService> CreateActiveWindowService()
    {
        var activeWindow = new Mock<IActiveWindowService>();
        activeWindow.Setup(service => service.GetActiveWindowProcessName()).Returns((string?)null);
        activeWindow.Setup(service => service.GetActiveWindowTitle()).Returns((string?)null);
        activeWindow.Setup(service => service.GetBrowserUrl()).Returns((string?)null);
        activeWindow
            .Setup(service => service.GetActiveWindowSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((ActiveWindowSnapshot?)null);
        activeWindow
            .Setup(service =>
                service.GetBrowserUrlForSnapshot(It.IsAny<ActiveWindowSnapshot?>(), It.IsAny<bool>())
            )
            .Returns((string?)null);
        activeWindow.Setup(service => service.GetRunningAppProcessNames()).Returns([]);
        return activeWindow;
    }

    private PluginManager CreatePluginManager()
    {
        var activeWindow = new Mock<IActiveWindowService>();
        var profiles = new Mock<IProfileService>();
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(s => s.Current).Returns(new AppSettings());
        profiles.SetupGet(p => p.Profiles).Returns([]);

        return new PluginManager(
            new PluginLoader(Path.Join(_tempDir, "PluginData")),
            new PluginEventBus(),
            activeWindow.Object,
            profiles.Object,
            settings.Object,
            []
        );
    }
}
