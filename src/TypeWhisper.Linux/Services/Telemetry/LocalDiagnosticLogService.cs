using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.Diagnostics;

namespace TypeWhisper.Linux.Services.Telemetry;

/// <summary>
///     Opt-in local trace of the same operations Sentry sees, written to <c>Logs/diagnostics.jsonl</c> and never
///     sent anywhere. Only event names, correlation IDs, timings, allowlisted tags and exception types and
///     method names are kept; <see cref="DiagnosticLogFile.Admit" /> drops everything else.
/// </summary>
public sealed class LocalDiagnosticLogService : IDiagnosticsReporter, IDisposable
{
    private const int QueueCapacity = 1024;
    private static readonly TimeSpan s_pruneCheckInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_flushTimeout = TimeSpan.FromSeconds(2);

    private readonly ISettingsService _settings;
    private readonly TimeProvider _time;
    private readonly DiagnosticLogFile _file;

    // Written off the caller's thread so file I/O never delays dictation. A full queue drops lines; flush and
    // clear requests wait for room and run after everything queued before them.
    private readonly Channel<object> _queue = Channel.CreateBounded<object>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true }
    );

    private readonly Lock _lock = new();
    private Task? _writer;
    private ITimer? _pruneTimer;
    private bool _started;
    private bool _disposed;

    // Read on dictation paths without the file lock; the file re-checks its preferences before writing.
    private volatile bool _enabled;

    // Bumped when the log is turned off, so lines queued earlier are discarded.
    private int _generation;

    // Makes the generation check and write atomic with configuration changes.
    private readonly Lock _writeGate = new();

    public LocalDiagnosticLogService(ISettingsService settings)
        : this(settings, Path.Join(TypeWhisperEnvironment.LogsPath, "diagnostics.jsonl"), TimeProvider.System)
    {
    }

    internal LocalDiagnosticLogService(ISettingsService settings, string path, TimeProvider time)
    {
        _settings = settings;
        _time = time;
        _file = new DiagnosticLogFile(path, PreferencesFrom(settings.Current), time.GetUtcNow);
        _enabled = _file.Preferences.Enabled;
    }

    private bool IsEnabled => _enabled;

    /// <summary>False when the last settings change could not delete or prune the log; the next one retries.</summary>
    public bool LastConfigureSucceeded { get; private set; } = true;

    public void Start()
    {
        lock (_lock)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            _settings.SettingsChanged += OnSettingsChanged;
            // Also retries a deletion that failed when the log was turned off.
            Configure(PreferencesFrom(_settings.Current));
            _writer = Task.Run(WriteQueuedLinesAsync);
            _pruneTimer = _time.CreateTimer(_ => _file.PruneIfDue(), null, s_pruneCheckInterval, s_pruneCheckInterval);
        }

        Enqueue(Line("app.start", data: AppData()));
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        lock (_lock)
        {
            var preferences = PreferencesFrom(settings);
            if (!_disposed && (preferences != _file.Preferences || !LastConfigureSucceeded))
            {
                Configure(preferences);
            }
        }
    }

    private void Configure(DiagnosticLogPreferences preferences)
    {
        lock (_writeGate)
        {
            if (!preferences.Enabled)
            {
                Interlocked.Increment(ref _generation);
            }

            _enabled = preferences.Enabled;
            LastConfigureSucceeded = _file.Configure(preferences);
        }
    }

    private static DiagnosticLogPreferences PreferencesFrom(AppSettings settings) =>
        new(settings.DiagnosticLogEnabled, DiagnosticLogPreferences.NormalizeRetentionDays(settings.DiagnosticLogRetentionDays));

    public void CaptureException(Exception exception, string operation, IReadOnlyDictionary<string, string>? tags = null)
    {
        if (!IsEnabled || exception is OperationCanceledException)
        {
            return;
        }

        Enqueue(ErrorLine("error.captured", exception, operation, tags));
    }

    /// <summary>
    ///     Records an exception that is about to end the process. Written synchronously because the queue may
    ///     never drain.
    /// </summary>
    public void CaptureCrash(Exception exception, string eventName)
    {
        try
        {
            if (IsEnabled)
            {
                _file.Write(ErrorLine(eventName, exception, operation: null, tags: null));
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Diagnostics] Crash capture failed: {ex.GetType().Name}");
        }
    }

    public IDiagnosticsOperation? BeginOperation(string name, string operation)
    {
        if (!IsEnabled)
        {
            return null;
        }

        var root = new LocalOperation(
            this, Volatile.Read(ref _generation), Guid.NewGuid(), _time.GetTimestamp(), EventName(name));
        root.WriteBegin();
        return root;
    }

    public void MarkStartupWindowShown(long elapsedMilliseconds)
    {
        Enqueue(Line("app.window-shown", data: new Dictionary<string, string>
        {
            ["startup.time_to_window_ms"] = elapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
        }));
    }

    /// <summary>Writes the export header and every retained entry; returns the number of entries.</summary>
    public async Task<int> ExportAsync(string destination)
    {
        try
        {
            await FlushAsync().WaitAsync(s_flushTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Export what is already on disk rather than failing the user's request.
        }

        var data = AppData();
        data["retention-days"] = _file.Preferences.RetentionDays.ToString(CultureInfo.InvariantCulture);
        return await Task.Run(() => _file.Export(destination, Line("diagnostics.export", data: data))).ConfigureAwait(false);
    }

    /// <summary>The retained entries for a support report; none while the log is off, even if a file remains.</summary>
    /// <exception cref="IOException">The log exists but could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The log exists but could not be read.</exception>
    public async Task<SupportReport.LogInfo> CaptureForReportAsync()
    {
        var preferences = _file.Preferences;
        if (preferences.Enabled)
        {
            try
            {
                await FlushAsync().WaitAsync(s_flushTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Report what is already on disk.
            }
        }

        var entries = await Task.Run(_file.Snapshot).ConfigureAwait(false);
        return new SupportReport.LogInfo(preferences.Enabled, preferences.RetentionDays, entries);
    }

    /// <summary>
    ///     Deletes the log after every line queued before this call, so no earlier entry can recreate it.
    /// </summary>
    public async Task<bool> ClearAsync()
    {
        var cleared = new ClearRequest();
        return await SendAsync(cleared, cleared.Completion, () => _file.Clear()).ConfigureAwait(false);
    }

    /// <summary>Completes once every line queued before this call has been written.</summary>
    internal Task FlushAsync()
    {
        var flushed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return SendAsync(flushed, flushed, () => true);
    }

    private async Task<bool> SendAsync(object request, TaskCompletionSource<bool> completion, Func<bool> withoutWriter)
    {
        lock (_lock)
        {
            if (_writer is null || _disposed)
            {
                return withoutWriter();
            }
        }

        try
        {
            await _queue.Writer.WriteAsync(request).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // Disposed while waiting for room: the writer will never see this request.
            return withoutWriter();
        }

        return await completion.Task.ConfigureAwait(false);
    }

    private sealed record QueuedLine(DiagnosticLogLine Line, int Generation);

    private sealed class ClearRequest
    {
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void Enqueue(DiagnosticLogLine line) => Enqueue(line, Volatile.Read(ref _generation));

    // Operations pass the generation they started in, so turning the log off ends them for good.
    private void Enqueue(DiagnosticLogLine line, int generation)
    {
        if (IsEnabled && generation == Volatile.Read(ref _generation))
        {
            _queue.Writer.TryWrite(new QueuedLine(line, generation));
        }
    }

    private async Task WriteQueuedLinesAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            switch (item)
            {
                case QueuedLine queued:
                    try
                    {
                        lock (_writeGate)
                        {
                            if (queued.Generation == Volatile.Read(ref _generation))
                            {
                                _file.Write(queued.Line);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[Diagnostics] Write failed: {ex.GetType().Name}");
                    }

                    break;
                case ClearRequest cleared:
                    cleared.Completion.TrySetResult(_file.Clear());
                    break;
                case TaskCompletionSource<bool> flushed:
                    flushed.TrySetResult(true);
                    break;
            }
        }
    }

    private DiagnosticLogLine Line(
        string eventName, Guid? correlationId = null, long? rootStarted = null, Dictionary<string, string>? data = null)
    {
        long? elapsed = rootStarted is { } started ? (long)_time.GetElapsedTime(started).TotalMilliseconds : null;
        return new DiagnosticLogLine(_time.GetUtcNow(), eventName, correlationId, elapsed, data is { Count: > 0 } ? data : null);
    }

    private DiagnosticLogLine ErrorLine(
        string eventName, Exception exception, string? operation, IReadOnlyDictionary<string, string>? tags)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        if (operation is not null)
        {
            data["operation"] = EventName(operation);
        }

        if (tags is not null)
        {
            foreach (var (key, value) in tags)
            {
                data[key] = value;
            }
        }

        AddErrorFacts(exception, data);
        return Line(eventName, data: data) with
        {
            Error = exception.GetType().FullName,
            HResult = $"0x{exception.HResult:X8}",
            Stack = Frames(exception),
        };
    }

    // The same facts the Sentry scrubber tags: innermost type, HTTP status and socket error, never messages.
    private static void AddErrorFacts(Exception exception, Dictionary<string, string> data)
    {
        Exception? innermost = null;
        var innermostDepth = -1;
        Walk(exception, 0);
        if (innermost is not null && innermost.GetType() != exception.GetType()
            && innermost.GetType().FullName is { } innerType)
        {
            data["inner"] = innerType;
        }

        return;

        void Walk(Exception? current, int depth)
        {
            while (current is not null && depth < 32)
            {
                switch (current)
                {
                    case HttpRequestException { StatusCode: { } status }:
                        data.TryAdd("http.status", ((int)status).ToString(CultureInfo.InvariantCulture));
                        break;
                    case SocketException socket:
                        data.TryAdd("socket.error", socket.SocketErrorCode.ToString());
                        break;
                    case AggregateException aggregate:
                        foreach (var inner in aggregate.InnerExceptions)
                        {
                            Walk(inner, depth + 1);
                        }

                        return;
                }

                if (depth > innermostDepth)
                {
                    innermost = current;
                    innermostDepth = depth;
                }

                current = current.InnerException;
                depth++;
            }
        }
    }

    private static string[] Frames(Exception exception) =>
        new StackTrace(exception, false).GetFrames()
            .Select(frame => frame.GetMethod())
            .Where(method => method is not null)
            .Select(method => $"{method!.DeclaringType?.FullName}.{method.Name}")
            .Take(12)
            .ToArray();

    private static Dictionary<string, string> AppData() => new(StringComparer.Ordinal)
    {
        ["version"] = AppVersion.Display,
        ["os"] = Environment.OSVersion.Version.ToString(),
        ["arch"] = RuntimeInformation.ProcessArchitecture.ToString(),
        ["runtime"] = Environment.Version.ToString(),
        ["install"] = InstallKindDetector.Detect(),
        ["startup.minimized"] = Program.StartMinimized ? "true" : "false",
#if DEBUG
        ["debug"] = "true",
#else
        ["debug"] = "false",
#endif
    };

    // Telemetry names use underscores ("vocabulary_rescore"); log events are lowercase with dots and dashes.
    internal static string EventName(string name) => name.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');

    public void Dispose()
    {
        Task? writer;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _settings.SettingsChanged -= OnSettingsChanged;
            _pruneTimer?.Dispose();
            if (_started)
            {
                Enqueue(Line("app.exit"));
            }

            _disposed = true;
            _queue.Writer.TryComplete();
            writer = _writer;
        }

        try
        {
            writer?.Wait(s_flushTimeout);
        }
        catch (AggregateException ex)
        {
            Trace.WriteLine($"[Diagnostics] Writer failed: {ex.InnerException?.GetType().Name}");
        }
    }

    private sealed class LocalOperation(
        LocalDiagnosticLogService owner, int generation, Guid correlationId, long rootStarted, string eventBase)
        : IDiagnosticsOperation
    {
        private readonly Lock _lock = new();
        private readonly long _started = owner._time.GetTimestamp();
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);
        private bool _finished;

        public void WriteBegin() => owner.Enqueue(owner.Line(eventBase + ".begin", correlationId, rootStarted), generation);

        // The description is free text, so it is not recorded.
        public IDiagnosticsOperation? StartChild(string operation, string? description = null)
        {
            lock (_lock)
            {
                if (_finished || !owner.IsEnabled || generation != Volatile.Read(ref owner._generation))
                {
                    return null;
                }

                var child = new LocalOperation(owner, generation, correlationId, rootStarted, EventName(operation));
                child.WriteBegin();
                return child;
            }
        }

        public void SetTag(string key, string value)
        {
            lock (_lock)
            {
                if (!_finished)
                {
                    _data[key] = value;
                }
            }
        }

        public void SetMeasurement(string name, double value, string unit)
        {
            lock (_lock)
            {
                if (!_finished && double.IsFinite(value))
                {
                    _data[name] = value.ToString("0.###", CultureInfo.InvariantCulture);
                }
            }
        }

        public void Finish(DiagnosticsOutcome outcome)
        {
            Dictionary<string, string> data;
            lock (_lock)
            {
                if (_finished)
                {
                    return;
                }

                _finished = true;
                data = new Dictionary<string, string>(_data, StringComparer.Ordinal);
            }

            // A tagged outcome ("insertion_failed") is more specific than the span status.
            data.TryAdd("outcome", outcome switch
            {
                DiagnosticsOutcome.Ok => "ok",
                DiagnosticsOutcome.Cancelled => "canceled",
                DiagnosticsOutcome.Failed => "failed",
                _ => "aborted",
            });
            data["duration-ms"] = ((long)owner._time.GetElapsedTime(_started).TotalMilliseconds)
                .ToString(CultureInfo.InvariantCulture);
            owner.Enqueue(owner.Line(eventBase + ".end", correlationId, rootStarted, data), generation);
        }

        public void Dispose() => Finish(DiagnosticsOutcome.Ok);
    }
}
