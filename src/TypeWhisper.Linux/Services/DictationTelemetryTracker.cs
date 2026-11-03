using System.Diagnostics;
using TypeWhisper.Linux.Services.Telemetry;

namespace TypeWhisper.Linux.Services;

internal sealed class DictationTelemetryTracker(IDiagnosticsReporter? reporter)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<int, Session> _sessions = [];

    private sealed class Session(IDiagnosticsOperation operation)
    {
        public IDiagnosticsOperation Operation { get; } = operation;
        public List<TrackedOperation> Children { get; } = [];
        public TrackedOperation? Capture { get; set; }
    }

    private static void Safely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Telemetry] Dictation diagnostics failed: {ex.Message}");
        }
    }

    public void Begin(int sessionId)
    {
        lock (_lock)
        {
            Safely(() =>
            {
                if (_sessions.ContainsKey(sessionId))
                {
                    return;
                }

                var operation = reporter?.BeginOperation("dictation.session", "dictation");
                if (operation is null)
                {
                    return;
                }

                var session = new Session(operation);
                _sessions.Add(sessionId, session);
                session.Capture = Child(sessionId, "audio.capture") as TrackedOperation;
            });
        }
    }

    public IDiagnosticsOperation? Child(int sessionId, string op, string? description = null)
    {
        lock (_lock)
        {
            IDiagnosticsOperation? result = null;
            Safely(() =>
            {
                if (!_sessions.TryGetValue(sessionId, out var session))
                {
                    return;
                }

                var child = session.Operation.StartChild(op, description);
                if (child is null)
                {
                    return;
                }

                var tracked = new TrackedOperation(this, child);
                session.Children.Add(tracked);
                result = tracked;
            });
            return result;
        }
    }

    public void EndCapture(int sessionId)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                session.Capture?.Finish(DiagnosticsOutcome.Ok);
            }
        }
    }

    public void Tag(int sessionId, string key, string value)
    {
        lock (_lock)
        {
            Safely(() =>
            {
                if (_sessions.TryGetValue(sessionId, out var session))
                {
                    session.Operation.SetTag(key, value);
                }
            });
        }
    }

    public void Measure(int sessionId, string name, double value, string unit)
    {
        lock (_lock)
        {
            Safely(() =>
            {
                if (_sessions.TryGetValue(sessionId, out var session))
                {
                    session.Operation.SetMeasurement(name, value, unit);
                }
            });
        }
    }

    public void Finish(int sessionId, string status)
    {
        var outcome = status switch
        {
            "ok" or "discarded" => DiagnosticsOutcome.Ok,
            "canceled" => DiagnosticsOutcome.Cancelled,
            "failed" or "insertion_failed" => DiagnosticsOutcome.Failed,
            _ => DiagnosticsOutcome.Aborted,
        };
        lock (_lock)
        {
            Tag(sessionId, "outcome", status);
            FinishCore(sessionId, outcome);
        }
    }

    public void Finish(int sessionId, DiagnosticsOutcome outcome)
    {
        Finish(sessionId, outcome switch
        {
            DiagnosticsOutcome.Ok => "ok",
            DiagnosticsOutcome.Cancelled => "canceled",
            DiagnosticsOutcome.Failed => "failed",
            _ => "aborted",
        });
    }

    private void FinishCore(int sessionId, DiagnosticsOutcome outcome)
    {
        lock (_lock)
        {
            if (!_sessions.Remove(sessionId, out var session))
            {
                return;
            }

            foreach (var child in session.Children)
            {
                child.Finish(outcome);
            }

            Safely(() => session.Operation.Finish(outcome));
        }
    }

    private sealed class TrackedOperation(DictationTelemetryTracker owner, IDiagnosticsOperation operation)
        : IDiagnosticsOperation
    {
        private bool _finished;
        private readonly List<TrackedOperation> _children = [];

        public IDiagnosticsOperation? StartChild(string op, string? description = null)
        {
            lock (owner._lock)
            {
                TrackedOperation? result = null;
                Safely(() =>
                {
                    if (_finished)
                    {
                        return;
                    }

                    if (operation.StartChild(op, description) is not { } child)
                    {
                        return;
                    }

                    result = new TrackedOperation(owner, child);
                    _children.Add(result);
                });
                return result;
            }
        }

        public void SetTag(string key, string value)
        {
            lock (owner._lock)
            {
                Safely(() =>
                {
                    if (!_finished)
                    {
                        operation.SetTag(key, value);
                    }
                });
            }
        }

        public void SetMeasurement(string name, double value, string unit)
        {
            lock (owner._lock)
            {
                Safely(() =>
                {
                    if (!_finished)
                    {
                        operation.SetMeasurement(name, value, unit);
                    }
                });
            }
        }

        public void Finish(DiagnosticsOutcome outcome)
        {
            lock (owner._lock)
            {
                if (_finished)
                {
                    return;
                }

                _finished = true;
                foreach (var child in _children)
                {
                    child.Finish(outcome);
                }

                Safely(() => operation.Finish(outcome));
            }
        }

        public void Dispose() => Finish(DiagnosticsOutcome.Ok);
    }
}
