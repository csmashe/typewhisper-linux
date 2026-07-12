namespace TypeWhisper.Linux.Tests;

// Captures a scheduled auto-hide (injected via the presenter/service) so a test can fire it
// deterministically instead of waiting real seconds. Re-arming replaces the pending handle;
// the SUT disposes the superseded handle, so firing "pending" only fires the latest — mirroring
// how a re-armed DispatcherTimer supersedes the last one.
internal sealed class FakeScheduler
{
    private ScheduledDelay? _pending;

    public TimeSpan? LastDelay => _pending?.Delay;

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var entry = new ScheduledDelay(delay, callback);
        _pending = entry;
        return entry;
    }

    public void FirePending()
    {
        _pending?.Fire();
    }

    private sealed class ScheduledDelay(TimeSpan delay, Action callback) : IDisposable
    {
        private bool _cancelled;

        public TimeSpan Delay { get; } = delay;

        public void Fire()
        {
            if (!_cancelled)
            {
                callback();
            }
        }

        public void Dispose()
        {
            _cancelled = true;
        }
    }
}
