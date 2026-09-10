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

/// <summary>
///     The flows that spend a recording before the LLM runs refuse a selection that cannot serve
///     the request up front, and the streaming path reports a configuration failure instead of
///     retrying it as a batch request.
/// </summary>
public sealed class SelectedProviderRefusalTests : IDisposable
{
    private readonly string _tempDir = TestPaths.CreateTempDirectory(
        "TypeWhisper.SelectedProviderRefusalTests"
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
    public void TransformSelection_RefusesUnavailableDefaultProvider_BeforeRecording()
    {
        var signedOut = new FakeProvider("com.test.cli", "CLI Provider", "default")
        {
            IsAvailable = false,
        };
        var ready = new FakeProvider("com.test.ready", "Ready Provider", "model-r");
        using var pluginManager = CreatePluginManager(signedOut, ready);
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { DefaultLlmProvider = "plugin:com.test.cli:default" }
        );
        var processing = new PromptProcessingService(
            pluginManager,
            settings.Object,
            new MemoryService(pluginManager)
        );

        var refusal = TransformSelectionService.DescribeProviderRefusal(processing);

        Assert.Equal(
            Loc.Instance.GetString("Prompts.SelectedProviderUnavailable", "CLI Provider"),
            refusal
        );
    }

    [Fact]
    public void TransformSelection_ProceedsWhenTheSelectedProviderCanServe()
    {
        var ready = new FakeProvider("com.test.ready", "Ready Provider", "model-r");
        using var pluginManager = CreatePluginManager(ready);
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { DefaultLlmProvider = "plugin:com.test.ready:model-r" }
        );
        var processing = new PromptProcessingService(
            pluginManager,
            settings.Object,
            new MemoryService(pluginManager)
        );

