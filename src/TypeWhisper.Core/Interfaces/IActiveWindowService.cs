namespace TypeWhisper.Core.Interfaces;

public interface IActiveWindowService
{
    string? GetActiveWindowProcessName();
    string? GetActiveWindowTitle();
    string? GetBrowserUrl(bool allowInteractiveCapture = true);

    /// <summary>Returns distinct process names of all visible windows (sorted).</summary>
    IReadOnlyList<string> GetRunningAppProcessNames();
}
