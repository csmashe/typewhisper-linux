using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using Xunit;

namespace TypeWhisper.Integration.Tests;

public sealed class DictationOrchestratorSpokenFormattingTests
{
    [Theory]
    [InlineData(SpokenFormattingStrategy.FallbackOnly, "hello, world\nnext?")]
    [InlineData(SpokenFormattingStrategy.NativeOnly, "hello comma world new line next question mark")]
    [Trait("Category", "Integration")]
    public Task Dictation_AppliesResolvedStrategy(SpokenFormattingStrategy strategy, string expected)
    {
        return BoundedTest.RunAsync(async () =>
        {
            var pipeline = new RecordingPipeline();
            await using var fixture = new OrchestratorCompositionFixture(pipeline: pipeline);
            fixture.Settings.Update(current => current.WithLanguageHints(["en"]) with
            {
                SpokenFormattingStrategy = strategy,
            });
            fixture.Plugin.EnqueueText("hello comma world new line next question mark");
            var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
            Assert.True(sessionId > 0);
            fixture.FeedNonSilentAudio();
            var resultTask = fixture.WaitForResultAsync(sessionId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            var result = await BoundedTest.WaitAsync(resultTask);
            Assert.Equal("ready", result.Status);
            Assert.Equal(expected, result.Text);
            Assert.NotNull(pipeline.Result);
            var spokenSteps = pipeline.Result.Steps.Where(step => step.Name is
                PostProcessingStepNames.SpokenFormatting or PostProcessingStepNames.SpokenCommands or PostProcessingStepNames.SpokenPunctuation);
            Assert.Equal(strategy == SpokenFormattingStrategy.FallbackOnly ? ["SpokenFormatting"] : [],
                spokenSteps.Select(step => step.Name));
        });
    }

    [Theory]
    [InlineData(true, "hello comma world", "en", "hello, world")]
    [InlineData(false, "hallo komma welt", "de", "hallo, welt")]
    [Trait("Category", "Integration")]
    public Task Dictation_ResolvesFormattingLanguageForEngineTranslation(
        bool translate, string transcript, string reportedLanguage, string expected)
    {
        return BoundedTest.RunAsync(async () =>
        {
            var pipeline = new RecordingPipeline();
            await using var fixture = new OrchestratorCompositionFixture(pipeline: pipeline);
            fixture.Plugin.SupportsTranslation = true;
            fixture.Settings.Update(current => current.WithLanguageHints(["de"]) with
            {
                SpokenFormattingStrategy = SpokenFormattingStrategy.FallbackOnly,
                TranscriptionTask = translate ? "translate" : "transcribe",
            });
            fixture.Plugin.EnqueueText(transcript, reportedLanguage);
            var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
            Assert.True(sessionId > 0);
            fixture.FeedNonSilentAudio();
            var resultTask = fixture.WaitForResultAsync(sessionId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            var result = await BoundedTest.WaitAsync(resultTask);

            Assert.Equal("ready", result.Status);
            Assert.Equal(expected, result.Text);
            Assert.Equal(translate, fixture.Plugin.ReceivedTranslate);
            Assert.NotNull(pipeline.Result);
            Assert.Contains(pipeline.Result.Steps, step => step.Name == PostProcessingStepNames.SpokenFormatting);
            Assert.DoesNotContain(pipeline.Result.Steps, step => step.Name is
                PostProcessingStepNames.SpokenCommands or PostProcessingStepNames.SpokenPunctuation);
        });
    }

    private sealed class RecordingPipeline : IPostProcessingPipeline
    {
        public PostProcessingResult? Result { get; private set; }

        public async Task<PostProcessingResult> ProcessAsync(string rawText, PipelineOptions options, CancellationToken ct = default)
        {
            Result = await new PostProcessingPipeline().ProcessAsync(rawText, options, ct);
            return Result;
        }
    }
}
