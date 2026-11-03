using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services.Telemetry;

public sealed class SentryTelemetryService(
    ISettingsService settings,
    Action<SentryOptions>? configureForTests = null,
    HttpMessageHandler? innerHandlerForTests = null) : IDiagnosticsReporter, IDisposable
{
    private readonly Lock _lock = new();
    private IDisposable? _sdk;

    private TelemetryConsentGate? _gate;

    private IDiagnosticsOperation? _startup;

    private bool _started;

    private bool _shutdown;

    private bool _disposed;

    internal TelemetryConsentGate? ConsentGate
    {
        get
        {
            lock (_lock)
            {
                return _gate;
            }
        }
    }

    public bool IsEnabled
    {
        get
        {
            lock (_lock)
            {
                return _sdk is not null;
            }
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_started || _disposed || _shutdown)
            {
                return;
            }

            _started = true;
            settings.SettingsChanged += OnSettingsChanged;
            ApplySetting(settings.Current.CrashReportingEnabled);
            if (_sdk is null)
            {
                return;
            }

            _startup = BeginOperation("app.startup", "app.start");
            _startup?.SetTag("startup.minimized", Program.StartMinimized ? "true" : "false");
        }
    }

    private void OnSettingsChanged(AppSettings _)
    {
        lock (_lock)
        {
            if (!_disposed && !_shutdown)
            {
                ApplySetting(settings.Current.CrashReportingEnabled);
            }
        }
    }

    private void ApplySetting(bool enabled)
    {
        try
        {
            switch (enabled)
            {
                case true when _sdk is null:
                    _gate = new TelemetryConsentGate(innerHandlerForTests ?? new HttpClientHandler());
                    _sdk = SentrySdk.Init(options =>
                    {
                        SentryOptionsFactory.Configure(options, SentryBuildInfo.Current(), _gate);
                        configureForTests?.Invoke(options);
                    });
                    break;
                case false when _sdk is not null:
                    _gate?.Close();
                    CloseSdk();
                    break;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Telemetry] Configuration failed: {ex.Message}");
        }
    }

    public void CaptureException(
        Exception exception, string operation, IReadOnlyDictionary<string, string>? tags = null)
    {
        lock (_lock)
        {
            if (_sdk is null || exception is OperationCanceledException)
            {
                return;
            }

            try
            {
                SentrySdk.CaptureException(exception, scope =>
                {
                    scope.SetTag("operation", operation);
                    if (tags is null)
                    {
                        return;
                    }

                    foreach (var tag in tags)
                    {
                        scope.SetTag(tag.Key, tag.Value);
                    }
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Telemetry] Capture failed: {ex.Message}");
            }
        }
    }

    public IDiagnosticsOperation? BeginOperation(string name, string operation)
    {
        lock (_lock)
        {
            if (_sdk is null)
            {
                return null;
            }

            try
            {
                return new SentryOperation(this, _sdk, SentrySdk.StartTransaction(name, operation));
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Telemetry] Transaction failed: {ex.Message}");
                return null;
            }
        }
    }

    public void MarkStartupWindowShown()
    {
        lock (_lock)
        {
            _startup?.SetMeasurement(
                "startup.time_to_window_ms", Program.BootStopwatch.ElapsedMilliseconds, "millisecond");
            _startup?.Finish(DiagnosticsOutcome.Ok);
            _startup = null;
        }
    }

    private void CloseSdk()
    {
        _startup?.Finish(DiagnosticsOutcome.Aborted);
        _startup = null;
        var sdk = _sdk;
        _sdk = null;
        try
        {
            sdk?.Dispose();
        }
        finally
        {
            _gate?.Dispose();
        }
    }

    public void Shutdown()
    {
        lock (_lock)
        {
            _shutdown = true;
            CloseSdk();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            settings.SettingsChanged -= OnSettingsChanged;
            Shutdown();
        }
    }

    private sealed class SentryOperation(SentryTelemetryService owner, IDisposable sdk, ISpan span)
        : IDiagnosticsOperation
    {
        private bool _finished;

        private void Apply(Action action)
        {
            lock (owner._lock)
            {
                // Old sessions must not send through a later opt-in SDK instance.
                if (_finished || !ReferenceEquals(owner._sdk, sdk))
                {
                    return;
                }

                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[Telemetry] Span failed: {ex.Message}");
                }
            }
        }

        public IDiagnosticsOperation? StartChild(string operation, string? description = null)
        {
            IDiagnosticsOperation? child = null;
            Apply(() => child = new SentryOperation(owner, sdk, span.StartChild(operation, description)));
            return child;
        }

        public void SetTag(string key, string value) => Apply(() => span.SetTag(key, value));

        public void SetMeasurement(string name, double value, string unit)
        {
            Apply(() => span.SetMeasurement(name, value, unit switch
            {
                "millisecond" => MeasurementUnit.Duration.Millisecond,
                "second" => MeasurementUnit.Duration.Second,
                _ => MeasurementUnit.None,
            }));
        }

        public void Finish(DiagnosticsOutcome outcome)
        {
            Apply(() =>
            {
                _finished = true;
                span.Finish(outcome switch
                {
                    DiagnosticsOutcome.Ok => SpanStatus.Ok,
                    DiagnosticsOutcome.Cancelled => SpanStatus.Cancelled,
                    DiagnosticsOutcome.Failed => SpanStatus.InternalError,
                    _ => SpanStatus.Aborted,
                });
            });
        }

        public void Dispose() => Finish(DiagnosticsOutcome.Ok);
    }
}
