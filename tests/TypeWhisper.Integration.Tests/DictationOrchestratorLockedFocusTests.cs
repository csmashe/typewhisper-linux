using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.ActiveWindow;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Integration.Tests;

public sealed class DictationOrchestratorLockedFocusTests
{
    [Theory]
    [InlineData(true, true, true, false, true, true)]
    [InlineData(false, true, true, false, true, false)]
    [InlineData(true, false, true, false, true, false)]
    [InlineData(true, true, false, false, true, false)]
    [InlineData(true, true, true, true, true, false)]
    [InlineData(true, true, true, null, true, false)]
    [InlineData(true, true, true, false, false, false)]
    [Trait("Category", "Integration")]
    public Task Recording_CapturesOnlyEligibleFieldAndPassesItToInsertion(
        bool enabled, bool autoPaste, bool running, bool? password, bool editable, bool expectedLock
    )
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture(
                focusedApp: ("firefox", "Editor")
            );
            // Keep the learner's listener lifecycle separate from the focus-lock assertions.
            fixture.Provider.GetRequiredService<TargetAppCorrectionLearningService>().Dispose();
            fixture.Settings.Update(settings => settings with
            {
                LockPasteToFocusedField = enabled,
                TargetAppCorrectionLearningEnabled = true,
                AutoPaste = autoPaste,
            });
            var target = new AtSpiElementRef("app", "/initial");
            fixture.AtSpi.IsRunning = running;
            fixture.AtSpi.CurrentFocusedElement = target;
            fixture.AtSpi.PasswordResult = password;
            fixture.AtSpi.EditableResult = editable;
            var atSpi = fixture.AtSpi;
            fixture.SystemAudio.OnPauseMedia = () =>
                atSpi.CurrentFocusedElement = new AtSpiElementRef("app", "/during-startup");
            fixture.Plugin.EnqueueText("dictated text");

