namespace TypeWhisper.Linux.Services.Telemetry;

public enum DiagnosticsOutcome
{
    Ok,
    Cancelled,
    Failed,
    Aborted,
}

public interface IDiagnosticsOperation : IDisposable
{
    IDiagnosticsOperation? StartChild(string operation, string? description = null);
    void SetTag(string key, string value);
    void SetMeasurement(string name, double value, string unit);
    void Finish(DiagnosticsOutcome outcome);
}
