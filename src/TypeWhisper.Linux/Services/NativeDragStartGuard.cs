namespace TypeWhisper.Linux.Services;

/// <summary>
///     Marks the synchronous hand-off of a move or resize drag to the window manager, so the
///     pointer capture loss it raises isn't taken for the end of the drag.
/// </summary>
internal sealed class NativeDragStartGuard
{
    public bool IsStarting { get; private set; }

    public void Run(Action startNativeDrag)
    {
        IsStarting = true;
        try
        {
            startNativeDrag();
        }
        finally
        {
            IsStarting = false;
        }
    }
}
