// ReSharper disable UnusedMemberInSuper.Global
// PluginSDK contract members are implemented by out-of-solution plugin projects and invoked by
// the host; the analyzer sees no in-solution caller, so these .Global inspections misfire.

// Public plugin-SDK surface. The per-item `disable once` directives below mark members
// ReSharper/Qodana cannot see used from this project (they are consumed by external plugins/
// the host). Per-item, not file-level, so a genuinely-unused member added later still surfaces.
namespace TypeWhisper.PluginSDK;

/// <summary>
///     Optional interface for plugins that offer one-shot commands in their settings view,
///     such as downloading or removing local model files. The host renders each action as a
///     button, runs one action per plugin at a time without a time limit, offers the user a
///     cancel button while it runs, and re-reads <see cref="GetSettingsActions" /> afterwards.
///     Progress is reported through <see cref="IPluginSettingsActivity" /> when implemented.
/// </summary>
/// <remarks>
///     Async members use the SDK cancellation-origin contract: success uses the existing return;
///     caller cancellation throws <see cref="OperationCanceledException" /> only when the supplied
///     token is requested; private deadlines throw <see cref="TimeoutException" /> (or a
///     provider-specific subclass); every other exception, including an OCE while the supplied
///     token is live, is a dependency fault.
/// </remarks>
// ReSharper disable once UnusedType.Global
public interface IPluginSettingsActions
{
    /// <summary>Returns the actions to show, reflecting the plugin's current state.</summary>
    IReadOnlyList<PluginSettingsAction> GetSettingsActions();

    /// <summary>
    ///     Runs the action with the given <see cref="PluginSettingsAction.Id" /> and returns
    ///     the message to show the user.
    /// </summary>
    Task<PluginSettingsValidationResult> ExecuteSettingsActionAsync(
        string actionId,
        CancellationToken ct
    );
}

/// <summary>Describes a command button in a plugin's settings view.</summary>
/// <param name="Id">Unique action identifier within the plugin.</param>
/// <param name="Label">Button text.</param>
/// <param name="Description">Optional explanation shown next to the button.</param>
/// <param name="IsEnabled">False to show the button disabled in the current state.</param>
/// <param name="ConfirmationMessage">
///     When set, the host asks the user to confirm with this message before running the action.
/// </param>
// ReSharper disable once UnusedType.Global
public sealed record PluginSettingsAction(
    string Id,
    string Label,
    string? Description = null,
    bool IsEnabled = true,
    string? ConfirmationMessage = null
);
