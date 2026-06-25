// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
using System.Runtime.CompilerServices;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
///     Plugin that provides LLM chat-completion capabilities (e.g. for translation, course correction).
/// </summary>
// ReSharper disable once UnusedType.Global
public interface ILlmProviderPlugin : ITypeWhisperPlugin
{
    /// <summary>Provider name shown in the UI (e.g. "OpenAI", "Groq").</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    string ProviderName { get; }

    /// <summary>Whether the provider is ready to accept requests (API key configured, etc.).</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    bool IsAvailable { get; }

    /// <summary>Models supported by this provider.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    IReadOnlyList<PluginModelInfo> SupportedModels { get; }

    /// <summary>Sends a chat completion request and returns the response text.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    // ReSharper disable once UnusedParameter.Global
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
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    // ReSharper disable once UnusedParameter.Global
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
