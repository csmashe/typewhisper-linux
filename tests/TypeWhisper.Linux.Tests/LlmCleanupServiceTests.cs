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
            BeforeProcess?.Invoke(ct);

            if (ProcessException is not null)
            {
                throw ProcessException;
            }

            return ThrowOnProcess ? throw new InvalidOperationException("Provider failed.") : Task.FromResult(_result);
        }

        public void Dispose() { }
    }
}
