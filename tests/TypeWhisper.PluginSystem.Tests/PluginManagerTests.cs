using Moq;
using System.Reflection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Tests;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class PluginManagerTests : IDisposable
{
    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly PluginEventBus _eventBus = new();
    private readonly PluginLoader _loader;
    private readonly string _pluginSearchDir;
    private readonly Mock<IProfileService> _profiles = new();
    private readonly Mock<ISettingsService> _settings = new();
    private PluginManager? _manager;

    public PluginManagerTests()
    {
        _pluginSearchDir = TestPaths.CreateTempDirectory(
            "TypeWhisper.PluginManagerTests"
        );
        _loader = new PluginLoader(Path.Join(_pluginSearchDir, "PluginData"));
        _profiles.Setup(p => p.Profiles).Returns(new List<Profile>());
        _settings.Setup(s => s.Current).Returns(new AppSettings());
    }

    public void Dispose()
    {
        _manager?.Dispose();
        try
        {
            TestPaths.DeleteDirectory(_pluginSearchDir);
        }
        catch
        {
            // Best-effort cleanup in tests
        }
    }

    [Fact]
    public async Task InitializeAsync_WithNoPluginDirs_AllPluginsIsEmpty()
    {
        var manager = CreateManager();

        await manager.InitializeAsync();

        Assert.Empty(manager.AllPlugins);
        Assert.Empty(manager.LlmProviders);
        Assert.Empty(manager.TranscriptionEngines);
        Assert.Empty(manager.PostProcessors);
    }

    [Fact]
    public void IsEnabled_UnknownPlugin_ReturnsFalse()
    {
        var manager = CreateManager();
        Assert.False(manager.IsEnabled("com.nonexistent.plugin"));
    }

    [Fact]
    public void GetPlugin_UnknownPlugin_ReturnsNull()
    {
        var manager = CreateManager();
        Assert.Null(manager.GetPlugin("com.nonexistent.plugin"));
    }

    [Fact]
    public async Task EnablePluginAsync_UnknownPlugin_DoesNotThrow()
    {
        var manager = CreateManager();
        var ex = await Record.ExceptionAsync(() => manager.EnablePluginAsync("com.nonexistent"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task DisablePluginAsync_UnknownPlugin_DoesNotThrow()
    {
        var manager = CreateManager();
        var ex = await Record.ExceptionAsync(() => manager.DisablePluginAsync("com.nonexistent"));
        Assert.Null(ex);
    }

    [Fact]
    public void EventBus_ReturnsSameInstance()
    {
        var manager = CreateManager();
        Assert.Same(_eventBus, manager.EventBus);
    }

    [Fact]
    public void Dispose_WithNoPlugins_DoesNotThrow()
    {
        var manager = CreateManager();
        var ex = Record.Exception(manager.Dispose);
        Assert.Null(ex);
    }

    [Fact]
    public async Task InitializeAsync_PersistsEnabledState_RespectedFromSettings()
    {
        var customSettings = new AppSettings
        {
            PluginEnabledState = new Dictionary<string, bool> { ["com.test.plugin"] = true },
        };
        _settings.Setup(s => s.Current).Returns(customSettings);

        var manager = CreateManager();
        await manager.InitializeAsync();

        Assert.Empty(manager.AllPlugins);
    }

    [Fact]
    public async Task InitializeAsync_EmptyPluginEnabledState_NoError()
    {
        _settings
            .Setup(s => s.Current)
            .Returns(new AppSettings { PluginEnabledState = new Dictionary<string, bool>() });

        var manager = CreateManager();
        var ex = await Record.ExceptionAsync(manager.InitializeAsync);
        Assert.Null(ex);
    }

    [Fact]
    public async Task CapabilityIndices_EmptyAfterInit_WithNoPlugins()
    {
        var manager = CreateManager();
        await manager.InitializeAsync();

        Assert.Empty(manager.LlmProviders);
        Assert.Empty(manager.TranscriptionEngines);
        Assert.Empty(manager.PostProcessors);
        Assert.Empty(manager.ActionPlugins);
    }

    [Fact]
    public async Task ActionPlugins_EmptyAfterInit_WithNoPlugins()
    {
        var manager = CreateManager();
        await manager.InitializeAsync();

        Assert.Empty(manager.ActionPlugins);
    }

    [Fact]
    public async Task PluginStateChanged_FiredOnInitialize()
    {
        var manager = CreateManager();
        var eventFired = false;
        manager.PluginStateChanged += (_, _) => eventFired = true;

        await manager.InitializeAsync();

        Assert.True(eventFired);
    }

    [Fact]
    public async Task LegacyRootKey_AuthenticatedAndPersisted_ClearsRootField()
    {
        var keyPath = Path.Join(_pluginSearchDir, "secret-protection.key");
        var protectedValue = ApiKeyProtection.Encrypt("provider-secret", keyPath);
        var current = new AppSettings { GroqApiKey = protectedValue };
        _settings.Setup(settings => settings.Current).Returns(() => current);
        _settings
            .Setup(settings => settings.Save(It.IsAny<AppSettings>()))
            .Callback<AppSettings>(saved => current = saved);
        var manager = CreateManager();
        AddPluginHost(manager, "com.typewhisper.groq", keyPath);

        await InvokeRootKeyMigration(manager);

        Assert.Equal("", current.GroqApiKey);
        var host = GetPluginHosts(manager)["com.typewhisper.groq"];
        Assert.Equal("provider-secret", await host.LoadSecretAsync("api-key"));
    }

    [Fact]
    public async Task LegacyRootKey_Undecryptable_DoesNotClearOrPersistCiphertext()
    {
        var keyPath = Path.Join(_pluginSearchDir, "secret-protection.key");
        var protectedValue = ApiKeyProtection.Encrypt("provider-secret", keyPath);
        var tampered = Convert.FromBase64String(protectedValue);
        tampered[^1] ^= 0x01;
        var stored = Convert.ToBase64String(tampered);
        var current = new AppSettings { GroqApiKey = stored };
        _settings.Setup(settings => settings.Current).Returns(() => current);
        _settings
            .Setup(settings => settings.Save(It.IsAny<AppSettings>()))
            .Callback<AppSettings>(saved => current = saved);
        var manager = CreateManager();
        AddPluginHost(manager, "com.typewhisper.groq", keyPath);

        await InvokeRootKeyMigration(manager);

        Assert.Equal(stored, current.GroqApiKey);
        Assert.Null(
            await GetPluginHosts(manager)["com.typewhisper.groq"]
                .LoadSecretAsync("api-key")
        );
        _settings.Verify(settings => settings.Save(It.IsAny<AppSettings>()), Times.Never);
    }

    private PluginManager CreateManager()
    {
        _manager = new PluginManager(
            _loader,
            _eventBus,
            _activeWindow.Object,
            _profiles.Object,
            _settings.Object,
            [_pluginSearchDir]
        );
        return _manager;
    }

    private void AddPluginHost(
        PluginManager manager,
        string pluginId,
        string keyPath
    )
    {
        GetPluginHosts(manager)[pluginId] = new PluginHostServices(
            pluginId,
            _pluginSearchDir,
            _activeWindow.Object,
            _eventBus,
            _profiles.Object,
            pluginDataRoot: Path.Join(_pluginSearchDir, "PluginData"),
            secretProtectionKeyFilePath: keyPath
        );
    }

    private static Dictionary<string, PluginHostServices> GetPluginHosts(
        PluginManager manager
    )
    {
        var field =
            typeof(PluginManager).GetField(
                "_hostServices",
                BindingFlags.Instance | BindingFlags.NonPublic
            )
            ?? throw new MissingFieldException(
                typeof(PluginManager).FullName,
                "_hostServices"
            );
        return (Dictionary<string, PluginHostServices>)field.GetValue(manager)!;
    }

    private static async Task InvokeRootKeyMigration(PluginManager manager)
    {
        var method =
            typeof(PluginManager).GetMethod(
                "MigrateApiKeysAsync",
                BindingFlags.Instance | BindingFlags.NonPublic
            )
            ?? throw new MissingMethodException(
                typeof(PluginManager).FullName,
                "MigrateApiKeysAsync"
            );
        await (Task)method.Invoke(manager, null)!;
    }
}

// Verifies enable/disable/capability-index logic without loading real plugin assemblies.
public sealed class PluginManagerWithFakePluginTests : IDisposable
{
    private static readonly TimeSpan s_shutdownTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan s_outerTimeout = TimeSpan.FromSeconds(2);

    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly Mock<IErrorLogService> _errorLog = new();
    private readonly PluginEventBus _eventBus = new();
    private readonly Mock<IProfileService> _profiles = new();
    private readonly Mock<ISettingsService> _settings = new();
    private PluginManager? _manager;

    public PluginManagerWithFakePluginTests()
    {
        _profiles.Setup(p => p.Profiles).Returns(new List<Profile>());
        _settings.Setup(s => s.Current).Returns(new AppSettings());
    }

    public void Dispose()
    {
        _manager?.Dispose();
    }

    [Fact]
    public async Task EnableAndDisable_TracksActivationState()
    {
        var mockPlugin = new Mock<ILlmProviderPlugin>();
        mockPlugin.Setup(p => p.PluginId).Returns("com.test.fake");
        mockPlugin.Setup(p => p.PluginName).Returns("Fake LLM");
        mockPlugin.Setup(p => p.PluginVersion).Returns("1.0.0");
        mockPlugin
            .Setup(p => p.ActivateAsync(It.IsAny<IPluginHostServices>()))
            .Returns(Task.CompletedTask);
        mockPlugin.Setup(p => p.DeactivateAsync()).Returns(Task.CompletedTask);
        mockPlugin.Setup(p => p.ProviderName).Returns("FakeProvider");
        mockPlugin.Setup(p => p.IsAvailable).Returns(true);
        mockPlugin.Setup(p => p.SupportedModels).Returns(new List<PluginModelInfo>());

        _manager = new PluginManager(
            new PluginLoader(TestPaths.NewTempPath("TypeWhisper.PluginManagerData")),
            _eventBus,
            _activeWindow.Object,
            _profiles.Object,
            _settings.Object,
            []
        );

        Assert.False(_manager.IsEnabled("com.test.fake"));

        await _manager.EnablePluginAsync("com.test.fake");
        Assert.False(_manager.IsEnabled("com.test.fake"));
    }

    [Fact]
    public async Task DisablePluginAsync_NotActivated_PersistsDisabledState()
    {
        AppSettings? savedSettings = null;
        _settings
            .Setup(s => s.Save(It.IsAny<AppSettings>()))
            .Callback<AppSettings>(s => savedSettings = s);

        _manager = new PluginManager(
            new PluginLoader(TestPaths.NewTempPath("TypeWhisper.PluginManagerData")),
            _eventBus,
            _activeWindow.Object,
            _profiles.Object,
            _settings.Object,
            []
        );

        await _manager.DisablePluginAsync("com.test.notfound");

        Assert.Null(savedSettings);
    }

    [Theory]
    [InlineData(PluginNetworkAccess.Local, false, true)]
    [InlineData(PluginNetworkAccess.Network, true, false)]
    [InlineData(PluginNetworkAccess.Mixed, true, false)]
    [InlineData(PluginNetworkAccess.UserControlled, true, false)]
    public void DefaultActivation_UsesDescriptorInsteadOfLegacyManifestFlag(
        PluginNetworkAccess networkAccess,
        bool legacyIsLocal,
        bool expectedEnabled
    )
    {
        var plugin = new LifecyclePlugin(
            "com.test.default-activation",
            () => Task.CompletedTask
        );
        var loaded = CreateLoadedPlugin(plugin, networkAccess, legacyIsLocal);

        Assert.Equal(expectedEnabled, PluginManager.IsEnabledByDefault(loaded));
    }

    [Fact]
    public async Task CapabilityIndices_ValidCustomTranscriptionId_RoundTripsWhileColonSiblingIsRejected()
    {
        const string validSelectionId = "server.work_1";
        var invalidProvider = new IdentifiedTranscriptionPlugin(
            "com.test.invalid-transcription",
            "server:work"
        );
        var validSibling = new IdentifiedTranscriptionPlugin(
            "com.test.valid-transcription",
            validSelectionId
        );

        var manager = await CreateManagerAsync(invalidProvider, validSibling);

        Assert.DoesNotContain(invalidProvider, manager.TranscriptionEngines);
        Assert.Contains(validSibling, manager.TranscriptionEngines);
        _errorLog.Verify(
            log => log.AddEntry(
                It.Is<string>(message =>
                    message.Contains("Skipping transcription engine", StringComparison.Ordinal)
                    && message.Contains("[A-Za-z0-9._-]+", StringComparison.Ordinal)
                ),
                ErrorCategory.Transcription
            ),
            Times.AtLeastOnce
        );

        var persistedId = ModelManagerService.GetPluginModelId(
            validSibling.GetTranscriptionSelectionId(),
            validSibling.TranscriptionModels[0].Id
        );
        var parsedId = ModelManagerService.ParsePluginModelId(persistedId);
        var resolvedProvider = Assert.Single(manager.TranscriptionEngines, provider =>
            provider.GetTranscriptionSelectionId() == parsedId.PluginId
        );

        Assert.Equal(validSelectionId, parsedId.PluginId);
        Assert.Equal(validSibling.TranscriptionModels[0].Id, parsedId.ModelId);
        Assert.Same(validSibling, resolvedProvider);
    }

    [Fact]
    public async Task CapabilityIndices_PluginIdFallbackLlmRoleRoundTripsWhileColonSiblingIsRejected()
    {
        var invalidProvider = new IdentifiedLlmPlugin(
            "com.test.invalid-llm",
            "server:work"
        );
        var validSibling = new FakeLlmPlugin("com.test.valid-llm");

        var manager = await CreateManagerAsync(invalidProvider, validSibling);

        Assert.DoesNotContain(invalidProvider, manager.LlmProviders);
        Assert.Contains(validSibling, manager.LlmProviders);
        Assert.Equal(validSibling.PluginId, validSibling.GetLlmSelectionId());
        _errorLog.Verify(
            log => log.AddEntry(
                It.Is<string>(message =>
                    message.Contains("Skipping LLM provider", StringComparison.Ordinal)
                    && message.Contains("[A-Za-z0-9._-]+", StringComparison.Ordinal)
                ),
                ErrorCategory.Prompt
            ),
            Times.AtLeastOnce
        );

        var persistedId = ModelManagerService.GetPluginModelId(
            validSibling.GetLlmSelectionId(),
            validSibling.SupportedModels[0].Id
        );
        var parsedId = ModelManagerService.ParsePluginModelId(persistedId);
        var resolvedProvider = Assert.Single(manager.LlmProviders, provider =>
            provider.GetLlmSelectionId() == parsedId.PluginId
        );

        Assert.Equal(validSibling.PluginId, parsedId.PluginId);
        Assert.Equal(validSibling.SupportedModels[0].Id, parsedId.ModelId);
        Assert.Same(validSibling, resolvedProvider);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    public async Task CapabilityIndices_EmptyOrWhitespaceCustomId_FallsBackThenValidatesPluginId(
        string customSelectionId
    )
    {
        var invalidFallback = new IdentifiedTranscriptionPlugin(
            "invalid:fallback",
            customSelectionId
        );
        var validFallback = new IdentifiedTranscriptionPlugin(
            "com.test.valid_fallback",
            customSelectionId
        );

        var manager = await CreateManagerAsync(invalidFallback, validFallback);

        Assert.DoesNotContain(invalidFallback, manager.TranscriptionEngines);
        Assert.Contains(validFallback, manager.TranscriptionEngines);
        Assert.Equal(
            validFallback.PluginId,
            validFallback.GetTranscriptionSelectionId()
        );
        _errorLog.Verify(
            log => log.AddEntry(
                It.Is<string>(message =>
                    message.Contains("Skipping transcription engine", StringComparison.Ordinal)
                ),
                ErrorCategory.Transcription
            ),
            Times.AtLeastOnce
        );
    }

    [Fact]
    public async Task CapabilityIndices_AdditionalNonOwningRolesSurfaceAndRemainStableAcrossRebuilds()
    {
        var parent = new FakeAdditionalRolesPlugin("com.test.additional-owner");
        var manager = await CreateManagerAsync(parent);

        var llmRoles = manager.LlmProviders;
        var transcriptionRoles = manager.TranscriptionEngines;
        var llmRole = Assert.Single(llmRoles);
        var transcriptionRole = Assert.Single(transcriptionRoles);

        Assert.Same(parent.Role, llmRole);
        Assert.Same(parent.Role, transcriptionRole);
        Assert.False(
            // ReSharper disable once ConditionIsAlwaysTrueOrFalse -- the static always-false is the invariant under test: a non-owning additional role must never also implement the owning-plugin interface.
            // ReSharper disable once CanSimplifyIsAssignableFrom -- the runtime reflection form is deliberate so a future type that breaks the ownership contract fails this assertion.
            typeof(ITypeWhisperPlugin).IsAssignableFrom(parent.Role.GetType())
        );

        parent.NotifyCapabilitiesChanged();

        Assert.Same(llmRole, Assert.Single(manager.LlmProviders));
        Assert.Same(transcriptionRole, Assert.Single(manager.TranscriptionEngines));
    }

    [Fact]
    public async Task Dispose_HangingDeactivation_ReturnsAndShutsDownLaterPlugin()
    {
        var hangingPlugin = new HangingDeactivationPlugin("com.test.hanging");
        var laterPlugin = new LifecyclePlugin(
            "com.test.later",
            () => Task.CompletedTask
        );
        var manager = await CreateManagerAsync(hangingPlugin, laterPlugin);
        var disposeTask = Task.Run(manager.Dispose);

        try
        {
            await disposeTask.WaitAsync(s_outerTimeout);

            Assert.Equal(1, laterPlugin.DeactivationCount);
            Assert.Equal(1, laterPlugin.DisposeCount);
        }
        finally
        {
            hangingPlugin.CompleteDeactivation();
            await disposeTask.WaitAsync(s_outerTimeout);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dispose_ThrowingDeactivation_DisposesPluginAndShutsDownLaterPlugin(
        bool throwsSynchronously
    )
    {
        Func<Task> throwingDeactivation = throwsSynchronously
            ? () => throw new InvalidOperationException("Synchronous deactivation failure")
            : () => Task.FromException(
                new InvalidOperationException("Asynchronous deactivation failure")
            );
        var throwingPlugin = new LifecyclePlugin(
            "com.test.throwing",
            throwingDeactivation
        );
        var laterPlugin = new LifecyclePlugin(
            "com.test.later",
            () => Task.CompletedTask
        );
        var manager = await CreateManagerAsync(throwingPlugin, laterPlugin);

        manager.Dispose();

        Assert.Equal(1, throwingPlugin.DeactivationCount);
        Assert.Equal(1, throwingPlugin.DisposeCount);
        Assert.Equal(1, laterPlugin.DeactivationCount);
        Assert.Equal(1, laterPlugin.DisposeCount);
    }

    [Fact]
    public async Task Dispose_TimedOutDeactivation_ObservesLateCompletion()
    {
        var hangingPlugin = new HangingDeactivationPlugin(
            "com.test.late-completion"
        );
        var manager = await CreateManagerAsync(hangingPlugin);
        var disposeTask = Task.Run(manager.Dispose);

        try
        {
            await disposeTask.WaitAsync(s_outerTimeout);

            // Under the ordering guarantee, Dispose must not have run yet; wait briefly
            // so this negative assertion isn't just early timing luck.
            await Task.Delay(100);
            Assert.False(hangingPlugin.DisposeCalled.IsCompleted);

            hangingPlugin.CompleteDeactivation();

            await hangingPlugin.DeactivationCompleted.WaitAsync(s_outerTimeout);
            await hangingPlugin.DisposeCalled.WaitAsync(s_outerTimeout);
            Assert.True(hangingPlugin.DidCompleteDeactivation);
        }
        finally
        {
            hangingPlugin.CompleteDeactivation();
            await disposeTask.WaitAsync(s_outerTimeout);
        }
    }

    private async Task<PluginManager> CreateManagerAsync(
        params ITypeWhisperPlugin[] plugins
    )
    {
        _manager = new PluginManager(
            new PluginLoader(TestPaths.NewTempPath("TypeWhisper.PluginManagerData")),
            _eventBus,
            _activeWindow.Object,
            _profiles.Object,
            _settings.Object,
            [],
            errorLog: _errorLog.Object,
            pluginShutdownTimeout: s_shutdownTimeout
        );

        var loadedPlugins = GetLoadedPlugins(_manager);
        foreach (var plugin in plugins)
        {
            loadedPlugins.Add(CreateLoadedPlugin(plugin));
            await _manager.EnablePluginAsync(plugin.PluginId);
        }

        return _manager;
    }

    private static LoadedPlugin CreateLoadedPlugin(
        ITypeWhisperPlugin plugin,
        PluginNetworkAccess networkAccess = PluginNetworkAccess.Network,
        bool? legacyIsLocal = null
    )
    {
        var testAssemblyPath = typeof(PluginManagerTests).Assembly.Location;
        var categories = plugin switch
        {
            ITranscriptionEnginePlugin => new[] { PluginCategory.Transcription },
            ILlmProviderPlugin => [PluginCategory.Llm],
            _ => [PluginCategory.Utility],
        };
        var manifest = new PluginManifest
        {
            Id = plugin.PluginId,
            Name = plugin.PluginName,
            Version = plugin.PluginVersion,
            AssemblyName = "fake.dll",
            PluginClass = plugin.GetType().FullName ?? plugin.GetType().Name,
            NetworkAccess = networkAccess,
            Categories = categories,
            IsLocal = legacyIsLocal,
        };
        return new LoadedPlugin(
            manifest,
            plugin,
            new PluginAssemblyLoadContext(testAssemblyPath),
            Path.GetDirectoryName(testAssemblyPath)!,
            PluginLoader.ResolveMetadata(manifest)
        );
    }

    private static List<LoadedPlugin> GetLoadedPlugins(PluginManager manager)
    {
        var field =
            typeof(PluginManager).GetField(
                "_allPlugins",
                BindingFlags.Instance | BindingFlags.NonPublic
            ) ?? throw new MissingFieldException(typeof(PluginManager).FullName, "_allPlugins");
        return (List<LoadedPlugin>)field.GetValue(manager)!;
    }

    private abstract class FakeCapabilityPlugin(string pluginId) : ITypeWhisperPlugin
    {
        public string PluginId { get; } = pluginId;
        public string PluginName => PluginId;
        public string PluginVersion => "1.0.0";

        public virtual Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;

        public Task DeactivateAsync() => Task.CompletedTask;

        public void Dispose() { }
    }

    private sealed class FakeAdditionalRolesPlugin(string pluginId)
        : FakeCapabilityPlugin(pluginId),
            IAdditionalLlmProvidersProvider,
            IAdditionalTranscriptionEnginesProvider
    {
        private IPluginHostServices? _host;

        public FakeAdditionalRole Role { get; } = new(pluginId);
        public IReadOnlyList<ILlmProviderRole> AdditionalLlmProviders => [Role];
        public IReadOnlyList<ITranscriptionEngineRole> AdditionalTranscriptionEngines => [Role];

        public override Task ActivateAsync(IPluginHostServices host)
        {
            _host = host;
            return Task.CompletedTask;
        }

        public void NotifyCapabilitiesChanged()
        {
            _host?.NotifyCapabilitiesChanged();
        }
    }

    private sealed class FakeAdditionalRole(string ownerPluginId)
        : ILlmProviderRole,
            ITranscriptionEngineRole,
            ILlmProviderSelectionIdentity,
            ITranscriptionEngineSelectionIdentity
    {
        public string PluginId { get; } = ownerPluginId;
        public string LlmSelectionId => "additional-llm";
        public string TranscriptionSelectionId => "additional-transcription";
        public string ProviderName => "Additional LLM";
        public bool IsAvailable => true;
        public IReadOnlyList<PluginModelInfo> SupportedModels { get; } =
            [new("llm-model", "LLM model")];
        public string ProviderId => TranscriptionSelectionId;
        public string ProviderDisplayName => "Additional transcription";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
            [new("transcription-model", "Transcription model")];
        // ReSharper disable once ReturnTypeCanBeNotNullable -- implements ITranscriptionEngineRole.SelectedModelId, whose contract is nullable.
        public string? SelectedModelId => TranscriptionModels[0].Id;
        public bool SupportsTranslation => false;

        public Task<string> ProcessAsync(
            string systemPrompt,
            string userText,
            string model,
            CancellationToken ct
        )
        {
            return Task.FromResult("");
        }

        public void SelectModel(string modelId) { }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct
        )
        {
            return Task.FromResult(new PluginTranscriptionResult("", null, 0, null));
        }
    }

    private class FakeTranscriptionPlugin(string pluginId)
        : FakeCapabilityPlugin(pluginId),
            ITranscriptionEnginePlugin
    {
        public string ProviderId => PluginId;
        public string ProviderDisplayName => PluginName;
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
            [new("model:version-1", "Test model")];
        // ReSharper disable once ReturnTypeCanBeNotNullable -- implements ITranscriptionEnginePlugin.SelectedModelId, whose contract is nullable.
        public string? SelectedModelId => TranscriptionModels[0].Id;
        public bool SupportsTranslation => false;

        public void SelectModel(string modelId) { }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct
        )
        {
            return Task.FromResult(new PluginTranscriptionResult("", null, 0, null));
        }
    }

    private sealed class IdentifiedTranscriptionPlugin(
        string pluginId,
        string customSelectionId
    )
        : FakeTranscriptionPlugin(pluginId),
            ITranscriptionEngineSelectionIdentity
    {
        public string TranscriptionSelectionId { get; } = customSelectionId;
    }

    private class FakeLlmPlugin(string pluginId)
        : FakeCapabilityPlugin(pluginId),
            ILlmProviderPlugin
    {
        public string ProviderName => PluginName;
        public bool IsAvailable => true;
        public IReadOnlyList<PluginModelInfo> SupportedModels { get; } =
            [new("model:version-1", "Test model")];

        public Task<string> ProcessAsync(
            string systemPrompt,
            string userText,
            string model,
            CancellationToken ct
        )
        {
            return Task.FromResult("");
        }
    }

    private sealed class IdentifiedLlmPlugin(
        string pluginId,
        string customSelectionId
    )
        : FakeLlmPlugin(pluginId),
            ILlmProviderSelectionIdentity
    {
        public string LlmSelectionId { get; } = customSelectionId;
    }

    private sealed class LifecyclePlugin(
        string pluginId,
        Func<Task> deactivateAsync
    ) : ITypeWhisperPlugin
    {
        private int _deactivationCount;
        private int _disposeCount;

        public string PluginId { get; } = pluginId;
        public string PluginName => PluginId;
        public string PluginVersion => "1.0.0";
        public int DeactivationCount => Volatile.Read(ref _deactivationCount);
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;

        public Task DeactivateAsync()
        {
            Interlocked.Increment(ref _deactivationCount);
            return deactivateAsync();
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
        }
    }

    private sealed class HangingDeactivationPlugin(string pluginId) : ITypeWhisperPlugin
    {
        private readonly TaskCompletionSource _deactivationCompleted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _deactivationRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _disposeCalled = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _didCompleteDeactivation;

        public string PluginId { get; } = pluginId;
        public string PluginName => PluginId;
        public string PluginVersion => "1.0.0";
        public Task DeactivationCompleted => _deactivationCompleted.Task;
        public Task DisposeCalled => _disposeCalled.Task;
        public bool DidCompleteDeactivation =>
            Volatile.Read(ref _didCompleteDeactivation) == 1;

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;

        public async Task DeactivateAsync()
        {
            await _deactivationRelease.Task;
            Volatile.Write(ref _didCompleteDeactivation, 1);
            _deactivationCompleted.TrySetResult();
        }

        public void CompleteDeactivation()
        {
            _deactivationRelease.TrySetResult();
        }

        public void Dispose()
        {
            _disposeCalled.TrySetResult();
        }
    }
}
