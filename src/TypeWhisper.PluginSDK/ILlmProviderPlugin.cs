using System.Runtime.CompilerServices;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
///     Plugin that provides LLM chat-completion capabilities (e.g. for translation, course correction).
/// </summary>
public interface ILlmProviderPlugin : ITypeWhisperPlugin
{
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
    ///     Streams the chat-completion response token-by-token. The default
    ///     implementation wraps <see cref="ProcessAsync" /> and yields the entire
    ///     result as a single chunk, so providers that do not override it remain
    ///     correct (one bulk yield, byte-identical to the batch path).
    /// </summary>
    async IAsyncEnumerable<string> ProcessStreamingAsync(
        string systemPrompt,
        string userText,
        string model,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        yield return await ProcessAsync(systemPrompt, userText, model, ct);
    }
}