using System.Text.Json;

namespace TypeWhisper.PluginSDK;

public enum PluginStateCorruptFilePolicy
{
    Throw,
    PreserveAndReset,
}

public sealed record PluginStateStoreOptions
{
    /// <summary>
    ///     Reopening the same state path compares this by reference, not by configuration: pass
    ///     the <em>same instance</em> every time (cache it in a static or field). Two separately
    ///     constructed but identically configured <see cref="JsonSerializerOptions" /> count as a
    ///     conflict and the second open throws <see cref="InvalidOperationException" />.
    /// </summary>
    public JsonSerializerOptions JsonOptions { get; init; } = new();
    public bool KeepLastKnownGoodBackup { get; init; }
    public PluginStateCorruptFilePolicy CorruptFilePolicy { get; init; }
}

/// <summary>
///     A JSON-file-backed store for one plugin's state. Values are snapshots: mutating one in
///     place only reaches disk if it is returned from <see cref="UpdateAsync" />, and a snapshot
///     held across calls may be shared with the store, so treat <typeparamref name="T" /> as
///     immutable and make every change through <see cref="UpdateAsync" />.
/// </summary>
public interface IPluginStateStore<T>
    where T : notnull
{
    ValueTask<T> ReadAsync(CancellationToken cancellationToken = default);

    ValueTask<T> UpdateAsync(
        Func<T, T> update,
        CancellationToken cancellationToken = default
    );
}
