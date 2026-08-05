using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Integration.Tests;

public sealed class DictationOrchestratorTerminalMultilineFallbackTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public Task TerminalMultilinePasteFailure_RecordsNonErrorClipboardDelivery()
    {
        return BoundedTest.RunAsync(async () =>
        {
            var previousLanguage = Loc.Instance.CurrentLanguage;
            try
            {
                Loc.Instance.Initialize(LocalizationDir());
                Loc.Instance.CurrentLanguage = "en";

                await using var fixture = new OrchestratorCompositionFixture(
                    focusedApp: ("konsole", "Konsole — integration")
                );
                fixture.Plugin.EnqueueText("first line\nsecond line");
                fixture.InsertionPlatform.EnqueuePasteOutcome(false);
                fixture.InsertionPlatform.EnqueuePasteOutcome(false);
                fixture.InsertionPlatform.EnqueuePasteOutcome(false);

                var inserted = new TaskCompletionSource<TextInsertedEvent>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                using var subscription = fixture.EventBus.Subscribe<TextInsertedEvent>(evt =>
                {
                    inserted.TrySetResult(evt);
                    return Task.CompletedTask;
                });
                var overlayStates = new ConcurrentQueue<DictationOverlayState>();
                fixture.Orchestrator.OverlayStateChanged += (_, state) =>
                    overlayStates.Enqueue(state);

                var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
                fixture.FeedNonSilentAudio();
                var resultTask = fixture.WaitForResultAsync(sessionId);
                await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
                var result = await BoundedTest.WaitAsync(resultTask);
                var insertedEvent = await BoundedTest.WaitAsync(inserted.Task);

                const string expectedMessage =
                    "Copied to clipboard. Paste into the terminal with Ctrl+Shift+V.";
                Assert.Equal(
                    expectedMessage,
                    Loc.Instance["TextInsertion.TerminalClipboardFallback"]
                );
                Assert.Equal("ready", result.Status);
                Assert.Contains(
                    overlayStates,
                    state =>
                        state
                            is {
                                ShowFeedback: true,
                                FeedbackIsError: false,
                                FeedbackText: expectedMessage,
                            }
                );
                Assert.Contains('\n', insertedEvent.Text);
                Assert.Equal("Konsole — integration", insertedEvent.AppName);
                Assert.Equal(insertedEvent.Text, fixture.InsertionPlatform.Clipboard);
                Assert.Empty(fixture.InsertionPlatform.Typed);
                Assert.Equal(3, fixture.InsertionPlatform.PasteAttemptCount);

                var history = Assert.Single(fixture.History.Records);
                Assert.Equal(TextInsertionStatus.CopiedToClipboard, history.InsertionStatus);
            }
            finally
            {
                Loc.Instance.CurrentLanguage = previousLanguage;
            }
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task TerminalMultilinePasteFailure_WithActionableReason_PrefersSetupGuidance()
    {
        return BoundedTest.RunAsync(async () =>
        {
            var previousLanguage = Loc.Instance.CurrentLanguage;
            try
            {
                Loc.Instance.Initialize(LocalizationDir());
                Loc.Instance.CurrentLanguage = "en";

                await using var fixture = new OrchestratorCompositionFixture(
                    focusedApp: ("konsole", "Konsole — integration")
                );
                fixture.Plugin.EnqueueText("first line\nsecond line");
                fixture.InsertionPlatform.EnqueuePasteOutcome(false);
                fixture.InsertionPlatform.SetFailureReason(
                    InsertionFailureReason.YdotoolSocketUnreachable
                );

                var overlayStates = new ConcurrentQueue<DictationOverlayState>();
                fixture.Orchestrator.OverlayStateChanged += (_, state) =>
                    overlayStates.Enqueue(state);

                var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
                fixture.FeedNonSilentAudio();
                var resultTask = fixture.WaitForResultAsync(sessionId);
                await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
                var result = await BoundedTest.WaitAsync(resultTask);

                // A terminal target must not hide the actionable setup diagnosis behind the
                // generic "paste with Ctrl+Shift+V" hint.
                const string expectedMessage =
                    "Copied to clipboard. ydotool socket not reachable — open Settings → Text insertion to check daemon status.";
                Assert.Equal("ready", result.Status);
                Assert.Contains(
                    overlayStates,
                    state =>
                        state
                            is {
                                ShowFeedback: true,
                                FeedbackIsError: false,
                                FeedbackText: expectedMessage,
                            }
                );
                Assert.DoesNotContain(
                    overlayStates,
                    state =>
                        state.FeedbackText
                        == Loc.Instance["TextInsertion.TerminalClipboardFallback"]
                );
                Assert.Empty(fixture.InsertionPlatform.Typed);
            }
            finally
            {
                Loc.Instance.CurrentLanguage = previousLanguage;
            }
        });
    }

    private static string LocalizationDir([CallerFilePath] string thisFile = "")
    {
        var testDir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(
            Path.Join(testDir, "..", "..", "src", "TypeWhisper.Linux", "Resources", "Localization")
        );
    }
}
