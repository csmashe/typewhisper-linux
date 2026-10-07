using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Models;
using Xunit;

namespace TypeWhisper.Integration.Tests;

/// <summary>
///     Runs the production snippet and dictionary services through a dictation, alongside the
///     spoken-formatting steps that run before them.
/// </summary>
public sealed class DictationOrchestratorSnippetDictionaryTests
{
    [Theory]
    [InlineData(SpokenFormattingStrategy.NativeOnly)]
    [InlineData(SpokenFormattingStrategy.FallbackOnly)]
    [Trait("Category", "Integration")]
    public Task Dictation_SnippetsMatchCompleteWordsInTheOriginalTranscript(SpokenFormattingStrategy strategy)
    {
        return BoundedTest.RunAsync(async () =>
        {
            var pipeline = new RecordingPipeline();
            await using var fixture = new OrchestratorCompositionFixture(pipeline: pipeline);
            UseStrategy(fixture, strategy);
            var snippets = fixture.Provider.GetRequiredService<ISnippetService>();
            snippets.AddSnippet(new Snippet { Id = "link", Trigger = "link", Replacement = "https://example.com/link" });
            snippets.AddSnippet(new Snippet { Id = "alpha", Trigger = "alpha", Replacement = "beta" });
            snippets.AddSnippet(new Snippet { Id = "beta", Trigger = "beta", Replacement = "gamma" });

            var result = await DictateAsync(fixture, "see the hyperlink links, then link. alpha");

            Assert.Equal("ready", result.Status);
            Assert.Equal("see the hyperlink links, then https://example.com/link beta", result.Text);
            Assert.Equal(
                [("alpha", 1), ("beta", 0), ("link", 1)],
                snippets.Snippets.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => (s.Id, s.UsageCount))
            );
            Assert.True(StepChanged(pipeline, PostProcessingStepNames.Snippets));
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task Dictation_ProfileScopedExactPhraseSnippet_AppliesOnlyInItsProfile()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture(
                focusedApp: ("mail-app", "Mail app — integration")
            );
            UseStrategy(fixture, SpokenFormattingStrategy.NativeOnly);
            fixture.Provider.GetRequiredService<IProfileService>().AddProfile(
                new Profile { Id = "mail", Name = "Mail", ProcessNames = ["mail-app"] }
            );
            var snippets = fixture.Provider.GetRequiredService<ISnippetService>();
            snippets.AddSnippet(
                new Snippet
                {
                    Id = "mail-sig",
                    Trigger = "sig",
                    Replacement = "Best regards",
                    TriggerMode = SnippetTriggerMode.ExactPhrase,
                    ProfileIds = ["mail"],
                }
            );
            snippets.AddSnippet(
                new Snippet { Id = "chat-sig", Trigger = "sig", Replacement = "Cheers", ProfileIds = ["chat"] }
            );

            var result = await DictateAsync(fixture, "Sig.");

            Assert.Equal("ready", result.Status);
            Assert.Equal("Best regards", result.Text);
        });
    }

    [Theory]
    [InlineData("Hello new line. Next item", "Hello \nNext item")]
    [InlineData("First new line, second new line; third", "First \nsecond \nthird")]
    [InlineData("Ends with new line.", "Ends with \n")]
    [Trait("Category", "Integration")]
    public Task Dictation_LayoutCorrection_ConsumesTheCommandsPunctuation(string transcript, string expected)
    {
        return BoundedTest.RunAsync(async () =>
        {
            var pipeline = new RecordingPipeline();
            await using var fixture = new OrchestratorCompositionFixture(pipeline: pipeline);
            // Native-only formatting leaves "new line" for the user's dictionary entry to handle.
            UseStrategy(fixture, SpokenFormattingStrategy.NativeOnly);
            AddLayoutCorrection(fixture);

            var result = await DictateAsync(fixture, transcript);

            Assert.Equal("ready", result.Status);
            Assert.True(StepChanged(pipeline, PostProcessingStepNames.Dictionary));
            Assert.Equal(expected, pipeline.Result!.Text);
            // The final-text policy trims delivery, which drops a trailing line break.
            Assert.Equal(expected.Trim(), result.Text);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task Dictation_LayoutCorrection_KeepsOrdinaryCorrectionPunctuation()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();
            UseStrategy(fixture, SpokenFormattingStrategy.NativeOnly);
            AddLayoutCorrection(fixture);
            fixture.Provider.GetRequiredService<IDictionaryService>().AddEntry(
                new DictionaryEntry
                {
                    Id = "word",
                    EntryType = DictionaryEntryType.Correction,
                    Original = "kubernets",
                    Replacement = "Kubernetes",
                }
            );

            var result = await DictateAsync(fixture, "I use kubernets, new line. It works");

            Assert.Equal("ready", result.Status);
            Assert.Equal("I use Kubernetes, \nIt works", result.Text);
        });
    }

    private static void UseStrategy(OrchestratorCompositionFixture fixture, SpokenFormattingStrategy strategy)
    {
        fixture.Settings.Update(current => current.WithLanguageHints(["en"]) with
        {
            SpokenFormattingStrategy = strategy,
        });
    }

    private static void AddLayoutCorrection(OrchestratorCompositionFixture fixture)
    {
        fixture.Provider.GetRequiredService<IDictionaryService>().AddEntry(
            new DictionaryEntry
            {
                Id = "layout",
                EntryType = DictionaryEntryType.Correction,
                Original = "new line",
                Replacement = @"\n",
                ExpandEscapes = true,
            }
        );
    }

    private static async Task<DictationSessionResult> DictateAsync(
        OrchestratorCompositionFixture fixture,
        string transcript
    )
    {
        fixture.Plugin.EnqueueText(transcript);
        var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
        Assert.True(sessionId > 0);
        fixture.FeedNonSilentAudio();
        var resultTask = fixture.WaitForResultAsync(sessionId);
        await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
        return await BoundedTest.WaitAsync(resultTask);
    }

    private static bool StepChanged(RecordingPipeline pipeline, string stepName)
    {
        Assert.NotNull(pipeline.Result);
        var step = Assert.Single(pipeline.Result.Steps, s => s.Name == stepName);
        return step.Changed;
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
