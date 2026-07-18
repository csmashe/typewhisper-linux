using System.Reflection;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class PromptsSectionViewModelTests : IDisposable
{
    private readonly HotkeyService _hotkeys;
    private readonly ProfileService _profiles;
    private readonly string _tempDir;

    public PromptsSectionViewModelTests()
    {
        _tempDir = Path.Join(
            Path.GetTempPath(),
            "TypeWhisper.Linux.PromptVmTests_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempDir);
        _profiles = new ProfileService(Path.Join(_tempDir, "profiles.json"));
        _hotkeys = TestShortcutBackend.CreateHotkeyService();
    }

    public void Dispose()
    {
        _hotkeys.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best-effort cleanup for temp test directories.
        }
    }

    [Fact]
    public void SaveAction_PersistsProviderOverrideAndTargetAction()
    {
        var prompts = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        using var pluginManager = TestPluginManagerFactory.Create();
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());

        var sut = new PromptsSectionViewModel(prompts, _profiles, _hotkeys, pluginManager, settings.Object);
        sut.StartCreateCommand.Execute(null);
        sut.EditName = "Rewrite";
        sut.EditSystemPrompt = "Rewrite this";
        sut.EditProviderOverride = "plugin:com.typewhisper.openai:gpt-4.1-mini";
        sut.EditTargetActionPluginId = "com.typewhisper.linear";
        sut.SaveActionCommand.Execute(null);

        var action = Assert.Single(prompts.Actions);
        Assert.Equal("plugin:com.typewhisper.openai:gpt-4.1-mini", action.ProviderOverride);
        Assert.Equal("com.typewhisper.linear", action.TargetActionPluginId);
    }

    [Fact]
    public void SaveAction_PersistsHotkeyAndManualOnlyForNewAction()
    {
        var prompts = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        using var pluginManager = TestPluginManagerFactory.Create();
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());

        var sut = new PromptsSectionViewModel(prompts, _profiles, _hotkeys, pluginManager, settings.Object);
        sut.StartCreateCommand.Execute(null);
        sut.EditName = "Manual rewrite";
        sut.EditSystemPrompt = "Do it";
        sut.EditHotkeyKey = " control + ALT + r ";
        sut.EditIsManualOnly = true;
        sut.SaveActionCommand.Execute(null);

        var action = Assert.Single(prompts.Actions);
        Assert.Equal("Ctrl+Alt+R", action.HotkeyKey);
        Assert.True(action.IsManualOnly);
    }

    [Fact]
    public void SaveAction_PersistsHotkeyAndManualOnlyForExistingAction()
    {
        var prompts = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        prompts.AddAction(
            new PromptAction
            {
                Id = "existing",
                Name = "Existing",
                SystemPrompt = "x"
            }
        );
        using var pluginManager = TestPluginManagerFactory.Create();
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());

        var sut = new PromptsSectionViewModel(prompts, _profiles, _hotkeys, pluginManager, settings.Object);
        sut.SelectedAction = sut.Actions.Single(a => a.Id == "existing");
        sut.EditHotkeyKey = "Ctrl+Alt+T";
        sut.EditIsManualOnly = true;
        sut.SaveActionCommand.Execute(null);

        var action = Assert.Single(prompts.Actions);
        Assert.Equal("Ctrl+Alt+T", action.HotkeyKey);
        Assert.True(action.IsManualOnly);
    }

    [Fact]
    public void OnSelectedActionChanged_PopulatesHotkeyAndManualOnlyFromAction()
    {
        var prompts = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        prompts.AddAction(
            new PromptAction
            {
                Id = "existing",
                Name = "Existing",
                SystemPrompt = "x",
                HotkeyKey = "Ctrl+Alt+R",
                IsManualOnly = true
            }
        );
        using var pluginManager = TestPluginManagerFactory.Create();
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());

        var sut = new PromptsSectionViewModel(prompts, _profiles, _hotkeys, pluginManager, settings.Object);
        sut.SelectedAction = sut.Actions.Single(a => a.Id == "existing");

        Assert.Equal("Ctrl+Alt+R", sut.EditHotkeyKey);
        Assert.True(sut.EditIsManualOnly);
    }

    [Fact]
    public void SaveAction_BlankHotkeyKeyPersistsAsNull()
    {
        var prompts = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        using var pluginManager = TestPluginManagerFactory.Create();
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());

        var sut = new PromptsSectionViewModel(prompts, _profiles, _hotkeys, pluginManager, settings.Object);
        sut.StartCreateCommand.Execute(null);
        sut.EditName = "Blank";
        sut.EditSystemPrompt = "x";
        sut.EditHotkeyKey = "   ";
        sut.SaveActionCommand.Execute(null);

        var action = Assert.Single(prompts.Actions);
        Assert.Null(action.HotkeyKey);
    }

    [Fact]
    public void SaveAction_MalformedNewDraftDoesNotPersistAndShowsFeedback()
    {
        var prompts = new Mock<IPromptActionService>();
        prompts.SetupGet(service => service.Actions).Returns([]);
        prompts.SetupGet(service => service.EnabledActions).Returns([]);
        using var pluginManager = TestPluginManagerFactory.Create();
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());
        var sut = new PromptsSectionViewModel(
            prompts.Object,
            _profiles,
            _hotkeys,
            pluginManager,
            settings.Object
        );
        sut.StartCreateCommand.Execute(null);
        sut.EditName = "Invalid";
        sut.EditSystemPrompt = "x";
        sut.EditHotkeyKey = "Ctrl+DefinitelyNotAKey";

        sut.SaveActionCommand.Execute(null);

        prompts.Verify(service => service.AddAction(It.IsAny<PromptAction>()), Times.Never);
        Assert.True(sut.ShowEditor);
        Assert.True(sut.IsCreatingNew);
        Assert.Equal("Ctrl+DefinitelyNotAKey", sut.EditHotkeyKey);
        Assert.False(string.IsNullOrWhiteSpace(sut.HotkeyValidationMessage));
    }

    [Fact]
    public void SaveAction_MalformedExistingDraftDoesNotUpdate()
    {
        var existing = new PromptAction
        {
            Id = "existing",
            Name = "Existing",
            SystemPrompt = "x",
            HotkeyKey = "Alt+F8"
        };
        var prompts = new Mock<IPromptActionService>();
        prompts.SetupGet(service => service.Actions).Returns([existing]);
        prompts.SetupGet(service => service.EnabledActions).Returns([existing]);
        using var pluginManager = TestPluginManagerFactory.Create();
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());
        var sut = new PromptsSectionViewModel(
            prompts.Object,
            _profiles,
            _hotkeys,
            pluginManager,
            settings.Object
        );
        sut.SelectedAction = Assert.Single(sut.Actions);
        sut.EditHotkeyKey = "Ctrl+NoSuchKey";

        sut.SaveActionCommand.Execute(null);

        prompts.Verify(service => service.UpdateAction(It.IsAny<PromptAction>()), Times.Never);
        Assert.Equal("Ctrl+NoSuchKey", sut.EditHotkeyKey);
        Assert.False(string.IsNullOrWhiteSpace(sut.HotkeyValidationMessage));
    }

    [Fact]
    public void SaveAction_CrossDynamicPrefixCollisionDoesNotPersist()
    {
        _profiles.AddProfile(
            new Profile
            {
                Id = "profile",
                Name = "Profile",
                HotkeyData = "Right Ctrl"
            }
        );
        var prompts = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        using var pluginManager = TestPluginManagerFactory.Create();
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());
        var sut = new PromptsSectionViewModel(
            prompts,
            _profiles,
            _hotkeys,
            pluginManager,
            settings.Object
        );
        sut.StartCreateCommand.Execute(null);
        sut.EditName = "Collision";
        sut.EditSystemPrompt = "x";
        sut.EditHotkeyKey = "Ctrl+Alt+R";

        sut.SaveActionCommand.Execute(null);

        Assert.Empty(prompts.Actions);
        Assert.False(string.IsNullOrWhiteSpace(sut.HotkeyValidationMessage));
    }

    [Fact]
    public void SelectedEditProvider_UpdatesProviderOverride()
    {
        var prompts = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        var provider = new FakeLlmProviderPlugin(
            "com.typewhisper.openai",
            "OpenAI",
            "gpt-4.1-mini"
        );
        using var pluginManager = TestPluginManagerFactory.Create(
            [provider],
            loadedPlugins:
            [
                TestPluginManagerFactory.CreateLoadedPlugin(_tempDir, provider.PluginId, provider)
            ]
        );
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());

        var sut = new PromptsSectionViewModel(prompts, _profiles, _hotkeys, pluginManager, settings.Object);
        var option = Assert.Single(
            sut.AvailableProviders,
            candidate => candidate.Value == "plugin:com.typewhisper.openai:gpt-4.1-mini"
        );

        sut.SelectedEditProvider = option;

        Assert.Equal("plugin:com.typewhisper.openai:gpt-4.1-mini", sut.EditProviderOverride);
        Assert.Equal(option, sut.SelectedEditProvider);
    }

    [Fact]
    public void SelectedSpokenCommandProvider_PersistsToSettings()
    {
        var prompts = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        var provider = new FakeLlmProviderPlugin(
            "com.typewhisper.openai",
            "OpenAI",
            "gpt-4.1-mini"
        );
        using var pluginManager = TestPluginManagerFactory.Create(
            [provider],
            loadedPlugins:
            [
                TestPluginManagerFactory.CreateLoadedPlugin(_tempDir, provider.PluginId, provider)
            ]
        );
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());

        var sut = new PromptsSectionViewModel(prompts, _profiles, _hotkeys, pluginManager, settings.Object);
        var option = Assert.Single(
            sut.AvailableProviders,
            candidate => candidate.Value == "plugin:com.typewhisper.openai:gpt-4.1-mini"
        );

        sut.SelectedSpokenCommandProvider = option;

        Assert.Equal(
            "plugin:com.typewhisper.openai:gpt-4.1-mini",
            settings.Object.Current.SpokenCommandLlmProvider
        );
        Assert.Equal(option, sut.SelectedSpokenCommandProvider);

        // Selecting the "use default provider" placeholder clears the override.
        var defaultOption = Assert.Single(sut.AvailableProviders, candidate => candidate.Value is null);
        sut.SelectedSpokenCommandProvider = defaultOption;

        Assert.Null(settings.Object.Current.SpokenCommandLlmProvider);
    }

    [Fact]
    public void SelectedEditProvider_IgnoresTransientSelectionChangesDuringProviderRefresh()
    {
        var prompts = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        var provider = new FakeLlmProviderPlugin(
            "com.typewhisper.openai",
            "OpenAI",
            "gpt-4.1-mini"
        );
        using var pluginManager = TestPluginManagerFactory.Create(
            [provider],
            loadedPlugins:
            [
                TestPluginManagerFactory.CreateLoadedPlugin(_tempDir, provider.PluginId, provider)
            ]
        );
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());
        var sut = new PromptsSectionViewModel(prompts, _profiles, _hotkeys, pluginManager, settings.Object)
        {
            EditProviderOverride = "plugin:com.typewhisper.openai:gpt-4.1-mini"
        };
        // Simulate the guard flag that the view-model sets while it rebuilds
        // the provider list — a null selection during that window must not
        // clear a previously configured override.
        SetPrivateField(sut, "_isRefreshingProviders", true);

        sut.SelectedEditProvider = null;

        Assert.Equal("plugin:com.typewhisper.openai:gpt-4.1-mini", sut.EditProviderOverride);
    }

    [Fact]
    public void CommandSettings_HydrateFromSettingsWithoutPersisting()
    {
        var prompts = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        using var pluginManager = TestPluginManagerFactory.Create();
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { CommandModeEnabled = true, CommandKeyphrase = "Jarvis" }
        );

        var sut = new PromptsSectionViewModel(prompts, _profiles, _hotkeys, pluginManager, settings.Object);

        Assert.True(sut.CommandModeEnabled);
        Assert.Equal("Jarvis", sut.CommandKeyphrase);
        // Hydration must not write the values it just read back to settings.
        settings.Verify(service => service.Save(It.IsAny<AppSettings>()), Times.Never);
    }

    [Fact]
    public void CommandSettings_HydrateBlankKeyphraseFallsBackToDefault()
    {
        var prompts = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        using var pluginManager = TestPluginManagerFactory.Create();
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { CommandKeyphrase = "   " }
        );

        var sut = new PromptsSectionViewModel(prompts, _profiles, _hotkeys, pluginManager, settings.Object);

        Assert.Equal(AppSettings.DefaultCommandKeyphrase, sut.CommandKeyphrase);
    }

    [Fact]
    public void CommandModeEnabled_TogglePersistsToSettings()
    {
        var prompts = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        using var pluginManager = TestPluginManagerFactory.Create();
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());

        var sut = new PromptsSectionViewModel(prompts, _profiles, _hotkeys, pluginManager, settings.Object)
        {
            CommandModeEnabled = true
        };

        Assert.True(sut.CommandModeEnabled);
        Assert.True(settings.Object.Current.CommandModeEnabled);
        settings.Verify(
            service => service.Save(It.Is<AppSettings>(saved => saved.CommandModeEnabled)),
            Times.Once
        );
    }

    [Fact]
    public void CommandKeyphrase_TrimmedValuePersistsNormalizedOnce()
    {
        var prompts = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        using var pluginManager = TestPluginManagerFactory.Create();
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());

        var sut = new PromptsSectionViewModel(prompts, _profiles, _hotkeys, pluginManager, settings.Object)
        {
            CommandKeyphrase = "  Jarvis  "
        };

        // The re-entrant normalization must land the trimmed value and persist it exactly once.
        Assert.Equal("Jarvis", sut.CommandKeyphrase);
        Assert.Equal("Jarvis", settings.Object.Current.CommandKeyphrase);
        settings.Verify(
            service => service.Save(It.Is<AppSettings>(saved => saved.CommandKeyphrase == "Jarvis")),
            Times.Once
        );
    }

    [Fact]
    public void CommandKeyphrase_BlankValueFallsBackToDefaultAndPersists()
    {
        var prompts = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        using var pluginManager = TestPluginManagerFactory.Create();
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { CommandKeyphrase = "Jarvis" }
        );

        var sut = new PromptsSectionViewModel(prompts, _profiles, _hotkeys, pluginManager, settings.Object)
        {
            CommandKeyphrase = "   "
        };

        Assert.Equal(AppSettings.DefaultCommandKeyphrase, sut.CommandKeyphrase);
        Assert.Equal(AppSettings.DefaultCommandKeyphrase, settings.Object.Current.CommandKeyphrase);
    }

    [Fact]
    public void CommandKeyphrase_UnchangedNormalizedValueHitsNoOpGuard()
    {
        var prompts = new PromptActionService(Path.Join(_tempDir, "prompt-actions.json"));
        using var pluginManager = TestPluginManagerFactory.Create();
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { CommandKeyphrase = "Jarvis" }
        );

        var sut = new PromptsSectionViewModel(prompts, _profiles, _hotkeys, pluginManager, settings.Object)
        {
            // Whitespace that normalizes back to the already-saved value: no persist.
            CommandKeyphrase = "  Jarvis  "
        };

        Assert.Equal("Jarvis", sut.CommandKeyphrase);
        settings.Verify(service => service.Save(It.IsAny<AppSettings>()), Times.Never);
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field =
            target
                .GetType()
                .GetField(
                    fieldName,
                    BindingFlags.Instance
                    | BindingFlags.NonPublic
                )
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        field.SetValue(target, value);
    }

    private sealed class FakeLlmProviderPlugin : ILlmProviderPlugin
    {
        public FakeLlmProviderPlugin(string pluginId, string providerName, string modelId)
        {
            PluginId = pluginId;
            ProviderName = providerName;
            SupportedModels = [new PluginModelInfo(modelId, "GPT-4.1 Mini")];
        }

        public string PluginId { get; }
        public string PluginName => ProviderName;
        public string PluginVersion => "1.0.0";
        public string ProviderName { get; }
        public bool IsAvailable => true;
        public IReadOnlyList<PluginModelInfo> SupportedModels { get; }

        public Task ActivateAsync(IPluginHostServices host)
        {
            return Task.CompletedTask;
        }

        public Task DeactivateAsync()
        {
            return Task.CompletedTask;
        }

        public Task<string> ProcessAsync(
            string systemPrompt,
            string userText,
            string model,
            CancellationToken ct
        )
        {
            return Task.FromResult(userText);
        }

        public void Dispose() { }
    }
}
