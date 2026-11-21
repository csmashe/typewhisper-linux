using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Integration.Tests.TestDoubles;
using TypeWhisper.Linux.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Integration.Tests;

/// <summary>
///     A stream that cannot carry the dictionary prompt only shows live text: it opens without the
///     prompt, and the complete recording, transcribed with the dictionary, supplies the final text.
/// </summary>
public sealed class DictationStreamingPreviewTests
{
    private const string Term = "Kubernetes";

    [Fact]
    [Trait("Category", "Integration")]
    public Task PreviewStream_LeavesTheFinalTextToTheRecordingTranscribedWithTheDictionary() =>
        BoundedTest.RunAsync(async () =>
        {
            await using var fixture = await CreateStreamingFixtureAsync(streamsPrompt: false);
            var session = fixture.Plugin.StreamingSession!;
            fixture.Plugin.EnqueueText("Batch text with Kubernetes.");

            var result = await DictateAsync(fixture, session);

            Assert.Equal("ready", result.Status);
            Assert.Equal("Batch text with Kubernetes.", result.Text);
            Assert.Equal([null], fixture.Plugin.StreamingPrompts);
            AssertCarriesTerm(Assert.Single(fixture.Plugin.ReceivedPrompts));
            Assert.Equal(0, session.FinalizeCount);
            Assert.True(session.Disposed);
        });

    [Fact]
    [Trait("Category", "Integration")]
    public Task FullStream_CarriesTheDictionaryAndSuppliesTheFinalText() =>
        BoundedTest.RunAsync(async () =>
        {
            await using var fixture = await CreateStreamingFixtureAsync(streamsPrompt: true);
            var session = fixture.Plugin.StreamingSession!;

            var result = await DictateAsync(fixture, session);

            Assert.Equal("ready", result.Status);
            Assert.Equal("Streamed text.", result.Text);
            AssertCarriesTerm(Assert.Single(fixture.Plugin.StreamingPrompts));
            Assert.Equal(0, fixture.Plugin.TranscriptionCount);
            Assert.True(session.FinalizeCount > 0);
        });

    private static async Task<OrchestratorCompositionFixture> CreateStreamingFixtureAsync(bool streamsPrompt)
    {
        var fixture = new OrchestratorCompositionFixture();
        fixture.Plugin.SupportsStreaming = true;
        fixture.Plugin.SupportsStructuredDictionaryTerms = true;
        fixture.Plugin.StreamingSession = new ScriptedStreamingSession("Streamed text.");
        fixture.Plugin.StreamsPrompt = prompt => streamsPrompt || prompt is null;
        fixture.Plugin.PreviewsPrompt = prompt => !streamsPrompt && prompt is not null;
        fixture.Provider.GetRequiredService<IDictionaryService>().AddEntry(
            new DictionaryEntry { Id = "term", EntryType = DictionaryEntryType.Term, Original = Term }
        );
        fixture.Settings.Update(current => current with
        {
            LiveTranscriptionEnabled = true,
            LiveTranscriptionStreamingEnabled = true,
        });
        // A recording only streams on an engine that is already active.
        await fixture.Provider.GetRequiredService<ModelManagerService>()
            .LoadModelAsync(fixture.Settings.Current.SelectedModelId!);
        return fixture;
    }

    private static async Task<DictationSessionResult> DictateAsync(
        OrchestratorCompositionFixture fixture,
        ScriptedStreamingSession session
    )
    {
        var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
        Assert.True(sessionId > 0);
        fixture.FeedNonSilentAudio();
        await BoundedTest.WaitAsync(session.FirstAudio.Task);
        var resultTask = fixture.WaitForResultAsync(sessionId);
        await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
        return await BoundedTest.WaitAsync(resultTask);
    }

    private static void AssertCarriesTerm(string? prompt) =>
        Assert.Equal([Term], PluginTranscriptionPrompt.Parse(prompt).DictionaryTerms);
}