            var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
            fixture.AtSpi.CurrentFocusedElement = new AtSpiElementRef("app", "/other");
            fixture.FeedNonSilentAudio();
            var resultTask = fixture.WaitForResultAsync(sessionId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            await BoundedTest.WaitAsync(resultTask);

            Assert.Equal(expectedLock ? target : null, fixture.AtSpi.LastFocusReadElement);
            Assert.Equal(expectedLock ? 1 : 0, fixture.AtSpi.FocusReadCount);
            if (enabled && autoPaste && running && !expectedLock)
            {
                Assert.Equal(InsertionFailureReason.LockedFieldUnavailable,
                    fixture.Provider.GetRequiredService<TextInsertionService>().LastFailureReason);
                Assert.Equal("dictated text ", fixture.InsertionPlatform.Clipboard);
                Assert.Empty(fixture.InsertionPlatform.Typed);
                Assert.Equal(0, fixture.InsertionPlatform.PasteAttemptCount);
            }
            Assert.Equal(0, fixture.AtSpi.StartRequestCount);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task MissingInitialFocus_ChangedAfterRecordingStarts_FallsBackToClipboard()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture(
                focusedApp: ("firefox", "Editor")
            );
            // Keep the learner's listener lifecycle separate from the focus-lock assertions.
            fixture.Provider.GetRequiredService<TargetAppCorrectionLearningService>().Dispose();
            fixture.Settings.Update(settings => settings with
            {
                LockPasteToFocusedField = true,
                TargetAppCorrectionLearningEnabled = true,
            });
            fixture.AtSpi.IsRunning = true;
            var target = new AtSpiElementRef("app", "/later");
            fixture.AtSpi.CurrentFocusedElement = null;
            fixture.AtSpi.BootstrapFocusTask = Task.FromResult<AtSpiElementRef?>(target);
            var atSpi = fixture.AtSpi;
            fixture.SystemAudio.OnPauseMedia = () => atSpi.CurrentFocusedElement = target;
            fixture.Plugin.EnqueueText("dictated text");

            var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
            fixture.FeedNonSilentAudio();
            var resultTask = fixture.WaitForResultAsync(sessionId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            await BoundedTest.WaitAsync(resultTask);

            Assert.Equal(target, fixture.AtSpi.CurrentFocusedElement);
            Assert.Equal(0, fixture.AtSpi.BootstrapFocusCount);
            Assert.Null(fixture.AtSpi.LastFocusReadElement);
            Assert.Equal(0, fixture.AtSpi.FocusReadCount);
            Assert.Equal(InsertionFailureReason.LockedFieldUnavailable,
                fixture.Provider.GetRequiredService<TextInsertionService>().LastFailureReason);
            Assert.Equal("dictated text ", fixture.InsertionPlatform.Clipboard);
            Assert.Empty(fixture.InsertionPlatform.Typed);
            Assert.Equal(0, fixture.InsertionPlatform.PasteAttemptCount);
            Assert.Equal(0, fixture.AtSpi.StartRequestCount);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task LockedCommand_UsesOneShotInsertionInsteadOfStreamingTyping()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture(
                focusedApp: ("codex", "Editor")
            );
            // Keep the learner's listener lifecycle separate from the focus-lock assertions.
            fixture.Provider.GetRequiredService<TargetAppCorrectionLearningService>().Dispose();
            fixture.Settings.Update(settings => settings with
            {
                LockPasteToFocusedField = true,
                TargetAppCorrectionLearningEnabled = true,
                CommandModeEnabled = true,
            });
            var target = new AtSpiElementRef("app", "/initial");
            fixture.AtSpi.IsRunning = true;
            fixture.AtSpi.CurrentFocusedElement = target;
            fixture.Plugin.EnqueueText("TypeWhisper write a haiku about the sea");
            fixture.Llm.EnqueueStream("Waves fold the gray shore, ", "salt light drifts through pines.");

            var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
            fixture.FeedNonSilentAudio();
            var resultTask = fixture.WaitForResultAsync(sessionId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            await BoundedTest.WaitAsync(resultTask);

            Assert.Equal(1, fixture.Llm.BatchCalls);
            Assert.Single(fixture.InsertionPlatform.Typed);
            Assert.Equal(target, fixture.AtSpi.LastFocusReadElement);
            Assert.Equal(0, fixture.InsertionPlatform.PasteAttemptCount);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Integration")]
    public Task MissingOrTimedOutLockedField_UsesLocalizedCompletionMessage(bool eligibilityTimesOut)
    {
        return BoundedTest.RunAsync(async () =>
        {
            var previousLanguage = Loc.Instance.CurrentLanguage;
            try
            {
                Loc.Instance.Initialize(LocalizationDir());
                Loc.Instance.CurrentLanguage = "en";
                await using var fixture = new OrchestratorCompositionFixture(
                    focusedApp: ("firefox", "Editor")
                );
                // Keep the learner's listener lifecycle separate from the focus-lock assertions.
                fixture.Provider.GetRequiredService<TargetAppCorrectionLearningService>().Dispose();
                fixture.Settings.Update(settings => settings with
                {
                    LockPasteToFocusedField = true,
                    TargetAppCorrectionLearningEnabled = true,
                });
                fixture.AtSpi.IsRunning = true;
                fixture.AtSpi.CurrentFocusedElement = new AtSpiElementRef("app", "/initial");
                if (eligibilityTimesOut)
                {
                    fixture.AtSpi.EditableTask = new TaskCompletionSource<bool?>().Task;
                }
                fixture.AtSpi.FocusedResult = null;
                fixture.AtSpi.GrabFocusResult = false;
                fixture.Plugin.EnqueueText("dictated text");
                var states = new ConcurrentQueue<DictationOverlayState>();
                fixture.Orchestrator.OverlayStateChanged += (_, state) => states.Enqueue(state);

                var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
                fixture.FeedNonSilentAudio();
                var resultTask = fixture.WaitForResultAsync(sessionId);
                await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
                await BoundedTest.WaitAsync(resultTask);

                Assert.Equal(eligibilityTimesOut ? 0 : 1, fixture.AtSpi.FocusReadCount);
                Assert.Equal(InsertionFailureReason.LockedFieldUnavailable,
                    fixture.Provider.GetRequiredService<TextInsertionService>().LastFailureReason);
                Assert.Equal("dictated text ", fixture.InsertionPlatform.Clipboard);
                Assert.Empty(fixture.InsertionPlatform.Typed);
                Assert.Equal(0, fixture.InsertionPlatform.PasteAttemptCount);
                Assert.Contains(states, state => state.FeedbackText == Loc.Instance["Dictation.LockedFieldGone"]);
            }
            finally
            {
                Loc.Instance.CurrentLanguage = previousLanguage;
            }
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task Recording_WithoutLearningConsent_PastesWithoutTouchingTheField()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture(
                focusedApp: ("gedit", "Editor")
            );
            // Keep the learner's listener lifecycle separate from the focus-lock assertions.
            fixture.Provider.GetRequiredService<TargetAppCorrectionLearningService>().Dispose();
            fixture.Settings.Update(settings => settings with
            {
                LockPasteToFocusedField = true,
                TargetAppCorrectionLearningEnabled = false,
            });
            fixture.AtSpi.IsRunning = true;
            fixture.AtSpi.CurrentFocusedElement = new AtSpiElementRef("app", "/initial");
            // Non-ASCII text selects paste even before the app snapshot is available.
            fixture.Plugin.EnqueueText("dictated café");

            var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
            fixture.FeedNonSilentAudio();
            var resultTask = fixture.WaitForResultAsync(sessionId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            await BoundedTest.WaitAsync(resultTask);

            Assert.Equal(0, fixture.AtSpi.FocusReadCount);
            Assert.NotEqual(InsertionFailureReason.LockedFieldUnavailable,
                fixture.Provider.GetRequiredService<TextInsertionService>().LastFailureReason);
            Assert.Equal(1, fixture.InsertionPlatform.PasteAttemptCount);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task ConsentWithdrawnMidRecording_DropsTheLockedField()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture(
                focusedApp: ("gedit", "Editor")
            );
            // Keep the learner's listener lifecycle separate from the focus-lock assertions.
            fixture.Provider.GetRequiredService<TargetAppCorrectionLearningService>().Dispose();
            fixture.Settings.Update(settings => settings with
            {
                LockPasteToFocusedField = true,
                TargetAppCorrectionLearningEnabled = true,
            });
            fixture.AtSpi.IsRunning = true;
            fixture.AtSpi.CurrentFocusedElement = new AtSpiElementRef("app", "/initial");
            // Non-ASCII text selects paste even before the app snapshot is available.
            fixture.Plugin.EnqueueText("dictated café");

            var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
            // Wait for the captured field to commit before withdrawing consent.
            await BoundedTest.WaitAsync(fixture.RecordingStarted.Task);
            fixture.Settings.Update(settings => settings with
            {
                TargetAppCorrectionLearningEnabled = false,
            });
            fixture.FeedNonSilentAudio();
            var resultTask = fixture.WaitForResultAsync(sessionId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            await BoundedTest.WaitAsync(resultTask);

            Assert.Equal(0, fixture.AtSpi.FocusReadCount);
            Assert.NotEqual(InsertionFailureReason.LockedFieldUnavailable,
                fixture.Provider.GetRequiredService<TextInsertionService>().LastFailureReason);
            Assert.Equal(1, fixture.InsertionPlatform.PasteAttemptCount);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task ConsentWithdrawnAfterStop_PastesWithoutTouchingTheField()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture(
                focusedApp: ("gedit", "Editor")
            );
            // Keep the learner's listener lifecycle separate from the focus-lock assertions.
            fixture.Provider.GetRequiredService<TargetAppCorrectionLearningService>().Dispose();
            fixture.Settings.Update(settings => settings with
            {
                LockPasteToFocusedField = true,
                TargetAppCorrectionLearningEnabled = true,
            });
            fixture.AtSpi.IsRunning = true;
            fixture.AtSpi.CurrentFocusedElement = new AtSpiElementRef("app", "/initial");
            // Non-ASCII text selects the paste path.
            fixture.Plugin.EnqueueResult(_ =>
            {
                // Consent is withdrawn after StopAsync snapshotted it, while transcription is still running.
                // ReSharper disable once AccessToDisposedClosure -- the callback runs inside the awaited dictation, before the fixture disposes.
                fixture.Settings.Update(settings => settings with { TargetAppCorrectionLearningEnabled = false });
                return Task.FromResult(new PluginTranscriptionResult("dictated café", "en", 1));
            });

            var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
            await BoundedTest.WaitAsync(fixture.RecordingStarted.Task);
            fixture.FeedNonSilentAudio();
            var resultTask = fixture.WaitForResultAsync(sessionId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            await BoundedTest.WaitAsync(resultTask);

            Assert.Equal(0, fixture.AtSpi.FocusReadCount);
            Assert.NotEqual(InsertionFailureReason.LockedFieldUnavailable,
                fixture.Provider.GetRequiredService<TextInsertionService>().LastFailureReason);
            Assert.Equal(1, fixture.InsertionPlatform.PasteAttemptCount);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task ConsentWithdrawnDuringCommand_OneShotInsertionDoesNotTouchTheField()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture(
                focusedApp: ("codex", "Editor")
            );
            // Keep the learner's listener lifecycle separate from the focus-lock assertions.
            fixture.Provider.GetRequiredService<TargetAppCorrectionLearningService>().Dispose();
            fixture.Settings.Update(settings => settings with
            {
                LockPasteToFocusedField = true,
                TargetAppCorrectionLearningEnabled = true,
                CommandModeEnabled = true,
            });
            var target = new AtSpiElementRef("app", "/initial");
            fixture.AtSpi.IsRunning = true;
            fixture.AtSpi.CurrentFocusedElement = target;
            fixture.Plugin.EnqueueText("TypeWhisper write a haiku about the sea");
            fixture.Llm.EnqueueStream("Waves fold the gray shore, ", "salt light drifts through pines.");

            // ReSharper disable once AccessToDisposedClosure -- the callback runs inside the awaited dictation, before the fixture disposes.
            fixture.Llm.BeforeBatchResponse = () => fixture.Settings.Update(settings => settings with
            {
                TargetAppCorrectionLearningEnabled = false,
            });

            var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
            fixture.FeedNonSilentAudio();
            var resultTask = fixture.WaitForResultAsync(sessionId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            await BoundedTest.WaitAsync(resultTask);

            Assert.Equal(1, fixture.Llm.BatchCalls);
            Assert.Null(fixture.AtSpi.LastFocusReadElement);
            Assert.Single(fixture.InsertionPlatform.Typed);
            Assert.Equal(0, fixture.InsertionPlatform.PasteAttemptCount);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task ConsentRestoredBeforeCommandInsertion_KeepsTheLockedField()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture(
                focusedApp: ("codex", "Editor")
            );
            // Keep the learner's listener lifecycle separate from the focus-lock assertions.
            fixture.Provider.GetRequiredService<TargetAppCorrectionLearningService>().Dispose();
            fixture.Settings.Update(settings => settings with
            {
                LockPasteToFocusedField = true,
                TargetAppCorrectionLearningEnabled = true,
                CommandModeEnabled = true,
            });
            var target = new AtSpiElementRef("app", "/initial");
            fixture.AtSpi.IsRunning = true;
            fixture.AtSpi.CurrentFocusedElement = target;
            fixture.Plugin.EnqueueResult(_ =>
            {
                // ReSharper disable once AccessToDisposedClosure -- the callback runs inside the awaited dictation, before the fixture disposes.
                fixture.Settings.Update(settings => settings with
                {
                    TargetAppCorrectionLearningEnabled = false,
                });
                return Task.FromResult(new PluginTranscriptionResult(
                    "TypeWhisper write a haiku about the sea", "en", 1));
            });
            fixture.Llm.EnqueueStream("Waves fold the gray shore, ", "salt light drifts through pines.");

            // ReSharper disable once AccessToDisposedClosure -- the callback runs inside the awaited dictation, before the fixture disposes.
            fixture.Llm.BeforeBatchResponse = () => fixture.Settings.Update(settings => settings with
            {
                TargetAppCorrectionLearningEnabled = true,
            });

            var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
            fixture.FeedNonSilentAudio();
            var resultTask = fixture.WaitForResultAsync(sessionId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            await BoundedTest.WaitAsync(resultTask);

            Assert.Equal(1, fixture.Llm.BatchCalls);
            Assert.Equal(target, fixture.AtSpi.LastFocusReadElement);
            Assert.Single(fixture.InsertionPlatform.Typed);
            Assert.Equal(0, fixture.InsertionPlatform.PasteAttemptCount);
        });
    }

    private static string LocalizationDir([CallerFilePath] string thisFile = "")
    {
        return Path.GetFullPath(Path.Join(
            Path.GetDirectoryName(thisFile)!, "..", "..", "src", "TypeWhisper.Linux", "Resources", "Localization"
        ));
    }
}
