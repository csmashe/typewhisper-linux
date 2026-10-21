namespace TypeWhisper.Linux.Services;

/// <summary>
///     Session-only sample text in the floating live-text window, started from Appearance so the
///     window can be placed and sized without dictating. Used on the UI thread only.
/// </summary>
public sealed class LiveTextPreviewService
{
    public bool IsActive { get; private set; }

    public event EventHandler? Changed;

    public void Start() => Set(true);

    public void Stop() => Set(false);

    private void Set(bool active)
    {
        if (IsActive == active)
        {
            return;
        }

        IsActive = active;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
