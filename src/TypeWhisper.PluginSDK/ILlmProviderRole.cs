// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
using System.Runtime.CompilerServices;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
///     Non-owning role that provides LLM chat-completion capabilities (e.g. for
///     translation and course correction). The owner is responsible for this role's
///     lifetime; hosts consume only the capability surface exposed here.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface ILlmProviderRole
{
    /// <summary>
    ///     Identifier of the owning plugin. Additional roles use their selection identity
    ///     to distinguish selectable providers while retaining the owner's plugin ID.
    /// </summary>
    string PluginId { get; }

    /// <summary>Provider name shown in the UI (e.g. "OpenAI", "Groq").</summary>
    string ProviderName { get; }

    /// <summary>Whether the provider is ready to accept requests (API key configured, etc.).</summary>
    bool IsAvailable { get; }

    /// <summary>Models supported by this provider.</summary>
    IReadOnlyList<PluginModelInfo> SupportedModels { get; }

    /// <summary>Sends a chat completion request and returns the response text.</summary>
    Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct
    );

    /// <summary>
    ///     Streams the response token-by-token. The default implementation wraps
    ///     <see cref="ProcessAsync" /> and yields a single chunk, so non-streaming
    ///     providers remain correct without overriding this method.
    /// </summary>
    async IAsyncEnumerable<string> ProcessStreamingAsync(
        string systemPrompt,
        string userText,
        string model,
        [EnumeratorCancellation]
        CancellationToken ct
    )
    {
        yield return await ProcessAsync(systemPrompt, userText, model, ct);
    }
}
