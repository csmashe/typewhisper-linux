using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Integration.Tests;

/// <summary>
///     Dictation sends enabled dictionary terms only to an engine that reads structured terms, with
///     each term intact, on the final pass, the live preview and a retry from history; other engines
///     keep receiving no prompt.
/// </summary>
public sealed class DictationStructuredDictionaryTermsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Integration")]
    public Task Dictation_AndRetry_SendTermsOnlyToAStructuredEngine(bool structured) =>
        BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();
            fixture.Plugin.SupportsStructuredDictionaryTerms = structured;
            fixture.Plugin.DictionaryTermsBudget = new DictionaryTermsBudget(MaxWordsPerTerm: 2);
            AddDictionary(fixture);
            fixture.Plugin.EnqueueFailure("provider unavailable");

            var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
            Assert.True(sessionId > 0);
            fixture.FeedNonSilentAudio();
            var resultTask = fixture.WaitForResultAsync(sessionId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            Assert.Equal("failed", (await BoundedTest.WaitAsync(resultTask)).Status);

            fixture.Plugin.EnqueueText("recovered");
            var record = Assert.Single(fixture.History.Records);
            var recovered = await BoundedTest.WaitAsync(fixture.Orchestrator.RetryFromHistoryAsync(record.Id));
            Assert.Equal(TranscriptionRecordStatus.Succeeded, recovered.Status);

            Assert.Equal(2, fixture.Plugin.ReceivedPrompts.Count);
            Assert.All(fixture.Plugin.ReceivedPrompts, prompt => AssertTerms(structured, prompt));
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Integration")]
    public Task LivePreview_SendsTermsOnlyToAStructuredEngine(bool structured) =>
        BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();
            fixture.Plugin.SupportsStructuredDictionaryTerms = structured;
            fixture.Plugin.DictionaryTermsBudget = new DictionaryTermsBudget(MaxWordsPerTerm: 2);
            AddDictionary(fixture);
            fixture.Settings.Update(current => current with
            {
                LiveTranscriptionEnabled = true,
                OnlineAsrBatchLiveTranscriptionEnabled = true,
            });
            // Preview only polls an already-loaded model and never loads one itself.
            await fixture.Provider.GetRequiredService<ModelManagerService>()
                .LoadModelAsync(fixture.Settings.Current.SelectedModelId!);
            fixture.Plugin.EnqueueText("preview");
            fixture.Plugin.EnqueueText("final");

            var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
            Assert.True(sessionId > 0);
            fixture.FeedNonSilentAudio();
            // The first poll is due three seconds into the recording.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4.5);
            while (fixture.Plugin.TranscriptionCount == 0 && DateTime.UtcNow < deadline)
            {
                fixture.FeedNonSilentAudio();
                await Task.Delay(100);
            }
            Assert.True(fixture.Plugin.TranscriptionCount > 0);
            var resultTask = fixture.WaitForResultAsync(sessionId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            Assert.Equal("ready", (await BoundedTest.WaitAsync(resultTask)).Status);

            Assert.True(fixture.Plugin.ReceivedPrompts.Count >= 2);
            Assert.All(fixture.Plugin.ReceivedPrompts, prompt => AssertTerms(structured, prompt));
        });

    // Only enabled terms within the engine's budget are sent; corrections and disabled terms never are.
    private static void AddDictionary(OrchestratorCompositionFixture fixture)
    {
        var dictionary = fixture.Provider.GetRequiredService<IDictionaryService>();
        dictionary.AddEntry(new DictionaryEntry { Id = "dc", EntryType = DictionaryEntryType.Term, Original = "Washington, D.C." });
        dictionary.AddEntry(new DictionaryEntry { Id = "quote", EntryType = DictionaryEntryType.Term, Original = "\"Grüße\"" });
        dictionary.AddEntry(new DictionaryEntry { Id = "long", EntryType = DictionaryEntryType.Term, Original = "three word term" });
        dictionary.AddEntry(new DictionaryEntry
        {
            Id = "off", EntryType = DictionaryEntryType.Term, Original = "Disabled", IsEnabled = false,
        });
        dictionary.AddEntry(new DictionaryEntry
        {
            Id = "fix", EntryType = DictionaryEntryType.Correction, Original = "kubernets", Replacement = "Kubernetes",
        });
    }

    private static void AssertTerms(bool structured, string? prompt)
    {
        if (!structured)
        {
            Assert.Null(prompt);
            return;
        }

        var parsed = PluginTranscriptionPrompt.Parse(prompt);
        Assert.Null(parsed.Text);
        Assert.Equal(["Washington, D.C.", "\"Grüße\""], parsed.DictionaryTerms);
    }
}
