using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using Xunit;

namespace TypeWhisper.Integration.Tests;

// Explicit Direct typing on a ydotool-only chain: ydotool drops non-ASCII yet exits 0.
public sealed class DictationOrchestratorDirectTypingUnicodeTests
{
    private const string UnicodeText = "QA café naïve — mañana, früh. 日本語テスト!";

    [Fact]
    [Trait("Category", "Integration")]
    public Task Dictation_CopiesNonAsciiInsteadOfReportingTyped()
    {
        return BoundedTest.RunAsync(async () =>
        {
            var previousLanguage = Loc.Instance.CurrentLanguage;
            try
            {
                Loc.Instance.Initialize(LocalizationDir());
                Loc.Instance.CurrentLanguage = "en";

                await using var fixture = CreateFixture();
                fixture.Plugin.EnqueueText(UnicodeText);
                var overlayStates = new ConcurrentQueue<DictationOverlayState>();
                fixture.Orchestrator.OverlayStateChanged += (_, state) => overlayStates.Enqueue(state);

                var result = await RunAsync(fixture);

                var expectedMessage = Loc.Instance["TextInsertion.NonAsciiTypingClipboardFallback"];
                Assert.Contains("ydotool", expectedMessage);
                Assert.Equal("ready", result.Status);
                Assert.Contains(
                    overlayStates,
                    state => state is { ShowFeedback: true, FeedbackIsError: false } && state.FeedbackText == expectedMessage
                );
                Assert.Empty(fixture.InsertionPlatform.Typed);
                Assert.Equal(0, fixture.InsertionPlatform.PasteAttemptCount);
                var history = Assert.Single(fixture.History.Records);
                Assert.Equal(UnicodeText, history.FinalText);
                // Insertion appends the usual trailing separator.
                Assert.Equal(UnicodeText + " ", fixture.InsertionPlatform.Clipboard);
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
    public Task SpokenCommand_CopiesNonAsciiInsteadOfTypingIt()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = CreateFixture();
            fixture.Settings.Save(fixture.Settings.Current with { CommandModeEnabled = true });
            const string firstDelta = "Café au lait — “quoted”, ";
            const string secondDelta = "salt light drifts through pines.";
            fixture.Plugin.EnqueueText("TypeWhisper write a line about coffee");
            fixture.Llm.EnqueueStream(firstDelta, secondDelta);

            var result = await RunAsync(fixture);

            Assert.Equal("ready", result.Status);
            Assert.Equal(firstDelta + secondDelta, result.Text);
            Assert.Empty(fixture.InsertionPlatform.Typed);
            Assert.Equal(0, fixture.InsertionPlatform.PasteAttemptCount);
            Assert.Equal(firstDelta + secondDelta, fixture.InsertionPlatform.Clipboard);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task SpokenCommand_WithNonAsciiAfterAsciiChunk_DoesNotLeaveTruncatedText()
    {
        return BoundedTest.RunAsync(async () =>
        {
            // Streaming would type the ASCII chunk, then fail on the later one and skip the copy.
            await using var fixture = CreateFixture();
            fixture.Settings.Save(fixture.Settings.Current with { CommandModeEnabled = true });
            const string firstDelta = "Steam curls above the morning cup, ";
            const string secondDelta = "café au lait — früh.";
            fixture.Plugin.EnqueueText("TypeWhisper write a line about coffee");
            fixture.Llm.EnqueueStream(firstDelta, secondDelta);

            var result = await RunAsync(fixture);

            Assert.Equal("ready", result.Status);
            Assert.Equal(firstDelta + secondDelta, result.Text);
            Assert.Empty(fixture.InsertionPlatform.Typed);
            Assert.Equal(0, fixture.InsertionPlatform.PasteAttemptCount);
            Assert.Equal(firstDelta + secondDelta, fixture.InsertionPlatform.Clipboard);
            Assert.Equal(0, fixture.Llm.StreamCalls);
        });
    }

    private static OrchestratorCompositionFixture CreateFixture()
    {
        var fixture = new OrchestratorCompositionFixture(focusedApp: ("python3", "twqa — integration"));
        fixture.InsertionPlatform.CanTypeNonAscii = false;
        fixture.Settings.Save(
            fixture.Settings.Current with
            {
                AppInsertionStrategies = new Dictionary<string, TextInsertionStrategy>
                {
                    ["python3"] = TextInsertionStrategy.DirectTyping,
                },
            }
        );
        return fixture;
    }

    private static async Task<DictationSessionResult> RunAsync(OrchestratorCompositionFixture fixture)
    {
        var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
        fixture.FeedNonSilentAudio();
        var resultTask = fixture.WaitForResultAsync(sessionId);
        await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
        return await BoundedTest.WaitAsync(resultTask);
    }

    private static string LocalizationDir([CallerFilePath] string thisFile = "")
    {
        var testDir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(
            Path.Join(testDir, "..", "..", "src", "TypeWhisper.Linux", "Resources", "Localization")
        );
    }
}
