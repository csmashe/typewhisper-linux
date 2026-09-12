using Sentry.Protocol;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using TypeWhisper.Linux.Services.Telemetry;
using Xunit;

namespace TypeWhisper.Linux.Tests.Telemetry;

public sealed class TelemetryScrubberTests
{
    private static string Scrub(string? text)
    {
        return TelemetryScrubber.ScrubText(text, "/home/alice", "alice", "alice-desktop");
    }

    [Theory]
    [InlineData("/home/alice/x", "~/x")]
    [InlineData("/home/alice", "~")]
    [InlineData("/home/bob/x", "/home/<user>/x")]
    [InlineData("/home/bob", "/home/<user>")]
    [InlineData("alice malice alice-desktop", "<user> malice <host>")]
    [InlineData("alice@example.com", "<email>")]
    [InlineData("bob@example.com 192.168.1.20", "<email> <ip>")]
    [InlineData("Bearer alice", "Bearer <redacted>")]
    [InlineData("wss://example.com/socket?q=private", "wss://example.com/socket?<redacted>")]
    [InlineData("ftp://example.com/file?q=private", "ftp://example.com/file?<redacted>")]
    [InlineData("Bearer abcdef1234567890", "Bearer <redacted>")]
    [InlineData("https://example.com/x?q=private&x=1 next", "https://example.com/x?<redacted> next")]
    [InlineData("sk-abcdefghijklmnop", "<redacted>")]
    [InlineData("gsk_abcdefghijklmnop", "<redacted>")]
    [InlineData("AIzaabcdefghijklmnopqrst", "<redacted>")]
    [InlineData("xai-abcdefghijklmnop", "<redacted>")]
    [InlineData("apiKey=abcdef", "<redacted>")]
    [InlineData("api-key: abcdef", "<redacted>")]
    [InlineData("token=abcdef secret: abcdef password=abcdef", "<redacted> <redacted> <redacted>")]
    [InlineData("{\"token\":\"opaque-value\",\"model\":\"x\"}", "{\"token\":\"<redacted>\",\"model\":\"x\"}")]
    [InlineData("Authorization: Basic abc123", "Authorization: Basic <redacted>")]
    [InlineData(null, "")]
    public void Text_redacts_identifiers_and_secrets(string? input, string expected)
    {
        Assert.Equal(expected, Scrub(input));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Automatic_exception_breadcrumb_redacts_all_messages(bool runtimeAuthored)
    {
        Exception original = runtimeAuthored
            ? ExceptionSamples.RuntimeFileFailure(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            : ExceptionSamples.AppThrown("transcript: the quick brown fox");
        var breadcrumb = new Breadcrumb(message: original.Message, type: "default",
            data: new Dictionary<string, string> { ["exception_message"] = original.Message }, category: "Exception");

        var scrubbed = TelemetryScrubber.ScrubExceptionBreadcrumb(breadcrumb, new SentryHint("exception", original));

        Assert.Equal("<redacted>", scrubbed!.Message);
        Assert.Equal("<redacted>", scrubbed.Data!["exception_message"]);
        Assert.Equal("Exception", scrubbed.Category);
        Assert.Equal(breadcrumb.Type, scrubbed.Type);
        Assert.Equal(breadcrumb.Level, scrubbed.Level);
        Assert.Equal(breadcrumb.Timestamp, scrubbed.Timestamp);
    }

    [Fact]
    public void App_authored_exception_message_is_redacted_without_changing_metadata()
    {
        var original = ExceptionSamples.AppThrown("transcript: the quick brown fox");
        Assert.NotNull(original.TargetSite);
        var exception = ToSentryException(original);
        exception.Module = "test-module";
        exception.ThreadId = 42;
        exception.Mechanism = new Mechanism
        {
            Type = "test",
        };
        var mechanism = exception.Mechanism;
        var e = new SentryEvent(original)
        {
            SentryExceptions = [exception],
        };
        TelemetryScrubber.Scrub(e, Scrub);
        Assert.Equal("<redacted>", exception.Value);
        Assert.Equal(typeof(InvalidOperationException).FullName, exception.Type);
        Assert.Equal("test-module", exception.Module);
        Assert.Equal(42, exception.ThreadId);
        Assert.Same(mechanism, exception.Mechanism);
    }

    [Fact]
    public void Runtime_authored_exception_message_is_redacted()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var original = ExceptionSamples.RuntimeFileFailure(home);
        var exception = ToSentryException(original);
        var e = new SentryEvent(original)
        {
            SentryExceptions = [exception],
        };
        TelemetryScrubber.Scrub(e);
        Assert.Equal("<redacted>", exception.Value);
    }

    [Fact]
    public void Unthrown_inner_exception_message_is_redacted()
    {
        var original = new Exception("outer", new InvalidOperationException("secret body"));
        var inner = ToSentryException(original.InnerException!);
        var e = new SentryEvent(original)
        {
            SentryExceptions = [ToSentryException(original), inner],
        };
        TelemetryScrubber.Scrub(e, Scrub);
        Assert.All(e.SentryExceptions, exception => Assert.Equal("<redacted>", exception.Value));
        Assert.Equal("<redacted>", inner.Value);
    }

    [Fact]
    public void Aggregate_redacts_runtime_and_app_authored_messages()
    {
        var runtime = ExceptionSamples.RuntimeFileFailure(Path.GetTempPath());
        var app = ExceptionSamples.AppThrown("transcript: the quick brown fox");
        var original = new AggregateException(app, new Exception("outer", new AggregateException(runtime)));
        var runtimeEntry = ToSentryException(runtime);
        var appEntry = ToSentryException(app);
        var e = new SentryEvent(original)
        {
            SentryExceptions = [runtimeEntry, appEntry],
        };
        TelemetryScrubber.Scrub(e, Scrub);
        Assert.Equal("<redacted>", runtimeEntry.Value);
        Assert.Equal("<redacted>", appEntry.Value);
    }

    [Fact]
    public void Exception_walk_stops_at_depth_limit()
    {
        var inner = new HttpRequestException("secret", null, HttpStatusCode.Unauthorized);
        Exception original = inner;
        for (var i = 0; i < 32; i++)
        {
            original = new Exception("outer", original);
        }

        var entry = ToSentryException(inner);
        var e = new SentryEvent(original)
        {
            SentryExceptions = [entry],
        };
        TelemetryScrubber.Scrub(e, Scrub);
        Assert.Equal("<redacted>", entry.Value);
        Assert.False(e.Tags.ContainsKey("http.status"));
        Assert.False(e.Tags.ContainsKey("error.inner_type"));
        Assert.Equal($"0x{original.HResult:X8}", e.Tags["error.hresult"]);
    }

    [Fact]
    public void Breadcrumb_without_exception_hint_uses_normal_text_scrubbing()
    {
        var breadcrumb = new Breadcrumb(message: "Bearer secret", type: "default",
            data: new Dictionary<string, string> { ["token"] = "Bearer secret" });

        var scrubbed = TelemetryScrubber.ScrubExceptionBreadcrumb(breadcrumb, new SentryHint());

        Assert.Equal("Bearer <redacted>", scrubbed!.Message);
        Assert.Equal("Bearer <redacted>", scrubbed.Data!["token"]);
    }

    [Fact]
    public void Wrapped_http_failure_reports_only_structured_error_facts()
    {
        var inner = new HttpRequestException("secret response body", inner: null,
            statusCode: HttpStatusCode.Unauthorized)
        {
            Data = { ["secret"] = "private data" },
        };
        var original = new InvalidOperationException("secret transcript", inner);
        var e = new SentryEvent(original);

        TelemetryScrubber.Scrub(e, Scrub);

        Assert.Equal(3, e.Tags.Count);
        Assert.Equal("401", e.Tags["http.status"]);
        Assert.Equal(typeof(HttpRequestException).FullName, e.Tags["error.inner_type"]);
        Assert.Equal($"0x{original.HResult:X8}", e.Tags["error.hresult"]);
    }

    [Fact]
    public void Socket_failure_reports_error_code_and_outer_hresult()
    {
        var original = new SocketException((int)SocketError.ConnectionRefused);
        var e = new SentryEvent(original);

        TelemetryScrubber.Scrub(e, Scrub);

        Assert.Equal(2, e.Tags.Count);
        Assert.Equal("ConnectionRefused", e.Tags["socket.error"]);
        Assert.Equal($"0x{original.HResult:X8}", e.Tags["error.hresult"]);
    }

    [Fact]
    public void Aggregate_error_facts_use_first_codes_and_deepest_non_aggregate_type()
    {
        var first = new HttpRequestException("first", new SocketException((int)SocketError.ConnectionRefused),
            HttpStatusCode.Unauthorized);
        var later = new HttpRequestException("later", new SocketException((int)SocketError.TimedOut),
            HttpStatusCode.ServiceUnavailable);
        var deepest = new InvalidOperationException("outer", new IOException("inner"));
        var original = new AggregateException(first, later, new AggregateException(deepest));
        var e = new SentryEvent(original);

        TelemetryScrubber.Scrub(e, Scrub);

        Assert.Equal(4, e.Tags.Count);
        Assert.Equal("401", e.Tags["http.status"]);
        Assert.Equal("ConnectionRefused", e.Tags["socket.error"]);
        Assert.Equal(typeof(IOException).FullName, e.Tags["error.inner_type"]);
        Assert.Equal($"0x{original.HResult:X8}", e.Tags["error.hresult"]);
    }

    [Fact]
    public void Equally_deep_inner_types_use_first_match()
    {
        var e = new SentryEvent(new AggregateException(new IOException("first"), new Exception("later")));

        TelemetryScrubber.Scrub(e, Scrub);

        Assert.Equal(typeof(IOException).FullName, e.Tags["error.inner_type"]);
    }

    [Fact]
    public void Event_without_original_exception_redacts_hand_added_exception()
    {
        var entry = new SentryException
        {
            Type = "System.IO.IOException",
            Value = "secret body",
        };
        var e = new SentryEvent
        {
            SentryExceptions = [entry],
        };
        TelemetryScrubber.Scrub(e, Scrub);
        Assert.Equal("<redacted>", entry.Value);
        Assert.Empty(e.Tags);
    }

    private static SentryException ToSentryException(Exception exception)
    {
        return new SentryException
        {
            Type = exception.GetType().FullName,
            Value = exception.Message,
        };
    }

    [Fact]
    public void Event_scrubs_all_surfaces_and_clears_identity()
    {
        var frame = new SentryStackFrame
        {
            FileName = "/home/alice/x.cs",
            AbsolutePath = "/home/alice/x.cs",
            Module = "/home/alice/module",
            Package = "/home/alice/package",
            Function = "Keep.Function",
        };
        var stack = new SentryStackTrace();
        stack.Frames.Add(frame);
        var exception = new SentryException
        {
            Value = "cannot open /home/alice/x",
            Stacktrace = stack,
        };
        var e = new SentryEvent
        {
            ServerName = "alice-desktop",
            Contexts =
            {
                ["private"] = "transcript",
                OperatingSystem = { Name = "Linux" },
                Device =
                {
                    Name = "alice-desktop",
                    DeviceUniqueIdentifier = "persistent",
                    Timezone = TimeZoneInfo.Utc,
                    BootTime = DateTimeOffset.UtcNow,
                },
                App =
                {
                    Hash = "persistent",
                    Identifier = "persistent",
                },
            },
            User = new SentryUser
            {
                Id = "persistent",
                Username = "alice",
                Email = "alice@example.com",
                IpAddress = "{{auto}}",
            },
            Request = new SentryRequest
            {
                Url = "https://private.example.com",
                Data = "prompt",
            },
            Message = new SentryMessage
            {
                Message = "/home/alice/x",
                Formatted = "alice",
                Params = ["/home/alice/x", 42],
            },
            SentryExceptions = [exception],
            SentryThreads = [new SentryThread
            {
                Stacktrace = stack,
            },

            ],
            DebugImages = [new DebugImage
            {
                CodeFile = "/home/alice/app",
                DebugFile = "/home/alice/app.pdb",
            },

            ],
        };
        e.SetExtra("private", "transcript");
        e.SetTag("file", "/home/alice/x");
        e.AddBreadcrumb("/home/alice/x", data: new Dictionary<string, string> { ["file"] = "/home/alice/x" });
        Assert.Same(e, TelemetryScrubber.Scrub(e, Scrub));
        Assert.Null(e.ServerName);
        Assert.Null(e.User.Id);
        Assert.Null(e.User.Username);
        Assert.Null(e.User.Email);
        Assert.Null(e.User.IpAddress);
        Assert.Null(e.Request.Url);
        Assert.Null(e.Request.Data);
        Assert.False(e.Contexts.ContainsKey("private"));
        Assert.Equal("Linux", e.Contexts.OperatingSystem.Name);
        Assert.Null(e.Contexts.Device.Name);
        Assert.Null(e.Contexts.Device.DeviceUniqueIdentifier);
        Assert.Null(e.Contexts.Device.Timezone);
        Assert.Null(e.Contexts.Device.BootTime);
        Assert.Null(e.Contexts.App.Hash);
        Assert.Null(e.Contexts.App.Identifier);
        Assert.Empty(e.Extra);
        Assert.Equal("~/x", e.Message.Message);
        Assert.Equal("<user>", e.Message.Formatted);
        Assert.Equal(["~/x", 42], e.Message.Params);
        Assert.Equal("<redacted>", exception.Value);
        Assert.Equal("~/x.cs", frame.FileName);
        Assert.Equal("~/x.cs", frame.AbsolutePath);
        Assert.Equal("~/module", frame.Module);
        Assert.Equal("~/package", frame.Package);
        Assert.Equal("Keep.Function", frame.Function);
        Assert.Equal("~/x", e.Tags["file"]);
        Assert.Equal("~/x", Assert.Single(e.Breadcrumbs).Message);
        Assert.Equal("~/x", Assert.Single(e.Breadcrumbs).Data!["file"]);
        Assert.Equal("~/app", Assert.Single(e.DebugImages).CodeFile);
        Assert.Equal("~/app.pdb", Assert.Single(e.DebugImages).DebugFile);
    }

    [Fact]
    public void Transaction_scrubs_name_spans_and_common_fields()
    {
        using var json = JsonDocument.Parse("""
            {"event_id":"01234567890123456789012345678901","transaction":"/home/alice/start",
             "start_timestamp":"2026-09-12T00:00:00Z",
             "contexts":{"trace":{"trace_id":"01234567890123456789012345678901",
             "span_id":"0123456789012345","op":"dictation"},"private":"prompt"},
             "spans":[{"trace_id":"01234567890123456789012345678901","span_id":"0123456789012346","op":"transcribe",
             "start_timestamp":"2026-09-12T00:00:00Z","description":"/home/alice/x",
             "data":{"path":"/home/alice/x"},"tags":{"host":"alice-desktop"}}]}
            """);
        var t = SentryTransaction.FromJson(json.RootElement);
        t.User = new SentryUser
        {
            Id = "persistent",
            IpAddress = "{{auto}}",
        };
        t.Request.Url = "private";
        t.SetData("private", "transcript");
        t.AddBreadcrumb("/home/alice/x");
        t.SetTag("path", "/home/alice/x");
        TelemetryScrubber.Scrub(t, Scrub);
        Assert.Equal("~/start", t.Name);
        Assert.Null(t.User.Id);
        Assert.Null(t.User.IpAddress);
        Assert.Null(t.Request.Url);
        Assert.Empty(t.Data);
        Assert.False(t.Contexts.ContainsKey("private"));
        Assert.Equal("~/x", t.Tags["path"]);
        Assert.Equal("~/x", Assert.Single(t.Breadcrumbs).Message);
        var span = Assert.Single(t.Spans);
        Assert.Equal("~/x", span.Description);
        Assert.Equal("~/x", span.Data["path"]);
        Assert.Equal("<host>", span.Tags["host"]);
    }

    [Fact]
    public void Context_key_matching_is_case_insensitive()
    {
        var e = new SentryEvent();
        var device = new Device
        {
            Name = "alice-desktop",
            DeviceUniqueIdentifier = "persistent",
        };
        var app = new Sentry.Protocol.App
        {
            Identifier = "persistent",
            Hash = "persistent",
        };
        e.Contexts["DEVICE"] = device;
        e.Contexts["APP"] = app;
        TelemetryScrubber.Scrub(e, Scrub);
        Assert.True(e.Contexts.ContainsKey("DEVICE"));
        Assert.True(e.Contexts.ContainsKey("APP"));
        Assert.Null(device.Name);
        Assert.Null(device.DeviceUniqueIdentifier);
        Assert.Null(app.Identifier);
        Assert.Null(app.Hash);
    }

    [Fact]
    public void Null_and_empty_surfaces_are_safe()
    {
        Assert.Null(TelemetryScrubber.Scrub((SentryEvent?)null));
        Assert.Null(TelemetryScrubber.Scrub((SentryTransaction?)null));
        Assert.Null(TelemetryScrubber.Scrub((Breadcrumb?)null));
        Assert.NotNull(TelemetryScrubber.Scrub(new SentryEvent()));
        Assert.NotNull(TelemetryScrubber.Scrub(new SentryTransaction("test", "test")));
        Assert.Equal("", TelemetryScrubber.ScrubText(null, null, null, null));
        Assert.Equal("/x", TelemetryScrubber.ScrubText("/x", "/", null, null));
        Assert.Equal("ab", TelemetryScrubber.ScrubText("ab", null, "ab", "ab"));
    }
}
