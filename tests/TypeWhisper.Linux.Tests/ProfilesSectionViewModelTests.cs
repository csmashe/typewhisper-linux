using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class ProfilesSectionViewModelTests : IDisposable
{
    private readonly HotkeyService _hotkeys = TestShortcutBackend.CreateHotkeyService();
    private readonly string _tempDir = TestPaths.CreateTempDirectory(
        "TypeWhisper.ProfilesSectionViewModelTests"
    );
    private readonly UiOperationGuard _uiOperations = new(
        Mock.Of<IErrorLogService>(),
        _ => Task.CompletedTask,
        (operation, reason) => $"{operation} failed: {reason}"
    );

    public void Dispose()
    {
        _hotkeys.Dispose();
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
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            _uiOperations
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
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            _uiOperations
        );

        activeWindow.VerifyNoOtherCalls();
    }

    [Fact]
    public void LanguageChange_RebuildsLocalizedOptions_PreservesSelectionsWithoutPersisting()
    {
        var originalLanguage = Loc.Instance.CurrentLanguage;
        try
        {
            Loc.Instance.CurrentLanguage = "en";
            var profiles = new Mock<IProfileService>();
            profiles.SetupGet(service => service.Profiles).Returns([]);
            var activeWindow = CreateActiveWindowService();
            using var pluginManager = CreatePluginManager();
            var promptActions = new PromptActionService(
                Path.Join(_tempDir, "localized-prompt-actions.json")
            );
            var sut = new ProfilesSectionViewModel(
                profiles.Object,
                activeWindow.Object,
                pluginManager,
                promptActions,
                _hotkeys,
                Mock.Of<IDetectionFailureTracker>(),
                new GnomeWindowCallsSetupHelper(),
                new BrowserAccessibilitySetupHelper(),
                _uiOperations
            )
            {
                EditStylePreset = ProfileStylePreset.Developer,
                EditHotkeyBehavior = ProfileHotkeyBehavior.ProcessSelectedText,
                EditCleanupLevelOverride = CleanupLevel.High,
                EditWhisperModeOverride = true,
                EditDeveloperFormattingOverride = false,
            };

            var styleBefore = sut.SelectedStylePresetOption!;
            var hotkeyBefore = sut.SelectedHotkeyBehaviorOption!;
            var cleanupBefore = sut.SelectedCleanupOverrideOption!;
            var whisperBefore = sut.SelectedWhisperModeOption!;
            var modelDefaultBefore = sut.ModelOptions[0];
            var promptDefaultBefore = sut.PromptActionOptions[0];
            HashSet<string?> expectedPropertyChanges =
            [
                nameof(ProfilesSectionViewModel.Summary),
                nameof(ProfilesSectionViewModel.SelectedProfileSummary),
                nameof(ProfilesSectionViewModel.SelectedProfileDisplayName),
                nameof(ProfilesSectionViewModel.MatchStatusText),
                nameof(ProfilesSectionViewModel.EditIsEnabledStatusText),
            ];
            HashSet<string?> propertyChanges = [];

            sut.StylePresetOptions.CollectionChanged += (_, _) =>
                sut.SelectedStylePresetOption = null;
            sut.HotkeyBehaviorOptions.CollectionChanged += (_, _) =>
                sut.SelectedHotkeyBehaviorOption = null;
            sut.CleanupOverrideOptions.CollectionChanged += (_, _) =>
                sut.SelectedCleanupOverrideOption = null;
            sut.PropertyChanged += (_, args) =>
            {
                propertyChanges.Add(args.PropertyName);

                // ReSharper disable once InvertIf -- the positive form states the property this handler reacts to.
                if (args.PropertyName == nameof(ProfilesSectionViewModel.WhisperModeOptions))
                {
                    sut.SelectedWhisperModeOption = null;
                    sut.SelectedDeveloperFormattingOverrideOption = null;
                }
            };

            Loc.Instance.CurrentLanguage = "de";

            Assert.Superset(expectedPropertyChanges, propertyChanges);
            Assert.NotEqual(styleBefore.Label, sut.SelectedStylePresetOption?.Label);
            Assert.NotSame(styleBefore, sut.SelectedStylePresetOption);
            Assert.Equal(ProfileStylePreset.Developer, sut.EditStylePreset);
            Assert.NotEqual(hotkeyBefore.Label, sut.SelectedHotkeyBehaviorOption?.Label);
            Assert.NotSame(hotkeyBefore, sut.SelectedHotkeyBehaviorOption);
            Assert.Equal(ProfileHotkeyBehavior.ProcessSelectedText, sut.EditHotkeyBehavior);
            Assert.NotEqual(cleanupBefore.Label, sut.SelectedCleanupOverrideOption?.Label);
            Assert.NotSame(cleanupBefore, sut.SelectedCleanupOverrideOption);
            Assert.Equal(CleanupLevel.High, sut.EditCleanupLevelOverride);
            Assert.NotEqual(whisperBefore.Label, sut.SelectedWhisperModeOption?.Label);
            Assert.True(sut.EditWhisperModeOverride);
            Assert.False(sut.EditDeveloperFormattingOverride);
            Assert.NotEqual(modelDefaultBefore.Label, sut.ModelOptions[0].Label);
            Assert.NotEqual(promptDefaultBefore.Label, sut.PromptActionOptions[0].Label);
            profiles.Verify(
                service => service.UpdateProfile(It.IsAny<Profile>()),
                Times.Never
            );
        }
        finally
        {
            Loc.Instance.CurrentLanguage = originalLanguage;
        }
    }

    [Fact]
    public void ToggleProfileEnabled_UsesAtomicServiceOperationAndRefreshesProfiles()
    {
        var profile = CreateEditableProfile(hotkeyData: "Alt+F8") with { IsEnabled = false };
        var committed = profile with { IsEnabled = true };
        var profiles = new Mock<IProfileService>();
        profiles
            .SetupSequence(service => service.Profiles)
            .Returns([profile])
            .Returns([profile])
            .Returns([committed]);
        profiles
            .Setup(service => service.ToggleProfileEnabled(profile.Id))
            .Returns(committed);
        var activeWindow = CreateActiveWindowService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        var sut = new ProfilesSectionViewModel(
            profiles.Object,
            activeWindow.Object,
            pluginManager,
            promptActions,
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            _uiOperations
        );

        sut.ToggleProfileEnabledCommand.Execute(profile);

        profiles.Verify(service => service.ToggleProfileEnabled(profile.Id), Times.Once);
        profiles.Verify(service => service.UpdateProfile(It.IsAny<Profile>()), Times.Never);
        Assert.True(Assert.Single(sut.Profiles).IsEnabled);
    }

    [Fact]
    public void ToggleProfileEnabled_CollidingDisabledProfile_DoesNotCallAtomicToggleAndShowsFeedback()
    {
        var profile = CreateEditableProfile(hotkeyData: "Alt+F8") with { IsEnabled = false };
        var profiles = CreateProfileServiceMock(profile);
        var activeWindow = CreateActiveWindowService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        promptActions.AddAction(
            new PromptAction
            {
                Id = "enabled-action",
                Name = "Enabled action",
                SystemPrompt = "x",
                HotkeyKey = "Alt+F8",
            }
        );
        var sut = new ProfilesSectionViewModel(
            profiles.Object,
            activeWindow.Object,
            pluginManager,
            promptActions,
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            _uiOperations
        );

        sut.ToggleProfileEnabledCommand.Execute(profile);

        profiles.Verify(service => service.ToggleProfileEnabled(profile.Id), Times.Never);
        Assert.Equal(profile, sut.SelectedProfile);
        Assert.False(string.IsNullOrWhiteSpace(sut.HotkeyValidationMessage));
    }

    [Fact]
    public void ToggleProfileEnabled_ProcessSelectedTextWithoutEnabledAction_DoesNotEnable()
    {
        var profile = CreateEditableProfile(hotkeyData: "Meta+F9") with
        {
            IsEnabled = false,
            HotkeyBehavior = ProfileHotkeyBehavior.ProcessSelectedText,
            PromptActionId = "missing-action",
        };
        var profiles = CreateProfileServiceMock(profile);
        var activeWindow = CreateActiveWindowService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        var sut = new ProfilesSectionViewModel(
            profiles.Object,
            activeWindow.Object,
            pluginManager,
            promptActions,
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            _uiOperations
        );

        sut.ToggleProfileEnabledCommand.Execute(profile);

        profiles.Verify(service => service.ToggleProfileEnabled(profile.Id), Times.Never);
        Assert.Equal(profile, sut.SelectedProfile);
        Assert.Equal(
            Loc.Instance["Profiles.HotkeyPromptActionRequired"],
            sut.HotkeyValidationMessage
        );
    }

    [Fact]
    public void ToggleProfileEnabled_DisablingProfile_NeverRunsActivationGate()
    {
        var profile = CreateEditableProfile(hotkeyData: "Ctrl+NoSuchKey") with
        {
            HotkeyBehavior = ProfileHotkeyBehavior.ProcessSelectedText,
            PromptActionId = "missing-action",
        };
        var committed = profile with { IsEnabled = false };
        var profiles = new Mock<IProfileService>();
        profiles
            .SetupSequence(service => service.Profiles)
            .Returns([profile])
            .Returns([committed]);
        profiles
            .Setup(service => service.ToggleProfileEnabled(profile.Id))
            .Returns(committed);
        var activeWindow = CreateActiveWindowService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        var sut = new ProfilesSectionViewModel(
            profiles.Object,
            activeWindow.Object,
            pluginManager,
            promptActions,
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            _uiOperations
        );

        sut.ToggleProfileEnabledCommand.Execute(profile);

        profiles.Verify(service => service.ToggleProfileEnabled(profile.Id), Times.Once);
        Assert.False(Assert.Single(sut.Profiles).IsEnabled);
        Assert.Null(sut.HotkeyValidationMessage);
    }

    [Fact]
    public void AddProfile_PersistenceFailure_DoesNotEscapeAndResyncsAndPresents()
    {
        var committed = CreateEditableProfile();
        var profiles = new Mock<IProfileService>();
        profiles.SetupGet(service => service.Profiles).Returns([committed]);
        profiles
            .Setup(service => service.AddProfile(It.IsAny<Profile>()))
            .Throws(new IOException("disk full"));
        var presented = new List<string>();
        var uiOperations = new UiOperationGuard(
            Mock.Of<IErrorLogService>(),
            message =>
            {
                presented.Add(message);
                return Task.CompletedTask;
            },
            (operation, reason) => $"{operation} failed: {reason}"
        );
        var activeWindow = CreateActiveWindowService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        var sut = new ProfilesSectionViewModel(
            profiles.Object,
            activeWindow.Object,
            pluginManager,
            promptActions,
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            uiOperations
        );

        var exception = Record.Exception(() => sut.AddProfileCommand.Execute(null));

        Assert.Null(exception);
        Assert.Equal(committed, Assert.Single(sut.Profiles));
        Assert.Equal(committed, sut.SelectedProfile);
        Assert.Equal(committed.Name, sut.EditName);
        Assert.Equal(["Add failed: disk full"], presented);
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
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            _uiOperations
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
    public void SaveProfile_PersistenceFailure_DoesNotEscapeAndResyncsAndPresents()
    {
        var committed = CreateEditableProfile() with { Name = "Committed" };
        var profiles = new Mock<IProfileService>();
        profiles.SetupGet(service => service.Profiles).Returns([committed]);
        profiles
            .Setup(service => service.UpdateProfile(It.IsAny<Profile>()))
            .Throws(new UnauthorizedAccessException("read-only profile store"));
        var presented = new List<string>();
        var uiOperations = new UiOperationGuard(
            Mock.Of<IErrorLogService>(),
            message =>
            {
                presented.Add(message);
                return Task.CompletedTask;
            },
            (operation, reason) => $"{operation} failed: {reason}"
        );
        var activeWindow = CreateActiveWindowService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        var sut = new ProfilesSectionViewModel(
            profiles.Object,
            activeWindow.Object,
            pluginManager,
            promptActions,
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            uiOperations
        )
        {
            EditName = "Unsaved draft",
        };

        var exception = Record.Exception(() => sut.SaveProfileCommand.Execute(null));

        Assert.Null(exception);
        Assert.Equal(committed, Assert.Single(sut.Profiles));
        Assert.Equal(committed, sut.SelectedProfile);
        Assert.Equal("Committed", sut.EditName);
        Assert.Equal(["Save failed: read-only profile store"], presented);
    }

    [Fact]
    public void SaveProfile_MalformedBindingDoesNotUpdateAndShowsFeedback()
    {
        var existing = CreateEditableProfile(hotkeyData: "Alt+F8");
        var profiles = CreateProfileServiceMock(existing);
        var activeWindow = CreateActiveWindowService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        var sut = new ProfilesSectionViewModel(
            profiles.Object,
            activeWindow.Object,
            pluginManager,
            promptActions,
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            _uiOperations
        )
        {
            EditHotkeyData = "Ctrl+NoSuchKey",
        };

        sut.SaveProfileCommand.Execute(null);

        profiles.Verify(service => service.UpdateProfile(It.IsAny<Profile>()), Times.Never);
        Assert.Equal("Alt+F8", Assert.Single(profiles.Object.Profiles).HotkeyData);
        Assert.Equal("Ctrl+NoSuchKey", sut.EditHotkeyData);
        Assert.False(string.IsNullOrWhiteSpace(sut.HotkeyValidationMessage));
    }

    [Fact]
    public void SaveProfile_DisabledWithCollidingRetainedHotkey_PersistsNonHotkeyEdits()
    {
        var existing = CreateEditableProfile(hotkeyData: "Alt+F8") with { IsEnabled = false };
        var profiles = CreateProfileServiceMock(existing);
        Profile? persisted = null;
        profiles
            .Setup(service => service.UpdateProfile(It.IsAny<Profile>()))
            .Callback<Profile>(profile => persisted = profile);
        var activeWindow = CreateActiveWindowService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        promptActions.AddAction(
            new PromptAction
            {
                Id = "enabled-action",
                Name = "Enabled action",
                SystemPrompt = "x",
                HotkeyKey = "Alt+F8",
            }
        );
        var sut = new ProfilesSectionViewModel(
            profiles.Object,
            activeWindow.Object,
            pluginManager,
            promptActions,
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            _uiOperations
        )
        {
            EditName = "Updated profile",
        };

        sut.SaveProfileCommand.Execute(null);

        profiles.Verify(service => service.UpdateProfile(It.IsAny<Profile>()), Times.Once);
        Assert.NotNull(persisted);
        Assert.Equal("Updated profile", persisted.Name);
        Assert.Equal("Alt+F8", persisted.HotkeyData);
        Assert.False(persisted.IsEnabled);
        Assert.Null(sut.HotkeyValidationMessage);
    }

    [Fact]
    public void SaveProfile_EnablingDisabledProfileWithCollision_DoesNotUpdate()
    {
        var existing = CreateEditableProfile(hotkeyData: "Alt+F8") with { IsEnabled = false };
        var profiles = CreateProfileServiceMock(existing);
        var activeWindow = CreateActiveWindowService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        promptActions.AddAction(
            new PromptAction
            {
                Id = "enabled-action",
                Name = "Enabled action",
                SystemPrompt = "x",
                HotkeyKey = "Alt+F8",
            }
        );
        var sut = new ProfilesSectionViewModel(
            profiles.Object,
            activeWindow.Object,
            pluginManager,
            promptActions,
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            _uiOperations
        )
        {
            EditIsEnabled = true,
        };

        sut.SaveProfileCommand.Execute(null);

        profiles.Verify(service => service.UpdateProfile(It.IsAny<Profile>()), Times.Never);
        Assert.False(string.IsNullOrWhiteSpace(sut.HotkeyValidationMessage));
    }

    [Fact]
    public void SaveProfile_CrossDynamicPrefixCollisionDoesNotUpdate()
    {
        var existing = CreateEditableProfile();
        var profiles = CreateProfileServiceMock(existing);
        var activeWindow = CreateActiveWindowService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        promptActions.AddAction(
            new PromptAction
            {
                Id = "action",
                Name = "Action",
                SystemPrompt = "x",
                HotkeyKey = "Right Ctrl",
            }
        );
        var sut = new ProfilesSectionViewModel(
            profiles.Object,
            activeWindow.Object,
            pluginManager,
            promptActions,
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            _uiOperations
        )
        {
            EditHotkeyData = "Ctrl+Alt+E",
        };

        sut.SaveProfileCommand.Execute(null);

        profiles.Verify(service => service.UpdateProfile(It.IsAny<Profile>()), Times.Never);
        Assert.Null(existing.HotkeyData);
        Assert.False(string.IsNullOrWhiteSpace(sut.HotkeyValidationMessage));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("missing", false)]
    [InlineData("disabled", true)]
    public void SaveProfile_SelectedTextBindingRequiresEnabledPromptAction(
        string? promptActionId,
        bool addDisabledAction
    )
    {
        var existing = CreateEditableProfile();
        var profiles = CreateProfileServiceMock(existing);
        var activeWindow = CreateActiveWindowService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        if (addDisabledAction)
        {
            promptActions.AddAction(
                new PromptAction
                {
                    Id = "disabled",
                    Name = "Disabled",
                    SystemPrompt = "x",
                    IsEnabled = false,
                }
            );
        }

        var sut = new ProfilesSectionViewModel(
            profiles.Object,
            activeWindow.Object,
            pluginManager,
            promptActions,
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            _uiOperations
        )
        {
            EditHotkeyBehavior = ProfileHotkeyBehavior.ProcessSelectedText,
            EditPromptActionId = promptActionId,
            EditHotkeyData = "Meta+F9",
        };

        sut.SaveProfileCommand.Execute(null);

        profiles.Verify(service => service.UpdateProfile(It.IsAny<Profile>()), Times.Never);
        Assert.False(string.IsNullOrWhiteSpace(sut.HotkeyValidationMessage));
    }

    [Fact]
    public void SaveProfile_SelectedTextBindingWithEnabledActionPersistsCanonicalChord()
    {
        var existing = CreateEditableProfile();
        var profiles = CreateProfileServiceMock(existing);
        Profile? persisted = null;
        profiles
            .Setup(service => service.UpdateProfile(It.IsAny<Profile>()))
            .Callback<Profile>(profile => persisted = profile);
        var activeWindow = CreateActiveWindowService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        promptActions.AddAction(
            new PromptAction
            {
                Id = "enabled",
                Name = "Enabled",
                SystemPrompt = "x",
            }
        );
        var sut = new ProfilesSectionViewModel(
            profiles.Object,
            activeWindow.Object,
            pluginManager,
            promptActions,
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            _uiOperations
        )
        {
            EditHotkeyBehavior = ProfileHotkeyBehavior.ProcessSelectedText,
            EditPromptActionId = "enabled",
            EditHotkeyData = " super + f9 ",
        };

        sut.SaveProfileCommand.Execute(null);

        profiles.Verify(service => service.UpdateProfile(It.IsAny<Profile>()), Times.Once);
        Assert.NotNull(persisted);
        Assert.Equal("Meta+F9", persisted.HotkeyData);
        Assert.Equal("enabled", persisted.PromptActionId);
        Assert.Null(sut.HotkeyValidationMessage);
    }

    [Theory]
    [InlineData(" alt + f8 ", "Alt+F8")]
    [InlineData("   ", null)]
    public void SaveProfile_StartDictationAcceptsValidOrBlankBinding(
        string draft,
        string? expected
    )
    {
        var existing = CreateEditableProfile(hotkeyData: "Ctrl+Alt+E");
        var profiles = CreateProfileServiceMock(existing);
        Profile? persisted = null;
        profiles
            .Setup(service => service.UpdateProfile(It.IsAny<Profile>()))
            .Callback<Profile>(profile => persisted = profile);
        var activeWindow = CreateActiveWindowService();
        using var pluginManager = CreatePluginManager();
        var promptActions = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        var sut = new ProfilesSectionViewModel(
            profiles.Object,
            activeWindow.Object,
            pluginManager,
            promptActions,
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            _uiOperations
        )
        {
            EditHotkeyBehavior = ProfileHotkeyBehavior.StartDictation,
            EditHotkeyData = draft,
        };

        sut.SaveProfileCommand.Execute(null);

        profiles.Verify(service => service.UpdateProfile(It.IsAny<Profile>()), Times.Once);
        Assert.NotNull(persisted);
        Assert.Equal(expected, persisted.HotkeyData);
        Assert.Null(sut.HotkeyValidationMessage);
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
                UrlPatterns = [],
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
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            _uiOperations
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
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            _uiOperations
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
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            _uiOperations
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
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            _uiOperations
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
                SystemPrompt = "a",
            }
        );
        promptActions.AddAction(
            new PromptAction
            {
                Id = "manual",
                Name = "Manual",
                SystemPrompt = "m",
                IsManualOnly = true,
            }
        );

        var sut = new ProfilesSectionViewModel(
            service,
            activeWindow.Object,
            pluginManager,
            promptActions,
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            _uiOperations
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
            _hotkeys,
            Mock.Of<IDetectionFailureTracker>(),
            new GnomeWindowCallsSetupHelper(),
            new BrowserAccessibilitySetupHelper(),
            _uiOperations
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

    private static Profile CreateEditableProfile(string? hotkeyData = null)
    {
        return new Profile
        {
            Id = "profile",
            Name = "Profile",
            HotkeyData = hotkeyData,
        };
    }

    private static Mock<IProfileService> CreateProfileServiceMock(Profile profile)
    {
        var profiles = new Mock<IProfileService>();
        profiles.SetupGet(service => service.Profiles).Returns([profile]);
        return profiles;
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
