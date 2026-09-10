namespace TypeWhisper.PluginSDK;

/// <summary>Optional health snapshot for sessions that can fault after finalization returns.</summary>
public interface IStreamingSessionHealth
{
    /// <summary>The first captured session fault, or null if none. Must be safe to read across threads.</summary>
    Exception? Fault { get; }
}
