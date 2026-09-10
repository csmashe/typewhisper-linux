using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.SupertonicTts;

internal sealed class SupertonicInactiveTtsPlaybackSession : ITtsPlaybackSession
{
    public static SupertonicInactiveTtsPlaybackSession Instance { get; } = new();

    private SupertonicInactiveTtsPlaybackSession()
    {
    }

    public bool IsActive => false;

    public event EventHandler? Completed
    {
        add => value?.Invoke(this, EventArgs.Empty);
        remove { }
    }

    public void Stop()
    {
    }
}
