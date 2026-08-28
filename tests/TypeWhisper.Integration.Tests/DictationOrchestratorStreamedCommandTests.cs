using TypeWhisper.Linux.Models;
using Xunit;

namespace TypeWhisper.Integration.Tests;

public sealed class DictationOrchestratorStreamedCommandTests
{
    // Each delta clears the orchestrator's 20-char streaming buffer threshold on its own, so
    // one delta is one typed chunk and the chunk boundaries are what the assertions read.
    private const string FirstDelta = "Waves fold the gray shore, ";
    private const string SecondDelta = "salt light drifts through pines, ";
    private const string ThirdDelta = "and the tide keeps its own time.";
    private const string Command = "TypeWhisper write a haiku about the sea";

    [Fact]
    [Trait("Category", "Integration")]
    public Task StreamedSpokenCommand_CarriesEachChunkOutcomeIntoFailureAndDeliveryState()
    {
        return BoundedTest.RunAsync(async () =>
        {
            // "codex" is an app the Auto insertion policy types into directly, which is what
            // lets a spoken command stream onto the page instead of taking the one-shot path.
            await using var fixture = new OrchestratorCompositionFixture(
                focusedApp: ("codex", "codex — integration")
            );
            fixture.Settings.Save(fixture.Settings.Current with { CommandModeEnabled = true });

            // A chunk that fails after delivering part of its text leaves a truncated prefix on
            // the page, so the whole result must NOT be re-inserted over it.
            fixture.Plugin.EnqueueText(Command);
            fixture.Llm.EnqueueStream(FirstDelta, SecondDelta, ThirdDelta);
            fixture.InsertionPlatform.EnqueueTypingOutcome(succeeds: false, deliveredPartial: true);

            var partialResult = await RunCommandAsync(fixture);

            Assert.Equal("failed", partialResult.Status);
            Assert.Equal([FirstDelta], fixture.InsertionPlatform.Typed);
            Assert.Empty(fixture.InsertionPlatform.ClipboardWrites);
            Assert.Empty(fixture.History.Records);
            Assert.Equal(0, fixture.Llm.BatchCalls);

            // A chunk that succeeds keeps the stream typing the remaining chunks and must not be
            // read as a failure, which would abandon the stream and re-insert the whole result.
            fixture.Plugin.EnqueueText(Command);
            fixture.Llm.EnqueueStream(FirstDelta, SecondDelta, ThirdDelta);

            var streamedResult = await RunCommandAsync(fixture);

            Assert.Equal("ready", streamedResult.Status);
            Assert.Equal(FirstDelta + SecondDelta + ThirdDelta, streamedResult.Text);
            // Typed is cumulative across both phases: the leading FirstDelta is the
            // phase-one partial delivery, followed by phase two's three chunks.
            Assert.Equal(
                [FirstDelta, FirstDelta, SecondDelta, ThirdDelta],
                fixture.InsertionPlatform.Typed
            );
            Assert.Empty(fixture.InsertionPlatform.ClipboardWrites);
            Assert.Equal(0, fixture.Llm.BatchCalls);
            Assert.Equal("idle", fixture.Orchestrator.CurrentStateLabel);
        });
    }

    private static async Task<DictationSessionResult> RunCommandAsync(
        OrchestratorCompositionFixture fixture
    )
    {
        var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
        fixture.FeedNonSilentAudio();
        var resultTask = fixture.WaitForResultAsync(sessionId);
        await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
        return await BoundedTest.WaitAsync(resultTask);
    }
}
