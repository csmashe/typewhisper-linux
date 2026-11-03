namespace TypeWhisper.PluginSDK;

/// <summary>The representation of each sample in a PCM playback request.</summary>
public enum PcmSampleFormat
{
    /// <summary>Signed 16-bit integer samples encoded least-significant byte first.</summary>
    Signed16LittleEndian,

    /// <summary>IEEE 754 single-precision samples encoded least-significant byte first.</summary>
    Float32,
}

/// <summary>
///     Interleaved PCM audio supplied by a plugin for host-managed playback.
/// </summary>
/// <param name="Payload">Interleaved samples in <paramref name="Format" />.</param>
/// <param name="SampleRate">Samples per second for each channel.</param>
/// <param name="Channels">Number of interleaved channels.</param>
/// <param name="Format">Representation of each sample in <paramref name="Payload" />.</param>
public sealed record PcmPlaybackRequest(
    ReadOnlyMemory<byte> Payload,
    int SampleRate,
    int Channels,
    PcmSampleFormat Format
);

/// <summary>Host-owned playback for interleaved PCM supplied by a plugin.</summary>
public interface IPluginPcmPlaybackService
{
    /// <summary>Whether a supported platform PCM player is currently available.</summary>
    bool IsAvailable { get; }

    /// <summary>Starts playback without waiting for the complete payload to reach the player.</summary>
    Task<ITtsPlaybackSession> PlayAsync(
        PcmPlaybackRequest request,
        CancellationToken cancellationToken
    );
}

/// <summary>
///     The implementation a host inherits when it provides no PCM playback of its own.
/// </summary>
public sealed class UnavailablePluginPcmPlaybackService : IPluginPcmPlaybackService
{
    /// <summary>The shared always-unavailable service.</summary>
    public static UnavailablePluginPcmPlaybackService Instance { get; } = new();

    private UnavailablePluginPcmPlaybackService()
    {
    }

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public Task<ITtsPlaybackSession> PlayAsync(
        PcmPlaybackRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ITtsPlaybackSession>(UnavailablePlaybackSession.Shared);
    }

    private sealed class UnavailablePlaybackSession : ITtsPlaybackSession
    {
        public static UnavailablePlaybackSession Shared { get; } = new();

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
}
