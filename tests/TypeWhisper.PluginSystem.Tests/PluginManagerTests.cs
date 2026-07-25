using Moq;
using System.Reflection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
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
}

// Verifies enable/disable/capability-index logic without loading real plugin assemblies.
public sealed class PluginManagerWithFakePluginTests : IDisposable
{
    private static readonly TimeSpan s_shutdownTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan s_outerTimeout = TimeSpan.FromSeconds(2);

    private readonly Mock<IActiveWindowService> _activeWindow = new();
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

    private static LoadedPlugin CreateLoadedPlugin(ITypeWhisperPlugin plugin)
    {
        var testAssemblyPath = typeof(PluginManagerTests).Assembly.Location;
        return new LoadedPlugin(
            new PluginManifest
            {
                Id = plugin.PluginId,
                Name = plugin.PluginName,
                Version = plugin.PluginVersion,
                AssemblyName = "fake.dll",
                PluginClass = plugin.GetType().FullName ?? plugin.GetType().Name,
            },
            plugin,
            new PluginAssemblyLoadContext(testAssemblyPath),
            Path.GetDirectoryName(testAssemblyPath)!
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
