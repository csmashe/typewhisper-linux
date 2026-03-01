using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK;

/// <summary>
/// Plugin that provides custom actions that can be triggered on transcribed text.
/// Actions appear in the prompt palette and can be invoked by the user.
/// </summary>
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
