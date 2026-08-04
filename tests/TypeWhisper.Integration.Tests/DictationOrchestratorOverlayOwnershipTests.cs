using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Plugins;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Integration.Tests;

public sealed class DictationOrchestratorOverlayOwnershipTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public Task NewRecordingOverlay_RejectsPredecessorPromptStreamDelta()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();
            var provider = new GatedLlmProvider();
            InjectLlmProvider(fixture.PluginManager, provider);

            const string promptActionId = "integration-overlay-ownership-prompt";
            const string profileId = "integration-overlay-ownership-profile";
            fixture.Provider.GetRequiredService<IPromptActionService>().AddAction(
                new PromptAction
                {
                    Id = promptActionId,
                    Name = "Gated overlay ownership prompt",
                    SystemPrompt = "Return the dictated text.",
                    ProviderOverride =
                        $"plugin:{GatedLlmProvider.Id}:{GatedLlmProvider.ModelId}",
                }
            );
            fixture.Provider.GetRequiredService<IProfileService>().AddProfile(
                new Profile
                {
                    Id = profileId,
                    Name = "Overlay ownership profile",
                    PromptActionId = promptActionId,
                }
            );
            fixture.Plugin.EnqueueText("old session text");

            var firstSessionId = await BoundedTest.WaitAsync(
                fixture.Orchestrator.StartAsync(profileId)
            );
            Assert.True(firstSessionId > 0);
            fixture.FeedNonSilentAudio();

            // Stop releases the toggle gate before prompt processing, then this provider parks
            // session one's pipeline immediately after streaming enumeration begins.
            var firstStopTask = fixture.Orchestrator.StopAsync();
            await BoundedTest.WaitAsync(provider.EnumerationBegan);

            var latestOverlay = DictationOverlayState.Hidden;
            // -1 until the handler samples it, so the assertion also proves the sample happened.
            var predecessorOwnedOverlayAtClaim = -1;
            var secondOverlayPublished = new TaskCompletionSource<DictationOverlayState>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            EventHandler<DictationOverlayState> overlayHandler = (_, state) =>
            {
                // SetOverlayState dispatches under _overlayStateLock: only snapshot and signal;
                // never mutate orchestrator state from this handler. The ownership read takes
                // only _recordingSessionLock, the same order production already uses.
                // ReSharper disable once AccessToModifiedClosure -- the shared sample is the point; the outer scope reads it back under Volatile after the handler runs.
                Volatile.Write(ref latestOverlay, state);
                if (!state.IsRecording)
                {
                    return;
                }

                // Sample at the overlay claim itself — the first IsRecording publish of session
                // two — so the test pins "generation advanced before the claim" rather than the
                // weaker "before RecordingStateChanged" that a later publish would still satisfy.
                // ReSharper disable once AccessToModifiedClosure -- same deliberate sample-and-read-back; the -1 sentinel is written from the outer scope on purpose.
                if (Volatile.Read(ref predecessorOwnedOverlayAtClaim) < 0)
                {
                    Volatile.Write(
                        ref predecessorOwnedOverlayAtClaim,
                        // ReSharper disable once AccessToDisposedClosure -- the handler is unsubscribed in the outer finally, before the fixture's await-using disposal.
                        fixture.Orchestrator.IsSessionStillOwningOverlay(firstSessionId) ? 1 : 0
                    );
                }

                secondOverlayPublished.TrySetResult(state);
            };

            var secondRecordingCallbackEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var releaseSecondRecordingCallback = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            EventHandler<bool> recordingHandler = (_, isRecording) =>
            {
                if (!isRecording)
                {
                    return;
                }

                secondRecordingCallbackEntered.TrySetResult();
                BoundedTest.WaitAsync(releaseSecondRecordingCallback.Task)
                    .GetAwaiter()
                    .GetResult();
            };

            var oldDeltaPublished = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            using var tokenSubscription = fixture.EventBus.Subscribe<LlmResponseTokenEvent>(
                tokenEvent =>
                {
                    if (
                        tokenEvent
                        is { IsFinal: false, AccumulatedText: GatedLlmProvider.StaleDelta }
                    )
                    {
                        oldDeltaPublished.TrySetResult();
                    }

                    return Task.CompletedTask;
                }
            );

            fixture.Orchestrator.OverlayStateChanged += overlayHandler;
            fixture.Orchestrator.RecordingStateChanged += recordingHandler;
            Task<int>? secondStartTask = null;
            var secondSessionId = 0;
            try
            {
                try
                {
                    // ReSharper disable once AccessToDisposedClosure -- the task is awaited in the inner finally, well before the fixture's await-using disposal.
                    secondStartTask = Task.Run(() => fixture.Orchestrator.StartAsync(profileId));
                    await BoundedTest.WaitAsync(secondRecordingCallbackEntered.Task);
                    await BoundedTest.WaitAsync(secondOverlayPublished.Task);
                    Assert.Equal(0, Volatile.Read(ref predecessorOwnedOverlayAtClaim));

                    // LlmResponseTokenEvent is published after the attempted synchronous overlay
                    // write, so observing it fences the stale mutation before the assertion below.
                    provider.ReleaseFirstDelta();
                    await BoundedTest.WaitAsync(oldDeltaPublished.Task);

                    var overlayAfterOldDelta = Volatile.Read(ref latestOverlay);
                    Assert.True(overlayAfterOldDelta.IsOverlayVisible);
                    Assert.True(overlayAfterOldDelta.IsRecording);
                    Assert.False(overlayAfterOldDelta.ShowFeedback);
                    Assert.Null(overlayAfterOldDelta.LlmResponseText);
                }
                finally
                {
                    provider.ReleaseFirstDelta();
                    releaseSecondRecordingCallback.TrySetResult();
                    try
                    {
                        if (secondStartTask is not null)
                        {
                            secondSessionId = await BoundedTest.WaitAsync(secondStartTask);
                        }

                        if (secondSessionId > 0 && fixture.Orchestrator.IsRecording)
                        {
                            await BoundedTest.WaitAsync(fixture.Orchestrator.CancelAsync());
                        }
                    }
                    finally
                    {
                        provider.ReleaseStream();
                        await BoundedTest.WaitAsync(firstStopTask);
                    }
                }
            }
            finally
            {
                fixture.Orchestrator.RecordingStateChanged -= recordingHandler;
                fixture.Orchestrator.OverlayStateChanged -= overlayHandler;
            }

            Assert.True(secondSessionId > 0);
            var firstResult = await BoundedTest.WaitAsync(
                fixture.WaitForResultAsync(firstSessionId)
            );
            var secondResult = await BoundedTest.WaitAsync(
                fixture.WaitForResultAsync(secondSessionId)
            );
            Assert.Equal("ready", firstResult.Status);
            Assert.Equal("canceled", secondResult.Status);
            Assert.False(fixture.Orchestrator.IsSessionInFlight(firstSessionId));
            Assert.False(fixture.Orchestrator.IsSessionInFlight(secondSessionId));
        });
    }

    // PluginManager exposes no public seam for injecting pre-loaded providers;
    // mirror the fixture's transcription-engine injection for this private list.
    private static void InjectLlmProvider(PluginManager pluginManager, ILlmProviderRole provider)
    {
        var field = typeof(PluginManager).GetField(
            "_llmProviders",
            BindingFlags.Instance | BindingFlags.NonPublic
        ) ?? throw new MissingFieldException(typeof(PluginManager).FullName, "_llmProviders");
        field.SetValue(pluginManager, new List<ILlmProviderRole> { provider });
    }

    private sealed class GatedLlmProvider : ILlmProviderRole
    {
        internal const string Id = "integration.gated-overlay-llm";
        internal const string ModelId = "gated-model";
        internal const string StaleDelta = "stale predecessor delta";

        private readonly TaskCompletionSource _enumerationBegan = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _releaseFirstDelta = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _releaseStream = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        internal Task EnumerationBegan => _enumerationBegan.Task;

        public string PluginId => Id;
        public string ProviderName => "Gated overlay ownership provider";
        public bool IsAvailable => true;
        public IReadOnlyList<PluginModelInfo> SupportedModels { get; } =
            [new(ModelId, "Gated model")];

        internal void ReleaseFirstDelta()
        {
            _releaseFirstDelta.TrySetResult();
        }

        internal void ReleaseStream()
        {
            _releaseStream.TrySetResult();
        }

        public Task<string> ProcessAsync(
            string systemPrompt,
            string userText,
            string model,
            CancellationToken ct
        )
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(StaleDelta);
        }

        public async IAsyncEnumerable<string> ProcessStreamingAsync(
            string systemPrompt,
            string userText,
            string model,
            [EnumeratorCancellation] CancellationToken ct
        )
        {
            // BoundedTest.WaitAsync takes no token, so these gates use its inner budget directly.
            _enumerationBegan.TrySetResult();
            await _releaseFirstDelta.Task
                .WaitAsync(BoundedTest.s_innerTimeout, ct)
                .ConfigureAwait(false);
            yield return StaleDelta;
            await _releaseStream.Task
                .WaitAsync(BoundedTest.s_innerTimeout, ct)
                .ConfigureAwait(false);
        }
    }
}
