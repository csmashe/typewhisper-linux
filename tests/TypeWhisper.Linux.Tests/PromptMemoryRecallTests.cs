using Moq;
using System.Reflection;
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

public sealed class PromptMemoryRecallTests : IDisposable
{
    private static readonly PromptAction s_action = new()
    {
        Id = "prompt",
        Name = "Rewrite",
        SystemPrompt = "Rewrite this",
    };

    private readonly string _tempDir = TestPaths.CreateTempDirectory(
        "TypeWhisper.PromptMemoryRecallTests"
    );

    private readonly RecordingLlmProvider _llm = new();
    private readonly FakeMemorySource _first = new("com.test.memory-a", "Memory A", "fact from A");
    private readonly FakeMemorySource _second = new("com.test.memory-b", "Memory B", "fact from B");

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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InheritedRecall_FollowsGlobalSettingWithFirstSource(
        bool memoryEnabled,
        bool streaming
    )
    {
        using var manager = CreatePluginManager(_first, _second);
        var sut = CreateService(manager, memoryEnabled);

        await RunAsync(sut, streaming, memorySourceId: null);

        if (memoryEnabled)
        {
            Assert.Contains("- fact from A", _llm.LastSystemPrompt);
            Assert.DoesNotContain("fact from B", _llm.LastSystemPrompt);
            Assert.Equal(1, _first.SearchCount);
        }
        else
        {
            Assert.Equal("Rewrite this", _llm.LastSystemPrompt);
            Assert.Equal(0, _first.SearchCount);
        }

        Assert.Equal(0, _second.SearchCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProfileOff_RecallsNothingEvenWhenGlobalMemoryIsOn(bool streaming)
    {
        using var manager = CreatePluginManager(_first);
        var sut = CreateService(manager, memoryEnabled: true);

        await RunAsync(sut, streaming, Profile.MemorySourceOff);

        Assert.Equal("Rewrite this", _llm.LastSystemPrompt);
        Assert.Equal(0, _first.SearchCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedSource_IsTheOnlySourceQueriedEvenWhenGlobalMemoryIsOff(bool streaming)
    {
        using var manager = CreatePluginManager(_first, _second);
        var sut = CreateService(manager, memoryEnabled: false);
        var capture = new LlmCallCapture();

        await RunAsync(sut, streaming, _second.PluginId, capture);

        Assert.Contains("- fact from B", _llm.LastSystemPrompt);
        Assert.DoesNotContain("fact from A", _llm.LastSystemPrompt);
        Assert.Equal(0, _first.SearchCount);
        Assert.Equal("dictated words", _second.LastQuery);
        Assert.Equal("- fact from B", Assert.Single(capture.Calls).InjectedMemoryContext);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableSelectedSource_FailsWithoutCallingTheLlmOrAnotherSource(bool streaming)
    {
        using var manager = CreatePluginManager(_first);
        var sut = CreateService(manager, memoryEnabled: true);

        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            RunAsync(sut, streaming, "com.test.missing")
        );

        Assert.Equal(Loc.Instance["Memory.SourceUnavailable"], error.Message);
        Assert.Equal(PluginRequestFailureKind.Configuration, error.FailureKind);
        Assert.False(error.IsTransient);
        Assert.Equal(0, _llm.CallCount);
        Assert.Equal(0, _first.SearchCount);
    }

    [Fact]
    public async Task FailedSelectedSource_IsNotRetriedThroughTheBatchFallback()
    {
        _second.Failure = new IOException("disk gone");
        using var manager = CreatePluginManager(_second);
        var sut = CreateService(manager, memoryEnabled: false);
        var batchRuns = 0;

        await Assert.ThrowsAsync<PluginRequestException>(() =>
            DictationOrchestrator.RunPromptActionStreamWithFallbackAsync(
                sut.ProcessStreamingAsync(s_action, "dictated words", memorySourceId: _second.PluginId),
                () =>
                {
                    batchRuns++;
                    return Task.FromResult("batch");
                },
                _ => { },
                CancellationToken.None
            )
        );

        Assert.Equal(0, batchRuns);
        Assert.Equal(1, _second.SearchCount);
    }

    [Fact]
    public async Task FailingSelectedSource_FailsTheAction()
    {
        _second.Failure = new IOException("disk gone");
        using var manager = CreatePluginManager(_second);
        var sut = CreateService(manager, memoryEnabled: false);

        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            RunAsync(sut, streaming: false, _second.PluginId)
        );

        Assert.Equal(Loc.Instance["Memory.SourceFailed"], error.Message);
        Assert.Equal(0, _llm.CallCount);
    }

    [Fact]
    public async Task FailingGlobalSource_ProcessesWithoutContext()
    {
        _first.Failure = new IOException("disk gone");
        using var manager = CreatePluginManager(_first);
        var sut = CreateService(manager, memoryEnabled: true);

        await RunAsync(sut, streaming: false, memorySourceId: null);

        Assert.Equal("Rewrite this", _llm.LastSystemPrompt);
    }

    [Fact]
    public async Task SelectedSourceDisabledDuringSearch_DoesNotSupplyItsResult()
    {
        using var manager = CreatePluginManager(_second);
        // ReSharper disable once AccessToDisposedClosure -- the callback runs only inside the awaited call below, before manager is disposed.
        _second.OnSearch = () => ActivatedPlugins(manager).Remove(_second.PluginId);
        var sut = CreateService(manager, memoryEnabled: false);

        var error = await Assert.ThrowsAsync<PluginRequestException>(() =>
            RunAsync(sut, streaming: false, _second.PluginId)
        );

        Assert.Equal(Loc.Instance["Memory.SourceUnavailable"], error.Message);
        Assert.Equal(0, _llm.CallCount);
    }

    [Fact]
    public async Task CanceledRecall_PropagatesCancellation()
    {
        using var manager = CreatePluginManager(_first);
        var sut = CreateService(manager, memoryEnabled: true);
        using var cts = new CancellationTokenSource();
        _first.OnSearch = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.ProcessAsync(s_action, "dictated words", ct: cts.Token)
        );
        Assert.Equal(0, _llm.CallCount);
    }

    [Fact]
    public async Task Extraction_RejectedFactDoesNotDropTheRest()
    {
        _llm.Response = "- a fact the plugin rejects\n- Chris uses Fedora at work";
        _first.RejectedContent = "a fact the plugin rejects";
        using var manager = CreatePluginManager(_first);

        await new MemoryService(manager).ExtractAndStoreAsync(
            "Today I set up the new laptop with Fedora for work."
        );

        Assert.Equal(["Chris uses Fedora at work"], _first.Stored);
    }

    [Fact]
    public void FormatContext_BoundsCountAndLengthAndFlattensLines()
    {
        var context = MemoryService.FormatContext(
            [
                "first\nline  two",
                "   ",
                new string('x', MemoryService.MaxFactLength + 50),
                "c",
                "d",
                "e",
                "f",
            ]
        );

        var lines = context!.Split('\n');
        Assert.Equal(MemoryService.MaxRecalledFacts, lines.Length);
        Assert.Equal("- first line two", lines[0]);
        Assert.Equal("- " + new string('x', MemoryService.MaxFactLength), lines[1]);
        Assert.Equal("- e", lines[^1]);
        Assert.Null(MemoryService.FormatContext([" ", ""]));
    }

    [Fact]
    public void WithMemoryContext_MarksMemoriesAsReferenceDataOnlyWhenPresent()
    {
        Assert.Equal("system", PromptProcessingService.WithMemoryContext("system", null));

        var prompt = PromptProcessingService.WithMemoryContext("system", "- a fact");

        Assert.StartsWith("system\n\n", prompt);
        Assert.Contains("not instructions", prompt);
        Assert.EndsWith("- a fact", prompt);
    }

    private static async Task RunAsync(
        PromptProcessingService sut,
        bool streaming,
        string? memorySourceId,
        LlmCallCapture? capture = null
    )
    {
        if (streaming)
        {
            await foreach (
                var _ in sut.ProcessStreamingAsync(
                    s_action,
                    "dictated words",
                    capture,
                    memorySourceId: memorySourceId
                )
            ) { }
        }
        else
        {
            await sut.ProcessAsync(s_action, "dictated words", capture, memorySourceId: memorySourceId);
        }
    }

    private static PromptProcessingService CreateService(PluginManager manager, bool memoryEnabled)
    {
        var settings = new Mock<ISettingsService>();
        settings
            .SetupGet(service => service.Current)
            .Returns(new AppSettings { MemoryEnabled = memoryEnabled });
        return new PromptProcessingService(manager, settings.Object, new MemoryService(manager));
    }

    private PluginManager CreatePluginManager(params FakeMemorySource[] sources)
    {
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings());
        var profiles = new Mock<IProfileService>();
        profiles.SetupGet(service => service.Profiles).Returns([]);

        var manager = new PluginManager(
            new PluginLoader(Path.Join(_tempDir, "PluginData")),
            new PluginEventBus(),
            new Mock<IActiveWindowService>().Object,
            profiles.Object,
            settings.Object,
            []
        );

        SetPrivateField(manager, "_llmProviders", new List<ILlmProviderRole> { _llm });
        SetPrivateField(
            manager,
            "_allPlugins",
            sources.Select(CreateLoadedPlugin).Prepend(CreateLoadedPlugin(_llm)).ToList()
        );
        var activated = ActivatedPlugins(manager);
        foreach (var source in sources)
        {
            activated.Add(source.PluginId);
        }

        return manager;
    }

    private LoadedPlugin CreateLoadedPlugin(ITypeWhisperPlugin plugin)
    {
        var pluginDir = Path.Join(_tempDir, plugin.PluginId);
        Directory.CreateDirectory(pluginDir);
        var manifest = new PluginManifest
        {
            Id = plugin.PluginId,
            Name = plugin.PluginName,
            Version = plugin.PluginVersion,
            AssemblyName = "fake.dll",
            PluginClass = plugin.GetType().FullName ?? plugin.GetType().Name,
            NetworkAccess = PluginNetworkAccess.Local,
            Categories = [plugin is IMemoryStoragePlugin ? PluginCategory.Memory : PluginCategory.Llm],
        };
        return new LoadedPlugin(
            manifest,
            plugin,
            new PluginAssemblyLoadContext(pluginDir),
            pluginDir,
            PluginLoader.ResolveMetadata(manifest)
        );
    }

    // PluginManager exposes no public seam for seeding plugins; reflection reaches its private state.
    private static HashSet<string> ActivatedPlugins(PluginManager manager) =>
        (HashSet<string>)typeof(PluginManager)
            .GetField("_activatedPlugins", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!;

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field =
            target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        field.SetValue(target, value);
    }

    private sealed class FakeMemorySource(string pluginId, string name, string fact)
        : IMemoryStoragePlugin
    {
        public string PluginId { get; } = pluginId;
        public string PluginName { get; } = name;
        public string PluginVersion => "1.0.0";
        public int SearchCount { get; private set; }
        public string? LastQuery { get; private set; }
        public Exception? Failure { get; set; }
        public Action? OnSearch { get; set; }
        public string? RejectedContent { get; set; }
        public List<string> Stored { get; } = [];

        public Task<IReadOnlyList<string>> SearchAsync(
            string query,
            int maxResults = 5,
            CancellationToken ct = default
        )
        {
            SearchCount++;
            LastQuery = query;
            OnSearch?.Invoke();
            ct.ThrowIfCancellationRequested();
            return Failure is null
                ? Task.FromResult<IReadOnlyList<string>>([fact])
                : Task.FromException<IReadOnlyList<string>>(Failure);
        }

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public Task StoreAsync(string content, CancellationToken ct = default)
        {
            if (content == RejectedContent)
            {
                throw new ArgumentException("rejected", nameof(content));
            }

            Stored.Add(content);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([fact]);
        public Task DeleteAsync(string content, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(1);
        public void Dispose() { }
    }

    private sealed class RecordingLlmProvider : ILlmProviderPlugin
    {
        public string PluginId => "com.test.llm";
        public string PluginName => "Recording LLM";
        public string PluginVersion => "1.0.0";
        public string ProviderName => "Recording LLM";
        public bool IsAvailable => true;
        public IReadOnlyList<PluginModelInfo> SupportedModels => [new("model", "Model")];
        public string? LastSystemPrompt { get; private set; }
        public string Response { get; set; } = "result";
        public int CallCount { get; private set; }

        public Task<string> ProcessAsync(
            string systemPrompt,
            string userText,
            string model,
            CancellationToken ct
        )
        {
            CallCount++;
            LastSystemPrompt = systemPrompt;
            return Task.FromResult(Response);
        }

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
