using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class PostProcessorStreamingDefaultTests
{
    [Fact]
    public async Task ProcessStreamingAsync_Default_YieldsSingleChunkEqualToBatchResult()
    {
        const string expected = "the full processed text";
        IPostProcessorPlugin plugin = new BatchOnlyPostProcessor(expected);

        var chunks = new List<string>();
        await foreach (var chunk in plugin.ProcessStreamingAsync(
                           "input", new PostProcessingContext(), CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Single(chunks);
        Assert.Equal(expected, chunks[0]);
    }

    [Fact]
    public async Task ProcessStreamingAsync_Default_HonorsPreCancelledToken()
    {
        IPostProcessorPlugin plugin = new BatchOnlyPostProcessor("unused");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in plugin.ProcessStreamingAsync(
                               "input", new PostProcessingContext(), cts.Token))
            {
            }
        });
    }

    /// <summary>
    ///     Concrete plugin implementing only <see cref="IPostProcessorPlugin.ProcessAsync" />
    ///     so the interface's default <c>ProcessStreamingAsync</c> body is exercised
    ///     (a Moq mock would supply its own member and bypass the default).
    /// </summary>
    private sealed class BatchOnlyPostProcessor(string result) : IPostProcessorPlugin
    {
        public string PluginId => "test.batch-only-postprocessor";
        public string PluginName => "Batch Only Post-Processor";
        public string PluginVersion => "1.0.0";
        public string ProcessorName => "BatchOnly";
        public int Priority => 0;

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public void Dispose() { }

        public Task<string> ProcessAsync(
            string text, PostProcessingContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }
}
