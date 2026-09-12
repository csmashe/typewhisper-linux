using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services.Telemetry;
using Xunit;

namespace TypeWhisper.Linux.Tests.Telemetry;

[Collection("SentrySdk")]
public sealed class SentryPipelineTests
{
    [Fact]
    public async Task Real_sdk_envelopes_are_anonymous()
    {
        var transport = new RecordingTransport();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var user = Environment.UserName;
        var host = Environment.MachineName;
        try
        {
            using var service = new SentryTelemetryService(new FakeSettingsService(AppSettings.Default with
            {
                CrashReportingEnabled = true,
            }), o => o.Transport = transport);
            service.Start();
            service.MarkStartupWindowShown();
            var withData = ExceptionSamples.AppThrown("transcript: the quick brown fox");
            withData.Data["secret"] = "secret-canary-123";
            SentrySdk.CaptureException(withData);
            SentrySdk.CaptureException(ExceptionSamples.RuntimeFileFailure(home));
            using (var transaction = service.BeginOperation("dictation.session", "dictation"))
            {
                transaction!.SetTag("engine.model", "safe-model");
                using var child = transaction.StartChild("transcribe", $"{home}/x by {user} on {host}");
                child!.SetMeasurement("test.duration", 10, "millisecond");
            }

            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            Assert.NotEmpty(transport.Envelopes);
            foreach (var raw in transport.Envelopes)
            {
                // The modules map lists loaded assembly names (e.g. xunit.runner.*), which can contain a
                // CI user name such as "runner" without being identity data.
                var envelope = System.Text.RegularExpressions.Regex.Replace(raw, "\"modules\":\\{[^}]*\\}", "\"modules\":{}");
                Assert.DoesNotContain("the quick brown fox", envelope);
                Assert.DoesNotContain("secret-canary-123", envelope);
                Assert.DoesNotContain("Could not find file", envelope);
                Assert.DoesNotContain(home, envelope);
                if (user.Length >= 3)
                {
                    Assert.DoesNotContain(user, envelope);
                }

                if (host.Length >= 3)
                {
                    Assert.DoesNotContain(host, envelope);
                }

                Assert.DoesNotContain("{{auto}}", envelope);
                Assert.DoesNotContain("server_name", envelope);
                Assert.Contains("typewhisper-linux@", envelope);
            }

            Assert.Contains(transport.Envelopes,
                e => RecordedEnvelope.ContainsException(e, "System.IO.FileNotFoundException", "<redacted>")
                    && e.Contains("error.hresult"));
            Assert.Contains(transport.Envelopes,
                e => RecordedEnvelope.ContainsException(e, "System.InvalidOperationException", "<redacted>"));
            Assert.Contains(transport.Envelopes, e => e.Contains("dictation.session"));
            SentrySdk.Close();
        }
        finally
        {
            SentrySdk.Close();
        }
    }

    [Fact]
    public async Task Real_sdk_exception_breadcrumb_message_and_data_are_redacted()
    {
        var transport = new RecordingTransport();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var original = ExceptionSamples.RuntimeFileFailure(home);
        try
        {
            using var gate = new TelemetryConsentGate(new HttpClientHandler());
            var options = new SentryOptions();
            SentryOptionsFactory.Configure(options, SentryBuildInfo.Current(), gate);
            options.Transport = transport;
            using var sdk = SentrySdk.Init(options);
            SentrySdk.AddBreadcrumb(new Breadcrumb(message: original.Message, type: "default", category: "Exception",
                data: new Dictionary<string, string> { ["exception_message"] = original.Message }),
                new SentryHint("exception", original));
            SentrySdk.CaptureException(original);
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));

            var envelope = Assert.Single(transport.Envelopes);
            Assert.DoesNotContain("Could not find file", envelope);
            Assert.DoesNotContain(home, envelope);
            using var payload = JsonDocument.Parse(envelope.Split('\n', StringSplitOptions.RemoveEmptyEntries)[2]);
            var breadcrumb = Assert.Single(payload.RootElement.GetProperty("breadcrumbs").EnumerateArray());
            Assert.Equal("<redacted>", breadcrumb.GetProperty("message").GetString());
            Assert.Equal("<redacted>", breadcrumb.GetProperty("data").GetProperty("exception_message").GetString());
        }
        finally
        {
            SentrySdk.Close();
        }
    }

    [Fact]
    public async Task Cancellation_filter_and_disabled_reporting_send_nothing()
    {
        var transport = new RecordingTransport();
        try
        {
            using var service = new SentryTelemetryService(new FakeSettingsService(), o => o.Transport = transport);
            service.Start();
            service.CaptureException(new IOException("disabled"), "test");
            Assert.Empty(transport.Envelopes);
            using var gate = new TelemetryConsentGate(new HttpClientHandler());
            var options = new SentryOptions();
            SentryOptionsFactory.Configure(options, SentryBuildInfo.Current(), gate);
            options.Transport = transport;
            using var sdk = SentrySdk.Init(options);
            SentrySdk.CaptureException(new OperationCanceledException("cancel"));
            await SentrySdk.FlushAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(transport.Envelopes);
        }
        finally
        {
            SentrySdk.Close();
        }
    }
}
