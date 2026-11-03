using Sentry.Infrastructure;
using TypeWhisper.Linux.Services.Telemetry;
using Xunit;

namespace TypeWhisper.Linux.Tests.Telemetry;

[Collection("SentrySdk")]
public sealed class SentryOptionsFactoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Configures_privacy_performance_and_build_options(bool debug)
    {
        var info = new SentryBuildInfo("https://key@example.com/1", "typewhisper-linux@test", "test", "local", debug);
        var o = new SentryOptions();
        using var gate = new TelemetryConsentGate(new HttpClientHandler());
        SentryOptionsFactory.Configure(o, info, gate);
        Assert.Same(gate, o.CreateHttpMessageHandler!());
        Assert.Equal(info.Dsn, o.Dsn);
        Assert.Equal(info.Release, o.Release);
        Assert.Equal(info.Environment, o.Environment);
        Assert.Equal(info.Distribution, o.Distribution);
        Assert.Equal(debug, o.Debug);
        if (debug)
        {
            Assert.IsType<TraceDiagnosticLogger>(o.DiagnosticLogger);
        }
        else
        {
            Assert.Null(o.DiagnosticLogger);
        }

        Assert.False(o.SendDefaultPii);
        Assert.False(o.IsEnvironmentUser);
        Assert.Null(o.ServerName);
        Assert.True(o.IsGlobalModeEnabled);
        Assert.False(o.AutoSessionTracking);
        Assert.Equal(1.0, o.TracesSampleRate);
        Assert.False(o.CaptureFailedRequests);
        Assert.True(o.DisableSentryHttpMessageHandler);
        Assert.False(o.SendClientReports);
        Assert.False(o.EnableLogs);
        Assert.True(o.AttachStacktrace);
        Assert.Equal(20, o.MaxBreadcrumbs);
        Assert.Null(o.CacheDirectoryPath);
        Assert.True(o.DisableFileWrite);
        Assert.Equal(TimeSpan.FromSeconds(2), o.ShutdownTimeout);
    }

    [Theory]
    [InlineData("https://override@example.com/2", "YES", true)]
    [InlineData("", "1", true)]
    [InlineData(null, "TrUe", true)]
    [InlineData(null, "0", false)]
    public void Build_info_honors_environment(string? dsn, string debug, bool expectedDebug)
    {
        var oldDsn = Environment.GetEnvironmentVariable("TYPEWHISPER_SENTRY_DSN");
        var oldDebug = Environment.GetEnvironmentVariable("TYPEWHISPER_SENTRY_DEBUG");
        try
        {
            Environment.SetEnvironmentVariable("TYPEWHISPER_SENTRY_DSN", dsn);
            Environment.SetEnvironmentVariable("TYPEWHISPER_SENTRY_DEBUG", debug);
            var info = SentryBuildInfo.Current();
            Assert.Equal(string.IsNullOrEmpty(dsn)
                ? "https://297e0ea78e245eddbb60ecd24fc31bf3@o4508223367282688.ingest.us.sentry.io/4512074711302144"
                : dsn, info.Dsn);
            Assert.StartsWith("typewhisper-linux@", info.Release);
            Assert.Equal(expectedDebug, info.Debug);
#if DEBUG
            Assert.Equal("development", info.Environment);
#else
            Assert.Equal("production", info.Environment);
#endif
            Assert.Equal(InstallKindDetector.Detect(), info.Distribution);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TYPEWHISPER_SENTRY_DSN", oldDsn);
            Environment.SetEnvironmentVariable("TYPEWHISPER_SENTRY_DEBUG", oldDebug);
        }
    }
}
