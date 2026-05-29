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
    ///     Streams the post-processed text token-by-token. The default
    ///     implementation wraps <see cref="ProcessAsync" /> and yields the entire
    ///     result as a single chunk, so processors that do not override it remain
    ///     correct (one bulk yield, byte-identical to the batch path). Real
    ///     per-token piping between pipeline steps is a deferred follow-up (C7
    ///     master plan, resolved Q3).
    /// </summary>
    async IAsyncEnumerable<string> ProcessStreamingAsync(
        string text,
        PostProcessingContext context,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        yield return await ProcessAsync(text, context, ct);
    }
}