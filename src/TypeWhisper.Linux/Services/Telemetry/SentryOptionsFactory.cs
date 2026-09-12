using Sentry.Infrastructure;

namespace TypeWhisper.Linux.Services.Telemetry;

public sealed record SentryBuildInfo(string Dsn, string Release, string Environment, string Distribution, bool Debug)
{
    private const string DefaultDsn =
        "https://297e0ea78e245eddbb60ecd24fc31bf3@o4508223367282688.ingest.us.sentry.io/4512074711302144";

    public static SentryBuildInfo Current()
    {
        var dsn = System.Environment.GetEnvironmentVariable("TYPEWHISPER_SENTRY_DSN");
        var debug = System.Environment.GetEnvironmentVariable("TYPEWHISPER_SENTRY_DEBUG");
        return new SentryBuildInfo(
            string.IsNullOrEmpty(dsn) ? DefaultDsn : dsn,
            "typewhisper-linux@" + AppVersion.Display,
#if DEBUG
            "development",
#else
            "production",
#endif
            InstallKindDetector.Detect(),
            new[] { "1", "true", "yes" }.Contains(debug, StringComparer.OrdinalIgnoreCase));
    }
}

public static class SentryOptionsFactory
{
    internal static void Configure(SentryOptions options, SentryBuildInfo info, TelemetryConsentGate gate)
    {
        options.Dsn = info.Dsn;
        options.Release = info.Release;
        options.Environment = info.Environment;
        options.Distribution = info.Distribution;
        options.Debug = info.Debug;
        options.DiagnosticLogger = info.Debug ? new TraceDiagnosticLogger(SentryLevel.Debug) : null;
        options.SendDefaultPii = false;
        options.IsEnvironmentUser = false;
        options.ServerName = null;
        options.IsGlobalModeEnabled = true;
        options.AutoSessionTracking = false;
        options.TracesSampleRate = 1.0;
        options.CaptureFailedRequests = false;
        options.DisableSentryHttpMessageHandler = true;
        options.SendClientReports = false;
        options.EnableLogs = false;
        options.AttachStacktrace = true;
        options.MaxBreadcrumbs = 20;
        options.CacheDirectoryPath = null;
        options.DisableFileWrite = true;
        options.ShutdownTimeout = TimeSpan.FromSeconds(2);
        options.CreateHttpMessageHandler = () => gate;
        options.DisableDiagnosticSourceIntegration();
        options.AddExceptionFilterForType<OperationCanceledException>();
        options.SetBeforeSend(TelemetryScrubber.Scrub);
        options.SetBeforeSendTransaction(TelemetryScrubber.Scrub);
        options.SetBeforeBreadcrumb(TelemetryScrubber.ScrubExceptionBreadcrumb);
    }
}
