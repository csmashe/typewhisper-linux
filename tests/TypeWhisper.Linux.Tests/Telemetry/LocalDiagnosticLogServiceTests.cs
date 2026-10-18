// ReSharper disable MethodHasAsyncOverload -- synchronous File.Read*/Write* is deliberate in these test assertions and fixtures.
using System.Net;
using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Telemetry;
using TypeWhisper.Tests;
using Xunit;

namespace TypeWhisper.Linux.Tests.Telemetry;

public sealed class LocalDiagnosticLogServiceTests : IDisposable
{
    private readonly string _directory = TestPaths.CreateTempDirectory("TypeWhisper.LocalDiagnosticLogServiceTests");
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));

    private string LogPath => Path.Join(_directory, "diagnostics.jsonl");

    public void Dispose()
    {
        try
        {
            TestPaths.DeleteDirectory(_directory);
        }
        catch (IOException)
        {
        }
    }

    private (LocalDiagnosticLogService Log, FakeSettingsService Settings) Start(bool enabled = true)
    {
        var settings = new FakeSettingsService(AppSettings.Default with { DiagnosticLogEnabled = enabled });
        var log = new LocalDiagnosticLogService(settings, LogPath, _time);
        log.Start();
        return (log, settings);
    }

    private List<JsonElement> Lines() => File.Exists(LogPath)
        ? File.ReadAllLines(LogPath).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList()
        : [];

    private static string Event(JsonElement line) => line.GetProperty("event").GetString()!;

    private static string? Data(JsonElement line, string key) =>
        line.TryGetProperty("data", out var data) && data.TryGetProperty(key, out var value) ? value.GetString() : null;

    [Fact]
    public void Off_by_default_writes_nothing_and_starts_no_operations()
    {
        var (log, _) = Start(enabled: false);
        using (log)
        {
            Assert.Null(log.BeginOperation("dictation.session", "dictation"));
            log.CaptureException(ExceptionSamples.AppThrown("secret"), "transcription");
        }

        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public async Task Dictation_session_is_correlated_timed_and_content_free()
    {
        var (log, _) = Start();
        using (log)
        {
            var tracker = new DictationTelemetryTracker(log);
            tracker.Begin(7);
            tracker.Tag(7, "mode", "PushToTalk");
            tracker.Tag(7, "engine.provider", "com.typewhisper.sherpa-onnx");
            tracker.Tag(7, "engine.model", "parakeet-tdt-0.6b");
            tracker.Tag(7, "window.title", "Inbox - private");
            tracker.Measure(7, "audio.duration_s", 2.25, "second");
            tracker.Measure(7, "post_process.my_secret_step_ms", 4, "millisecond");
            _time.Advance(TimeSpan.FromMilliseconds(400));
            tracker.EndCapture(7);
            var transcribe = tracker.Child(7, "transcribe");
            var rescore = tracker.Child(7, "vocabulary_rescore", "free text description");
            _time.Advance(TimeSpan.FromMilliseconds(100));
            transcribe!.Finish(DiagnosticsOutcome.Failed);
            rescore!.Finish(DiagnosticsOutcome.Ok);
            tracker.Finish(7, "insertion_failed");
            await log.FlushAsync();
        }

        var lines = Lines();
        Assert.Equal(
            ["app.start", "dictation.session.begin", "audio.capture.begin", "audio.capture.end", "transcribe.begin",
                "vocabulary-rescore.begin", "transcribe.end", "vocabulary-rescore.end", "dictation.session.end", "app.exit"],
            lines.Select(Event));
        var session = lines.Where(line => line.TryGetProperty("correlationId", out _)).ToList();
        Assert.Single(session.Select(line => line.GetProperty("correlationId").GetGuid()).Distinct());
        var capture = session.Single(line => Event(line) == "audio.capture.end");
        Assert.Equal("400", Data(capture, "duration-ms"));
        Assert.Equal(400, capture.GetProperty("elapsedMs").GetInt64());
        Assert.Equal("failed", Data(session.Single(line => Event(line) == "transcribe.end"), "outcome"));
        var end = session.Single(line => Event(line) == "dictation.session.end");
        Assert.Equal("insertion_failed", Data(end, "outcome"));
        Assert.Equal("PushToTalk", Data(end, "mode"));
        Assert.Equal("parakeet-tdt-0.6b", Data(end, "engine.model"));
        Assert.Equal("2.25", Data(end, "audio.duration_s"));
        Assert.Equal("500", Data(end, "duration-ms"));
        var text = File.ReadAllText(LogPath);
        Assert.DoesNotContain("private", text);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("free text", text);
    }

    [Fact]
    public async Task Exceptions_keep_type_hresult_frames_and_facts_but_never_the_message()
    {
        var (log, _) = Start();
        using (log)
        {
            var failure = new InvalidOperationException("transcript: hello from /home/someone",
                new HttpRequestException("token sk-123", null, HttpStatusCode.Unauthorized));
            Exception thrown;
            try
            {
                throw failure;
            }
            catch (InvalidOperationException ex)
            {
                thrown = ex;
            }

            log.CaptureException(thrown, "plugin.activate", new Dictionary<string, string> { ["plugin.id"] = "com.typewhisper.openai" });
            log.CaptureException(new OperationCanceledException(), "transcription");
            await log.FlushAsync();
        }

        var error = Assert.Single(Lines(), line => Event(line) == "error.captured");
        Assert.Equal("System.InvalidOperationException", error.GetProperty("error").GetString());
        Assert.Equal($"0x{new InvalidOperationException().HResult:X8}", error.GetProperty("hresult").GetString());
        Assert.Equal("plugin.activate", Data(error, "operation"));
        Assert.Equal("com.typewhisper.openai", Data(error, "plugin.id"));
        Assert.Equal("401", Data(error, "http.status"));
        Assert.Equal("System.Net.Http.HttpRequestException", Data(error, "inner"));
        Assert.Contains(error.GetProperty("stack").EnumerateArray(), frame => frame.GetString()!.Contains(nameof(LocalDiagnosticLogServiceTests)));
        var text = File.ReadAllText(LogPath);
        Assert.DoesNotContain("hello", text);
        Assert.DoesNotContain("sk-123", text);
        Assert.DoesNotContain("/home/someone", text);
    }

    [Fact]
    public async Task Turning_the_log_off_deletes_it_and_stops_open_operations_from_writing()
    {
        var (log, settings) = Start();
        using (log)
        {
            var operation = log.BeginOperation("dictation.session", "dictation");
            await log.FlushAsync();
            Assert.True(File.Exists(LogPath));

            settings.Update(current => current with { DiagnosticLogEnabled = false });
            Assert.True(log.LastConfigureSucceeded);
            Assert.False(File.Exists(LogPath));

            Assert.Null(operation!.StartChild("transcribe"));
            operation.Finish(DiagnosticsOutcome.Ok);
            await log.FlushAsync();
        }

        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public async Task Lines_queued_before_the_log_was_turned_off_do_not_return_when_it_is_turned_back_on()
    {
        var (log, settings) = Start();
        using (log)
        {
            for (var index = 0; index < 500; index++)
            {
                log.MarkStartupWindowShown(index);
            }

            settings.Update(current => current with { DiagnosticLogEnabled = false });
            settings.Update(current => current with { DiagnosticLogEnabled = true });
            log.BeginOperation("dictation.session", "dictation");
            await log.FlushAsync();
        }

        Assert.Equal(["dictation.session.begin", "app.exit"], Lines().Select(Event));
    }

    [Fact]
    public async Task Operations_started_before_the_log_was_turned_off_stay_out_after_it_is_turned_back_on()
    {
        var (log, settings) = Start();
        using (log)
        {
            var operation = log.BeginOperation("dictation.session", "dictation");
            operation!.SetTag("mode", "PushToTalk");
            settings.Update(current => current with { DiagnosticLogEnabled = false });
            settings.Update(current => current with { DiagnosticLogEnabled = true });

            Assert.Null(operation.StartChild("transcribe"));
            operation.Finish(DiagnosticsOutcome.Ok);
            await log.FlushAsync();
        }

        Assert.Equal(["app.exit"], Lines().Select(Event));
    }

    [Fact]
    public async Task Start_deletes_a_log_left_behind_while_disabled()
    {
        File.WriteAllText(LogPath, "{\"time\":\"2026-10-13T12:00:00+00:00\",\"event\":\"app.start\"}\n");
        var (log, _) = Start(enabled: false);
        using (log)
        {
            Assert.False(File.Exists(LogPath));
            Assert.Equal(0, await log.ExportAsync(Path.Join(_directory, "export.jsonl")));
        }
    }

    [Fact]
    public async Task Export_flushes_pending_lines_and_writes_a_header()
    {
        var (log, settings) = Start();
        settings.Update(current => current with { DiagnosticLogRetentionDays = 14 });
        var destination = Path.Join(_directory, "export.jsonl");
        int count;
        using (log)
        {
            log.MarkStartupWindowShown(850);
            count = await log.ExportAsync(destination);
        }

        Assert.Equal(2, count);
        var exported = File.ReadAllLines(destination).Select(line => JsonDocument.Parse(line).RootElement).ToList();
        Assert.Equal(["diagnostics.export", "app.start", "app.window-shown"], exported.Select(Event));
        Assert.Equal("14", Data(exported[0], "retention-days"));
        Assert.Equal(AppVersion.Display, Data(exported[0], "version"));
        Assert.Equal("850", Data(exported[2], "startup.time_to_window_ms"));
    }

    [Fact]
    public async Task Export_completes_while_the_queue_is_saturated()
    {
        var (log, _) = Start();
        int count;
        using (log)
        {
            for (var index = 0; index < 5_000; index++)
            {
                log.MarkStartupWindowShown(index);
            }

            count = await log.ExportAsync(Path.Join(_directory, "export.jsonl"));
        }

        Assert.True(count > 0);
        Assert.Equal(count + 1, File.ReadAllLines(Path.Join(_directory, "export.jsonl")).Length);
    }

    [Fact]
    public async Task Clear_deletes_the_log_and_new_events_are_still_recorded()
    {
        var (log, _) = Start();
        using (log)
        {
            // Entries still queued when the user deletes the log must not recreate it afterwards.
            for (var index = 0; index < 200; index++)
            {
                log.MarkStartupWindowShown(index);
            }

            Assert.True(await log.ClearAsync());
            await log.FlushAsync();
            Assert.False(File.Exists(LogPath));

            log.BeginOperation("dictation.session", "dictation");
            await log.FlushAsync();
        }

        Assert.Equal(["dictation.session.begin", "app.exit"], Lines().Select(Event));
    }

    [Fact]
    public async Task Crash_capture_is_written_synchronously()
    {
        var (log, _) = Start();
        await log.FlushAsync();
        log.CaptureCrash(ExceptionSamples.AppThrown("secret crash text"), "app.crash");

        var crash = Assert.Single(Lines(), line => Event(line) == "app.crash");
        Assert.Equal("System.InvalidOperationException", crash.GetProperty("error").GetString());
        Assert.DoesNotContain("secret", File.ReadAllText(LogPath));
        log.Dispose();
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private long _ticks;

        public override DateTimeOffset GetUtcNow() => start + TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }
}

public sealed class CompositeDiagnosticsReporterTests
{
    [Fact]
    public void Each_reporter_gets_every_call_and_one_failure_does_not_stop_the_other()
    {
        var first = new RecordingReporter();
        var second = new RecordingReporter();
        var composite = new CompositeDiagnosticsReporter(new ThrowingReporter(), first, second);

        var operation = composite.BeginOperation("dictation.session", "dictation");
        var child = operation!.StartChild("transcribe");
        operation.SetTag("mode", "Hybrid");
        child!.Finish(DiagnosticsOutcome.Failed);
        operation.Finish(DiagnosticsOutcome.Ok);
        composite.CaptureException(new InvalidOperationException(), "transcription");

        foreach (var reporter in new[] { first, second })
        {
            var session = Assert.Single(reporter.Operations).Value;
            Assert.Equal("Hybrid", session.Tags["mode"]);
            Assert.Equal(DiagnosticsOutcome.Ok, Assert.Single(session.Finishes));
            Assert.Equal(DiagnosticsOutcome.Failed, Assert.Single(Assert.Single(session.Children).Value.Finishes));
        }
    }

    [Fact]
    public void Returns_no_operation_when_no_reporter_is_collecting()
    {
        var composite = new CompositeDiagnosticsReporter(new ThrowingReporter(), new NullReporter());

        Assert.Null(composite.BeginOperation("dictation.session", "dictation"));
    }

    private sealed class ThrowingReporter : IDiagnosticsReporter
    {
        public void CaptureException(Exception exception, string operation, IReadOnlyDictionary<string, string>? tags = null) =>
            throw new InvalidOperationException();

        public IDiagnosticsOperation BeginOperation(string name, string operation) => throw new InvalidOperationException();
    }

    private sealed class NullReporter : IDiagnosticsReporter
    {
        public void CaptureException(Exception exception, string operation, IReadOnlyDictionary<string, string>? tags = null)
        {
        }

        public IDiagnosticsOperation? BeginOperation(string name, string operation) => null;
    }
}
