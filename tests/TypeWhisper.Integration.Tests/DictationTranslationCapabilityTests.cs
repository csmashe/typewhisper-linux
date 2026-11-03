using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Integration.Tests.TestDoubles;
using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Integration.Tests;

/// <summary>
///     A translate task on a model that declares it cannot translate is refused before the model
///     loads (after capture for an ordinary start, before capture for a forced profile), keeps the
///     capture for retry, and never reaches the engine through the live preview either.
/// </summary>
public sealed class DictationTranslationCapabilityTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public Task TranslateOnModelThatCannotTranslate_FailsBeforeLoadAndKeepsCaptureForRetry() =>
        BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();
            DeclareTranslation(fixture, false);
            fixture.Settings.Update(current => current with { TranscriptionTask = "translate" });
            var rejection = fixture.Provider.GetRequiredService<ModelManagerService>()
                .GetTranslationRejection(fixture.Settings.Current.SelectedModelId);
            Assert.NotNull(rejection);

            var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync());
            Assert.True(sessionId > 0);
            fixture.FeedNonSilentAudio();
            var resultTask = fixture.WaitForResultAsync(sessionId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            var result = await BoundedTest.WaitAsync(resultTask);

            Assert.Equal("failed", result.Status);
            Assert.Equal(rejection, result.Message);
            Assert.Equal((0, 0), (fixture.Plugin.LoadCount, fixture.Plugin.TranscriptionCount));
            var record = Assert.Single(fixture.History.Records);
            Assert.Equal(TranscriptionRecordStatus.TranscriptionFailed, record.Status);
            Assert.Equal(rejection, record.FailureMessage);
            Assert.Equal("translate", record.TranscriptionTaskUsed);
            Assert.NotNull(record.AudioFileName);

            // Retry keeps the recording's task, so it is refused the same way until the model can run it.
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Orchestrator.RetryFromHistoryAsync(record.Id));
            Assert.Equal(rejection, error.Message);
            Assert.Equal((0, 0), (fixture.Plugin.LoadCount, fixture.Plugin.TranscriptionCount));

            DeclareTranslation(fixture, true);
            fixture.Plugin.EnqueueText("translated");
            var recovered = await BoundedTest.WaitAsync(fixture.Orchestrator.RetryFromHistoryAsync(record.Id));
            Assert.Equal(TranscriptionRecordStatus.Succeeded, recovered.Status);
            Assert.Equal([true], fixture.Plugin.ReceivedTranslations);
        });

    [Fact]
    [Trait("Category", "Integration")]
    public Task ForcedProfileTranslateOnModelThatCannotTranslate_RefusesBeforeCapture() =>
        BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();
            DeclareTranslation(fixture, false);
            fixture.Provider.GetRequiredService<IProfileService>().AddProfile(new Profile
            {
                Id = "forced-translate", Name = "Forced translate", SelectedTask = "translate",
            });

            var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync("forced-translate"));

            Assert.True(sessionId <= 0);
            Assert.False(fixture.Orchestrator.IsRecording);
            Assert.Equal((0, 0), (fixture.Plugin.LoadCount, fixture.Plugin.TranscriptionCount));
            Assert.Empty(fixture.History.Records);
        });

    [Fact]
    [Trait("Category", "Integration")]
    public Task ForcedProfileTranscribe_StillStartsWithTheSameModel() =>
        BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();
            DeclareTranslation(fixture, false);
            fixture.Settings.Update(current => current with { TranscriptionTask = "translate" });
            fixture.Provider.GetRequiredService<IProfileService>().AddProfile(new Profile
            {
                Id = "forced-transcribe", Name = "Forced transcribe", SelectedTask = "transcribe",
            });
            fixture.Plugin.EnqueueText("hello");

            var sessionId = await BoundedTest.WaitAsync(fixture.Orchestrator.StartAsync("forced-transcribe"));
            Assert.True(sessionId > 0);
            fixture.FeedNonSilentAudio();
            var resultTask = fixture.WaitForResultAsync(sessionId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            var result = await BoundedTest.WaitAsync(resultTask);

            Assert.Equal("ready", result.Status);
            Assert.Equal([false], fixture.Plugin.ReceivedTranslations);
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Integration")]
    public Task LivePreview_FollowsTheRecordingTaskAndSkipsAModelThatCannotTranslate(bool modelTranslates) =>
        BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();
            DeclareTranslation(fixture, modelTranslates);
            fixture.Settings.Update(current => current with
            {
                LiveTranscriptionEnabled = true,
                OnlineAsrBatchLiveTranscriptionEnabled = true,
                TranscriptionTask = "translate",
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
            var previewCalls = fixture.Plugin.TranscriptionCount;
            var resultTask = fixture.WaitForResultAsync(sessionId);
            await BoundedTest.WaitAsync(fixture.Orchestrator.StopAsync());
            var result = await BoundedTest.WaitAsync(resultTask);

            if (modelTranslates)
            {
                Assert.True(previewCalls > 0);
                Assert.Equal("ready", result.Status);
                Assert.All(fixture.Plugin.ReceivedTranslations, Assert.True);
            }
            else
            {
                Assert.Equal(0, previewCalls);
                Assert.Equal("failed", result.Status);
                Assert.Equal(0, fixture.Plugin.TranscriptionCount);
            }
        });

    private static void DeclareTranslation(OrchestratorCompositionFixture fixture, bool supported)
    {
        fixture.Plugin.SupportsTranslation = supported;
        fixture.Plugin.TranscriptionModels =
        [
            new PluginModelInfo(RecordingTranscriptionPlugin.ModelId, "Scripted model")
            {
                SupportsTranslation = supported,
            },
        ];
    }
}
