using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
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

    [Fact]
    public async Task StartupBeforePlugins_RetainsConfiguredTtsPreferenceAndRehydratesWithoutSaving()
    {
        using var harness = await TestHarness.CreateAsync(enablePluginBeforeViewModel: false);

        Assert.Equal(
            MutableTtsPlugin.ProviderId,
            harness.Settings.Object.Current.SpokenFeedbackProviderId
        );
        Assert.Equal("before-voice", harness.Settings.Object.Current.SpokenFeedbackVoiceId);
        Assert.Equal(
            AppSettings.DefaultSpokenFeedbackProviderId,
            harness.ViewModel.SelectedSpokenFeedbackProviderId
        );
        Assert.Equal(
            AppSettings.DefaultSpokenFeedbackProviderId,
            harness.ViewModel.SelectedSpokenFeedbackProviderOption?.Id
        );
        harness.Settings.Verify(
            service => service.Save(It.IsAny<AppSettings>()),
            Times.Never
        );

        await harness.PluginManager.EnablePluginAsync(harness.Plugin.PluginId);
        harness.ApplyPostedActions();

        Assert.Equal(
            MutableTtsPlugin.ProviderId,
            harness.ViewModel.SelectedSpokenFeedbackProviderId
        );
        Assert.Equal(
            MutableTtsPlugin.ProviderId,
            harness.ViewModel.SelectedSpokenFeedbackProviderOption?.Id
        );
        Assert.Equal("before-voice", harness.ViewModel.SelectedSpokenFeedbackVoiceId);
        Assert.Equal("before-voice", harness.ViewModel.SelectedSpokenFeedbackVoiceOption?.Id);
        Assert.Equal(
            MutableTtsPlugin.ProviderId,
            harness.Settings.Object.Current.SpokenFeedbackProviderId
        );
        Assert.Equal("before-voice", harness.Settings.Object.Current.SpokenFeedbackVoiceId);
        harness.Settings.Verify(
            service => service.Save(It.IsAny<AppSettings>()),
            Times.Never
        );
    }

    [Fact]
    public async Task MemoryCapabilityChanges_PreserveConfiguredEnabledPreferenceWithoutSaving()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings { MemoryEnabled = true }
        );
        var plugin = new MutableMemoryLlmPlugin();
        var pluginDirectory = Path.GetDirectoryName(
            typeof(AdvancedSectionViewModelTests).Assembly.Location
        )!;
        var loadedPlugin = TestPluginManagerFactory.CreateLoadedPlugin(
            pluginDirectory,
            plugin.PluginId,
            plugin
        );
        using var pluginManager = TestPluginManagerFactory.Create(
            loadedPlugins: [loadedPlugin]
        );
        var systemProvider = new MutableTtsPlugin(
            "linux-system",
            "System provider",
            "system-voice",
            "System voice"
        );
        using var speechFeedback = new SpeechFeedbackService(
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

        Assert.False(viewModel.CanUseMemory);
        Assert.False(viewModel.MemoryEnabled);
        Assert.True(settings.Object.Current.MemoryEnabled);
        settings.Verify(service => service.Save(It.IsAny<AppSettings>()), Times.Never);

        await pluginManager.EnablePluginAsync(plugin.PluginId);
        ApplyPostedActions(postedActions);

        Assert.True(viewModel.CanUseMemory);
        Assert.True(viewModel.MemoryEnabled);
        Assert.True(settings.Object.Current.MemoryEnabled);
        settings.Verify(service => service.Save(It.IsAny<AppSettings>()), Times.Never);

        await pluginManager.DisablePluginAsync(plugin.PluginId);
        ApplyPostedActions(postedActions);

        Assert.False(viewModel.CanUseMemory);
        Assert.False(viewModel.MemoryEnabled);
        Assert.True(settings.Object.Current.MemoryEnabled);
        settings.Verify(service => service.Save(It.IsAny<AppSettings>()), Times.Never);
    }

    [Fact]
    public async Task SpokenFeedbackCapabilityChanges_PreserveConfiguredEnabledPreferenceWithoutSaving()
    {
        var settings = TestPluginManagerFactory.CreateSettings(
            new AppSettings
            {
                SpokenFeedbackEnabled = true,
                SpokenFeedbackProviderId = "linux-system",
            }
        );
        var auxPlugin = new MutableTtsPlugin(
            "aux-provider",
            "Aux provider",
            "aux-voice",
            "Aux voice"
        );
        var pluginDirectory = Path.GetDirectoryName(
            typeof(AdvancedSectionViewModelTests).Assembly.Location
        )!;
        var loadedPlugin = TestPluginManagerFactory.CreateLoadedPlugin(
            pluginDirectory,
            auxPlugin.PluginId,
            auxPlugin
        );
        using var pluginManager = TestPluginManagerFactory.Create(
            loadedPlugins: [loadedPlugin]
        );
        var systemProvider = new MutableTtsPlugin(
            "linux-system",
            "System provider",
            "system-voice",
            "System voice"
        );
        using var speechFeedback = new SpeechFeedbackService(
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

        Assert.True(viewModel.CanUseSpokenFeedback);
        Assert.True(viewModel.SpokenFeedbackEnabled);
        Assert.True(settings.Object.Current.SpokenFeedbackEnabled);
        settings.Verify(service => service.Save(It.IsAny<AppSettings>()), Times.Never);

        systemProvider.IsConfigured = false;
        await pluginManager.EnablePluginAsync(auxPlugin.PluginId);
        ApplyPostedActions(postedActions);

        Assert.False(viewModel.CanUseSpokenFeedback);
        Assert.False(viewModel.SpokenFeedbackEnabled);
        Assert.True(settings.Object.Current.SpokenFeedbackEnabled);
        settings.Verify(service => service.Save(It.IsAny<AppSettings>()), Times.Never);

        systemProvider.IsConfigured = true;
        await pluginManager.DisablePluginAsync(auxPlugin.PluginId);
        ApplyPostedActions(postedActions);

        Assert.True(viewModel.CanUseSpokenFeedback);
        Assert.True(viewModel.SpokenFeedbackEnabled);
        Assert.True(settings.Object.Current.SpokenFeedbackEnabled);
        settings.Verify(service => service.Save(It.IsAny<AppSettings>()), Times.Never);
    }

    [Fact]
    public async Task ProgrammaticVoiceFallback_DoesNotSelectOrSave_UserVoiceEditDoes()
    {
        using var harness = await TestHarness.CreateAsync();
        harness.Settings.Invocations.Clear();
        harness.Plugin.ResetSelectVoiceCalls();
        harness.Plugin.SetState(
            "Fallback provider",
            "available-voice",
            "Available voice",
            selectedVoiceId: "missing-voice"
        );

        harness.Plugin.NotifyCapabilitiesChanged();
        harness.ApplyPostedActions();

        Assert.Equal(
            SpeechFeedbackService.DefaultVoiceOptionId,
            harness.ViewModel.SelectedSpokenFeedbackVoiceId
        );
        Assert.Equal(0, harness.Plugin.SelectVoiceCallCount);
        Assert.Equal("before-voice", harness.Settings.Object.Current.SpokenFeedbackVoiceId);
        harness.Settings.Verify(
            service => service.Save(It.IsAny<AppSettings>()),
            Times.Never
        );

        harness.ViewModel.SelectedSpokenFeedbackVoiceOption = Assert.Single(
            harness.ViewModel.SpokenFeedbackVoices,
            voice => voice.Id == "available-voice"
        );

        Assert.Equal(1, harness.Plugin.SelectVoiceCallCount);
        Assert.Equal("available-voice", harness.Plugin.SelectedVoiceId);
        Assert.Equal(
            "available-voice",
            harness.Settings.Object.Current.SpokenFeedbackVoiceId
        );
        harness.Settings.Verify(
            service => service.Save(It.IsAny<AppSettings>()),
            Times.Once
        );
    }

    [Fact]
    public async Task LanguageChange_RebuildsLocalizedOptions_PreservesSelectionWithoutSaving()
    {
        var originalLanguage = Loc.Instance.CurrentLanguage;
        try
        {
            Loc.Instance.CurrentLanguage = "en";
            using var harness = await TestHarness.CreateAsync();
            harness.ViewModel.SelectedAutoUnloadOption = Assert.Single(
                harness.ViewModel.AutoUnloadOptions,
                option => option.Seconds == 300
            );
            harness.ViewModel.SelectedHistoryRetention = Assert.Single(
                harness.ViewModel.HistoryRetentionOptions,
                option => option.Mode == HistoryRetentionMode.UntilAppCloses
            );
            harness.Settings.Invocations.Clear();

            var autoUnloadBefore = harness.ViewModel.SelectedAutoUnloadOption!;
            var retentionBefore = harness.ViewModel.SelectedHistoryRetention!;
            var defaultVoiceBefore = Assert.Single(
                harness.ViewModel.SpokenFeedbackVoices,
                voice => voice.Id == SpeechFeedbackService.DefaultVoiceOptionId
            );
            var selectedVoiceIdBefore = harness.ViewModel.SelectedSpokenFeedbackVoiceId;
            HashSet<string?> expectedPropertyChanges =
            [
                nameof(AdvancedSectionViewModel.SpokenFeedbackHint),
                nameof(AdvancedSectionViewModel.MemoryHint),
            ];
            HashSet<string?> propertyChanges = [];
            harness.ViewModel.PropertyChanged += (_, args) =>
            {
                propertyChanges.Add(args.PropertyName);

                // ReSharper disable once ConvertIfStatementToSwitchStatement -- independent property-name checks, each with its own suppression comment.
                if (args.PropertyName == nameof(AdvancedSectionViewModel.AutoUnloadOptions))
                {
                    // ReSharper disable once AccessToDisposedClosure -- handler runs synchronously while setting Loc.Instance.CurrentLanguage below, before the using disposes harness at scope end.
                    harness.ViewModel.SelectedAutoUnloadOption = null;
                }

                if (args.PropertyName == nameof(AdvancedSectionViewModel.HistoryRetentionOptions))
                {
                    // ReSharper disable once AccessToDisposedClosure -- handler runs synchronously while setting Loc.Instance.CurrentLanguage below, before the using disposes harness at scope end.
                    harness.ViewModel.SelectedHistoryRetention = null;
                }
            };

            Loc.Instance.CurrentLanguage = "de";

            Assert.Superset(expectedPropertyChanges, propertyChanges);
            Assert.NotEqual(
                autoUnloadBefore.DisplayName,
                harness.ViewModel.SelectedAutoUnloadOption?.DisplayName
            );
            Assert.NotSame(autoUnloadBefore, harness.ViewModel.SelectedAutoUnloadOption);
            Assert.Equal(300, harness.ViewModel.SelectedAutoUnloadOption?.Seconds);
            Assert.NotEqual(
                retentionBefore.DisplayName,
                harness.ViewModel.SelectedHistoryRetention?.DisplayName
            );
            Assert.NotSame(retentionBefore, harness.ViewModel.SelectedHistoryRetention);
            Assert.Equal(
                HistoryRetentionMode.UntilAppCloses,
                harness.ViewModel.SelectedHistoryRetention?.Mode
            );
            Assert.Null(harness.ViewModel.SelectedHistoryRetention?.Minutes);

            var defaultVoiceAfter = Assert.Single(
                harness.ViewModel.SpokenFeedbackVoices,
                voice => voice.Id == SpeechFeedbackService.DefaultVoiceOptionId
            );
            Assert.NotEqual(defaultVoiceBefore.DisplayName, defaultVoiceAfter.DisplayName);
            Assert.NotSame(defaultVoiceBefore, defaultVoiceAfter);
            Assert.Equal(
                selectedVoiceIdBefore,
                harness.ViewModel.SelectedSpokenFeedbackVoiceId
            );

            harness.Settings.Verify(
                service => service.Save(It.IsAny<AppSettings>()),
                Times.Never
            );
        }
        finally
        {
            Loc.Instance.CurrentLanguage = originalLanguage;
        }
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

    private static void ApplyPostedActions(List<Action> postedActions)
    {
        var actions = postedActions.ToList();
        postedActions.Clear();
        foreach (var action in actions)
        {
            action();
        }
    }

    private sealed class TestHarness : IDisposable
    {
        private TestHarness(
            Mock<ISettingsService> settings,
            PluginManager pluginManager,
            SpeechFeedbackService speechFeedback,
            MutableTtsPlugin plugin,
            AdvancedSectionViewModel viewModel,
            List<Action> postedActions
        )
        {
            Settings = settings;
            PluginManager = pluginManager;
            SpeechFeedback = speechFeedback;
            Plugin = plugin;
            ViewModel = viewModel;
            PostedActions = postedActions;
        }

        public Mock<ISettingsService> Settings { get; }
        public PluginManager PluginManager { get; }
        private SpeechFeedbackService SpeechFeedback { get; }
        public MutableTtsPlugin Plugin { get; }
        public AdvancedSectionViewModel ViewModel { get; }
        public List<Action> PostedActions { get; }

        public static async Task<TestHarness> CreateAsync(
            bool enablePluginBeforeViewModel = true
        )
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
            if (enablePluginBeforeViewModel)
            {
                await pluginManager.EnablePluginAsync(plugin.PluginId);
            }

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
                settings,
                pluginManager,
                speechFeedback,
                plugin,
                viewModel,
                postedActions
            );
        }

        public void ApplyPostedActions()
        {
            AdvancedSectionViewModelTests.ApplyPostedActions(PostedActions);
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
            string voiceName,
            string? selectedVoiceId = null
        )
        {
            ProviderIdValue = providerId;
            SetState(displayName, voiceId, voiceName, selectedVoiceId);
        }

        public string PluginId => $"plugin.{ProviderIdValue}";
        public string PluginName => ProviderDisplayName;
        public string PluginVersion => "1.0.0";
        private string ProviderIdValue { get; }
        string ITtsProviderPlugin.ProviderId => ProviderIdValue;
        public string ProviderDisplayName { get; private set; } = "";
        public bool IsConfigured { get; set; } = true;
        public IReadOnlyList<PluginVoiceInfo> AvailableVoices { get; private set; } = [];
        public string? SelectedVoiceId { get; private set; }
        public int SelectVoiceCallCount { get; private set; }

        public Task ActivateAsync(IPluginHostServices host)
        {
            _host = host;
            return Task.CompletedTask;
        }

        public Task DeactivateAsync()
        {
            return Task.CompletedTask;
        }

        public void SetState(
            string displayName,
            string voiceId,
            string voiceName,
            string? selectedVoiceId = null
        )
        {
            ProviderDisplayName = displayName;
            AvailableVoices = [new PluginVoiceInfo(voiceId, voiceName)];
            SelectedVoiceId = selectedVoiceId ?? voiceId;
        }

        public void NotifyCapabilitiesChanged()
        {
            Assert.NotNull(_host);
            _host.NotifyCapabilitiesChanged();
        }

        public void SelectVoice(string? voiceId)
        {
            SelectVoiceCallCount++;
            SelectedVoiceId = voiceId;
        }

        public void ResetSelectVoiceCalls()
        {
            SelectVoiceCallCount = 0;
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

    private sealed class MutableMemoryLlmPlugin : IMemoryStoragePlugin, ILlmProviderPlugin
    {
        public string PluginId => "plugin.mutable-memory-llm";
        public string PluginName => "Mutable memory and LLM";
        public string PluginVersion => "1.0.0";
        public string ProviderName => "Mutable LLM";
        public bool IsAvailable => true;
        public IReadOnlyList<PluginModelInfo> SupportedModels { get; } = [];

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
            return Task.FromResult("");
        }

        public Task StoreAsync(string content, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> SearchAsync(
            string query,
            int maxResults = 5,
            CancellationToken ct = default
        )
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<IReadOnlyList<string>> GetAllAsync(CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task DeleteAsync(string content, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task ClearAllAsync(CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task<int> CountAsync(CancellationToken ct = default)
        {
            return Task.FromResult(0);
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