        Assert.Null(TransformSelectionService.DescribeProviderRefusal(processing));
    }

    // Only a forced-profile (profile hotkey) start knows its profile before the microphone opens;
    // an ordinary start matches after, and reports the same problem through the failure path.
    [Fact]
    public void ProfilePromptAction_ForcedProfileStart_RefusesBeforeTheMicrophoneOpens()
    {
        var ready = new FakeProvider("com.test.ready", "Ready Provider", "model-r");
        using var pluginManager = CreatePluginManager(ready);
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());
        var processing = new PromptProcessingService(
            pluginManager,
            settings.Object,
            new MemoryService(pluginManager)
        );
        var profile = new Profile { Id = "p1", Name = "Work", PromptActionId = "rewrite" };
        var actions = new[]
        {
            new PromptAction
            {
                Id = "rewrite",
                Name = "Rewrite",
                SystemPrompt = "Rewrite this",
                ProviderOverride = "plugin:com.test.uninstalled:model-u",
            },
        };

        var problem = DictationOrchestrator.DescribeProfilePromptActionProviderProblem(
            profile,
            actions,
            processing
        );

        Assert.Equal(
            Loc.Instance.GetString(
                "Prompts.SelectedProviderMissing",
                "com.test.uninstalled · model-u"
            ),
            problem
        );
    }

    [Fact]
    public void ProfilePromptAction_WithNoBoundAction_DoesNotRefuse()
    {
        using var pluginManager = CreatePluginManager();
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());
        var processing = new PromptProcessingService(
            pluginManager,
            settings.Object,
            new MemoryService(pluginManager)
        );

        Assert.Null(
            DictationOrchestrator.DescribeProfilePromptActionProviderProblem(
                new Profile { Id = "p1", Name = "Work" },
                [],
                processing
            )
        );
    }

    [Fact]
    public async Task StreamingConfigurationFailure_SurfacesInsteadOfRetryingAsBatch()
    {
        var signedOut = new FakeProvider("com.test.cli", "CLI Provider", "default")
        {
            IsAvailable = false,
        };
        using var pluginManager = CreatePluginManager(signedOut);
        var settings = TestPluginManagerFactory.CreateSettings(new AppSettings());
        var processing = new PromptProcessingService(
            pluginManager,
            settings.Object,
            new MemoryService(pluginManager)
        );
        var action = new PromptAction
        {
            Id = "rewrite",
            Name = "Rewrite",
            SystemPrompt = "Rewrite this",
            ProviderOverride = "plugin:com.test.cli:default",
        };
        var batchRuns = 0;

        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            DictationOrchestrator.RunPromptActionStreamWithFallbackAsync(
                processing.ProcessStreamingAsync(action, "hello", ct: CancellationToken.None),
                () =>
                {
                    batchRuns++;
                    return Task.FromResult("batch");
                },
                _ => { },
                CancellationToken.None
            )
        );

        Assert.Equal(PluginRequestFailureKind.Configuration, error.FailureKind);
        Assert.False(error.IsTransient);
        Assert.Equal(
            Loc.Instance.GetString("Prompts.SelectedProviderUnavailable", "CLI Provider"),
            error.Message
        );
        Assert.Equal(0, batchRuns);
        // The dictation failure handler shows an InvalidOperationException's own message, so a
        // provider problem found after a context match still names the provider in the overlay.
        Assert.IsType<InvalidOperationException>(error, exactMatch: false);
    }

    [Fact]
    public async Task StreamingInvalidRequestFailure_StillFallsBackToBatch()
    {
        var batchRuns = 0;

        var outcome = await DictationOrchestrator.RunPromptActionStreamWithFallbackAsync(
            FaultingStream(
                new PluginRequestException(
                    "streaming is not supported here",
                    PluginRequestFailureKind.InvalidRequest,
                    isTransient: false
                )
            ),
            () =>
            {
                batchRuns++;
                return Task.FromResult("batch");
            },
            _ => { },
            CancellationToken.None
        );

        // Non-transient but specific to the streaming request (a gateway rejecting stream=true):
        // only a configuration failure fails the batch retry identically.
        Assert.Equal("batch", outcome.Text);
        Assert.True(outcome.StreamFaulted);
        Assert.Equal(1, batchRuns);
    }

    [Fact]
    public void TwoBrokenSelections_LogOneEntryEach_WhenTheyAlternate()
    {
        var signedOut = new FakeProvider("com.test.cli", "CLI Provider", "default")
        {
            IsAvailable = false,
        };
        using var pluginManager = CreatePluginManager(signedOut);
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { DefaultLlmProvider = "plugin:com.test.cli:default" }
        );
        var errorLog = new RecordingErrorLogService();
        var processing = new PromptProcessingService(
            pluginManager,
            settings.Object,
            new MemoryService(pluginManager),
            errorLog
        );

        // An unavailable default and a prompt override on an uninstalled provider, hit alternately.
        for (var round = 0; round < 2; round++)
        {
            processing.TryDescribeSelectedProviderProblem();
            processing.TryDescribeSelectedProviderProblem("plugin:com.test.uninstalled:model-u");
        }

        Assert.Equal(
            new[]
            {
                Loc.Instance.GetString("Prompts.SelectedProviderUnavailable", "CLI Provider"),
                Loc.Instance.GetString(
                    "Prompts.SelectedProviderMissing",
                    "com.test.uninstalled · model-u"
                ),
            },
            errorLog.AddedEntries.Select(entry => entry.Message).ToArray()
        );
    }

    private static async IAsyncEnumerable<string> FaultingStream(Exception failure)
    {
        await Task.Yield();
        yield return "partial";
        throw failure;
    }

    [Fact]
    public async Task ConfigurationFailure_LogsOnePromptEntryPerDistinctMessage()
    {
        var signedOut = new FakeProvider("com.test.cli", "CLI Provider", "default")
        {
            IsAvailable = false,
        };
        using var pluginManager = CreatePluginManager(signedOut);
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { DefaultLlmProvider = "plugin:com.test.cli:default" }
        );
        var errorLog = new RecordingErrorLogService();
        var processing = new PromptProcessingService(
            pluginManager,
            settings.Object,
            new MemoryService(pluginManager),
            errorLog
        );
        var action = new PromptAction
        {
            Id = "rewrite",
            Name = "Rewrite",
            SystemPrompt = "Rewrite this",
        };

        // Three dictations against the same broken configuration.
        processing.TryDescribeSelectedProviderProblem();
        await Assert.ThrowsAsync<PluginRequestException>(() =>
            processing.ProcessAsync(action, "hello", ct: CancellationToken.None)
        );
        processing.TryDescribeSelectedProviderProblem();

        var entry = Assert.Single(errorLog.AddedEntries);
        Assert.Equal(ErrorCategory.Prompt, entry.Category);
        Assert.Equal(
            Loc.Instance.GetString("Prompts.SelectedProviderUnavailable", "CLI Provider"),
            entry.Message
        );
    }

    private PluginManager CreatePluginManager(params FakeProvider[] providers)
    {
        return TestPluginManagerFactory.Create(
            providers,
            loadedPlugins: providers
                .Select(provider =>
                    TestPluginManagerFactory.CreateLoadedPlugin(
                        _tempDir,
                        provider.PluginId,
                        provider
                    )
                )
                .ToList()
        );
    }

    private sealed class RecordingErrorLogService : IErrorLogService
    {
        public List<(string Message, string Category)> AddedEntries { get; } = [];

        public IReadOnlyList<ErrorLogEntry> Entries => [];

        public event Action? EntriesChanged;

        public void AddEntry(string message, string category = ErrorCategory.General)
        {
            AddedEntries.Add((message, category));
            EntriesChanged?.Invoke();
        }

        public void ClearAll()
        {
            AddedEntries.Clear();
            EntriesChanged?.Invoke();
        }

        public string ExportDiagnostics()
        {
            return string.Empty;
        }
    }

    private sealed class FakeProvider : ILlmProviderPlugin
    {
        public FakeProvider(string pluginId, string providerName, string modelId)
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
