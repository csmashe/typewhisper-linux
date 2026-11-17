using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

namespace TypeWhisper.Linux.Tests;

public sealed class LlmCleanupServiceTests
{
    [Fact]
    public async Task CleanAsync_Deadline_CancelsProviderAndReturnsLight()
    {
        var provider = new FakeLlmProviderPlugin("unused")
        {
            Process = async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "unreachable";
            },
        };
        var sut = CreateService([provider]);
        var statuses = new List<string>();
        var capture = new LlmCallCapture();

        var result = await sut.CleanAsync("uh hello", CleanupLevel.Medium,
            message => { statuses.Add(message); return Task.CompletedTask; },
            capture: capture, isDraft: true, draftTimeout: TimeSpan.FromMilliseconds(50),
            referenceContext: "ProjectIris").WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("Hello", result);
        Assert.True(provider.LastToken.IsCancellationRequested);
        Assert.DoesNotContain(statuses, status => status.Contains("wait limit"));
        var call = Assert.Single(capture.Calls);
        Assert.Contains("ProjectIris", provider.LastSystemPrompt);
        Assert.Equal(provider.LastSystemPrompt, call.SystemPromptSent);
        Assert.True(string.IsNullOrEmpty(call.ResponseReceived));
    }

    [Fact]
    public async Task CleanAsync_ProviderIgnoresCancellation_DoesNotBlockOtherCleanup()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeLlmProviderPlugin("unused") { Process = _ => release.Task };
        var sut = CreateService([provider]);
        try
        {
            Assert.Equal("Hello", await sut.CleanAsync("uh hello", CleanupLevel.High,
                isDraft: true, draftTimeout: TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(2)));
            var draftToken = provider.LastToken;
            provider.Process = _ => Task.FromResult("Final cleanup");
            Assert.Equal("Final cleanup", await sut.CleanAsync("uh world", CleanupLevel.Medium, ct: CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
            Assert.Equal(2, provider.ProcessCalls);
            Assert.True(draftToken.IsCancellationRequested);
        }
        finally
        {
            release.TrySetResult("late result must not be inserted");
        }
    }

    [Fact]
    public async Task CleanAsync_DefaultHasNoDeadline()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeLlmProviderPlugin("unused")
        {
            Process = _ => { entered.SetResult(); return release.Task; },
        };
        var cleanup = CreateService([provider]).CleanAsync("hello", CleanupLevel.High);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(TimeSpan.FromSeconds(5.2));
            Assert.False(cleanup.IsCompleted);
            Assert.False(provider.LastToken.IsCancellationRequested);
            release.SetResult("Final cleanup");
            Assert.Equal("Final cleanup", await cleanup);
        }
        finally { release.TrySetResult("released"); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DraftFailure_DoesNotLogOrSuppressFinalFailure(bool configurationFailure)
    {
        var provider = new FakeLlmProviderPlugin("unused")
        {
            IsAvailable = !configurationFailure,
            ProcessException = new InvalidOperationException("Provider failed."),
        };
        var entries = new List<string>();
        var sut = CreateService([provider], RecordingErrorLog(entries),
            new AppSettings { DefaultLlmProvider = "plugin:com.test.cleanup:model-a" });
        await sut.CleanAsync("hello", CleanupLevel.High, isDraft: true);
        Assert.Empty(entries);
        await sut.CleanAsync("hello", CleanupLevel.High);
        Assert.Single(entries);
        await sut.CleanAsync("hello", CleanupLevel.High, isDraft: true);
        await sut.CleanAsync("hello", CleanupLevel.High);
        Assert.Single(entries);
    }

    [Fact]
    public async Task CleanAsync_DeadlineDuringRetryDelay_DoesNotRetryBeyondBudget()
    {
        var provider = new FakeLlmProviderPlugin("unused")
        {
            ProcessException = new PluginRequestException("network", PluginRequestFailureKind.Network,
                retryAfter: TimeSpan.FromSeconds(5)),
        };
        var sut = CreateService([provider]);

        Assert.Equal("Hello", await sut.CleanAsync("uh hello", CleanupLevel.Medium,
            isDraft: true, draftTimeout: TimeSpan.FromMilliseconds(50)).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, provider.ProcessCalls);
    }

    [Fact]
    public async Task CleanAsync_CallerCancelsUncooperativeProvider_ThrowsWithoutWaitingForProvider()
    {
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeLlmProviderPlugin("unused")
        {
            Process = _ => { entered.SetResult(); return release.Task; },
        };
        var sut = CreateService([provider]);
        var capture = new LlmCallCapture();
        try
        {
            var cleanup = sut.CleanAsync("uh hello", CleanupLevel.Medium,
                capture: capture, isDraft: true, draftTimeout: TimeSpan.FromSeconds(10), ct: caller.Token,
                referenceContext: "ProjectIris");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                cleanup.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
            Assert.True(provider.LastToken.IsCancellationRequested);
            var call = Assert.Single(capture.Calls);
            Assert.Contains("ProjectIris", provider.LastSystemPrompt);
            Assert.Equal(provider.LastSystemPrompt, call.SystemPromptSent);
            Assert.True(string.IsNullOrEmpty(call.ResponseReceived));
        }
        finally
        {
            release.TrySetResult("late result");
        }
    }

    [Fact]
    public async Task CleanAsync_ZeroBudget_DisablesDeadlineAndWaitsForRequestedCleanup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeLlmProviderPlugin("unused")
        {
            Process = _ => { entered.SetResult(); return release.Task; },
        };
        var sut = CreateService([provider]);
        var cleanup = sut.CleanAsync("uh hello", CleanupLevel.High, isDraft: true, draftTimeout: TimeSpan.Zero);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(cleanup.IsCompleted);
            release.SetResult("requested cleanup");
            Assert.Equal("requested cleanup", await cleanup.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            release.TrySetResult("requested cleanup");
        }
    }

    [Fact]
    public async Task CleanAsync_ProviderCancelsCallerThenReturnsSuccess_CallerCancellationWins()
    {
        using var caller = new CancellationTokenSource();
        var provider = new FakeLlmProviderPlugin("must not be inserted")
        {
            // ReSharper disable once AccessToDisposedClosure -- the provider runs inside the awaited CleanAsync, before the using disposes caller.
            BeforeProcess = _ => caller.Cancel(),
        };
        var sut = CreateService([provider]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.CleanAsync("uh hello", CleanupLevel.Medium, ct: caller.Token));
    }

    [Fact]
    public async Task CleanAsync_TransientFailureThenSuccess_RetriesOnce()
    {
        var attempts = 0;
        var provider = new FakeLlmProviderPlugin("clean result")
        {
            BeforeProcess = _ =>
            {
                if (++attempts == 1)
                    throw new PluginRequestException("network", PluginRequestFailureKind.Network, retryAfter: TimeSpan.Zero);
            },
        };
        var sut = CreateService([provider]);
        var capture = new LlmCallCapture();
        Assert.Equal("clean result", await sut.CleanAsync("uh hello", CleanupLevel.Medium,
            capture: capture, referenceContext: "ProjectIris"));
        Assert.Equal(2, attempts);
        Assert.Equal(2, provider.SystemPrompts.Count);
        Assert.Contains("ProjectIris", provider.SystemPrompts[0]);
        Assert.Equal(provider.SystemPrompts[0], provider.SystemPrompts[1]);
        var call = Assert.Single(capture.Calls);
        Assert.Equal(provider.SystemPrompts[1], call.SystemPromptSent);
        Assert.Equal("clean result", call.ResponseReceived);
    }

    [Fact]
    public async Task CleanAsync_FinalAndBudgetedDraftKeepTheirOwnReferenceAndProvenance()
    {
        var provider = new FakeLlmProviderPlugin("clean result");
        var sut = CreateService([provider]);
        var finalCapture = new LlmCallCapture();
        var draftCapture = new LlmCallCapture();

        Assert.Equal("clean result", await sut.CleanAsync("uh atlas", CleanupLevel.Medium,
            capture: finalCapture, referenceContext: "CloudAtlas"));
        Assert.Equal("clean result", await sut.CleanAsync("uh iris", CleanupLevel.High,
            capture: draftCapture, isDraft: true, draftTimeout: TimeSpan.FromSeconds(2),
            referenceContext: "ProjectIris"));

        Assert.Equal(2, provider.SystemPrompts.Count);
        Assert.StartsWith(CleanupService.MediumSystemPrompt, provider.SystemPrompts[0]);
        Assert.Contains("CloudAtlas", provider.SystemPrompts[0]);
        Assert.DoesNotContain("ProjectIris", provider.SystemPrompts[0]);
        Assert.StartsWith(CleanupService.HighSystemPrompt, provider.SystemPrompts[1]);
        Assert.Contains("ProjectIris", provider.SystemPrompts[1]);
        Assert.DoesNotContain("CloudAtlas", provider.SystemPrompts[1]);
        var finalCall = Assert.Single(finalCapture.Calls);
        var draftCall = Assert.Single(draftCapture.Calls);
        Assert.Equal(provider.SystemPrompts[0], finalCall.SystemPromptSent);
        Assert.Equal(provider.SystemPrompts[1], draftCall.SystemPromptSent);
        Assert.Equal(PromptProcessingService.FormatPromptActionInput("Atlas"), finalCall.UserPromptSent);
        Assert.Equal(PromptProcessingService.FormatPromptActionInput("Iris"), draftCall.UserPromptSent);
    }

    [Theory]
    [InlineData(CleanupLevel.None, "uh project iris")]
    [InlineData(CleanupLevel.Light, "Project iris")]
    public async Task CleanAsync_LocalLevelsIgnoreReferenceWithoutConsultingPromptServices(
        CleanupLevel level, string expected)
    {
        var provider = new FakeLlmProviderPlugin("must not be called") { ThrowOnProcess = true };
        using var manager = TestPluginManagerFactory.Create([provider]);
        var settings = new Mock<ISettingsService>(MockBehavior.Strict);
        var prompts = new PromptProcessingService(manager, settings.Object, new MemoryService(manager));
        var sut = new LlmCleanupService(new CleanupService(), prompts);
        var capture = new LlmCallCapture();

        Assert.Equal(expected, await sut.CleanAsync("uh project iris", level,
            capture: capture, referenceContext: "Clipboard text: ProjectIris"));

        Assert.Equal(0, provider.ProcessCalls);
        Assert.Empty(capture.Calls);
        settings.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CompleteCleanupAsync_NewReferenceDiscardsOtherwiseMatchingCachedDraft()
    {
        var results = new Queue<string>(["Draft without reference.", "Final with reference."]);
        var provider = new FakeLlmProviderPlugin("unused")
        {
            Process = _ => Task.FromResult(results.Dequeue()),
        };
        using var manager = TestPluginManagerFactory.Create([provider]);
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings
        {
            DefaultLlmProvider = "plugin:com.test.cleanup:model-a",
        });
        var prompts = new PromptProcessingService(manager, settings.Object, new MemoryService(manager));
        var cleanup = new LlmCleanupService(new CleanupService(), prompts);
        var published = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var selection = Assert.IsType<string>(prompts.GetCleanupProviderSelection());
        using var draft = new LiveCleanupDraft(cleanup, CleanupLevel.Medium, "en", selection,
            (text, _) => Task.FromResult(text), (_, text) => published.TrySetResult(text), TimeSpan.Zero);
        draft.Submit("uh project iris");
        Assert.Equal("Draft without reference.", await published.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        draft.StopScheduling();
        Assert.True(draft.MatchesFinal(CleanupLevel.Medium, selection));
        var capture = new LlmCallCapture();

        var result = await DictationOrchestrator.CompleteCleanupAsync(
            cleanup, prompts, draft, "uh project iris", CleanupLevel.Medium, capture, null,
            CancellationToken.None, referenceContext: "ProjectIris");

        Assert.Equal("Final with reference.", result);
        Assert.False(draft.Reused);
        Assert.Equal(2, provider.ProcessCalls);
        Assert.DoesNotContain("<reference_context>", provider.SystemPrompts[0]);
        Assert.Contains("ProjectIris", provider.SystemPrompts[1]);
        var call = Assert.Single(capture.Calls);
        Assert.Equal(provider.SystemPrompts[1], call.SystemPromptSent);
        Assert.Equal("Final with reference.", call.ResponseReceived);
        Assert.Null(await draft.ReuseAsync("uh project iris", null, CancellationToken.None));
    }

    [Fact]
    public async Task CleanAsync_Light_UsesDeterministicCleanup()
    {
        var sut = CreateService([]);

        var result = await sut.CleanAsync("uh hello", CleanupLevel.Light);

        Assert.Equal("Hello", result);
    }

    [Fact]
    public async Task CleanAsync_Light_KeepsBareUm_ButStripsDoubledUmm()
    {
        var sut = CreateService([]);

        // Bare "um" is a real word in German/Dutch/Swedish/Danish/Portuguese, so the
        // language-agnostic deterministic pass deliberately leaves it alone and only strips the
        // doubled spelling that collides with nothing (audit §1 M4).
        Assert.Equal("Um hello", await sut.CleanAsync("um hello", CleanupLevel.Light));
        Assert.Equal("Hello", await sut.CleanAsync("umm hello", CleanupLevel.Light));
    }

    [Fact]
    public async Task CleanAsync_Medium_UsesConfiguredLlmPrompt()
    {
        var provider = new FakeLlmProviderPlugin("polished text");
        var sut = CreateService([provider]);

        var result = await sut.CleanAsync("uh hello", CleanupLevel.Medium);

        Assert.Equal("polished text", result);
        Assert.Equal(CleanupService.MediumSystemPrompt, provider.LastSystemPrompt);
        Assert.Equal(PromptProcessingService.FormatPromptActionInput("Hello"), provider.LastUserText);
    }

    [Fact]
    public async Task CleanAsync_High_UsesConfiguredLlmPrompt()
    {
        var provider = new FakeLlmProviderPlugin("concise text");
        var sut = CreateService([provider]);

        var result = await sut.CleanAsync("uh hello there", CleanupLevel.High);

        Assert.Equal("concise text", result);
        Assert.Equal(CleanupService.HighSystemPrompt, provider.LastSystemPrompt);
        Assert.Equal(PromptProcessingService.FormatPromptActionInput("Hello there"), provider.LastUserText);
    }

    [Fact]
    public async Task CleanAsync_Medium_FallsBackToLightWhenNoProviderAvailable()
    {
        var statuses = new List<string>();
        var sut = CreateService([]);

        var result = await sut.CleanAsync(
            "uh hello",
            CleanupLevel.Medium,
            message =>
            {
                statuses.Add(message);
                return Task.CompletedTask;
            }
        );

        Assert.Equal("Hello", result);
        Assert.Contains("Cleanup provider unavailable. Using Light cleanup.", statuses);
    }

    [Fact]
    public async Task CleanAsync_Medium_FallsBackToLightWhenProviderFails()
    {
        var statuses = new List<string>();
        var provider = new FakeLlmProviderPlugin("unused") { ThrowOnProcess = true };
        var sut = CreateService([provider]);

        var result = await sut.CleanAsync(
            "uh hello",
            CleanupLevel.Medium,
            message =>
            {
                statuses.Add(message);
                return Task.CompletedTask;
            }
        );

        Assert.Equal("Hello", result);
        Assert.Contains("Cleanup failed. Using Light cleanup.", statuses);
    }

    [Fact]
    public async Task CleanAsync_DependencyCancellationWithLiveCaller_FallsBackToLight()
    {
        var statuses = new List<string>();
        var provider = new FakeLlmProviderPlugin("unused")
        {
            ProcessException = new OperationCanceledException("provider canceled"),
        };
        var sut = CreateService([provider]);

        var result = await sut.CleanAsync(
            "uh hello",
            CleanupLevel.Medium,
            message =>
            {
                statuses.Add(message);
                return Task.CompletedTask;
            },
            ct: CancellationToken.None
        );

        Assert.Equal("Hello", result);
        Assert.Contains("Cleanup failed. Using Light cleanup.", statuses);
    }

    [Fact]
    public async Task CleanAsync_PrivateTimeout_FallsBackToLight()
    {
        var statuses = new List<string>();
        var provider = new FakeLlmProviderPlugin("unused")
        {
            ProcessException = new TimeoutException("provider deadline"),
        };
        var sut = CreateService([provider]);

        var result = await sut.CleanAsync(
            "uh hello",
            CleanupLevel.High,
            message =>
            {
                statuses.Add(message);
                return Task.CompletedTask;
            }
        );

        Assert.Equal("Hello", result);
        Assert.Contains("Cleanup failed. Using Light cleanup.", statuses);
    }

    [Fact]
    public async Task CleanAsync_GenuineCallerCancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var provider = new FakeLlmProviderPlugin("unused")
        {
            ProcessException = new OperationCanceledException(cts.Token),
        };
        var sut = CreateService([provider]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.CleanAsync("uh hello", CleanupLevel.Medium, ct: cts.Token));
    }

    [Fact]
    public async Task CleanAsync_DependencyFaultRacingCallerCancellation_CallerWins()
    {
        using var cts = new CancellationTokenSource();
        var provider = new FakeLlmProviderPlugin("unused")
        {
            // ReSharper disable once AccessToDisposedClosure -- the hook only runs inside the awaited
            // CleanAsync call below, which completes before the using-scope disposes cts.
            BeforeProcess = _ => cts.Cancel(),
            ProcessException = new HttpRequestException("provider fault"),
        };
        var sut = CreateService([provider]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.CleanAsync("uh hello", CleanupLevel.Medium, ct: cts.Token));
    }

    [Fact]
    public async Task CleanAsync_Medium_FallsBackToLightWhenUnavailableStatusCallbackFails()
    {
        var sut = CreateService([]);

        var result = await sut.CleanAsync(
            "uh hello",
            CleanupLevel.Medium,
            _ => throw new InvalidOperationException("Status failed.")
        );

        Assert.Equal("Hello", result);
    }

    [Fact]
    public async Task CleanAsync_Medium_FallsBackToLightWhenFailureStatusCallbackFails()
    {
        var provider = new FakeLlmProviderPlugin("unused") { ThrowOnProcess = true };
        var sut = CreateService([provider]);

        var result = await sut.CleanAsync(
            "uh hello",
            CleanupLevel.Medium,
            _ => throw new InvalidOperationException("Status failed.")
        );

        Assert.Equal("Hello", result);
    }

    [Fact]
    public async Task CleanAsync_LogsTheSameFailureAgain_AfterASuccessfulCleanup()
    {
        var provider = new FakeLlmProviderPlugin("polished text")
        {
            ProcessException = new InvalidOperationException("Provider failed."),
        };
        var entries = new List<string>();
        var sut = CreateService([provider], errorLog: RecordingErrorLog(entries));

        await sut.CleanAsync("uh hello", CleanupLevel.Medium);
        provider.ProcessException = null;
        await sut.CleanAsync("uh hello", CleanupLevel.Medium);
        provider.ProcessException = new InvalidOperationException("Provider failed.");
        await sut.CleanAsync("uh hello", CleanupLevel.Medium);

        Assert.Equal(2, entries.Count);
        Assert.All(
            entries,
            entry => Assert.Equal(
                "AI cleanup failed and fell back to Light cleanup: Provider failed.",
                entry
            )
        );
    }

    [Fact]
    public async Task CleanAsync_ConfigurationFailure_LogsExactlyOneEntry()
    {
        var signedOut = new FakeLlmProviderPlugin("unused") { IsAvailable = false };
        var entries = new List<string>();
        var errorLog = RecordingErrorLog(entries);
        var sut = CreateService(
            [signedOut],
            errorLog,
            new AppSettings { DefaultLlmProvider = "plugin:com.test.cleanup:model-a" }
        );

        var result = await sut.CleanAsync("uh hello", CleanupLevel.Medium);

        // PromptProcessingService already logged the provider problem; the wrapper would be the
        // same event described twice.
        Assert.Equal("Hello", result);
        Assert.Equal(
            Loc.Instance.GetString("Prompts.SelectedProviderUnavailable", "Cleanup Provider"),
            Assert.Single(entries)
        );
    }

    private static IErrorLogService RecordingErrorLog(List<string> entries)
    {
        var errorLog = new Mock<IErrorLogService>();
        errorLog
            .Setup(service => service.AddEntry(It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string>((message, _) => entries.Add(message));
        return errorLog.Object;
    }

    private static LlmCleanupService CreateService(
        IReadOnlyList<ILlmProviderRole> providers,
        IErrorLogService? errorLog = null,
        AppSettings? appSettings = null
    )
    {
        var pluginManager = TestPluginManagerFactory.Create(providers);
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(appSettings ?? new AppSettings());
        var promptProcessing = new PromptProcessingService(
            pluginManager,
            settings.Object,
            new MemoryService(pluginManager),
            errorLog
        );
        return new LlmCleanupService(new CleanupService(), promptProcessing, errorLog);
    }

    private sealed class FakeLlmProviderPlugin : ILlmProviderPlugin
    {
        private readonly string _result;

        public FakeLlmProviderPlugin(string result)
        {
            _result = result;
            SupportedModels = [new PluginModelInfo("model-a", "Model A")];
        }

        public string? LastSystemPrompt { get; private set; }
        public string? LastUserText { get; private set; }
        public bool ThrowOnProcess { get; init; }
        public Exception? ProcessException { get; set; }
        public Action<CancellationToken>? BeforeProcess { get; init; }
        public Func<CancellationToken, Task<string>>? Process { get; set; }
        public CancellationToken LastToken { get; private set; }
        public int ProcessCalls { get; private set; }
        public List<string> SystemPrompts { get; } = [];

        public string PluginId => "com.test.cleanup";
        public string PluginName => "Cleanup Provider";
        public string PluginVersion => "1.0.0";
        public string ProviderName => "Cleanup Provider";
        public bool IsAvailable { get; init; } = true;
        public IReadOnlyList<PluginModelInfo> SupportedModels { get; }

        public Task ActivateAsync(IPluginHostServices host)
        {
            return Task.CompletedTask;
        }

        public Task DeactivateAsync()
        {
            return Task.CompletedTask;
        }

        public Task<string> ProcessAsync(
            string systemPrompt,
            string userText,
            string model,
            CancellationToken ct
        )
        {
            LastSystemPrompt = systemPrompt;
            LastUserText = userText;
            LastToken = ct;
            SystemPrompts.Add(systemPrompt);
            ProcessCalls++;
            BeforeProcess?.Invoke(ct);

            if (ProcessException is not null)
            {
                throw ProcessException;
            }

            return ThrowOnProcess ? throw new InvalidOperationException("Provider failed.") : Process?.Invoke(ct) ?? Task.FromResult(_result);
        }

        public void Dispose() { }
    }
}
