namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// Context passed to an action plugin when executing an action.
/// </summary>
/// <param name="AppName">Display name of the active foreground application, or null.</param>
/// <param name="ProcessName">Process name of the active foreground application, or null.</param>
/// <param name="Url">URL from the active browser tab, if available.</param>
/// <param name="Language">Detected or configured language code, or null.</param>
/// <param name="OriginalText">The original transcribed text before any processing.</param>
public sealed record ActionContext(
    string? AppName,
    string? ProcessName,
    string? Url,
    string? Language,
    string? OriginalText);
