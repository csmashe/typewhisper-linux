using Moq;
using System.Reflection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class PromptProcessingServiceTests : IDisposable
{
    private readonly string _tempDir;

    public PromptProcessingServiceTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "TypeWhisper.Linux.PromptTests_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
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
    public async Task ProcessAsync_UsesDefaultProvider_WhenNoOverrideIsSet()
    {
        var provider = new FakeLlmProviderPlugin("com.test.default", "Default Provider", "model-a");
        using var pluginManager = CreatePluginManager(
            [provider],
            [CreateLoadedPlugin(provider.PluginId, provider)]
        );
        var settings = CreateSettings(
            new AppSettings { DefaultLlmProvider = "plugin:com.test.default:model-a" }
        );

        var sut = new PromptProcessingService(
            pluginManager,
            settings.Object,
            new MemoryService(pluginManager)
        );

        var result = await sut.ProcessAsync(
            new PromptAction
            {
                Id = "prompt",
                Name = "Rewrite",
                SystemPrompt = "Rewrite this"
            },
            "hello",
            CancellationToken.None
        );

        Assert.Equal(
            $"processed:Default Provider:model-a:{PromptProcessingService.FormatPromptActionInput("hello")}",
            result
        );
    }

    [Fact]
    public async Task ProcessAsync_UsesPromptOverride_WhenProvided()
    {
        var defaultProvider = new FakeLlmProviderPlugin(
            "com.test.default",
            "Default Provider",
            "model-a"
        );
        var overrideProvider = new FakeLlmProviderPlugin(
            "com.test.override",
            "Override Provider",
            "model-b"
        );
        using var pluginManager = CreatePluginManager(
            [defaultProvider, overrideProvider],
            [
                CreateLoadedPlugin(defaultProvider.PluginId, defaultProvider),
                CreateLoadedPlugin(overrideProvider.PluginId, overrideProvider)
            ]
        );
        var settings = CreateSettings(
            new AppSettings { DefaultLlmProvider = "plugin:com.test.default:model-a" }
        );

        var sut = new PromptProcessingService(
            pluginManager,
            settings.Object,
            new MemoryService(pluginManager)
        );

        var result = await sut.ProcessAsync(
            new PromptAction
            {
                Id = "prompt",
                Name = "Rewrite",
                SystemPrompt = "Rewrite this",
                ProviderOverride = "plugin:com.test.override:model-b"
            },
            "hello",
            CancellationToken.None
        );

        Assert.Equal(
            $"processed:Override Provider:model-b:{PromptProcessingService.FormatPromptActionInput("hello")}",
            result
        );
    }

    [Fact]
    public async Task ProcessAsync_FallsBackToFirstAvailableProvider_WhenNoDefaultIsConfigured()
    {
        var provider = new FakeLlmProviderPlugin("com.test.first", "First Provider", "model-z");
        using var pluginManager = CreatePluginManager(
            [provider],
            [CreateLoadedPlugin(provider.PluginId, provider)]
        );
        var settings = CreateSettings(new AppSettings());

        var sut = new PromptProcessingService(
            pluginManager,
            settings.Object,
            new MemoryService(pluginManager)
        );

        var result = await sut.ProcessAsync(
            new PromptAction
            {
                Id = "prompt",
                Name = "Rewrite",
                SystemPrompt = "Rewrite this"
            },
            "hello",
            CancellationToken.None
        );

        Assert.Equal(
            $"processed:First Provider:model-z:{PromptProcessingService.FormatPromptActionInput("hello")}",
            result
        );
    }

    [Fact]
    public void FormatPromptActionInput_LabelsInputText()
    {
        var result = PromptProcessingService.FormatPromptActionInput("Ryan, this is a test.");

        Assert.Contains("Text to process:", result);
        Assert.Contains("Ryan, this is a test.", result);
    }

    private static Mock<ISettingsService> CreateSettings(AppSettings current)
    {
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(current);
        return settings;
    }

    private static PluginManager CreatePluginManager(
        IReadOnlyList<ILlmProviderPlugin> llmProviders,
        IReadOnlyList<LoadedPlugin> loadedPlugins
    )
    {
        var activeWindow = new Mock<IActiveWindowService>();
        var profiles = new Mock<IProfileService>();
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings());
        profiles.SetupGet(service => service.Profiles).Returns([]);

        var pluginManager = new PluginManager(
            new PluginLoader(),
            new PluginEventBus(),
            activeWindow.Object,
            profiles.Object,
            settings.Object
        );

        SetPrivateField(pluginManager, "_llmProviders", llmProviders.ToList());
        SetPrivateField(pluginManager, "_allPlugins", loadedPlugins.ToList());

        return pluginManager;
    }

    private LoadedPlugin CreateLoadedPlugin(string pluginId, ITypeWhisperPlugin plugin)
    {
        var pluginDir = Path.Combine(_tempDir, pluginId);
        Directory.CreateDirectory(pluginDir);

        return new LoadedPlugin(
            new PluginManifest
            {
                Id = pluginId,
                Name = plugin.PluginName,
                Version = plugin.PluginVersion,
                AssemblyName = "fake.dll",
                PluginClass = plugin.GetType().FullName ?? plugin.GetType().Name
            },
            plugin,
            new PluginAssemblyLoadContext(pluginDir),
            pluginDir
        );
    }

    // PluginManager exposes no public seam for injecting pre-loaded plugins;
    // reflection is the only way to seed test doubles into the private lists.
    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field =
            target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        field.SetValue(target, value);
    }

    private sealed class FakeLlmProviderPlugin : ILlmProviderPlugin
    {
        private readonly string _modelId;

        public FakeLlmProviderPlugin(string pluginId, string providerName, string modelId)
        {
            PluginId = pluginId;
            ProviderName = providerName;
            _modelId = modelId;
            SupportedModels = [new PluginModelInfo(modelId, modelId.ToUpperInvariant())];
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
            return Task.FromResult($"processed:{ProviderName}:{model}:{userText}");
        }

        public void Dispose() { }
    }
}