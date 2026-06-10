using System.Runtime.CompilerServices;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
///     Plugin that processes transcribed text after transcription (e.g. formatting, filtering).
///     Plugins are executed in ascending <see cref="Priority" /> order.
/// </summary>
public interface IPostProcessorPlugin : ITypeWhisperPlugin
{
    /// <summary>Display name for this processor.</summary>
    string ProcessorName { get; }

    /// <summary>Execution priority. Lower values run first.</summary>
    int Priority { get; }

    /// <summary>Processes the transcribed text and returns the modified version.</summary>
    Task<string> ProcessAsync(string text, PostProcessingContext context, CancellationToken ct);

    /// <summary>
    ///     Streams post-processed text token-by-token. The default implementation
    ///     wraps <see cref="ProcessAsync" /> and yields the full result as one chunk,
    ///     keeping non-overriding processors correct. True per-token pipeline piping
    ///     is a deferred follow-up.
    /// </summary>
    async IAsyncEnumerable<string> ProcessStreamingAsync(
        string text,
        PostProcessingContext context,
        [EnumeratorCancellation]
        CancellationToken ct
    )
    {
        yield return await ProcessAsync(text, context, ct);
    }
}