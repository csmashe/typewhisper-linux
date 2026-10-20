using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core;
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
    [InlineData("Pause… then continue.")]
    [InlineData("  Pause... then continue.  ")]
    [InlineData("I think I think we should")]
    [InlineData("I think we should I think we should continue.")]
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

    [Theory]
    [InlineData("…")]
    [InlineData(" ... … ")]
    [Trait("Category", "Integration")]
    public Task ProcessText_RejectsTextThatNormalizesToEmpty(string transcript)
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Orchestrator.ProcessTextAsync(new TextProcessingRequest(transcript)));
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task ProcessText_ExpandsSnippetsAndCorrectionsWithoutRecordingUsageOrWritingFiles()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();
            UseEnglish(fixture);
            var snippets = fixture.Provider.GetRequiredService<ISnippetService>();
            var dictionary = fixture.Provider.GetRequiredService<IDictionaryService>();
            var lastUsed = new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc);
            snippets.AddSnippet(new Snippet
            {
                Id = "sig", Trigger = "sig", Replacement = "kubernets",
                UsageCount = 7, LastUsedAt = lastUsed,
            });
            dictionary.AddEntry(new DictionaryEntry
            {
                Id = "correction", EntryType = DictionaryEntryType.Correction,
                Original = "kubernets", Replacement = "Kubernetes", UsageCount = 4,
                TimesApplied = 3, TimesCorrected = 2, LastUsedAt = lastUsed, LastCorrectedAt = lastUsed,
            });
            var originalSnippets = snippets.Snippets.ToArray();
            var originalEntries = dictionary.Entries.ToArray();
            var files = new[] { "snippets.json", "dictionary.json" }
                .Select(name => Path.Join(TypeWhisperEnvironment.DataPath, name)).ToArray();
            var before = files.Select(path => (Bytes: File.ReadAllBytes(path), Written: File.GetLastWriteTimeUtc(path))).ToArray();

            for (var i = 0; i < 2; i++)
            {
                var result = await fixture.Orchestrator.ProcessTextAsync(new TextProcessingRequest("sig"));
                Assert.Equal("Kubernetes", result.Text);
            }

            Assert.Equal(originalSnippets, snippets.Snippets);
            Assert.Equal(originalEntries, dictionary.Entries);
            for (var i = 0; i < files.Length; i++)
            {
                Assert.Equal(before[i].Bytes, File.ReadAllBytes(files[i]));
                Assert.Equal(before[i].Written, File.GetLastWriteTimeUtc(files[i]));
            }
            Assert.Empty(fixture.Statistics.Records);
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
