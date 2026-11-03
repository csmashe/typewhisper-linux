using System.Diagnostics;

namespace TypeWhisper.Linux.Services.Telemetry;

/// <summary>
///     Sends every report to each reporter (Sentry and the local diagnostic log), so each keeps its own
///     consent; a failure in one never reaches the other or the caller.
/// </summary>
public sealed class CompositeDiagnosticsReporter(params IDiagnosticsReporter[] reporters) : IDiagnosticsReporter
{
    public void CaptureException(Exception exception, string operation, IReadOnlyDictionary<string, string>? tags = null)
    {
        foreach (var reporter in reporters)
        {
            Safely(() => reporter.CaptureException(exception, operation, tags));
        }
    }

    public IDiagnosticsOperation? BeginOperation(string name, string operation) =>
        Combine(reporters.Select(reporter => Safely(() => reporter.BeginOperation(name, operation))));

    private static IDiagnosticsOperation? Combine(IEnumerable<IDiagnosticsOperation?> operations)
    {
        var started = operations.OfType<IDiagnosticsOperation>().ToArray();
        return started.Length switch
        {
            0 => null,
            1 => started[0],
            _ => new CompositeOperation(started),
        };
    }

    private static void Safely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Telemetry] Reporter failed: {ex.GetType().Name}");
        }
    }

    private static T? Safely<T>(Func<T?> action)
        where T : class
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Telemetry] Reporter failed: {ex.GetType().Name}");
            return null;
        }
    }

    private sealed class CompositeOperation(IDiagnosticsOperation[] operations) : IDiagnosticsOperation
    {
        public IDiagnosticsOperation? StartChild(string operation, string? description = null) =>
            Combine(operations.Select(parent => Safely(() => parent.StartChild(operation, description))));

        public void SetTag(string key, string value) => Each(operation => operation.SetTag(key, value));

        public void SetMeasurement(string name, double value, string unit) =>
            Each(operation => operation.SetMeasurement(name, value, unit));

        public void Finish(DiagnosticsOutcome outcome) => Each(operation => operation.Finish(outcome));

        public void Dispose() => Each(operation => operation.Dispose());

        private void Each(Action<IDiagnosticsOperation> action)
        {
            foreach (var operation in operations)
            {
                Safely(() => action(operation));
            }
        }
    }
}
