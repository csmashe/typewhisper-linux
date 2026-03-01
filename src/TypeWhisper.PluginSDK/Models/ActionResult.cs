namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// Result returned from an action plugin execution.
/// </summary>
/// <param name="Success">Whether the action completed successfully.</param>
/// <param name="Message">Optional user-facing message describing the result.</param>
/// <param name="Url">Optional URL to open or display after the action completes.</param>
/// <param name="Icon">Optional icon system name for the result notification.</param>
/// <param name="DisplayDuration">How long to display the result notification, in seconds.</param>
public sealed record ActionResult(
    bool Success,
    string? Message = null,
    string? Url = null,
    string? Icon = null,
    double DisplayDuration = 3.0);
