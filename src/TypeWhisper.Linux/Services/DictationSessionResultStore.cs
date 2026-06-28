using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using TypeWhisper.Linux.Models;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Short-lived in-memory cache of completed dictation results, keyed by
///     <see cref="DictationSessionResult.SessionId" />. CLI/script clients use
///     <c>GET /v1/dictation/transcription?sessionId=&lt;id&gt;</c> to poll until
///     a result is available (or until the entry's TTL elapses). Entries
///     evict 5 minutes after they are recorded.
/// </summary>
public sealed class DictationSessionResultStore : IDisposable
{
    private static readonly TimeSpan s_defaultTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_sweepInterval = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<int, Entry> _entries = new();
    private readonly Timer _timer;
    private readonly TimeSpan _ttl;
    private bool _disposed;

    public DictationSessionResultStore()
        : this(s_defaultTtl)
    {
    }

    internal DictationSessionResultStore(TimeSpan ttl)
    {
        _ttl = ttl;
        _timer = new Timer(_ => EvictExpired(DateTime.UtcNow), null, s_sweepInterval, s_sweepInterval);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
    }

    public void Record(DictationSessionResult result)
    {
        _entries[result.SessionId] = new Entry(result, DateTime.UtcNow);
    }

    public bool TryGet(int sessionId, [NotNullWhen(true)] out DictationSessionResult? result)
    {
        if (_entries.TryGetValue(sessionId, out var entry) && !IsExpired(entry, DateTime.UtcNow))
        {
            result = entry.Result;
            return true;
        }

        result = null;
        return false;
    }

    public void Clear(int sessionId)
    {
        _entries.TryRemove(sessionId, out _);
    }

    internal void EvictNow(DateTime asOfUtc)
    {
        EvictExpired(asOfUtc);
    }

    private void EvictExpired(DateTime asOfUtc)
    {
        foreach (var pair in _entries.Where(pair => IsExpired(pair.Value, asOfUtc)))
        {
            _entries.TryRemove(pair.Key, out _);
        }
    }

    private bool IsExpired(Entry entry, DateTime asOfUtc)
    {
        return asOfUtc - entry.StoredAtUtc > _ttl;
    }

    private readonly record struct Entry(DictationSessionResult Result, DateTime StoredAtUtc);
}