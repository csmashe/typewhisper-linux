using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class DictationOrchestratorLiveCleanupTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"CleanupTimeoutSeconds\":5}")]
    public void ExistingSettings_DefaultToOffAndHaveNoFinalTimeout(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.False(settings.LiveCleanupEnabled);
        Assert.DoesNotContain("CleanupTimeoutSeconds", JsonSerializer.Serialize(settings));
        Assert.False(new AppSettings().LiveCleanupEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scheduling_OffOrUnresolvedPolicy_ReturnsBeforeOtherServices(bool enabled)
    {
        // Every other dependency is absent. Touching audio, actions, profiles, language
        // resolution or the draft lock before these guards fails this test.
        var sut = (DictationOrchestrator)RuntimeHelpers.GetUninitializedObject(typeof(DictationOrchestrator));
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(s => s.Current).Returns(new AppSettings { LiveCleanupEnabled = enabled });
        typeof(DictationOrchestrator).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sut, settings.Object);
        typeof(DictationOrchestrator).GetMethod("ScheduleLiveCleanup", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sut, [0, null, "private provisional words"]);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task FinalPipeline_OffHitAndMiss_CountRequestsAndPreserveNormalization(bool enabled, bool match)
    {
        var provider = new Provider();
        var manager = TestPluginManagerFactory.Create([provider]);
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(s => s.Current).Returns(new AppSettings());
        var prompts = new PromptProcessingService(manager, settings.Object, new MemoryService(manager));
        var cleanup = new LlmCleanupService(new CleanupService(), prompts);
        var pipeline = new PostProcessingPipeline();
        var options = new PipelineOptions
        {
            NormalizeSpokenLineBreaks = true,
            NormalizeSpokenPunctuation = true,
            ConfiguredLanguage = "en",
            DetectedLanguage = "en",
            TranscriptionNumberNormalizationEnabled = true,
            AppFormatter = AppFormatterService.Format,
        };
        const string raw = "send twenty three files question mark new line tomorrow";
        using var draft = enabled ? new LiveCleanupDraft(cleanup, CleanupLevel.Medium, "en",
            prompts.GetCleanupProviderSelection()!, async (text, token) =>
                (await pipeline.ProcessAsync(text, options, token)).Text, (_, _) => { }, TimeSpan.Zero) : null;
        if (draft is not null)
        {
            draft.Submit(raw);
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            draft.StopScheduling();
        }
        var finalOptions = options with
        {
            CleanupHandler = (text, token) => DictationOrchestrator.CompleteCleanupAsync(
                cleanup, prompts, draft, text, CleanupLevel.Medium, "en", null, null, token),
        };
        var final = await pipeline.ProcessAsync(match ? raw : raw + " morning", finalOptions)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains("23", provider.LastInput);
        Assert.Contains("?", provider.LastInput);
        Assert.DoesNotContain("question mark", provider.LastInput);
        Assert.DoesNotContain("new line", provider.LastInput);
        Assert.Equal(enabled && !match ? 2 : 1, provider.Calls);
        Assert.Equal(enabled && match, draft?.Reused ?? false);
        Assert.Contains("23", final.Text);
    }

    [Fact]
    public async Task MismatchedDraft_IgnoringProviderAndBlockingCancellation_DoNotDelayFinal()
    {
        using var releaseCallback = new ManualResetEventSlim();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Provider
        {
            Process = async (text, token) =>
            {
                if (text.Contains("obsolete", StringComparison.OrdinalIgnoreCase))
                {
                    using var registration = token.Register(() => releaseCallback.Wait());
                    entered.SetResult(token);
                    return await release.Task;
                }
                return "Final result";
            },
        };
        var manager = TestPluginManagerFactory.Create([provider]);
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(s => s.Current).Returns(new AppSettings());
        var prompts = new PromptProcessingService(manager, settings.Object, new MemoryService(manager));
        var cleanup = new LlmCleanupService(new CleanupService(), prompts);
        using var draft = new LiveCleanupDraft(cleanup, CleanupLevel.Medium, "en",
            prompts.GetCleanupProviderSelection()!, (text, _) => Task.FromResult(text), (_, _) => { }, TimeSpan.Zero);
        try
        {
            draft.Submit("obsolete");
            var draftToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            draft.StopScheduling();
            Assert.Equal("Final result", await DictationOrchestrator.CompleteCleanupAsync(
                cleanup, prompts, draft, "corrected final", CleanupLevel.Medium, "en", null, null, default)
                .WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.True(draftToken.IsCancellationRequested);
            Assert.Equal(2, provider.Calls);
        }
        finally { releaseCallback.Set(); release.TrySetResult("obsolete result"); }
    }

    private sealed class Provider : ILlmProviderPlugin
    {
        public int Calls;
        public string LastInput = "";
        public Func<string, CancellationToken, Task<string>>? Process { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string PluginId => "test.live";
        public string PluginName => "Live test";
        public string PluginVersion => "1";
        public string ProviderName => "Live test";
        public bool IsAvailable => true;
        public IReadOnlyList<PluginModelInfo> SupportedModels => [new("model", "Model")];
        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastInput = userText;
            Entered.TrySetResult();
            return Process?.Invoke(userText, ct) ?? Task.FromResult(userText);
        }
        public void Dispose() { }
    }
}
