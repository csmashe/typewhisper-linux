using Moq;
using System.Reflection;
using System.Runtime.CompilerServices;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class PromptProcessingServiceTests : IDisposable
{
    private readonly string _tempDir = TestPaths.CreateTempDirectory(
        "TypeWhisper.PromptProcessingServiceTests"
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsync_TransientFailureThenSuccess(bool streaming)
    {
        var provider = new RetryProvider(false);
        using var manager = CreatePluginManager([provider], []);
        var sut = new PromptProcessingService(manager, CreateSettings(new AppSettings()).Object, new MemoryService(manager));
        var action = new PromptAction { Id = "retry", Name = "Retry", SystemPrompt = "system" };
        var result = "";
        if (streaming)
        {
            await foreach (var token in sut.ProcessStreamingAsync(action, "input"))
                result += token;
        }
        else
            result = await sut.ProcessAsync(action, "input");
        Assert.Equal("result", result);
        Assert.Equal(2, provider.Attempts);
    }

    [Fact]
    public async Task ProcessStreamingAsync_DoesNotRetryAfterFirstToken()
    {
        var provider = new RetryProvider(true);
        using var manager = CreatePluginManager([provider], []);
        var sut = new PromptProcessingService(manager, CreateSettings(new AppSettings()).Object, new MemoryService(manager));
        var chunks = new List<string>();
        await Assert.ThrowsAsync<PluginRequestException>(async () =>
        {
            await foreach (var token in sut.ProcessStreamingAsync(new PromptAction { Id = "retry", Name = "Retry", SystemPrompt = "system" }, "input"))
                chunks.Add(token);
        });
        Assert.Equal(["partial"], chunks);
        Assert.Equal(1, provider.Attempts);
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
                SystemPrompt = "Rewrite this",
            },
            "hello",
            ct: CancellationToken.None
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
                CreateLoadedPlugin(overrideProvider.PluginId, overrideProvider),
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
                ProviderOverride = "plugin:com.test.override:model-b",
            },
            "hello",
            ct: CancellationToken.None
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
                SystemPrompt = "Rewrite this",
            },
            "hello",
            ct: CancellationToken.None
        );

        Assert.Equal(
            $"processed:First Provider:model-z:{PromptProcessingService.FormatPromptActionInput("hello")}",
            result
        );
    }

    [Fact]
    public void HasNoProviderForRequest_IsFalseWhenAnExplicitSelectionExists()
    {
        using var pluginManager = CreatePluginManager([], []);
        var settings = CreateSettings(
            new AppSettings { DefaultLlmProvider = "plugin:com.test.signed-out:model-s" }
        );

        var sut = new PromptProcessingService(
            pluginManager,
            settings.Object,
            new MemoryService(pluginManager)
        );

        // A selection resolves to its own specific failure, so callers must not pre-empt it.
        Assert.False(sut.HasNoProviderForRequest());
        Assert.False(sut.HasNoProviderForRequest("plugin:com.test.other:model-o"));
    }

    [Fact]
    public void HasNoProviderForRequest_IsTrueWhenNothingIsSelectedOrAvailable()
    {
        using var pluginManager = CreatePluginManager([], []);
        var settings = CreateSettings(new AppSettings());

        var sut = new PromptProcessingService(
            pluginManager,
            settings.Object,
            new MemoryService(pluginManager)
        );

        Assert.True(sut.HasNoProviderForRequest());
    }

    [Fact]
    public async Task ProcessAsync_SkipsUnavailableProvider_WhenFallingBackWithNoSelection()
    {
        var unavailable = new FakeLlmProviderPlugin("com.test.signed-out", "Signed Out", "model-s")
        {
            IsAvailable = false,
        };
        var available = new FakeLlmProviderPlugin("com.test.ready", "Ready Provider", "model-r");
        using var pluginManager = CreatePluginManager(
            [unavailable, available],
            [
                CreateLoadedPlugin(unavailable.PluginId, unavailable),
                CreateLoadedPlugin(available.PluginId, available),
            ]
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
                SystemPrompt = "Rewrite this",
            },
            "hello",
            ct: CancellationToken.None
        );

        Assert.Equal(
            $"processed:Ready Provider:model-r:{PromptProcessingService.FormatPromptActionInput("hello")}",
            result
        );
        Assert.Equal(0, unavailable.CallCount);
    }

    [Fact]
    public async Task ProcessAsync_MissingProviderOverride_FailsInsteadOfUsingAnotherProvider()
    {
        var fallback = new FakeLlmProviderPlugin("com.test.fallback", "Fallback Provider", "model-f");
        using var pluginManager = CreatePluginManager(
            [fallback],
            [CreateLoadedPlugin(fallback.PluginId, fallback)]
        );
        var settings = CreateSettings(new AppSettings());

        var sut = new PromptProcessingService(
            pluginManager,
            settings.Object,
            new MemoryService(pluginManager)
        );

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => sut.ProcessAsync(
            new PromptAction
            {
                Id = "prompt",
                Name = "Rewrite",
                SystemPrompt = "Rewrite this",
                ProviderOverride = "plugin:com.test.missing:model-m",
            },
            "hello",
            ct: CancellationToken.None
        ));

        Assert.Equal(PluginRequestFailureKind.Configuration, error.FailureKind);
        Assert.False(error.IsTransient);
        Assert.Equal(
            Loc.Instance.GetString(
                "Prompts.SelectedProviderMissing",
                "com.test.missing · model-m"
            ),
            error.Message
        );
        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public async Task ProcessAsync_UnavailableProviderOverride_FailsInsteadOfUsingAnotherProvider()
    {
        var unavailable = new FakeLlmProviderPlugin("com.test.signed-out", "Signed Out", "model-s")
        {
            IsAvailable = false,
        };
        var fallback = new FakeLlmProviderPlugin("com.test.fallback", "Fallback Provider", "model-f");
        using var pluginManager = CreatePluginManager(
            [unavailable, fallback],
            [
                CreateLoadedPlugin(unavailable.PluginId, unavailable),
                CreateLoadedPlugin(fallback.PluginId, fallback),
            ]
        );
        var settings = CreateSettings(new AppSettings());

        var sut = new PromptProcessingService(
            pluginManager,
            settings.Object,
            new MemoryService(pluginManager)
        );

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => sut.ProcessAsync(
            new PromptAction
            {
                Id = "prompt",
                Name = "Rewrite",
                SystemPrompt = "Rewrite this",
                ProviderOverride = "plugin:com.test.signed-out:model-s",
            },
            "hello",
            ct: CancellationToken.None
        ));

        Assert.Equal(PluginRequestFailureKind.Configuration, error.FailureKind);
        Assert.False(error.IsTransient);
        Assert.Equal(
            Loc.Instance.GetString("Prompts.SelectedProviderUnavailable", "Signed Out"),
            error.Message
        );
        Assert.Equal(0, unavailable.CallCount);
        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public async Task ProcessAsync_UnavailableDefaultProvider_FailsInsteadOfUsingAnotherProvider()
    {
        var unavailable = new FakeLlmProviderPlugin("com.test.signed-out", "Signed Out", "model-s")
        {
            IsAvailable = false,
        };
        var fallback = new FakeLlmProviderPlugin("com.test.fallback", "Fallback Provider", "model-f");
        using var pluginManager = CreatePluginManager(
            [unavailable, fallback],
            [
                CreateLoadedPlugin(unavailable.PluginId, unavailable),
                CreateLoadedPlugin(fallback.PluginId, fallback),
            ]
        );
        var settings = CreateSettings(
            new AppSettings { DefaultLlmProvider = "plugin:com.test.signed-out:model-s" }
        );

        var sut = new PromptProcessingService(
            pluginManager,
            settings.Object,
            new MemoryService(pluginManager)
        );

        var error = await Assert.ThrowsAsync<PluginRequestException>(() => sut.ProcessSystemPromptAsync(
            "Clean this up",
            "hello",
            ct: CancellationToken.None
        ));

        Assert.Equal(PluginRequestFailureKind.Configuration, error.FailureKind);
        Assert.Equal(
            Loc.Instance.GetString("Prompts.SelectedProviderUnavailable", "Signed Out"),
            error.Message
        );
        Assert.Equal(0, unavailable.CallCount);
        Assert.Equal(0, fallback.CallCount);
    }

    [Fact]
    public async Task ProcessStreamingAsync_StreamsProviderResponse_AndFramesInputLikeBatch()
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

        var action = new PromptAction { Id = "prompt", Name = "Rewrite", SystemPrompt = "Rewrite this" };

        var chunks = new List<string>();
        await foreach (var chunk in sut.ProcessStreamingAsync(action, "hello", ct: CancellationToken.None))
            chunks.Add(chunk);

        // The fake provider does not override ProcessStreamingAsync, so the SDK
        // default wrap yields exactly one bulk chunk equal to the batch result.
        Assert.Equal(
            $"processed:Default Provider:model-a:{PromptProcessingService.FormatPromptActionInput("hello")}",
            string.Concat(chunks)
        );
    }

    [Fact]
    public async Task ProcessStreamingAsync_ThrowsWhenNoProviderAvailable()
    {
        using var pluginManager = CreatePluginManager([], []);
        var settings = CreateSettings(new AppSettings());

        var sut = new PromptProcessingService(
            pluginManager,
            settings.Object,
            new MemoryService(pluginManager)
        );

        var action = new PromptAction { Id = "prompt", Name = "Rewrite", SystemPrompt = "Rewrite this" };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in sut.ProcessStreamingAsync(action, "hello", ct: CancellationToken.None))
            {
                // Drain the stream to force enumeration so the throw surfaces.
            }
        });
    }

    // Framing behavior (FormatPromptActionInput) is covered by PromptProcessingInputFramingTests.

    [Fact]
    public async Task ProcessAsync_WithCapture_RecordsPromptActionProvenance()
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

        var capture = new LlmCallCapture();
        await sut.ProcessAsync(
            new PromptAction { Id = "prompt", Name = "Rewrite", SystemPrompt = "Rewrite this" },
            "hello",
            capture,
            CancellationToken.None
        );

        var call = Assert.Single(capture.Calls);
        Assert.Equal("PromptAction", call.Stage);
        Assert.Equal("Rewrite this", call.SystemPromptSent);
        Assert.Equal(PromptProcessingService.FormatPromptActionInput("hello"), call.UserPromptSent);
        Assert.Equal("Default Provider", call.ProviderName);
        Assert.Equal("com.test.default", call.ProviderId);
        Assert.Equal("model-a", call.ModelId);
        Assert.False(call.RanLocally);
        Assert.Null(call.InjectedMemoryContext);
        Assert.Equal(
            $"processed:Default Provider:model-a:{PromptProcessingService.FormatPromptActionInput("hello")}",
            call.ResponseReceived
        );
    }

    [Fact]
    public async Task ProcessAsync_WithLocalDescriptor_MarksRanLocally()
    {
        var provider = new FakeLlmProviderPlugin("com.test.local", "Local Provider", "model-l");
        using var pluginManager = CreatePluginManager(
            [provider],
            [
                CreateLoadedPlugin(
                    provider.PluginId,
                    provider,
                    PluginNetworkAccess.Local
                ),
            ]
        );
        var settings = CreateSettings(
            new AppSettings { DefaultLlmProvider = "plugin:com.test.local:model-l" }
        );

        var sut = new PromptProcessingService(
            pluginManager,
            settings.Object,
            new MemoryService(pluginManager)
        );

        var capture = new LlmCallCapture();
        await sut.ProcessAsync(
            new PromptAction { Id = "prompt", Name = "Rewrite", SystemPrompt = "Rewrite this" },
            "hello",
            capture,
            CancellationToken.None
        );

        var call = Assert.Single(capture.Calls);
        Assert.True(call.RanLocally);
    }

    [Theory]
    [InlineData(PluginNetworkAccess.Network)]
    [InlineData(PluginNetworkAccess.Mixed)]
    [InlineData(PluginNetworkAccess.UserControlled)]
    public async Task ProcessAsync_WithNonLocalDescriptor_DoesNotMarkRanLocally(
        PluginNetworkAccess networkAccess
    )
    {
        var provider = new FakeLlmProviderPlugin(
            "com.test.non-local",
            "Non-local Provider",
            "model-n"
        );
        using var pluginManager = CreatePluginManager(
            [provider],
            [CreateLoadedPlugin(provider.PluginId, provider, networkAccess)]
        );
        var settings = CreateSettings(
            new AppSettings
            {
                DefaultLlmProvider = "plugin:com.test.non-local:model-n",
            }
        );
        var sut = new PromptProcessingService(
            pluginManager,
            settings.Object,
            new MemoryService(pluginManager)
        );

        var capture = new LlmCallCapture();
        await sut.ProcessAsync(
            new PromptAction
            {
                Id = "prompt",
                Name = "Rewrite",
                SystemPrompt = "Rewrite this",
            },
            "hello",
            capture,
            CancellationToken.None
        );

        Assert.False(Assert.Single(capture.Calls).RanLocally);
    }

    [Fact]
    public async Task ProcessAsync_WithNullCapture_RecordsNothing()
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

        // No capture argument — completing without throwing is the assertion here
        // (nothing is recorded without a sink); the streaming variant below covers
        // the single-entry guarantee.
        await sut.ProcessAsync(
            new PromptAction { Id = "prompt", Name = "Rewrite", SystemPrompt = "Rewrite this" },
            "hello",
            ct: CancellationToken.None
        );
    }

    [Fact]
    public async Task ProcessSystemPromptAsync_WithCapture_TagsCleanupStage()
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

        var capture = new LlmCallCapture();
        await sut.ProcessSystemPromptAsync("Clean this up", "hello", capture, CancellationToken.None);

        var call = Assert.Single(capture.Calls);
        Assert.Equal("Cleanup", call.Stage);
        Assert.Equal("Clean this up", call.SystemPromptSent);
        Assert.Equal(PromptProcessingService.FormatPromptActionInput("hello"), call.UserPromptSent);
        Assert.Null(call.InjectedMemoryContext);
        Assert.Equal(
            $"processed:Default Provider:model-a:{PromptProcessingService.FormatPromptActionInput("hello")}",
            call.ResponseReceived
        );
    }

    [Fact]
    public async Task ProcessStreamingThenBatchFallback_RecordsExactlyOneProvenanceEntry()
    {
        // The fake faults its stream so RunPromptActionAsync's streaming→batch
        // retry fires. The streaming call records provenance before yielding; the
        // batch fallback is passed a null capture, so exactly one entry survives.
        var provider = new FaultingStreamProviderPlugin(
            "com.test.fault",
            "Faulting Provider",
            "model-f"
        );
        using var pluginManager = CreatePluginManager(
            [provider],
            [CreateLoadedPlugin(provider.PluginId, provider)]
        );
        var settings = CreateSettings(
            new AppSettings { DefaultLlmProvider = "plugin:com.test.fault:model-f" }
        );

        var sut = new PromptProcessingService(
            pluginManager,
            settings.Object,
            new MemoryService(pluginManager)
        );

        var action = new PromptAction { Id = "prompt", Name = "Rewrite", SystemPrompt = "Rewrite this" };
        var capture = new LlmCallCapture();

        // Streaming attempt with the capture; drain until it faults.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in sut.ProcessStreamingAsync(action, "hello", capture, CancellationToken.None))
            {
                // Drain to force the fault to surface.
            }
        });

        // Batch fallback re-runs the same call, but with a null capture.
        await sut.ProcessAsync(action, "hello", ct: CancellationToken.None);

        Assert.Single(capture.Calls);
        Assert.Equal("PromptAction", capture.Calls[0].Stage);
    }

    [Fact]
    public async Task ProcessStreamingAsync_WithCapture_RecordsAccumulatedResponse()
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

        var action = new PromptAction { Id = "prompt", Name = "Rewrite", SystemPrompt = "Rewrite this" };
        var capture = new LlmCallCapture();

        // Drain to completion so the finally block records the accumulated reply.
        await foreach (var _ in sut.ProcessStreamingAsync(action, "hello", capture, CancellationToken.None))
        {
            // Intentionally empty.
        }

        var call = Assert.Single(capture.Calls);
        Assert.Equal(
            $"processed:Default Provider:model-a:{PromptProcessingService.FormatPromptActionInput("hello")}",
            call.ResponseReceived
        );
    }

    private static Mock<ISettingsService> CreateSettings(AppSettings current)
    {
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(current);
        return settings;
    }

    private PluginManager CreatePluginManager(
        IReadOnlyList<ILlmProviderRole> llmProviders,
        IReadOnlyList<LoadedPlugin> loadedPlugins
    )
    {
        var activeWindow = new Mock<IActiveWindowService>();
        var profiles = new Mock<IProfileService>();
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings());
        profiles.SetupGet(service => service.Profiles).Returns([]);

        var pluginManager = new PluginManager(
            new PluginLoader(Path.Join(_tempDir, "PluginData")),
            new PluginEventBus(),
            activeWindow.Object,
            profiles.Object,
            settings.Object,
            []
        );

        SetPrivateField(pluginManager, "_llmProviders", llmProviders.ToList());
        SetPrivateField(pluginManager, "_allPlugins", loadedPlugins.ToList());

        return pluginManager;
    }

    private LoadedPlugin CreateLoadedPlugin(
        string pluginId,
        ITypeWhisperPlugin plugin,
        PluginNetworkAccess networkAccess = PluginNetworkAccess.Network
    )
    {
        var pluginDir = Path.Join(_tempDir, pluginId);
        Directory.CreateDirectory(pluginDir);

        var manifest = new PluginManifest
        {
            Id = pluginId,
            Name = plugin.PluginName,
            Version = plugin.PluginVersion,
            AssemblyName = "fake.dll",
            PluginClass = plugin.GetType().FullName ?? plugin.GetType().Name,
            NetworkAccess = networkAccess,
            Categories = [PluginCategory.Llm],
        };
        return new LoadedPlugin(
            manifest,
            plugin,
            new PluginAssemblyLoadContext(pluginDir),
            pluginDir,
            PluginLoader.ResolveMetadata(manifest)
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

    private sealed class RetryProvider(bool failAfterToken) : ILlmProviderPlugin
    {
        public int Attempts { get; private set; }
        public string PluginId => "retry";
        public string PluginName => "Retry";
        public string PluginVersion => "1.0";
        public string ProviderName => "Retry";
        public bool IsAvailable => true;
        public IReadOnlyList<PluginModelInfo> SupportedModels => [new("model", "Model")];
        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public void Dispose() { }
        public Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct) =>
            ++Attempts == 1
                ? throw new PluginRequestException("network", PluginRequestFailureKind.Network, retryAfter: TimeSpan.Zero)
                : Task.FromResult("result");
        public async IAsyncEnumerable<string> ProcessStreamingAsync(string systemPrompt, string userText, string model, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            Attempts++;
            if (failAfterToken)
                yield return "partial";
            if (failAfterToken || Attempts == 1)
                throw new PluginRequestException("network", PluginRequestFailureKind.Network, retryAfter: TimeSpan.Zero);
            yield return "result";
        }
    }

    private sealed class FakeLlmProviderPlugin : ILlmProviderPlugin
    {
        public FakeLlmProviderPlugin(string pluginId, string providerName, string modelId)
        {
            PluginId = pluginId;
            ProviderName = providerName;
            SupportedModels = [new PluginModelInfo(modelId, modelId.ToUpperInvariant())];
        }

        public string PluginId { get; }
        public string PluginName => ProviderName;
        public string PluginVersion => "1.0.0";
        public string ProviderName { get; }
        public bool IsAvailable { get; init; } = true;
        public IReadOnlyList<PluginModelInfo> SupportedModels { get; }

        // Lets a test prove no other provider was reached after a selected one failed.
        public int CallCount { get; private set; }

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
            CallCount++;
            return Task.FromResult($"processed:{ProviderName}:{model}:{userText}");
        }

        public void Dispose() { }
    }

    // Streams nothing and faults on enumeration, exercising the streaming→batch
    // fallback. ProcessAsync (the batch path) succeeds so the retry completes.
    private sealed class FaultingStreamProviderPlugin : ILlmProviderPlugin
    {
        public FaultingStreamProviderPlugin(string pluginId, string providerName, string modelId)
        {
            PluginId = pluginId;
            ProviderName = providerName;
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
            return Task.FromResult($"batch:{ProviderName}:{model}:{userText}");
        }

#pragma warning disable CS1998 // async iterator that yields nothing before faulting
        // ReSharper disable once AsyncMethodWithoutAwait -- deliberate throwing async iterator test double; must stay async+iterator so it faults lazily on enumeration, not eagerly at the call.
        public async IAsyncEnumerable<string> ProcessStreamingAsync(
            string systemPrompt,
            string userText,
            string model,
            [EnumeratorCancellation]
            CancellationToken ct
        )
        {
            // Never-taken loop keeps this a valid iterator method without yielding.
            // ReSharper disable once ConditionIsAlwaysTrueOrFalse -- the always-false condition is intentional; it makes this a yielding iterator that never actually yields.
            for (var i = 0; i < 0; i++)
            {
                yield return "";
            }

            throw new InvalidOperationException("stream faulted");
        }
#pragma warning restore CS1998

        public void Dispose() { }
    }
}
