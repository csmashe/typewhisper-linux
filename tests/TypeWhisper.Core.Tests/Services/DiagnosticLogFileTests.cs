using System.Text.Json;
using TypeWhisper.Core.Services.Diagnostics;
using TypeWhisper.Tests;

namespace TypeWhisper.Core.Tests.Services;

public sealed class DiagnosticLogFileTests : IDisposable
{
    private static readonly string[] s_paddingKeys =
        ["arch", "cleanup.level", "engine.model", "engine.provider", "install", "mode", "os", "plugin.id", "runtime", "version"];

    private readonly string _directory = TestPaths.CreateTempDirectory("TypeWhisper.DiagnosticLogFileTests");
    private DateTimeOffset _now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private string LogPath => Path.Join(_directory, "Logs", "diagnostics.jsonl");

    private static DiagnosticLogPreferences On(int retentionDays = 7) => new(Enabled: true, RetentionDays: retentionDays);

    private DiagnosticLogFile Log(DiagnosticLogPreferences? preferences = null) => new(LogPath, preferences ?? On(), () => _now);

    private DiagnosticLogLine Line(string name) => new(_now, name);

    private string[] Lines() => File.Exists(LogPath) ? File.ReadAllLines(LogPath) : [];

    private string[] Events() => Lines().Select(json => JsonDocument.Parse(json).RootElement.GetProperty("event").GetString()!).ToArray();

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

    [Fact]
    public void Preferences_default_to_off_and_unoffered_retention_falls_back_to_seven_days()
    {
        Assert.False(new DiagnosticLogPreferences().Enabled);
        Assert.Equal(7, DiagnosticLogPreferences.NormalizeRetentionDays(999));
        Assert.Equal(14, DiagnosticLogPreferences.NormalizeRetentionDays(14));
        Assert.False(Log().Configure(new DiagnosticLogPreferences(true, 5)));
    }

    [Fact]
    public void Disabled_snapshot_does_not_read_a_leftover_log()
    {
        Log().Write(Line("dictation.session.begin"));
        // The deletion may have failed earlier; a disabled log must still expose nothing.
        Assert.Empty(Log(new DiagnosticLogPreferences()).Snapshot());
        Assert.Single(Lines());
    }

