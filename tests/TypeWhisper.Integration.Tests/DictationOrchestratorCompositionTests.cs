using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Integration.Tests;

public sealed class DictationOrchestratorCompositionTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public Task StartStop_ComposesCaptureTranscriptionPipelineAndInsertion()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();
            fixture.Plugin.EnqueueText("hello question mark");
            var captured = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            fixture.Orchestrator.RecordingCaptured += (_, path) => captured.TrySetResult(path);

            var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
            Assert.True(sessionId > 0);
            Assert.True(fixture.Orchestrator.IsRecording);
            fixture.FeedNonSilentAudio();

            var resultTask = fixture.WaitForResultAsync(sessionId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            var result = await BoundedTest.WaitAsync(resultTask);
            var capturePath = await BoundedTest.WaitAsync(captured.Task);
            var recordingStarted = await BoundedTest.WaitAsync(
                fixture.RecordingStarted.Task
            );
            var transcriptionPublished = await BoundedTest.WaitAsync(
                fixture.TranscriptionPublished.Task
            );
            var transcriptionReady = await BoundedTest.WaitAsync(
                fixture.TranscriptionReady.Task
            );

            Assert.Equal("ready", result.Status);
            Assert.Equal("hello?", result.Text);
            Assert.Equal("hello?", transcriptionReady);
            Assert.Equal("hello?", transcriptionPublished.Text);
            Assert.NotNull(recordingStarted);
            Assert.Equal(1, fixture.Plugin.TranscriptionCount);
            Assert.True(File.Exists(capturePath));
            Assert.StartsWith(
                Path.GetFullPath(TypeWhisperEnvironment.AudioPath),
                Path.GetFullPath(capturePath),
                StringComparison.Ordinal
            );
            Assert.Equal(["hello? "], fixture.InsertionPlatform.Typed);

            var history = Assert.Single(fixture.History.Records);
            Assert.Equal("hello question mark", history.RawText);
            Assert.Equal("hello?", history.FinalText);
            Assert.True(File.Exists(Path.Join(TypeWhisperEnvironment.DataPath, "history.json")));
            var recent = fixture.RecentStore.LatestEntry(fixture.History.Records);
            Assert.NotNull(recent);
            Assert.Equal("hello?", recent.FinalText);
            Assert.True(fixture.SessionResults.TryGet(sessionId, out var storedResult));
            Assert.Equal(result, storedResult);

            // Detects capture ownership, post-processing, persistence, event publication,
            // insertion delivery, and terminal gate/in-flight leaks in the assembled service.
            Assert.False(fixture.Orchestrator.IsSessionInFlight(sessionId));
            Assert.False(fixture.Orchestrator.IsRecording);
            Assert.Equal("idle", fixture.Orchestrator.CurrentStateLabel);
            Assert.Equal(0, fixture.AudioBoundary.ActiveStreams);

            // A dictation cycle resolves the focused window to pick a profile, which probes
            // gdbus and xdotool by design; the boundary intercepts them, so nothing executes.
            // The contract worth asserting is that NOTHING ELSE reaches the runner — any other
            // command means a service escaped the injected boundary. (ServiceGraphLifecycleTests
            // asserts the stricter "zero requests", which holds there because it only builds and
            // disposes the graph without dictating.)
            string[] activeWindowProbes = ["gdbus", "xdotool"];
            Assert.Equal(
                [],
                fixture
                    .ProcessRunner.Requests.Where(request =>
                        !activeWindowProbes.Contains(request.Split(' ')[1])
                    )
                    .ToArray()
            );
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task CancelDuringCapture_DiscardsWithoutTranscriptionOrInsertion()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();

            var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
            Assert.True(sessionId > 0);
            fixture.FeedNonSilentAudio();

            var resultTask = fixture.WaitForResultAsync(sessionId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.CancelAsync());
            var result = await BoundedTest.WaitAsync(resultTask);

            Assert.Equal("canceled", result.Status);
            Assert.Equal(0, fixture.Plugin.TranscriptionCount);
            Assert.Empty(fixture.InsertionPlatform.Typed);
            Assert.Empty(fixture.InsertionPlatform.ClipboardWrites);
            Assert.Empty(fixture.History.Records);
            Assert.Empty(
                Directory.GetFiles(
                    TypeWhisperEnvironment.AudioPath,
                    "dictation-*.wav",
                    SearchOption.TopDirectoryOnly
                )
            );

            // Detects cancel intent being lost across the stop gate, retained capture,
            // accidental downstream work, and system-audio/session ownership leaks.
            Assert.False(fixture.SystemAudio.IsDucked);
            Assert.False(fixture.SystemAudio.IsPaused);
            Assert.True(fixture.SystemAudio.RestoreCount > 0);
            Assert.True(fixture.SystemAudio.ResumeCount > 0);
            Assert.False(fixture.Orchestrator.IsSessionInFlight(sessionId));
            Assert.Equal("idle", fixture.Orchestrator.CurrentStateLabel);
            Assert.Equal(0, fixture.AudioBoundary.ActiveStreams);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task TranscriptionFailure_DoesNotPoisonNextSession()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();
            fixture.Plugin.EnqueueFailure("scripted first-session failure");
            fixture.Plugin.EnqueueText("second session question mark");

            var firstId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
            fixture.FeedNonSilentAudio();
            var firstResultTask = fixture.WaitForResultAsync(firstId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            var firstResult = await BoundedTest.WaitAsync(firstResultTask);

            var secondId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
            fixture.FeedNonSilentAudio();
            var secondResultTask = fixture.WaitForResultAsync(secondId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            var secondResult = await BoundedTest.WaitAsync(secondResultTask);

            Assert.Equal("failed", firstResult.Status);
            Assert.Contains(
                "scripted first-session failure",
                firstResult.Message,
                StringComparison.Ordinal
            );
            Assert.Equal("ready", secondResult.Status);
            Assert.Equal("second session?", secondResult.Text);
            Assert.Equal(2, fixture.Plugin.TranscriptionCount);
            Assert.Equal(["second session? "], fixture.InsertionPlatform.Typed);
            Assert.Single(fixture.History.Records);

            // Detects leaked model leases, insertion reservations, toggle ownership,
            // and in-flight session entries after a real plugin exception.
            Assert.False(fixture.Orchestrator.IsSessionInFlight(firstId));
            Assert.False(fixture.Orchestrator.IsSessionInFlight(secondId));
            Assert.False(fixture.Orchestrator.IsRecording);
            Assert.Equal("idle", fixture.Orchestrator.CurrentStateLabel);
            Assert.Equal(1, fixture.AudioBoundary.MaxActiveStreams);
            Assert.Equal(0, fixture.AudioBoundary.ActiveStreams);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task StopDuringDelayedStartup_IsDeferredAndSerialized()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture(
                soundFeedbackEnabled: true
            );
            fixture.CueProcessRunner.BlockNextStartCue();

            var firstStart = fixture.Orchestrator.StartAsync();
            try
            {
                await BoundedTest.WaitAsync(fixture.CueProcessRunner.WaitForCueAsync());
                var duplicateStart = fixture.Orchestrator.StartAsync();
                var pendingStop = fixture.Orchestrator.StopAsync();

                Assert.Equal(0, await BoundedTest.WaitAsync(duplicateStart));
                await BoundedTest.WaitAsync(pendingStop);
                fixture.CueProcessRunner.ReleaseCue();

                var firstId = await BoundedTest.WaitAsync(firstStart);
                var firstResult = await fixture.WaitForResultAsync(firstId);

                Assert.True(firstId > 0);
                Assert.Equal("discarded", firstResult.Status);
                Assert.Equal(1, fixture.AudioBoundary.OpenCount);
                Assert.Equal(1, fixture.AudioBoundary.MaxActiveStreams);
                Assert.Equal(0, fixture.AudioBoundary.ActiveStreams);
                Assert.False(fixture.Orchestrator.IsSessionInFlight(firstId));
                Assert.Equal("idle", fixture.Orchestrator.CurrentStateLabel);

                fixture.Settings.Save(
                    fixture.Settings.Current with { SoundFeedbackEnabled = false }
                );
                fixture.Plugin.EnqueueText("serialized follow up question mark");
                var nextId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
                fixture.FeedNonSilentAudio();
                var nextResultTask = fixture.WaitForResultAsync(nextId);
                await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
                var nextResult = await BoundedTest.WaitAsync(nextResultTask);

                Assert.Equal("ready", nextResult.Status);
                Assert.Equal(["serialized follow up? "], fixture.InsertionPlatform.Typed);
                Assert.Equal(2, fixture.AudioBoundary.OpenCount);
                Assert.Equal(1, fixture.AudioBoundary.MaxActiveStreams);
                Assert.Equal(0, fixture.AudioBoundary.ActiveStreams);
                Assert.False(fixture.Orchestrator.IsSessionInFlight(nextId));

                // Detects duplicate stream ownership, a dropped deferred stop, and a
                // startup/stop gate left poisoned for the following real session.
                Assert.False(fixture.Orchestrator.IsRecording);
                Assert.Equal("idle", fixture.Orchestrator.CurrentStateLabel);
            }
            finally
            {
                fixture.CueProcessRunner.ReleaseCue();
            }
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task ForcedProfileLanguage_ReachesBatchTranscription_WhenSnapshotFails()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();
            // Headless: the active-window snapshot cannot resolve here, so this pins
            // that a hotkey-forced profile's language survives snapshot failure all
            // the way to the batch transcription call.
            var profiles = fixture.Provider.GetRequiredService<IProfileService>();
            profiles.AddProfile(
                new Profile
                {
                    Id = "integration-forced-de",
                    Name = "Forced German",
                    InputLanguage = "de",
                }
            );
            fixture.Plugin.EnqueueText("hallo");

            var sessionId = await BoundedTest.WaitAsync(
                fixture.Orchestrator.StartAsync("integration-forced-de")
            );
            fixture.FeedNonSilentAudio();
            var resultTask = fixture.WaitForResultAsync(sessionId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            var result = await BoundedTest.WaitAsync(resultTask);

            Assert.Equal("ready", result.Status);
            Assert.Equal(["de"], fixture.Plugin.ReceivedLanguages);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task LateProfileLanguageOutsideEngineList_IsRejectedWithDetailedOverlayFeedback()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture(
                focusedApp: ("language-app", "Language app — integration")
            );
            var constrained = new LanguageConstrainedRole(fixture.Plugin);
            PluginManagerTestAccess.SetTranscriptionEngines(fixture.PluginManager, [constrained]);
            var profiles = fixture.Provider.GetRequiredService<IProfileService>();
            profiles.AddProfile(
                new Profile
                {
                    Id = "integration-late-language",
                    Name = "Late German",
                    ProcessNames = ["language-app"],
                    InputLanguage = "de",
                }
            );

            var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
            await BoundedTest.WaitAsync(fixture.RecordingStarted.Task);
            fixture.FeedNonSilentAudio();
            var resultTask = fixture.WaitForResultAsync(sessionId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            var result = await BoundedTest.WaitAsync(resultTask);

            var expected = Loc.Instance.GetString(
                "LanguageSelection.LanguageNotSupported",
                constrained.ProviderId,
                "de",
                "en"
            );
            var overlay = fixture.Provider.GetRequiredService<OverlayCoordinator>();

            Assert.Equal("failed", result.Status);
            Assert.Equal(expected, result.Message);
            Assert.Equal(0, fixture.Plugin.TranscriptionCount);
            Assert.Empty(fixture.Plugin.ReceivedLanguages);
            Assert.True(overlay.PresentedState.ShowFeedback);
            Assert.True(overlay.PresentedState.FeedbackIsError);
            Assert.Equal(
                $"Transcription failed: {expected}",
                overlay.PresentedState.FeedbackText
            );
            Assert.Empty(fixture.InsertionPlatform.Typed);
            Assert.Empty(fixture.History.Records);
        });
    }

    private sealed class LanguageConstrainedRole(ITranscriptionEngineRole inner)
        : ITranscriptionEngineRole,
            ITranscriptionLanguageSelectionCapabilities
    {
        public string PluginId => inner.PluginId;
        public string ProviderId => inner.ProviderId;
        public string ProviderDisplayName => inner.ProviderDisplayName;
        public bool IsConfigured => inner.IsConfigured;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels => inner.TranscriptionModels;
        public string? SelectedModelId => inner.SelectedModelId;
        public bool SupportsTranslation => inner.SupportsTranslation;
        public IReadOnlyList<string> SupportedLanguages => ["en"];
        public LanguageSelectionSupport AutomaticDetectionSupport =>
            LanguageSelectionSupport.Supported;
        public LanguageSelectionSupport ExplicitSelectionSupport =>
            LanguageSelectionSupport.Supported;

        public void SelectModel(string modelId)
        {
            inner.SelectModel(modelId);
        }

        public Task LoadModelAsync(string modelId, CancellationToken ct)
        {
            return inner.LoadModelAsync(modelId, ct);
        }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct
        )
        {
            return inner.TranscribeAsync(wavAudio, language, translate, prompt, ct);
        }
    }
}
