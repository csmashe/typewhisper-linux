using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Integration.Tests.TestDoubles;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Integration.Tests;

public sealed class OverlayCoordinationInterleavingTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public Task DictationRecording_SuppressesTransformProcessingAndTerminalFeedback()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();
            var transform = PrepareTransform(fixture);
            var transcription = new GatedTranscriptionPlugin();
            var transformCommand = transcription.Enqueue("make it concise");
            InjectTranscriptionEngine(fixture.PluginManager, transcription);
            var llm = new GatedLlmProvider();
            var transformResult = llm.Enqueue("short selection");
            InjectLlmProvider(fixture.PluginManager, llm);
            var coordinator = fixture.Provider.GetRequiredService<OverlayCoordinator>();

            await BoundedTest.WaitAsync(transform.ToggleAsync());
            fixture.FeedNonSilentAudio();
            var transformStop = transform.ToggleAsync();
            var dictationSessionId = 0;
            try
            {
                await BoundedTest.WaitAsync(transformCommand.Entered);
                Assert.Equal(
                    "Transcribing transform command...",
                    coordinator.PresentedState.StatusText
                );

                dictationSessionId = await BoundedTest.WaitAsync(
                    fixture.Orchestrator.StartAsync()
                );
                Assert.True(dictationSessionId > 0);
                AssertDictationRecording(coordinator.PresentedState);

                transformCommand.Release();
                await BoundedTest.WaitAsync(transformResult.Entered);
                AssertDictationRecording(coordinator.PresentedState);

                transformResult.Release();
                await BoundedTest.WaitAsync(transformStop);
                AssertDictationRecording(coordinator.PresentedState);

                // Invalidating the winning claim proves the transform terminal outcome was
                // discarded while suppressed instead of waiting to resurface.
                coordinator.Acquire(OverlayRequester.Dictation);
                Assert.Equal(DictationOverlayState.Hidden, coordinator.PresentedState);
            }
            finally
            {
                transformCommand.Release();
                transformResult.Release();
                await IgnoreFailureAsync(transformStop);
                if (dictationSessionId > 0 && fixture.Orchestrator.IsRecording)
                {
                    await BoundedTest.WaitAsync(fixture.Orchestrator.CancelAsync());
                }
            }
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task TransformRecording_SuppressesDictationProcessingAndTerminalFeedback()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();
            var transform = PrepareTransform(fixture);
            var transcription = new GatedTranscriptionPlugin();
            var dictationText = transcription.Enqueue("dictation text");
            InjectTranscriptionEngine(fixture.PluginManager, transcription);
            var llm = new GatedLlmProvider();
            var dictationResult = llm.Enqueue("processed dictation");
            InjectLlmProvider(fixture.PluginManager, llm);
            const string promptActionId = "interleaving-dictation-prompt";
            const string profileId = "interleaving-dictation-profile";
            fixture.Provider.GetRequiredService<IPromptActionService>().AddAction(
                new PromptAction
                {
                    Id = promptActionId,
                    Name = "Interleaving prompt",
                    SystemPrompt = "Return the text.",
                    ProviderOverride =
                        $"plugin:{GatedLlmProvider.Id}:{GatedLlmProvider.ModelId}",
                }
            );
            fixture.Provider.GetRequiredService<IProfileService>().AddProfile(
                new Profile
                {
                    Id = profileId,
                    Name = "Interleaving profile",
                    PromptActionId = promptActionId,
                }
            );
            var coordinator = fixture.Provider.GetRequiredService<OverlayCoordinator>();

            var sessionId = await BoundedTest.WaitAsync(
                fixture.Orchestrator.StartAsync(profileId)
            );
            fixture.FeedNonSilentAudio();
            var dictationStop = fixture.Orchestrator.StopAsync();
            try
            {
                await BoundedTest.WaitAsync(dictationText.Entered);
                Assert.True(coordinator.PresentedState.IsOverlayVisible);
                Assert.False(coordinator.PresentedState.IsRecording);

                await BoundedTest.WaitAsync(transform.ToggleAsync());
                AssertTransformRecording(coordinator.PresentedState);

                dictationText.Release();
                await BoundedTest.WaitAsync(dictationResult.Entered);
                AssertTransformRecording(coordinator.PresentedState);

                dictationResult.Release();
                await BoundedTest.WaitAsync(dictationStop);
                AssertTransformRecording(coordinator.PresentedState);

                var transformCancel = transcription.Enqueue("cancel");
                transformCancel.Release();
                fixture.FeedNonSilentAudio();
                await BoundedTest.WaitAsync(transform.ToggleAsync());
                Assert.Equal("ready", (await fixture.WaitForResultAsync(sessionId)).Status);
            }
            finally
            {
                dictationText.Release();
                dictationResult.Release();
                await IgnoreFailureAsync(dictationStop);
            }
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task AudioOwnerRecording_BeatsOtherWorkflowCaptureFailureFeedback()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();
            var transform = PrepareTransform(fixture);
            var coordinator = fixture.Provider.GetRequiredService<OverlayCoordinator>();
            var transformFailure = new TaskCompletionSource<DictationOverlayState>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            transform.OverlayStateChanged += (_, state) =>
            {
                if (state.ShowFeedback)
                {
                    transformFailure.TrySetResult(state);
                }
            };

            var dictationSessionId = await BoundedTest.WaitAsync(
                fixture.Orchestrator.StartAsync()
            );
            Assert.True(dictationSessionId > 0);
            AssertDictationRecording(coordinator.PresentedState);

            var failedTransformStart = transform.ToggleAsync();
            await BoundedTest.WaitAsync(transformFailure.Task);
            AssertDictationRecording(coordinator.PresentedState);

            await BoundedTest.WaitAsync(fixture.Orchestrator.CancelAsync());
            await BoundedTest.WaitAsync(failedTransformStart);

            await BoundedTest.WaitAsync(transform.ToggleAsync());
            AssertTransformRecording(coordinator.PresentedState);

            var failedDictationStart = await BoundedTest.WaitAsync(
                fixture.Orchestrator.StartAsync()
            );
            Assert.Equal(0, failedDictationStart);
            AssertTransformRecording(coordinator.PresentedState);

            fixture.Plugin.EnqueueText("cancel");
            fixture.FeedNonSilentAudio();
            await BoundedTest.WaitAsync(transform.ToggleAsync());
        });
    }

    private static TransformSelectionService PrepareTransform(
        OrchestratorCompositionFixture fixture
    )
    {
        var textInsertion = fixture.Provider.GetRequiredService<TextInsertionService>();
        SetField(textInsertion, "_platform", new SelectionTextInsertionPlatform());
        var transform = fixture.Provider.GetRequiredService<TransformSelectionService>();
        SetField(transform, "_activeWindow", new FixedActiveWindowService());
        SetField(transform, "_showWarningDialog", new Func<string, Task>(
            static _ => Task.CompletedTask
        ));
        return transform;
    }

    private static void AssertDictationRecording(DictationOverlayState state)
    {
        Assert.True(state.IsOverlayVisible);
        Assert.True(state.IsRecording);
        Assert.False(state.ShowFeedback);
        Assert.Equal(Loc.Instance["Dictation.StatusRecording"], state.StatusText);
    }

    private static void AssertTransformRecording(DictationOverlayState state)
    {
        Assert.True(state.IsOverlayVisible);
        Assert.True(state.IsRecording);
        Assert.False(state.ShowFeedback);
        Assert.Equal(Loc.Instance["Overlay.TransformPrompt"], state.StatusText);
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await BoundedTest.WaitAsync(task);
        }
        catch
        {
            // Cleanup must release provider gates without masking the primary assertion.
        }
    }

    private static void SetField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(target.GetType().FullName, name);
        field.SetValue(target, value);
    }

    private static void InjectTranscriptionEngine(
        PluginManager pluginManager,
        ITranscriptionEngineRole plugin
    )
    {
        SetField(pluginManager, "_transcriptionEngines", new List<ITranscriptionEngineRole>
        {
            plugin,
        });
    }

    private static void InjectLlmProvider(PluginManager pluginManager, ILlmProviderRole provider)
    {
        SetField(pluginManager, "_llmProviders", new List<ILlmProviderRole> { provider });
    }

    private sealed class ProviderGate(string result)
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        internal Task Entered => _entered.Task;
        internal string Result { get; } = result;

        internal void Release()
        {
            _release.TrySetResult();
        }

        internal async Task WaitAsync(CancellationToken ct)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(ct);
        }
    }

    private sealed class GatedTranscriptionPlugin : ITranscriptionEnginePlugin
    {
        private readonly Queue<ProviderGate> _gates = [];

        public string PluginId => RecordingTranscriptionPlugin.Id;
        public string PluginName => "Gated interleaving transcription";
        public string PluginVersion => "1.0.0";
        public string ProviderId => RecordingTranscriptionPlugin.Id;
        public string ProviderDisplayName => "Gated interleaving transcription";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
            [new(RecordingTranscriptionPlugin.ModelId, "Gated model")];
        public string? SelectedModelId { get; private set; } =
            RecordingTranscriptionPlugin.ModelId;
        public bool SupportsTranslation => false;

        internal ProviderGate Enqueue(string result)
        {
            var gate = new ProviderGate(result);
            _gates.Enqueue(gate);
            return gate;
        }

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;

        public void SelectModel(string modelId)
        {
            SelectedModelId = modelId;
        }

        public Task LoadModelAsync(string modelId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SelectedModelId = modelId;
            return Task.CompletedTask;
        }

        public Task UnloadModelAsync()
        {
            SelectedModelId = null;
            return Task.CompletedTask;
        }

        public async Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct
        )
        {
            var gate = _gates.Count > 0
                ? _gates.Dequeue()
                : throw new InvalidOperationException("No gated transcription remains.");
            await gate.WaitAsync(ct);
            return new PluginTranscriptionResult(gate.Result, language ?? "en", 1);
        }

        public void Dispose() { }
    }

    private sealed class GatedLlmProvider : ILlmProviderRole
    {
        internal const string Id = "integration.gated-interleaving-llm";
        internal const string ModelId = "gated-interleaving-model";

        private readonly Queue<ProviderGate> _gates = [];

        public string PluginId => Id;
        public string ProviderName => "Gated interleaving LLM";
        public bool IsAvailable => true;
        public IReadOnlyList<PluginModelInfo> SupportedModels { get; } =
            [new(ModelId, "Gated interleaving model")];

        internal ProviderGate Enqueue(string result)
        {
            var gate = new ProviderGate(result);
            _gates.Enqueue(gate);
            return gate;
        }

        public async Task<string> ProcessAsync(
            string systemPrompt,
            string userText,
            string model,
            CancellationToken ct
        )
        {
            var gate = NextGate();
            await gate.WaitAsync(ct);
            return gate.Result;
        }

        public async IAsyncEnumerable<string> ProcessStreamingAsync(
            string systemPrompt,
            string userText,
            string model,
            [EnumeratorCancellation]
            CancellationToken ct
        )
        {
            var gate = NextGate();
            await gate.WaitAsync(ct);
            yield return gate.Result;
        }

        private ProviderGate NextGate()
        {
            return _gates.Count > 0
                ? _gates.Dequeue()
                : throw new InvalidOperationException("No gated LLM result remains.");
        }
    }

    private sealed class FixedActiveWindowService : IActiveWindowService
    {
        private static readonly ActiveWindowSnapshot s_snapshot = new(
            "integration-editor",
            "Integration editor",
            "integration-window",
            "integration.editor",
            "integration"
        );

        public string? GetActiveWindowProcessName() => s_snapshot.ProcessName;
        public string? GetActiveWindowTitle() => s_snapshot.Title;
        public string? GetBrowserUrl(bool allowInteractiveCapture = true) => null;

        public Task<ActiveWindowSnapshot?> GetActiveWindowSnapshotAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<ActiveWindowSnapshot?>(s_snapshot);
        }

        public string? GetBrowserUrlForSnapshot(
            ActiveWindowSnapshot? activeWindowSnapshot,
            bool honorMissBackoff = false
        ) => null;

        public IReadOnlyList<string> GetRunningAppProcessNames() => [];
    }

    private sealed class SelectionTextInsertionPlatform : ITextInsertionPlatform
    {
        private string? _clipboard;

        public bool IsClipboardSetAvailable => true;
        public bool IsPasteAvailable => true;
        public bool IsKdePlasma => false;
        public bool PrefersDirectTypingForUnknownTarget => true;
        public InsertionFailureReason LastFailureReason => InsertionFailureReason.None;
        public bool LastTypingDeliveredPartialText => false;

        public Task<string?> TryGetClipboardTextAsync() => Task.FromResult(_clipboard);

        public Task<bool> SetClipboardTextAsync(string text)
        {
            _clipboard = text;
            return Task.FromResult(true);
        }

        public Task<bool> ClipboardHasNonTextFormatsAsync() => Task.FromResult(false);
        public Task DelayAsync(TimeSpan delay) => Task.CompletedTask;
        public string GetActiveWindowId() => "integration-window";
        public Task<bool> ActivateWindowAsync(string windowId) => Task.FromResult(true);
        public Task<bool> SendPasteAsync(bool useTerminalShortcut = false) => Task.FromResult(true);
        public Task<bool> TypeTextAsync(string text) => Task.FromResult(true);

        public Task<bool> SendCopyAsync(bool useTerminalShortcut)
        {
            _clipboard = "selected source text";
            return Task.FromResult(true);
        }

        public Task<bool> SendEnterAsync() => Task.FromResult(true);
    }
}
