using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Integration.Tests.TestDoubles;

// Streams a scripted spoken-command response delta by delta so the orchestrator's
// streamed-insertion path runs for real. BatchCalls counts the non-streaming fallback,
// which a streamed command must not reach.
internal sealed class ScriptedLlmProvider : ILlmProviderRole
{
    private const string Id = "integration.scripted-llm";
    private const string ModelId = "scripted-llm-model";

    private readonly ConcurrentQueue<string[]> _streams = new();
    private int _batchCalls;

    public string PluginId => Id;
    public string ProviderName => "Scripted integration LLM";
    public bool IsAvailable => true;
    public IReadOnlyList<PluginModelInfo> SupportedModels { get; } =
        [new(ModelId, "Scripted LLM model")];

    internal int BatchCalls => Volatile.Read(ref _batchCalls);

    internal void EnqueueStream(params string[] deltas)
    {
        _streams.Enqueue(deltas);
    }

    public Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _batchCalls);
        return Task.FromResult(
            _streams.TryDequeue(out var deltas) ? string.Concat(deltas) : string.Empty
        );
    }

    public async IAsyncEnumerable<string> ProcessStreamingAsync(
        string systemPrompt,
        string userText,
        string model,
        [EnumeratorCancellation]
        CancellationToken ct
    )
    {
        if (!_streams.TryDequeue(out var deltas))
        {
            throw new InvalidOperationException("No scripted LLM stream remains.");
        }

        foreach (var delta in deltas)
        {
            ct.ThrowIfCancellationRequested();
            // Yield so the consumer really resumes between deltas rather than draining a
            // synchronous iterator in one pass.
            await Task.Yield();
            yield return delta;
        }
    }
}

// Last resort in the provider chain is a real compositor probe that cannot resolve headlessly,
// so a test that needs a known focused app (insertion strategy, profile match) supplies it here.
internal sealed class ScriptedActiveWindowProvider : IActiveWindowProvider
{
    private readonly ActiveWindowSnapshot _snapshot;

    internal ScriptedActiveWindowProvider(string processName, string? title)
    {
        _snapshot = new ActiveWindowSnapshot(processName, title, null, null, Name);
    }

    public string Name => "integration-scripted";

    public bool IsApplicable()
    {
        return true;
    }

    public Task<ActiveWindowSnapshot?> TryGetActiveWindowAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<ActiveWindowSnapshot?>(_snapshot);
    }
}
