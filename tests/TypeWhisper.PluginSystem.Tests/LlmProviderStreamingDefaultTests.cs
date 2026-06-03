using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class LlmProviderStreamingDefaultTests
{
    [Fact]
    public async Task ProcessStreamingAsync_Default_YieldsSingleChunkEqualToBatchResult()
    {
        const string expected = "the full batch response";
        ILlmProviderPlugin plugin = new BatchOnlyLlmPlugin(expected);

        var chunks = new List<string>();
        await foreach (var chunk in plugin.ProcessStreamingAsync(
                           "system", "user", "model", CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Single(chunks);
        Assert.Equal(expected, chunks[0]);
    }

    [Fact]
    public async Task ProcessStreamingAsync_Default_HonorsPreCancelledToken()
    {
        ILlmProviderPlugin plugin = new BatchOnlyLlmPlugin("unused");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in plugin.ProcessStreamingAsync(
                               "system", "user", "model", cts.Token))
            {
            }
        });
    }

    /// <summary>
    ///     Concrete plugin implementing only <see cref="ILlmProviderPlugin.ProcessAsync" />
    ///     so the interface's default <c>ProcessStreamingAsync</c> body is exercised
    ///     (a Moq mock would supply its own member and bypass the default).
    /// </summary>
    private sealed class BatchOnlyLlmPlugin(string result) : ILlmProviderPlugin
    {
        public string PluginId => "test.batch-only-llm";
        public string PluginName => "Batch Only LLM";
        public string PluginVersion => "1.0.0";
        public string ProviderName => "BatchOnly";
        public bool IsAvailable => true;
        public IReadOnlyList<PluginModelInfo> SupportedModels => [];

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public void Dispose() { }

        public Task<string> ProcessAsync(
            string systemPrompt, string userText, string model, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }
}