    [Fact]
    public void Writes_one_compact_json_line_per_event_with_an_owner_only_file()
    {
        var correlation = Guid.NewGuid();
        Log().Write(new DiagnosticLogLine(_now, "insert.end", correlation, 1234,
            new Dictionary<string, string> { ["outcome"] = "ok", ["duration-ms"] = "12" }));

        using var document = JsonDocument.Parse(Assert.Single(Lines()));
        var root = document.RootElement;
        Assert.Equal("insert.end", root.GetProperty("event").GetString());
        Assert.Equal(correlation, root.GetProperty("correlationId").GetGuid());
        Assert.Equal(1234, root.GetProperty("elapsedMs").GetInt64());
        Assert.Equal("ok", root.GetProperty("data").GetProperty("outcome").GetString());
        Assert.False(root.TryGetProperty("error", out _));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(LogPath));
        }
    }

    [Fact]
    public void Admits_only_content_free_values()
    {
        var line = DiagnosticLogFile.Admit(new DiagnosticLogLine(_now, "Please paste my secret sentence", Guid.Empty, -5,
            new Dictionary<string, string>
            {
                ["engine.model"] = "parakeet-tdt-0.6b",
                ["engine.provider"] = "com.typewhisper.sherpa-onnx",
                ["title"] = "Inbox - Mail",
                ["bad key"] = "x",
                ["text"] = "hunter2",
                ["profile.used"] = "maybe",
                ["transcript.chars"] = "12a",
                ["language"] = "my language",
                ["outcome"] = "ok",
                ["engine.model.path"] = "/home/someone/models/x.bin",
            },
            "System.IO.IOException: /home/someone/notes.txt", "E_FAIL",
            ["TypeWhisper.Linux.App.<OnFrameworkInitializationCompleted>b__0_1", "at /home/someone/file.cs line 3"]));

        Assert.Equal("diagnostics.invalid-event", line.Event);
        Assert.Null(line.CorrelationId);
        Assert.Null(line.ElapsedMs);
        Assert.Equal(new Dictionary<string, string>
        {
            ["engine.model"] = "parakeet-tdt-0.6b",
            ["engine.provider"] = "com.typewhisper.sherpa-onnx",
            ["outcome"] = "ok",
        }, line.Data);
        Assert.Null(line.Error);
        Assert.Null(line.HResult);
        Assert.Equal(["TypeWhisper.Linux.App.<OnFrameworkInitializationCompleted>b__0_1"], line.Stack);
    }

    [Theory]
    [InlineData("engine.model", "/home/someone/model.gguf", false)]
    [InlineData("engine.model", "sk-proj abc", false)]
    [InlineData("engine.model", "gpt-4o-mini-transcribe", true)]
    [InlineData("language", "en", true)]
    [InlineData("language", "de-DE", true)]
    [InlineData("language", "auto", true)]
    [InlineData("language", "Hello there", false)]
    [InlineData("audio.duration_s", "2.5", true)]
    [InlineData("audio.duration_s", "-1", false)]
    [InlineData("profile.used", "true", true)]
    [InlineData("profile.used", "True", false)]
    [InlineData("operation", "plugin.activate", true)]
    [InlineData("operation", "Copy the selection", false)]
    [InlineData("inner", "System.Net.Sockets.SocketException", true)]
    [InlineData("inner", "connection refused by host", false)]
    [InlineData("outcome", "insertion_failed", true)]
    [InlineData("outcome", "Failed: disk full", false)]
    public void Each_admitted_key_checks_its_value_domain(string key, string value, bool admitted)
    {
        var line = DiagnosticLogFile.Admit(new DiagnosticLogLine(_now, "check",
            Data: new Dictionary<string, string> { [key] = value }));

        Assert.Equal(admitted, line.Data?.ContainsKey(key) == true);
    }

    [Fact]
    public void Keeps_exception_types_and_hresults()
    {
        var line = DiagnosticLogFile.Admit(new DiagnosticLogLine(_now, "error.captured",
            Error: "System.Net.Http.HttpRequestException", HResult: "0x80131500"));

        Assert.Equal("System.Net.Http.HttpRequestException", line.Error);
        Assert.Equal("0x80131500", line.HResult);
    }

    [Fact]
    public void Reads_back_lines_with_errors_when_pruning()
    {
        var log = Log();
        log.Write(new DiagnosticLogLine(_now, "error.captured", Error: "System.InvalidOperationException", HResult: "0x80131509"));
        _now = _now.AddHours(2);
        Assert.True(log.Configure(On()));

        using var document = JsonDocument.Parse(Assert.Single(Lines()));
        Assert.Equal("0x80131509", document.RootElement.GetProperty("hresult").GetString());
    }

    [Fact]
    public void Writes_nothing_while_off_and_deletes_the_log_when_turned_off()
    {
        var log = Log();
        log.Write(Line("app.start"));
        Assert.Single(Lines());

        Assert.True(log.Configure(new DiagnosticLogPreferences()));
        Assert.False(File.Exists(LogPath));
        log.Write(Line("app.start"));
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public void Deletes_lines_older_than_the_retention_period()
    {
        var log = Log(On(3));
        log.Write(Line("old"));
        _now = _now.AddDays(2);
        log.Write(Line("recent"));
        _now = _now.AddDays(2);
        log.Write(Line("new"));

        Assert.Equal(["recent", "new"], Events());
    }

    [Fact]
    public void Shorter_retention_prunes_immediately()
    {
        var log = Log(On(30));
        log.Write(Line("old"));
        _now = _now.AddDays(5);
        log.Write(Line("new"));

        Assert.True(log.Configure(On(3)));

        Assert.Equal(["new"], Events());
    }

    [Fact]
    public void Trims_to_the_newest_lines_when_the_file_grows_too_large()
    {
        var log = Log();
        var padding = s_paddingKeys.ToDictionary(key => key, _ => new string('x', 64));
        for (var index = 0; index < 3_000; index++)
        {
            log.Write(new DiagnosticLogLine(_now, "dictation.step",
                Data: new Dictionary<string, string>(padding) { ["transcript.chars"] = index.ToString() }));
        }

        Assert.True(new FileInfo(LogPath).Length <= DiagnosticLogFile.MaximumBytes);
        Assert.Contains("\"transcript.chars\":\"2999\"", Lines()[^1]);
    }

    [Fact]
    public void Export_writes_the_header_and_drops_lines_the_log_did_not_write()
    {
        var log = Log();
        log.Write(Line("dictation.session.begin"));
        File.AppendAllText(LogPath, "{\"time\":\"2026-10-13T12:00:00+00:00\",\"event\":\"dictation.session.begin\",\"data\":{\"text\":\"hello there\"}}\n");
        File.AppendAllText(LogPath, "{\"time\":\"2026-10-13T12:00:00+00:00\",\"event\":\"dictation.session.begin\",\"message\":\"private transcript\"}\n");
        File.AppendAllText(LogPath, "not json\n");
        log.Write(Line("dictation.session.end"));
        var destination = Path.Join(_directory, "export.jsonl");

        var count = log.Export(destination, new DiagnosticLogLine(_now, "diagnostics.export",
            Data: new Dictionary<string, string> { ["version"] = "0.17.0" }));

        Assert.Equal(2, count);
        var exported = File.ReadAllLines(destination);
        Assert.Equal(3, exported.Length);
        Assert.Contains("\"diagnostics.export\"", exported[0]);
        Assert.DoesNotContain(exported, line => line.Contains("hello") || line.Contains("private"));
    }

    [Fact]
    public void Export_of_a_disabled_log_has_only_the_header()
    {
        Log().Write(Line("app.start"));
        var destination = Path.Join(_directory, "export.jsonl");

        Assert.Equal(0, Log(new DiagnosticLogPreferences()).Export(destination, Line("diagnostics.export")));

        Assert.Contains("diagnostics.export", Assert.Single(File.ReadAllLines(destination)));
    }

    [Fact]
    public void Export_reports_an_unreadable_log_instead_of_writing_an_empty_export()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        var log = Log();
        log.Write(Line("app.start"));
        File.SetUnixFileMode(LogPath, UnixFileMode.None);
        var destination = Path.Join(_directory, "export.jsonl");
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => log.Export(destination, Line("diagnostics.export")));
            Assert.False(File.Exists(destination));
        }
        finally
        {
            File.SetUnixFileMode(LogPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void Snapshot_keeps_only_retained_and_admitted_lines()
    {
        var log = Log(On(1));
        log.Write(Line("dictation.old"));
        _now += TimeSpan.FromDays(2);
        log.Write(Line("dictation.current"));
        File.AppendAllText(LogPath, "{\"time\":\"2026-10-15T12:00:00+00:00\",\"event\":\"dictation.secret\",\"message\":\"private transcript\"}\n");

        Assert.Equal("dictation.current", Assert.Single(log.Snapshot()).Event);
    }

    [Fact]
    public void Drops_read_back_lines_with_null_values_instead_of_throwing()
    {
        var log = Log();
        log.Write(Line("app.start"));
        File.AppendAllText(LogPath, "{\"time\":\"2026-10-13T12:00:00+00:00\",\"event\":\"dictation.start\",\"data\":{\"mode\":null}}\n");
        File.AppendAllText(LogPath, "{\"time\":\"2026-10-13T12:00:00+00:00\",\"event\":\"app.crash\",\"stack\":[null]}\n");
        File.AppendAllText(LogPath, "{\"time\":\"2026-10-13T12:00:00+00:00\",\"event\":null}\n");

        Assert.True(log.Configure(On()));
        Assert.Contains("app.start", Assert.Single(Lines()));
        Assert.Equal("diagnostics.invalid-event", DiagnosticLogFile.Admit(new DiagnosticLogLine(_now, null!,
            Data: new Dictionary<string, string> { ["mode"] = null! }, Stack: [null!])).Event);
    }

    [Fact]
    public void Drops_lines_written_with_a_local_offset()
    {
        var log = Log();
        File.AppendAllText(LogPath.EnsureDirectory(), "{\"time\":\"2026-10-13T14:00:00+02:00\",\"event\":\"app.start\"}\n");

        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public void Reads_only_the_newest_part_of_an_oversized_log()
    {
        var log = Log();
        log.Write(Line("old"));
        File.AppendAllText(LogPath, new string('x', 3 * 1024 * 1024) + "\n");
        File.AppendAllText(LogPath, "{\"time\":\"2026-10-13T12:00:00+00:00\",\"event\":\"recent\"}\n");

        Assert.True(log.Configure(On()));
        Assert.Equal(["recent"], Events());
    }

    [Fact]
    public void Prunes_expired_entries_without_new_writes_once_due()
    {
        var log = Log(On(1));
        log.Write(Line("old"));
        _now = _now.AddMinutes(30);
        log.PruneIfDue();
        Assert.Single(Lines());

        _now = _now.AddDays(2);
        log.PruneIfDue();
        Assert.Empty(Lines());
    }

    [Fact]
    public void Clear_deletes_every_line_and_logging_continues()
    {
        var log = Log();
        log.Write(Line("app.start"));
        Assert.True(log.Clear());
        Assert.False(File.Exists(LogPath));

        log.Write(Line("app.window-shown"));
        Assert.Equal(["app.window-shown"], Events());
    }
}

internal static class DiagnosticLogFileTestPaths
{
    public static string EnsureDirectory(this string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
