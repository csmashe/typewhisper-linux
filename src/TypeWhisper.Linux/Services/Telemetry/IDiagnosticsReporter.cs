namespace TypeWhisper.Linux.Services.Telemetry;

public interface IDiagnosticsReporter
{
    void CaptureException(Exception exception, string operation, IReadOnlyDictionary<string, string>? tags = null);
    IDiagnosticsOperation? BeginOperation(string name, string operation);
}
