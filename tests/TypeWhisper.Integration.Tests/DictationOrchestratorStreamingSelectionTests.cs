using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Integration.Tests.TestDoubles;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Integration.Tests;

public sealed class DictationOrchestratorStreamingSelectionTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public Task Start_SkipsStreaming_WhenPluginSettingsChangedTheEngineSelection() =>
        BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();
            fixture.Plugin.SupportsStreaming = true;
            fixture.Settings.Update(current => current with
            {
                LiveTranscriptionEnabled = true,
                LiveTranscriptionStreamingEnabled = true,
            });
            // A recording only streams on an engine that is already active.
            await fixture.Provider.GetRequiredService<ModelManagerService>()
                .LoadModelAsync(fixture.Settings.Current.SelectedModelId!);

            Assert.True(await DictateAsync(fixture));

            // A plugin settings save selects another model behind the active one.
            fixture.Plugin.SelectModel("other-model");

            Assert.False(await DictateAsync(fixture));
            Assert.Equal(RecordingTranscriptionPlugin.ModelId, fixture.Plugin.SelectedModelId);
        });

    /// <summary>Runs one dictation and returns whether it opened a streaming session.</summary>
    private static async Task<bool> DictateAsync(OrchestratorCompositionFixture fixture)
    {
        // The double cannot stream, so every dictation ends in a batch transcription.
        fixture.Plugin.EnqueueText("dictated");
        var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
        var streaming = StreamingCoordinator(fixture.Orchestrator) is not null;
        fixture.FeedNonSilentAudio();
        var resultTask = fixture.WaitForResultAsync(sessionId);
        await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
        Assert.Equal("ready", (await BoundedTest.WaitAsync(resultTask)).Status);
        return streaming;
    }

    private static object? StreamingCoordinator(DictationOrchestrator orchestrator)
    {
        const string name = "_streamingCoordinator";
        var field = typeof(DictationOrchestrator).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic
        ) ?? throw new MissingFieldException(typeof(DictationOrchestrator).FullName, name);
        return field.GetValue(orchestrator);
    }
}
