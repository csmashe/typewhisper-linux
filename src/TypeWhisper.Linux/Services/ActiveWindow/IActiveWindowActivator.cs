namespace TypeWhisper.Linux.Services.ActiveWindow;

/// <summary>An active-window provider that can also activate a window by the id it reported.</summary>
public interface IActiveWindowActivator
{
    /// <summary>Returns false when the window could not be activated; never throws.</summary>
    Task<bool> TryActivateWindowAsync(string windowId, CancellationToken ct);
}
