// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
using System.Runtime.CompilerServices;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
///     Plugin that processes transcribed text after transcription (e.g. formatting, filtering).
///     Plugins are executed in ascending <see cref="Priority" /> order.
/// </summary>
/// <remarks>
///     Async members use the SDK cancellation-origin contract: success uses the existing
///     return/stream terminal; caller cancellation throws <see cref="OperationCanceledException" />
///     only when the supplied token is requested; private deadlines throw <see cref="TimeoutException" />
///     (or a provider-specific subclass); every other exception, including an OCE while the
///     supplied token is live, is a dependency fault. At catch time caller cancellation wins
///     over a private timeout, which wins over a dependency fault; if both tokens are requested,
///     caller cancellation wins.
/// </remarks>
// ReSharper disable once UnusedType.Global
public interface IPostProcessorPlugin : ITypeWhisperPlugin
{
    /// <summary>Display name for this processor.</summary>
    // ReSharper disable once UnusedMember.Global
    string ProcessorName { get; }

    /// <summary>Execution priority. Lower values run first.</summary>
    int Priority { get; }

    /// <summary>Processes the transcribed text and returns the modified version.</summary>
    // ReSharper disable UnusedParameter.Global
    Task<string> ProcessAsync(string text, PostProcessingContext context, CancellationToken ct);
    // ReSharper restore UnusedParameter.Global

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
