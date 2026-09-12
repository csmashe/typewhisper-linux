using System.Net;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Telemetry;
using Xunit;

namespace TypeWhisper.Linux.Tests.Telemetry;

[Collection("SentrySdk")]
public sealed class SentryTelemetryServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void App_exit_flushes_queued_reports_with_consent(bool dispose)
    {
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            CrashReportingEnabled = true,
        });
        using var handler = new RecordingHttpHandler();
        using var logger = new QueueUntilShutdownLogger();
        var service = CreateHttpService(settings, handler, logger);
        try
        {
            service.Start();
            var gate = Assert.IsType<TelemetryConsentGate>(service.ConsentGate);
            service.CaptureException(new IOException("kept"), "kept");
            Assert.Empty(handler.Requests);
            if (dispose)
            {
                service.Dispose();
            }
            else
            {
                service.Shutdown();
            }

            Assert.True(gate.IsOpen);
            Assert.False(logger.TimedOut);
            Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Post && r.Body.Contains("kept"));
            Assert.Contains(handler.Requests,
                r => RecordedEnvelope.ContainsException(r.Body, "System.IO.IOException", "<redacted>"));
        }
        finally
        {
            try
            {
                SentrySdk.Close();
            }
            finally
            {
                service.Dispose();
            }
        }
    }

    [Fact]
    public void Opt_out_discards_queued_reports_before_sdk_disposal()
    {
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            CrashReportingEnabled = true,
        });
        using var handler = new RecordingHttpHandler();
        using var logger = new QueueUntilShutdownLogger();
        using var service = CreateHttpService(settings, handler, logger);
        try
        {
            service.Start();
            var gate = Assert.IsType<TelemetryConsentGate>(service.ConsentGate);
            service.CaptureException(new IOException("first-event"), "first-event");
            Assert.Empty(handler.Requests);
            settings.Change(settings.Current with
            {
                CrashReportingEnabled = false,
            });
            Assert.False(service.IsEnabled);
            Assert.False(gate.IsOpen);
            Assert.False(logger.TimedOut);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            SentrySdk.Close();
        }
    }

    [Fact]
    public void Opting_back_in_uses_fresh_gate_and_only_delivers_second_event()
    {
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            CrashReportingEnabled = true,
        });
        using var handler = new RecordingHttpHandler();
        using var logger = new QueueUntilShutdownLogger();
        using var service = CreateHttpService(settings, handler, logger);
        try
        {
            service.Start();
            var firstGate = Assert.IsType<TelemetryConsentGate>(service.ConsentGate);
            service.CaptureException(new IOException("first-event"), "first-event");
            settings.Change(settings.Current with
            {
                CrashReportingEnabled = false,
            });
            settings.Change(settings.Current with
            {
                CrashReportingEnabled = true,
            });
            var secondGate = Assert.IsType<TelemetryConsentGate>(service.ConsentGate);
            Assert.NotSame(firstGate, secondGate);
            Assert.False(firstGate.IsOpen);
            Assert.True(secondGate.IsOpen);
            service.CaptureException(new IOException("second-event"), "second-event");
            service.Shutdown();
            Assert.False(logger.TimedOut);
            var request = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("second-event", request.Body);
            Assert.DoesNotContain("first-event", request.Body);
        }
        finally
        {
            SentrySdk.Close();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Consent_gate_delegates_only_while_open(bool closed, bool synchronous)
    {
        using var handler = new RecordingHttpHandler();
        using var gate = new TelemetryConsentGate(handler);
        using var client = new HttpMessageInvoker(gate);
        using var content = new StringContent("report");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com");
        request.Content = content;
        if (closed)
        {
            gate.Close();
        }

        using var response = synchronous
            // ReSharper disable once MethodHasAsyncOverload -- this theory explicitly exercises the synchronous send overload.
            ? client.Send(request, CancellationToken.None)
            : await client.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Same(request, response.RequestMessage);
        Assert.Equal(!closed, gate.IsOpen);
        if (closed)
        {
            Assert.Empty(handler.Requests);
            await Assert.ThrowsAsync<ObjectDisposedException>(content.ReadAsStringAsync);
        }
        else
        {
            Assert.Equal("report", Assert.Single(handler.Requests).Body);
        }

    }

    private static SentryTelemetryService CreateHttpService(
        FakeSettingsService settings, RecordingHttpHandler handler, QueueUntilShutdownLogger logger)
    {
        return new SentryTelemetryService(settings, options =>
        {
            logger.Reset();
            options.Dsn = "https://key@example.com/1";
            options.Debug = true;
            options.DiagnosticLevel = SentryLevel.Debug;
            options.DiagnosticLogger = logger;
        }, handler);
    }

    [Fact]
    public void Lifecycle_tracks_consent_and_disposal()
    {
        var settings = new FakeSettingsService();
        var transport = new RecordingTransport();
        var service = new SentryTelemetryService(settings, o => o.Transport = transport);
        try
        {
            service.Start();
            service.Start();
            Assert.False(service.IsEnabled);
            Assert.Null(service.BeginOperation("test", "test"));
            service.CaptureException(new IOException("test"), "test");
            settings.Change(settings.Current with
            {
                CrashReportingEnabled = true,
            });
            Assert.True(service.IsEnabled);
            settings.Change(settings.Current with
            {
                CrashReportingEnabled = false,
            });
            Assert.False(service.IsEnabled);
            settings.Change(settings.Current with
            {
                CrashReportingEnabled = true,
            });
            Assert.True(service.IsEnabled);
            service.Shutdown();
            service.Shutdown();
            Assert.False(service.IsEnabled);
            service.Dispose();
            settings.Change(settings.Current with
            {
                CrashReportingEnabled = true,
            });
            Assert.False(service.IsEnabled);
            Assert.Empty(transport.Envelopes);
        }
        finally
        {
            try
            {
                SentrySdk.Close();
            }
            finally
            {
                service.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(true, "ok")]
    [InlineData(false, "aborted")]
    public void Startup_finishes_on_window_or_shutdown(bool windowShown, string status)
    {
        var transport = new RecordingTransport();
        using var service = new SentryTelemetryService(new FakeSettingsService(AppSettings.Default with
        {
            CrashReportingEnabled = true,
        }), o => o.Transport = transport);
        try
        {
            service.Start();
            Assert.True(service.IsEnabled);
            if (windowShown)
            {
                service.MarkStartupWindowShown();
                service.MarkStartupWindowShown();
            }

            service.Shutdown();
            var envelope = Assert.Single(transport.Envelopes);
            Assert.Contains("app.startup", envelope);
            Assert.Contains("\"status\":\"" + status + "\"", envelope);
            if (windowShown)
            {
                Assert.Contains("startup.time_to_window_ms", envelope);
            }
        }
        finally
        {
            SentrySdk.Close();
        }
    }

    [Theory]
    [InlineData(DiagnosticsOutcome.Ok, "ok")]
    [InlineData(DiagnosticsOutcome.Cancelled, "cancelled")]
    [InlineData(DiagnosticsOutcome.Failed, "internal_error")]
    [InlineData(DiagnosticsOutcome.Aborted, "aborted")]
    public async Task Operation_finishes_once_and_maps_units(DiagnosticsOutcome outcome, string status)
    {
        var settings = new FakeSettingsService();
        var transport = new RecordingTransport();
        using var service = new SentryTelemetryService(settings, o => o.Transport = transport);
        try
        {
            service.Start();
            settings.Change(settings.Current with
            {
                CrashReportingEnabled = true,
            });
            using var operation = service.BeginOperation("test", "test");
            operation!.SetMeasurement("duration.ms", 1, "millisecond");
            operation.SetMeasurement("duration.s", 2, "second");
            operation.SetMeasurement("chars", 3, "none");
            using (var child = operation.StartChild("child"))
            {
                child!.SetTag("result", "ok");
            }

            operation.Finish(outcome);
            operation.Finish(DiagnosticsOutcome.Failed);
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            var envelope = Assert.Single(transport.Envelopes);
            Assert.Contains("\"status\":\"" + status + "\"", envelope);
            Assert.Contains("millisecond", envelope);
            Assert.Contains("second", envelope);
            Assert.Contains("none", envelope);
        }
        finally
        {
            SentrySdk.Close();
        }
    }

    [Fact]
    public async Task Old_operations_cannot_report_after_opt_out_and_back_in()
    {
        var settings = new FakeSettingsService();
        var transport = new RecordingTransport();
        using var service = new SentryTelemetryService(settings, o => o.Transport = transport);
        try
        {
            service.Start();
            settings.Change(settings.Current with
            {
                CrashReportingEnabled = true,
            });
            var operation = service.BeginOperation("old", "test");
            settings.Change(settings.Current with
            {
                CrashReportingEnabled = false,
            });
            settings.Change(settings.Current with
            {
                CrashReportingEnabled = true,
            });
            operation!.Dispose();
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(transport.Envelopes);
        }
        finally
        {
            SentrySdk.Close();
        }
    }
}
