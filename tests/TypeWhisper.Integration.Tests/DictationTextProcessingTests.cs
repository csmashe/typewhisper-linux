using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Models;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Integration.Tests;

/// <summary>
///     Text processing powers the local text route used by quality evals. It must produce the
///     same final text that a dictation of the same transcript produces, or eval scores would
///     describe a different pipeline than the one users get.
/// </summary>
public sealed class DictationTextProcessingTests
{
    [Theory]
    [InlineData("see the hyperlink links, then link. alpha")]
    [InlineData("Hello new line. Next item")]
    [InlineData("um so the meeting is at 3 pm")]
    [Trait("Category", "Integration")]
    public Task ProcessText_MatchesDictationOfTheSameTranscript(string transcript)
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();
            UseEnglish(fixture);
            var snippets = fixture.Provider.GetRequiredService<ISnippetService>();
            snippets.AddSnippet(new Snippet { Id = "link", Trigger = "link", Replacement = "https://example.com/link" });
            snippets.AddSnippet(new Snippet { Id = "alpha", Trigger = "alpha", Replacement = "beta" });

            var dictated = await DictateAsync(fixture, transcript);
            var processed = await fixture.Orchestrator.ProcessTextAsync(new TextProcessingRequest(transcript));

            Assert.Equal("ready", dictated.Status);
            Assert.Equal(dictated.Text, processed.Text);
            Assert.Equal(transcript, processed.RawText);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task ProcessText_MatchesProfilesByAppProcessLikeDictation()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture(focusedApp: ("mail-app", "Mail app — integration"));
            UseEnglish(fixture);
            fixture.Provider.GetRequiredService<IProfileService>().AddProfile(
                new Profile { Id = "mail", Name = "Mail", ProcessNames = ["mail-app"] });
            fixture.Provider.GetRequiredService<ISnippetService>().AddSnippet(new Snippet
            {
                Id = "mail-sig", Trigger = "sig", Replacement = "Best regards",
                TriggerMode = SnippetTriggerMode.ExactPhrase, ProfileIds = ["mail"],
            });

            var dictated = await DictateAsync(fixture, "Sig.");
            var matched = await fixture.Orchestrator.ProcessTextAsync(new TextProcessingRequest("Sig.", AppProcess: "mail-app"));
            var forced = await fixture.Orchestrator.ProcessTextAsync(new TextProcessingRequest("Sig.", ProfileId: "mail"));
            var unmatched = await fixture.Orchestrator.ProcessTextAsync(new TextProcessingRequest("Sig.", AppProcess: "terminal"));

            Assert.Equal("Best regards", dictated.Text);
            Assert.Equal("Best regards", matched.Text);
            Assert.Equal("mail", matched.ProfileId);
            Assert.Equal("Best regards", forced.Text);
            Assert.NotEqual("Best regards", unmatched.Text);
            Assert.Null(unmatched.ProfileId);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task ProcessText_ReportsEveryPipelineStepWithTimings()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();
            UseEnglish(fixture);
            fixture.Provider.GetRequiredService<ISnippetService>().AddSnippet(
                new Snippet { Id = "alpha", Trigger = "alpha", Replacement = "beta" });

            var processed = await fixture.Orchestrator.ProcessTextAsync(new TextProcessingRequest("alpha"));

            var snippetStep = Assert.Single(processed.Steps, step => step.Name == PostProcessingStepNames.Snippets);
            Assert.True(snippetStep.Changed);
            Assert.All(processed.Steps, step => Assert.True(step.Milliseconds >= 0));
            Assert.Equal(CleanupLevel.None, processed.CleanupLevel);
            Assert.Empty(processed.LlmCalls);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task ProcessText_RejectsUnknownProfilesAndEmptyText()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();

            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                fixture.Orchestrator.ProcessTextAsync(new TextProcessingRequest("hello", ProfileId: "missing")));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                fixture.Orchestrator.ProcessTextAsync(new TextProcessingRequest("   ")));
        });
    }

    private static void UseEnglish(OrchestratorCompositionFixture fixture)
    {
        fixture.Settings.Update(current => current.WithLanguageHints(["en"]));
    }

    [Theory]
    [InlineData("success", true)]
    [InlineData("empty", false)]
    [InlineData("failure", false)]
    [Trait("Category", "Integration")]
    public Task ProcessText_ReportsWhetherCleanupProducedACompletedResponse(string outcome, bool succeeded)
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();
            UseEnglish(fixture);
            fixture.Settings.Update(current => current with
            {
                CleanupLevel = CleanupLevel.Medium,
                DefaultLlmProvider = "plugin:integration.scripted-llm:scripted-llm-model",
            });
            if (outcome == "success") fixture.Llm.EnqueueStream("Send the report.");
            if (outcome == "failure") fixture.Llm.Failure = new InvalidOperationException("provider failed");

            var result = await fixture.Orchestrator.ProcessTextAsync(new TextProcessingRequest("umm send the report"));

            Assert.Equal(succeeded, Assert.Single(result.LlmCalls).Succeeded);
            Assert.Equal(CleanupLevel.Medium, result.CleanupLevel);
            Assert.Equal(succeeded ? "Send the report." : "Send the report", result.Text);
        });
    }

    private static async Task<DictationSessionResult> DictateAsync(OrchestratorCompositionFixture fixture, string transcript)
    {
        fixture.Plugin.EnqueueText(transcript);
        var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
        Assert.True(sessionId > 0);
        fixture.FeedNonSilentAudio();
        var resultTask = fixture.WaitForResultAsync(sessionId);
        await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
        return await BoundedTest.WaitAsync(resultTask);
    }
}
