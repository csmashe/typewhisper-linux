using Microsoft.Extensions.DependencyInjection;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Models;
using TypeWhisper.Linux.Services;
using Xunit;

namespace TypeWhisper.Integration.Tests;

public sealed class DictationOrchestratorAutoEnterTests
{
    [Theory]
    [InlineData(ProfileAutoEnterMode.SpokenCommand, "hello there", "hello there", 0)]
    [InlineData(ProfileAutoEnterMode.SpokenCommand, "hello there press enter", "hello there", 1)]
    [InlineData(ProfileAutoEnterMode.Never, "hello there press enter", "hello there press enter", 0)]
    [InlineData(ProfileAutoEnterMode.Always, "hello there", "hello there", 1)]
    [InlineData(ProfileAutoEnterMode.Always, "hello there press enter", "hello there", 1)]
    [Trait("Category", "Integration")]
    public Task Dictation_PressesEnterAccordingToTheMatchedProfile(
        ProfileAutoEnterMode mode, string transcript, string expectedText, int expectedEnters)
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = CreateFixture(mode);

            var result = await DictateAsync(fixture, transcript);

            Assert.Equal("ready", result.Status);
            Assert.Equal(expectedText, result.Text);
            Assert.Equal(expectedEnters, fixture.InsertionPlatform.EnterCount);
        });
    }

    [Fact]
    [Trait("Category", "Integration")]
    public Task Always_DoesNotSubmitWhenAPostProcessingStepFailed()
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = CreateFixture(ProfileAutoEnterMode.Always, new FailingStepPipeline());

            var result = await DictateAsync(fixture, "hello there");

            Assert.Equal("hello there", result.Text);
            Assert.Equal(0, fixture.InsertionPlatform.EnterCount);
        });
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [Trait("Category", "Integration")]
    public Task Always_DoesNotSubmitWhenCleanupFellBackToLight(bool providerFails, int expectedEnters)
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = CreateFixture(ProfileAutoEnterMode.Always, cleanup: CleanupLevel.Medium);
            fixture.Settings.Update(current => current with
            {
                DefaultLlmProvider = "plugin:integration.scripted-llm:scripted-llm-model",
            });
            if (providerFails)
                fixture.Llm.Failure = new InvalidOperationException("provider failed");
            else
                fixture.Llm.EnqueueStream("Hello there.");

            var result = await DictateAsync(fixture, "hello there");

            Assert.False(string.IsNullOrWhiteSpace(result.Text));
            Assert.Equal(expectedEnters, fixture.InsertionPlatform.EnterCount);
        });
    }

    [Theory]
    [InlineData(ProfileAutoEnterMode.SpokenCommand, "hello there")]
    [InlineData(ProfileAutoEnterMode.Never, "hello there press enter")]
    [Trait("Category", "Integration")]
    public Task ProcessText_HandlesPressEnterLikeDictation(ProfileAutoEnterMode mode, string expectedText)
    {
        return BoundedTest.RunAsync(async () =>
        {
            await using var fixture = CreateFixture(mode);

            var result = await fixture.Orchestrator.ProcessTextAsync(
                new TextProcessingRequest("hello there press enter", ProfileId: "chat"));

            Assert.Equal(expectedText, result.Text);
        });
    }

    private static OrchestratorCompositionFixture CreateFixture(
        ProfileAutoEnterMode mode, IPostProcessingPipeline? pipeline = null, CleanupLevel? cleanup = null)
    {
        var fixture = new OrchestratorCompositionFixture(focusedApp: ("chat-app", "Chat"), pipeline: pipeline);
        fixture.Settings.Update(current => current.WithLanguageHints(["en"]));
        fixture.Provider.GetRequiredService<IProfileService>().AddProfile(new Profile
        {
            Id = "chat", Name = "Chat", ProcessNames = ["chat-app"], AutoEnterMode = mode,
            CleanupLevelOverride = cleanup,
        });
        return fixture;
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

    private sealed class FailingStepPipeline : IPostProcessingPipeline
    {
        public async Task<PostProcessingResult> ProcessAsync(string rawText, PipelineOptions options, CancellationToken ct = default)
        {
            var result = await new PostProcessingPipeline().ProcessAsync(rawText, options, ct);
            return result with
            {
                Steps = [.. result.Steps, new PostProcessingStepResult(PostProcessingStepNames.Llm, false, false, "provider failed")],
            };
        }
    }
}
