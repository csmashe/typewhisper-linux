// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
///     Plugin that provides custom actions that can be triggered on transcribed text.
///     Actions appear in the prompt palette and can be invoked by the user.
/// </summary>
// ReSharper disable once UnusedType.Global
public interface IActionPlugin : ITypeWhisperPlugin
{
    /// <summary>Unique action identifier (e.g. "translate-to-french").</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    string ActionId { get; }

    /// <summary>Human-readable action name for the UI.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    string ActionName { get; }

    /// <summary>Optional system icon name for the action.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    string? ActionIcon { get; }

    /// <summary>Executes the action on the given input text.</summary>
    // ReSharper disable once UnusedMember.Global
    // ReSharper disable once UnusedMemberInSuper.Global
    // ReSharper disable once UnusedParameter.Global
    Task<ActionResult> ExecuteAsync(string input, ActionContext context, CancellationToken ct);
}
