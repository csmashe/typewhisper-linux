using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictionaryAliasSuggestionsViewModelTests : IDisposable
{
    private const string LocalId = "com.typewhisper.gemma-local";
    private const string CloudId = "com.example.cloud";
    private readonly string _root = TestPaths.NewTempPath("TypeWhisper.AliasSuggestions");
    private readonly FakeLlm _local = new(LocalId, "Gemma", "gemma-4");
    private readonly FakeLlm _cloud = new(CloudId, "Cloud", "gpt");
    private readonly PluginManager _plugins;
    private readonly DictionaryService _dictionary;

    public DictionaryAliasSuggestionsViewModelTests()
    {
        Directory.CreateDirectory(_root);
        _plugins = TestPluginManagerFactory.Create(
            llmProviders: [_cloud, _local],
            loadedPlugins:
            [
                TestPluginManagerFactory.CreateLoadedPlugin(_root, LocalId, _local, PluginNetworkAccess.Local),
                TestPluginManagerFactory.CreateLoadedPlugin(_root, CloudId, _cloud),
            ]);
        _dictionary = new DictionaryService(Path.Join(_root, "dictionary.json"));
    }

    public void Dispose()
    {
        _plugins.Dispose();
        Directory.Delete(_root, true);
    }

    [Fact]
    public void OffersOnlyReadyModelsOfLocalPlugins()
    {
        var service = new DictionaryAliasSuggestionService(_plugins);

        var model = Assert.Single(service.GetLocalModels());
        Assert.Equal(LocalId, model.ProviderId);
        Assert.Equal("gemma-4", model.ModelId);

        _local.Available = false;
        Assert.Empty(service.GetLocalModels());
    }

    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("http://LOCALHOST:11434/v1")]
    [InlineData("https://127.0.0.1")]
    [InlineData("http://127.5.6.7:8080")]
    [InlineData("http://[::1]:11434")]
    public void LoopbackEndpoints_CountAsLocal(string url)
    {
        Assert.True(LoopbackEndpoint.IsLoopback(new Uri(url)));
    }

    [Theory]
    [InlineData("http://0.0.0.0:11434")]
    [InlineData("http://192.168.1.10:11434")]
    [InlineData("http://10.0.0.5")]
    [InlineData("https://api.openai.com")]
    [InlineData("http://localhost.example.com")]
    [InlineData("http://mylaptop:11434")]
    [InlineData("ftp://localhost")]
    [InlineData("file:///run/ollama.sock")]
    public void OtherEndpoints_DoNotCountAsLocal(string url)
    {
        Assert.False(LoopbackEndpoint.IsLoopback(new Uri(url)));
    }

    [Fact]
    public void MissingOrRelativeEndpoint_DoesNotCountAsLocal()
    {
        Assert.False(LoopbackEndpoint.IsLoopback(null));
        Assert.False(LoopbackEndpoint.IsLoopback(new Uri("/v1", UriKind.Relative)));
    }

    [Fact]
    public void ConfigurableServers_AreOfferedOnlyForLoopbackEndpoints()
    {
        var local = new FakeEndpointLlm("ollama-local", "Ollama", "llama3", "http://localhost:11434");
        var remote = new FakeEndpointLlm("ollama-office", "Office", "llama3", "http://192.168.1.10:11434");
        using var plugins = CreateUserControlledPlugins(local, remote);
        var service = new DictionaryAliasSuggestionService(plugins);

        var model = Assert.Single(service.GetLocalModels());
        Assert.Equal("ollama-local", model.ProviderId);
    }

    [Theory]
    [InlineData(PluginNetworkAccess.Network)]
    [InlineData(PluginNetworkAccess.Mixed)]
    public void LoopbackEndpoint_OfAPluginNotDeclaredUserControlled_IsNotOffered(PluginNetworkAccess access)
    {
        var claimant = new FakeEndpointLlm("claimant", "Claimant", "model", "http://localhost:11434");
        using var plugins = CreateEndpointPlugins(access, claimant);

        Assert.Empty(new DictionaryAliasSuggestionService(plugins).GetLocalModels());
    }

    [Fact]
    public void ConfigurableServer_HidesModelsItRunsInTheCloud()
    {
        var server = new FakeEndpointLlm("ollama-local", "Ollama", "llama3", "http://localhost:11434")
        {
            SupportedModels =
            [
                new PluginModelInfo("llama3", "llama3"),
                new PluginModelInfo("gpt-oss:120b-cloud", "cloud"),
                new PluginModelInfo("deepseek:cloud", "cloud"),
            ],
        };
        using var plugins = CreateUserControlledPlugins(server);

        Assert.Equal(["llama3"], new DictionaryAliasSuggestionService(plugins).GetLocalModels().Select(m => m.ModelId));
    }

    [Fact]
    public async Task ConfigurableServer_MovedToARemoteEndpoint_IsRefusedAtRequestTime()
    {
        var server = new FakeEndpointLlm("ollama-local", "Ollama", "llama3", "http://localhost:11434");
        using var plugins = CreateUserControlledPlugins(server);
        var service = new DictionaryAliasSuggestionService(plugins);
        var model = Assert.Single(service.GetLocalModels());

        server.Endpoint = new Uri("https://api.example.com");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SuggestAsync("TypeWhisper", "English", model, CancellationToken.None));
        Assert.Equal(0, server.Calls);
    }

    [Fact]
    public async Task RefusesCloudProviderEvenWhenRequestedById()
    {
        var service = new DictionaryAliasSuggestionService(_plugins);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SuggestAsync(
            "TypeWhisper", "English", new LocalAliasModel(CloudId, "gpt", "Cloud"), CancellationToken.None));
        Assert.Equal(0, _cloud.Calls);
    }

    [Fact]
    public async Task RechecksModelAtRequestTime()
    {
        var service = new DictionaryAliasSuggestionService(_plugins);
        var model = service.GetLocalModels()[0];
        _local.Models = [new PluginModelInfo("other", "Other")];

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SuggestAsync("TypeWhisper", "English", model, CancellationToken.None));
        Assert.Equal(0, _local.Calls);
    }

    [Fact]
    public async Task GenerationShowsUncheckedSuggestionsAndWritesNothing()
    {
        var sut = CreateViewModel();
        sut.OpenCommand.Execute("TypeWhisper");
        _local.Respond("[\"Type Whisper\",\"Teip Wisper\"]");

        await sut.GenerateAsync();

        Assert.True(sut.IsReviewed);
        Assert.Equal(["Type Whisper", "Teip Wisper"], sut.Suggestions.Select(item => item.Alias));
        Assert.All(sut.Suggestions, item => Assert.False(item.IsSelected));
        Assert.False(sut.CanRunPrimary);
        Assert.Empty(_dictionary.Entries);
        Assert.Contains("\"word\":\"TypeWhisper\"", _local.LastInput);
    }

    [Fact]
    public async Task SaveWritesOnlySelectedAliasesTowardTheTerm()
    {
        var sut = CreateViewModel();
        string? savedTerm = null;
        sut.Saved += term => savedTerm = term;
        sut.OpenCommand.Execute("TypeWhisper");
        _local.Respond("[\"Type Whisper\",\"Teip Wisper\"]");
        await sut.GenerateAsync();

        sut.Suggestions[1].IsSelected = true;
        Assert.True(sut.CanRunPrimary);
        await sut.RunPrimaryCommand.ExecuteAsync(null);

        Assert.Equal("TypeWhisper", savedTerm);
        Assert.False(sut.IsOpen);
        var correction = Assert.Single(_dictionary.Entries, e => e.EntryType == DictionaryEntryType.Correction);
        Assert.Equal("Teip Wisper", correction.Original);
        Assert.Equal("TypeWhisper", correction.Replacement);
        Assert.Contains(_dictionary.Entries, e => e is { EntryType: DictionaryEntryType.Term, Original: "TypeWhisper" });
    }

    [Fact]
    public async Task ExistingMappingsAreShownButCannotBeSelected()
    {
        _dictionary.AddEntries(
        [
            new DictionaryEntry
            {
                Id = "same", EntryType = DictionaryEntryType.Correction, Original = "Type Whisper",
                Replacement = "TypeWhisper", IsEnabled = false,
            },
            new DictionaryEntry
            {
                Id = "other", EntryType = DictionaryEntryType.Correction, Original = "Tight Whisper",
                Replacement = "Tight", IsEnabled = true,
            },
        ]);
        var sut = CreateViewModel();
        sut.OpenCommand.Execute("TypeWhisper");
        _local.Respond("[\"Type Whisper\",\"Tight Whisper\",\"Teip Wisper\"]");

        await sut.GenerateAsync();

        Assert.Equal([false, false, true], sut.Suggestions.Select(item => item.IsAvailable));
        Assert.Contains("Tight", sut.Suggestions[1].Label);
    }

    [Fact]
    public async Task ConflictCreatedAfterReviewBlocksSaveAndKeepsDictionary()
    {
        var sut = CreateViewModel();
        sut.OpenCommand.Execute("TypeWhisper");
        _local.Respond("[\"Teip Wisper\"]");
        await sut.GenerateAsync();
        sut.Suggestions[0].IsSelected = true;
        _dictionary.AddEntry(new DictionaryEntry
        {
            Id = "late", EntryType = DictionaryEntryType.Correction, Original = "teip wisper", Replacement = "Other",
        });

        await sut.RunPrimaryCommand.ExecuteAsync(null);

        Assert.True(sut.IsOpen);
        Assert.Equal(Loc.Instance.GetString("Dictionary.AliasesConflict", "Teip Wisper"), sut.Status);
        Assert.Single(_dictionary.Entries);
    }

    [Fact]
    public async Task EditingTermDuringRequestDiscardsTheLateResult()
    {
        var sut = CreateViewModel();
        sut.OpenCommand.Execute("TypeWhisper");
        var pending = _local.Hold();

        var generation = sut.GenerateAsync();
        Assert.True(sut.IsBusy);
        sut.Term = "GitHub";
        Assert.True(_local.LastToken.IsCancellationRequested);
        pending.SetResult("[\"Type Whisper\"]");
        await generation;

        Assert.False(sut.IsReviewed);
        Assert.Empty(sut.Suggestions);
        Assert.False(sut.IsBusy);
    }

    [Fact]
    public async Task RefreshKeepsReviewWhileTheSameModelIsOffered()
    {
        var sut = CreateViewModel();
        sut.OpenCommand.Execute("TypeWhisper");
        _local.Respond("[\"Teip Wisper\"]");
        await sut.GenerateAsync();
        // The ComboBox writes null back when its items are cleared.
        sut.Models.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                sut.SelectedModel = null;
            }
        };

        sut.RefreshModels();
        Assert.True(sut.IsReviewed);

        _local.Available = false;
        sut.RefreshModels();
        Assert.False(sut.IsReviewed);
    }

    [Fact]
    public async Task StopKeepsPanelOpenAndAddsNothing()
    {
        var sut = CreateViewModel();
        sut.OpenCommand.Execute("TypeWhisper");
        var pending = _local.Hold();

        var generation = sut.GenerateAsync();
        sut.StopCommand.Execute(null);
        pending.SetResult("[\"Type Whisper\"]");
        await generation;

        Assert.True(sut.IsOpen);
        Assert.False(sut.IsReviewed);
        Assert.Equal(Loc.Instance["Dictionary.AliasesCanceled"], sut.Status);
        Assert.Empty(_dictionary.Entries);
    }

    [Fact]
    public async Task StopReleasesTheCommandWhileTheProviderNeverReturns()
    {
        var sut = CreateViewModel();
        sut.OpenCommand.Execute("TypeWhisper");
        _local.Hold();

        var generation = sut.RunPrimaryCommand.ExecuteAsync(null);
        Assert.True(sut.IsBusy);
        sut.StopCommand.Execute(null);
        await generation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(sut.IsBusy);
        Assert.False(sut.RunPrimaryCommand.IsRunning);
        Assert.True(sut.RunPrimaryCommand.CanExecute(null));
        Assert.Equal(Loc.Instance["Dictionary.AliasesCanceled"], sut.Status);
    }

    [Fact]
    public async Task CloseReleasesTheCommandWhileTheProviderNeverReturns()
    {
        var sut = CreateViewModel();
        sut.OpenCommand.Execute("TypeWhisper");
        _local.Hold();

        var generation = sut.RunPrimaryCommand.ExecuteAsync(null);
        sut.CloseCommand.Execute(null);
        await generation.WaitAsync(TimeSpan.FromSeconds(5));
        sut.OpenCommand.Execute("TypeWhisper");
        _local.Hold().SetResult("[\"Teip Wisper\"]");
        await sut.RunPrimaryCommand.ExecuteAsync(null);

        Assert.True(sut.IsReviewed);
        Assert.Equal(2, _local.Calls);
    }

    [Fact]
    public async Task MalformedOutputIsReportedWithoutSuggestions()
    {
        var sut = CreateViewModel();
        sut.OpenCommand.Execute("TypeWhisper");
        _local.Respond("Sure! Here are some ideas: Type Whisper");

        await sut.GenerateAsync();

        Assert.False(sut.IsReviewed);
        Assert.Equal(Loc.Instance["Dictionary.AliasesUnusable"], sut.Status);
    }

    [Fact]
    public void WithoutLocalModelExplainsAndCannotGenerate()
    {
        _local.Available = false;
        var sut = CreateViewModel();

        sut.OpenCommand.Execute("TypeWhisper");

        Assert.True(sut.HasNoModels);
        Assert.False(sut.CanRunPrimary);
        Assert.Equal(Loc.Instance["Dictionary.AliasesNoModel"], sut.Status);
    }

    private DictionaryAliasSuggestionsViewModel CreateViewModel() =>
        new(
            _dictionary,
            new DictionaryAliasSuggestionService(_plugins),
            TestPluginManagerFactory.CreateSettings(new AppSettings()).Object);

    private const string ConfigurableId = "com.typewhisper.openai-compatible";

    private PluginManager CreateUserControlledPlugins(params FakeEndpointLlm[] roles) =>
        CreateEndpointPlugins(PluginNetworkAccess.UserControlled, roles);

    private PluginManager CreateEndpointPlugins(PluginNetworkAccess access, params FakeEndpointLlm[] roles) =>
        TestPluginManagerFactory.Create(
            llmProviders: roles,
            loadedPlugins:
            [
                TestPluginManagerFactory.CreateLoadedPlugin(
                    _root, ConfigurableId, new FakeLlm(ConfigurableId, "OpenAI Compatible", "none"), access),
            ]);

    // One role per configured server, all owned by the same user-controlled plugin.
    private sealed class FakeEndpointLlm(string selectionId, string name, string modelId, string endpoint)
        : ILlmProviderRole, ILlmProviderSelectionIdentity, ILlmProviderEndpoint
    {
        public Uri? Endpoint { get; set; } = new(endpoint);
        public int Calls { get; private set; }

        public string PluginId => ConfigurableId;
        public string LlmSelectionId => selectionId;
        public string ProviderName => name;
        public bool IsAvailable => true;
        public IReadOnlyList<PluginModelInfo> SupportedModels { get; init; } = [new(modelId, modelId)];
        public Uri? LlmEndpoint => Endpoint;

        public Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult("[]");
        }
    }

    private sealed class FakeLlm(string pluginId, string name, string modelId) : ILlmProviderPlugin
    {
        private TaskCompletionSource<string> _next = new();

        public bool Available { get; set; } = true;
        public IReadOnlyList<PluginModelInfo> Models { get; set; } = [new(modelId, modelId)];
        public int Calls { get; private set; }
        public string LastInput { get; private set; } = "";
        public CancellationToken LastToken { get; private set; }

        public string PluginId => pluginId;
        public string PluginName => name;
        public string PluginVersion => "1.0";
        public string ProviderName => name;
        public bool IsAvailable => Available;
        public IReadOnlyList<PluginModelInfo> SupportedModels => Available ? Models : [];

        public void Respond(string response) => _next.TrySetResult(response);

        public TaskCompletionSource<string> Hold() => _next = new TaskCompletionSource<string>();

        // Ignores cancellation on purpose, like a provider that finishes regardless.
        public Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
        {
            Calls++;
            LastInput = userText;
            LastToken = ct;
            return _next.Task;
        }

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
