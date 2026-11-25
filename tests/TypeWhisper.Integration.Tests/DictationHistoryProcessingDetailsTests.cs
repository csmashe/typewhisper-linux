using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using Xunit;

namespace TypeWhisper.Integration.Tests;

public sealed class DictationHistoryProcessingDetailsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PromptAction_RecordsLlmAndTimings_AndKeepsPromptsOnlyWithProvenance(bool captureProvenance) =>
        BoundedTest.RunAsync(async () =>
        {
            await using var fixture = new OrchestratorCompositionFixture();
            fixture.Settings.Save(fixture.Settings.Current with { CaptureLlmProvenance = captureProvenance });
            AddPromptProfile(fixture);
            fixture.Llm.EnqueueStream("Rewritten ", "text");
            fixture.Plugin.EnqueueText("spoken text");

            await DictateAsync(fixture, "profile");

            var record = Assert.Single(fixture.History.Records);
            Assert.Equal(TranscriptionRecordStatus.Succeeded, record.Status);
            Assert.True(record.TranscriptionSeconds >= 0);
            Assert.Equal(fixture.Llm.ProviderName, record.LlmProvider);
            Assert.Equal("scripted-llm-model", record.LlmModel);
            Assert.True(record.LlmSeconds >= 0);
            Assert.Equal(captureProvenance ? 1 : 0, record.LlmCalls.Count);
        });

    [Fact]
    public Task PlainDictation_RecordsTranscriptionTimeButNoLlm() => BoundedTest.RunAsync(async () =>
    {
        await using var fixture = new OrchestratorCompositionFixture();
        fixture.Plugin.EnqueueText("spoken text");

        await DictateAsync(fixture);

        var record = Assert.Single(fixture.History.Records);
        Assert.True(record.TranscriptionSeconds >= 0);
        Assert.Null(record.LlmProvider);
        Assert.Null(record.LlmModel);
        Assert.Null(record.LlmSeconds);
    });

    [Fact]
    public Task TranscriptionTime_CountsWaitsBetweenTheRecordingEndAndTheDecode() => BoundedTest.RunAsync(async () =>
    {
        await using var fixture = new OrchestratorCompositionFixture();
        fixture.Orchestrator.RecordingCaptured += (_, _) => Thread.Sleep(300);
        fixture.Plugin.EnqueueText("spoken text");

        await DictateAsync(fixture);

        var record = Assert.Single(fixture.History.Records);
        Assert.True(record.TranscriptionSeconds >= 0.3, $"Recorded {record.TranscriptionSeconds} s.");
    });

    [Fact]
    public Task FailedPromptAction_NamesTheLlmButRecordsNoLlmTime() => BoundedTest.RunAsync(async () =>
    {
        await using var fixture = new OrchestratorCompositionFixture();
        AddPromptProfile(fixture);
        fixture.Llm.Failure = new InvalidOperationException("processing failed");
        fixture.Plugin.EnqueueText("spoken text");

        await DictateAsync(fixture, "profile");

        var record = Assert.Single(fixture.History.Records);
        Assert.Equal(fixture.Llm.ProviderName, record.LlmProvider);
        Assert.Null(record.LlmSeconds);
    });

    [Fact]
    public Task Retry_RecordsFreshTimings() => BoundedTest.RunAsync(async () =>
    {
        await using var fixture = new OrchestratorCompositionFixture();
        fixture.Plugin.EnqueueFailure("provider unavailable");
        await DictateAsync(fixture);
        var failed = Assert.Single(fixture.History.Records);
        Assert.Null(failed.TranscriptionSeconds);

        fixture.Plugin.EnqueueText("recovered");
        var recovered = await fixture.Orchestrator.RetryFromHistoryAsync(failed.Id);

        Assert.True(recovered.TranscriptionSeconds >= 0);
        Assert.Null(recovered.LlmProvider);
    });

    [Fact]
    public Task RetryWhosePromptFails_RecordsTheRetrysTimings() => BoundedTest.RunAsync(async () =>
    {
        await using var fixture = new OrchestratorCompositionFixture();
        fixture.Settings.Save(fixture.Settings.Current with { CaptureLlmProvenance = true });
        AddPromptProfile(fixture);
        fixture.Plugin.EnqueueFailure("provider unavailable");
        await DictateAsync(fixture, "profile");
        var failed = Assert.Single(fixture.History.Records);
        Assert.Null(failed.TranscriptionSeconds);

        fixture.Llm.Failure = new InvalidOperationException("processing failed");
        fixture.Plugin.EnqueueText("new raw text");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator.RetryFromHistoryAsync(failed.Id));

        var retried = Assert.Single(fixture.History.Records);
        Assert.Equal(TranscriptionRecordStatus.ProcessingFailed, retried.Status);
        Assert.Equal("new raw text", retried.RawText);
        Assert.True(retried.TranscriptionSeconds >= 0);
        Assert.Equal(fixture.Llm.ProviderName, retried.LlmProvider);
        Assert.Null(retried.LlmSeconds);
        Assert.NotEmpty(retried.LlmCalls);
    });

    [Fact]
    public Task RetryWhosePromptFails_KeepsTheLlmDetailsOfTheKeptFinalText() => BoundedTest.RunAsync(async () =>
    {
        await using var fixture = new OrchestratorCompositionFixture();
        fixture.Settings.Save(fixture.Settings.Current with { CaptureLlmProvenance = true });
        AddPromptProfile(fixture);
        fixture.Llm.EnqueueStream("Rewritten ", "text");
        fixture.Plugin.EnqueueText("spoken text");
        await DictateAsync(fixture, "profile");
        var original = Assert.Single(fixture.History.Records);
        var originalCall = Assert.Single(original.LlmCalls);
        Assert.NotNull(original.LlmSeconds);

        fixture.Llm.Failure = new InvalidOperationException("processing failed");
        fixture.Plugin.EnqueueText("new raw text");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator.RetryFromHistoryAsync(original.Id));

        var retried = Assert.Single(fixture.History.Records);
        Assert.Equal("new raw text", retried.RawText);
        Assert.Equal(original.FinalText, retried.FinalText);
        Assert.Equal(original.LlmSeconds, retried.LlmSeconds);
        Assert.Equal(originalCall.ResponseReceived, Assert.Single(retried.LlmCalls).ResponseReceived);
    });

    private static void AddPromptProfile(OrchestratorCompositionFixture fixture)
    {
        fixture.Provider.GetRequiredService<IPromptActionService>().AddAction(new PromptAction
        {
            Id = "prompt", Name = "Prompt", SystemPrompt = "Rewrite the input.",
            ProviderOverride = "plugin:integration.scripted-llm:scripted-llm-model",
        });
        fixture.Provider.GetRequiredService<IProfileService>().AddProfile(new Profile
        {
            Id = "profile", Name = "Profile", PromptActionId = "prompt",
        });
    }

    private static async Task DictateAsync(OrchestratorCompositionFixture fixture, string? profileId = null)
    {
        Assert.True(await fixture.Orchestrator.StartAsync(profileId) > 0);
        fixture.FeedNonSilentAudio();
        await fixture.Orchestrator.StopAsync();
    }
}
