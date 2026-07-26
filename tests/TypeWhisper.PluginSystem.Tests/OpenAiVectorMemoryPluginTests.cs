using System.Net;
using System.Text;
using TypeWhisper.Plugin.OpenAiVectorMemory;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Tests;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class OpenAiVectorMemoryPluginTests : IDisposable
{
    private const string EmbeddingResponse = """
        {"data":[{"embedding":[0.25,0.5]}]}
        """;

    private readonly string _tempDir = TestPaths.CreateTempDirectory(
        "TypeWhisper.OpenAiVectorMemoryPluginTests"
    );

    private string MemoryPath => Path.Join(_tempDir, "vector-memories.json");

    public void Dispose()
    {
        try
        {
            TestPaths.DeleteDirectory(_tempDir);
        }
        catch
        {
            /* best effort */
        }
    }

    [Fact]
    public async Task StoreAsync_WhenAtomicMoveFails_RollsBackCacheAndPersistsOnlyLaterSuccess()
    {
        Directory.CreateDirectory(MemoryPath);
        using var plugin = await CreatePluginAsync(new EmbeddingHandler());

        await Assert.ThrowsAnyAsync<IOException>(() =>
            plugin.StoreAsync("Failed memory", CancellationToken.None)
        );
        Assert.Empty(await plugin.GetAllAsync(CancellationToken.None));

        Directory.Delete(MemoryPath);
        await plugin.StoreAsync("Successful memory", CancellationToken.None);

        using var reloaded = await CreatePluginAsync(new EmbeddingHandler());
        Assert.Equal(
            ["Successful memory"],
            await reloaded.GetAllAsync(CancellationToken.None)
        );
    }

    [Fact]
    public async Task StoreAsync_WhenSaveIsCanceled_RollsBackCacheAndRethrowsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var plugin = await CreatePluginAsync(
            new EmbeddingHandler(cancellation)
        );

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            plugin.StoreAsync("Canceled memory", cancellation.Token)
        );

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(await plugin.GetAllAsync(CancellationToken.None));
    }

    private async Task<OpenAiVectorMemoryPlugin> CreatePluginAsync(HttpMessageHandler handler)
    {
        var plugin = new OpenAiVectorMemoryPlugin(handler);
        await plugin.ActivateAsync(new TestPluginHostServices(_tempDir));
        return plugin;
    }

    private sealed class EmbeddingHandler(
        CancellationTokenSource? cancelOnResponseDispose = null
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            HttpContent content = cancelOnResponseDispose is null
                ? new StringContent(EmbeddingResponse, Encoding.UTF8, "application/json")
                : new CancelOnDisposeContent(EmbeddingResponse, cancelOnResponseDispose);

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content,
                }
            );
        }
    }

    private sealed class CancelOnDisposeContent(
        string content,
        CancellationTokenSource cancellation
    ) : StringContent(content, Encoding.UTF8, "application/json")
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                cancellation.Cancel();

            base.Dispose(disposing);
        }
    }

    private sealed class TestPluginHostServices(string pluginDataDirectory)
        : IPluginHostServices
    {
        public string PluginDataDirectory { get; } = pluginDataDirectory;
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new TestPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public IPluginLocalization Localization { get; } = new TestPluginLocalization();

        public Task StoreSecretAsync(string key, string value) => Task.CompletedTask;
        public Task<string?> LoadSecretAsync(string key) => Task.FromResult<string?>("test-key");
        public Task DeleteSecretAsync(string key) => Task.CompletedTask;
        public T? GetSetting<T>(string key) => default;
        public void SetSetting<T>(string key, T value) { }
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() { }
    }

    private sealed class TestPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(key, args);
    }

    private sealed class TestPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }

        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent =>
            new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
