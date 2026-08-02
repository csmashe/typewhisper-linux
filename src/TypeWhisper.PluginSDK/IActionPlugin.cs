// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
///     Plugin that provides custom actions that can be triggered on transcribed text.
///     Actions appear in the prompt palette and can be invoked by the user.
/// </summary>
/// <remarks>
///     Async members use the SDK cancellation-origin contract: success uses the existing return;
///     caller cancellation throws <see cref="OperationCanceledException" /> only when the supplied
///     token is requested; private deadlines throw <see cref="TimeoutException" /> (or a
///     provider-specific subclass); every other exception, including an OCE while the supplied
///     token is live, is a dependency fault. At catch time caller cancellation wins over a private
///     timeout, which wins over a dependency fault; if both tokens are requested, caller wins.
/// </remarks>
// ReSharper disable once UnusedType.Global
public interface IActionPlugin : ITypeWhisperPlugin
{
    /// <summary>Unique action identifier (e.g. "translate-to-french").</summary>
    string ActionId { get; }

    /// <summary>Human-readable action name for the UI.</summary>
    string ActionName { get; }

    /// <summary>Optional system icon name for the action.</summary>
    string? ActionIcon { get; }

    /// <summary>Executes the action on the given input text.</summary>
    Task<ActionResult> ExecuteAsync(string input, ActionContext context, CancellationToken ct);
}
