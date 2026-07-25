using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.Linux.ViewModels.Sections;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class AdvancedSectionViewModelTests
{
    [Fact]
    public async Task CapabilityEvent_OnWorkerThread_DefersCompleteBoundStateRefresh()
    {
        using var harness = await TestHarness.CreateAsync();
        var providerMutations = 0;
        var voiceMutations = 0;
        var propertyNames = new List<string?>();
        // ReSharper disable AccessToModifiedClosure -- deliberate shared counters incremented from the handler and read via Volatile.Read after the worker completes.
        harness.ViewModel.SpokenFeedbackProviders.CollectionChanged += (_, _) =>
            Interlocked.Increment(ref providerMutations);
        harness.ViewModel.SpokenFeedbackVoices.CollectionChanged += (_, _) =>
            Interlocked.Increment(ref voiceMutations);
        // ReSharper restore AccessToModifiedClosure
        harness.ViewModel.PropertyChanged += (_, args) => propertyNames.Add(args.PropertyName);

        await Task.Run(() =>
        {
            harness.Plugin.SetState("Worker provider", "worker-voice", "Worker voice");
            harness.Plugin.NotifyCapabilitiesChanged();
        });

        Assert.Equal(2, harness.PostedActions.Count);
        Assert.Equal(0, Volatile.Read(ref providerMutations));
        Assert.Equal(0, Volatile.Read(ref voiceMutations));
        Assert.Empty(propertyNames);
        Assert.Equal(
            "Before provider",
            GetPluginProvider(harness.ViewModel).DisplayName
        );
        Assert.Equal(
            ["before-voice"],
            GetPluginVoices(harness.ViewModel).Select(voice => voice.Id)
        );

        foreach (var action in harness.PostedActions)
        {
            action();
        }

        Assert.True(Volatile.Read(ref providerMutations) > 0);
        Assert.True(Volatile.Read(ref voiceMutations) > 0);
        Assert.Equal(
            "Worker provider",
            GetPluginProvider(harness.ViewModel).DisplayName
        );
        Assert.Equal(
            ["worker-voice"],
            GetPluginVoices(harness.ViewModel).Select(voice => voice.Id)
        );
        Assert.Contains(nameof(AdvancedSectionViewModel.CanUseMemory), propertyNames);
        Assert.Contains(
            nameof(AdvancedSectionViewModel.ShowMemoryUnavailableReason),
            propertyNames
        );
        Assert.Contains(nameof(AdvancedSectionViewModel.MemoryHint), propertyNames);
        Assert.Contains(nameof(AdvancedSectionViewModel.CanUseSpokenFeedback), propertyNames);
        Assert.Contains(
            nameof(AdvancedSectionViewModel.ShowSpokenFeedbackUnavailableReason),
            propertyNames
        );
        Assert.Contains(nameof(AdvancedSectionViewModel.SpokenFeedbackHint), propertyNames);
    }

    [Fact]
    public async Task RapidCapabilityEvents_OnlyLatestPostedGenerationApplies()
    {
        using var harness = await TestHarness.CreateAsync();
        var capabilityPropertyRaises = 0;
        var providerMutations = 0;
        var voiceMutations = 0;
        harness.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AdvancedSectionViewModel.CanUseSpokenFeedback))
            {
                capabilityPropertyRaises++;
            }
        };
        harness.ViewModel.SpokenFeedbackProviders.CollectionChanged += (_, _) =>
            providerMutations++;
        harness.ViewModel.SpokenFeedbackVoices.CollectionChanged += (_, _) =>
            voiceMutations++;

        for (var generation = 1; generation <= 3; generation++)
        {
            harness.Plugin.SetState(
                $"Provider {generation}",
                $"voice-{generation}",
                $"Voice {generation}"
            );
            harness.Plugin.NotifyCapabilitiesChanged();
        }

        Assert.Equal(6, harness.PostedActions.Count);
        foreach (var staleAction in harness.PostedActions.Take(
                     harness.PostedActions.Count - 1
                 ))
        {
            staleAction();
        }

        Assert.Equal(0, capabilityPropertyRaises);
        Assert.Equal(0, providerMutations);
        Assert.Equal(0, voiceMutations);
        Assert.Equal(
            "Before provider",
            GetPluginProvider(harness.ViewModel).DisplayName
        );
        Assert.Equal(
            ["before-voice"],
            GetPluginVoices(harness.ViewModel).Select(voice => voice.Id)
        );

        harness.PostedActions[^1]();

        Assert.Equal(1, capabilityPropertyRaises);
        Assert.True(providerMutations > 0);
        Assert.True(voiceMutations > 0);
        Assert.Equal("Provider 3", GetPluginProvider(harness.ViewModel).DisplayName);
        Assert.Equal(
            ["voice-3"],
            GetPluginVoices(harness.ViewModel).Select(voice => voice.Id)
        );
    }

    [Fact]
    public async Task PostedCapabilityRefresh_ReadsPluginStateAtApplyTime()
    {
        using var harness = await TestHarness.CreateAsync();
        harness.Plugin.SetState("Event-time provider", "event-voice", "Event voice");
        harness.Plugin.NotifyCapabilitiesChanged();

        Assert.Equal(2, harness.PostedActions.Count);
        harness.Plugin.SetState("Apply-time provider", "apply-voice", "Apply voice");

        foreach (var action in harness.PostedActions)
        {
            action();
        }

        Assert.Equal(
            "Apply-time provider",
            GetPluginProvider(harness.ViewModel).DisplayName
        );
        var voice = Assert.Single(GetPluginVoices(harness.ViewModel));
        Assert.Equal("apply-voice", voice.Id);
        Assert.Equal("Apply voice", voice.DisplayName);
    }

    private static TtsProviderOption GetPluginProvider(AdvancedSectionViewModel viewModel)
    {
        return Assert.Single(
            viewModel.SpokenFeedbackProviders,
            provider => provider.Id == MutableTtsPlugin.ProviderId
        );
    }

    private static IEnumerable<TtsVoiceOption> GetPluginVoices(
        AdvancedSectionViewModel viewModel
    )
    {
        return viewModel.SpokenFeedbackVoices.Where(voice =>
            voice.Id != SpeechFeedbackService.DefaultVoiceOptionId
        );
    }

    private sealed class TestHarness : IDisposable
    {
        private TestHarness(
            PluginManager pluginManager,
            SpeechFeedbackService speechFeedback,
            MutableTtsPlugin plugin,
            AdvancedSectionViewModel viewModel,
            List<Action> postedActions
        )
        {
            PluginManager = pluginManager;
            SpeechFeedback = speechFeedback;
            Plugin = plugin;
            ViewModel = viewModel;
            PostedActions = postedActions;
        }

        private PluginManager PluginManager { get; }
        private SpeechFeedbackService SpeechFeedback { get; }
        public MutableTtsPlugin Plugin { get; }
        public AdvancedSectionViewModel ViewModel { get; }
        public List<Action> PostedActions { get; }

        public static async Task<TestHarness> CreateAsync()
        {
            var settings = TestPluginManagerFactory.CreateSettings(
                new AppSettings
                {
                    SpokenFeedbackProviderId = MutableTtsPlugin.ProviderId,
                    SpokenFeedbackVoiceId = "before-voice",
                }
            );
            var plugin = new MutableTtsPlugin(
                MutableTtsPlugin.ProviderId,
                "Before provider",
                "before-voice",
                "Before voice"
            );
            var pluginDirectory = Path.GetDirectoryName(
                typeof(AdvancedSectionViewModelTests).Assembly.Location
            )!;
            var loadedPlugin = TestPluginManagerFactory.CreateLoadedPlugin(
                pluginDirectory,
                plugin.PluginId,
                plugin
            );
            var pluginManager = TestPluginManagerFactory.Create(loadedPlugins: [loadedPlugin]);
            await pluginManager.EnablePluginAsync(plugin.PluginId);

            var systemProvider = new MutableTtsPlugin(
                "linux-system",
                "System provider",
                "system-voice",
                "System voice"
            );
            var speechFeedback = new SpeechFeedbackService(
                settings.Object,
                pluginManager,
                systemProvider
            );
            var postedActions = new List<Action>();
            var viewModel = new AdvancedSectionViewModel(
                settings.Object,
                speechFeedback,
                pluginManager,
                postedActions.Add
            );
            return new TestHarness(
                pluginManager,
                speechFeedback,
                plugin,
                viewModel,
                postedActions
            );
        }

        public void Dispose()
        {
            SpeechFeedback.Dispose();
            PluginManager.Dispose();
        }
    }

    private sealed class MutableTtsPlugin : ITtsProviderPlugin
    {
        public const string ProviderId = "mutable-provider";

        private IPluginHostServices? _host;

        public MutableTtsPlugin(
            string providerId,
            string displayName,
            string voiceId,
            string voiceName
        )
        {
            ProviderIdValue = providerId;
            SetState(displayName, voiceId, voiceName);
        }

        public string PluginId => $"plugin.{ProviderIdValue}";
        public string PluginName => ProviderDisplayName;
        public string PluginVersion => "1.0.0";
        private string ProviderIdValue { get; }
        string ITtsProviderPlugin.ProviderId => ProviderIdValue;
        public string ProviderDisplayName { get; private set; } = "";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginVoiceInfo> AvailableVoices { get; private set; } = [];
        public string? SelectedVoiceId { get; private set; }

        public Task ActivateAsync(IPluginHostServices host)
        {
            _host = host;
            return Task.CompletedTask;
        }

        public Task DeactivateAsync()
        {
            return Task.CompletedTask;
        }

        public void SetState(string displayName, string voiceId, string voiceName)
        {
            ProviderDisplayName = displayName;
            AvailableVoices = [new PluginVoiceInfo(voiceId, voiceName)];
            SelectedVoiceId = voiceId;
        }

        public void NotifyCapabilitiesChanged()
        {
            Assert.NotNull(_host);
            _host.NotifyCapabilitiesChanged();
        }

        public void SelectVoice(string? voiceId)
        {
            SelectedVoiceId = voiceId;
        }

        public Task<ITtsPlaybackSession> SpeakAsync(
            TtsSpeakRequest request,
            CancellationToken ct
        )
        {
            return Task.FromResult<ITtsPlaybackSession>(InactivePlaybackSession.Instance);
        }

        public void Dispose() { }
    }

    private sealed class InactivePlaybackSession : ITtsPlaybackSession
    {
        public static InactivePlaybackSession Instance { get; } = new();

        public bool IsActive => false;

        public event EventHandler? Completed
        {
            add { value?.Invoke(this, EventArgs.Empty); }
            remove { }
        }

        public void Stop() { }
    }
}
