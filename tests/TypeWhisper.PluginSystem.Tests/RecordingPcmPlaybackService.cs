using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

internal sealed class RecordingPcmPlaybackService(bool isAvailable = true)
    : IPluginPcmPlaybackService
{
    private readonly ITtsPlaybackSession _session = new RecordingTtsPlaybackSession();

    public bool IsAvailable { get; } = isAvailable;
    public List<PcmPlaybackRequest> Requests { get; } = [];

    public Task<ITtsPlaybackSession> PlayAsync(
        PcmPlaybackRequest request,
        CancellationToken cancellationToken
    )
    {
        Requests.Add(request);
        return Task.FromResult(_session);
    }
}

internal sealed class RecordingTtsPlaybackSession : ITtsPlaybackSession
{
    public bool IsActive { get; private set; } = true;

    public event EventHandler? Completed;

    public void Stop()
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        Completed?.Invoke(this, EventArgs.Empty);
    }
}
