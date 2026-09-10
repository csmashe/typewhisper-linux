using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Applies the configured history retention policy on startup, shutdown,
///     and whenever settings or history changes. Lives outside IHistoryService
///     to keep policy logic separate from the data layer; "clear on close" mode
///     runs a wipe at both startup (last-session leftovers) and shutdown.
/// </summary>
public sealed class HistoryRetentionCoordinator : IDisposable
{
    private readonly IHistoryService _history;
    private readonly ISettingsService _settings;
    private int _applyInProgress;
    private bool _initialized;

    public HistoryRetentionCoordinator(IHistoryService history, ISettingsService settings)
    {
        _history = history;
        _settings = settings;
    }

    public void Dispose()
    {
        if (!_initialized)
        {
            return;
        }

        _settings.SettingsChanged -= OnSettingsChanged;
        _history.RecordsChanged -= OnRecordsChanged;
        _initialized = false;
    }

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _settings.SettingsChanged += OnSettingsChanged;
        _history.RecordsChanged += OnRecordsChanged;
        _initialized = true;

        ApplyRetention(_settings.Current, HistoryRetentionTrigger.Startup);
    }

    public void HandleShutdown()
    {
        if (!_initialized)
        {
            return;
        }

        ApplyRetention(_settings.Current, HistoryRetentionTrigger.Shutdown);
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        if (!_initialized)
        {
            return;
        }

        ApplyRetention(settings, HistoryRetentionTrigger.SettingsChanged);
    }

    private void OnRecordsChanged()
    {
        // RecordsChanged fires from within history mutations, so guard against
        // re-entrant calls while a previous apply is still running.
        if (!_initialized || Volatile.Read(ref _applyInProgress) != 0)
        {
            return;
        }

        ApplyRetention(_settings.Current, HistoryRetentionTrigger.HistoryChanged);
    }

    private void ApplyRetention(AppSettings settings, HistoryRetentionTrigger trigger)
    {
        if (Interlocked.Exchange(ref _applyInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            // Forever retains everything; UntilAppCloses outside startup/shutdown is a no-op.
            // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault -- only the actionable cases are handled; remaining enum values are deliberate no-ops.
            switch (settings.HistoryRetentionMode)
            {
                case HistoryRetentionMode.Duration:
                    _history.PurgeOldRecords(
                        TimeSpan.FromMinutes(settings.HistoryRetentionMinutes)
                    );
                    break;
                case HistoryRetentionMode.UntilAppCloses
                    when trigger
                        is HistoryRetentionTrigger.Startup
                        or HistoryRetentionTrigger.Shutdown:
                    _history.ClearAll();
                    break;
            }
        }
        finally
        {
            Volatile.Write(ref _applyInProgress, 0);
        }
    }

    private enum HistoryRetentionTrigger
    {
        Startup,
        SettingsChanged,
        HistoryChanged,
        Shutdown,
    }
}