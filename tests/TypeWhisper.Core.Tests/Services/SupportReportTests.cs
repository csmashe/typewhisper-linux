using System.Text.Json;
using TypeWhisper.Core.Services.Diagnostics;
using TypeWhisper.Tests;
using Report = TypeWhisper.Core.Services.Diagnostics.SupportReport;

namespace TypeWhisper.Core.Tests.Services;

public sealed class SupportReportTests : IDisposable
{
    private readonly string _directory = TestPaths.CreateTempDirectory("TypeWhisper.SupportReportTests");

    public void Dispose() => TestPaths.DeleteDirectory(_directory);

    private static Report Empty() =>
        new(DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null, []);

    [Theory]
    [InlineData("/home/private/model.gguf")]
    [InlineData("models/private.gguf")]
    [InlineData(@"models\private.gguf")]
    [InlineData(@"C:\Users\private\model.gguf")]
    [InlineData("C:private.gguf")]
    [InlineData("./private")]
    [InlineData("../private")]
    [InlineData("private..gguf")]
    [InlineData("~/private")]
    [InlineData("https://private.example/api?key=secret")]
    [InlineData("private sentence")]
    [InlineData("org/model-1.2:latest")]
    [InlineData("-private")]
    [InlineData("")]
    public void Identifiers_omit_paths_urls_and_free_text(string input) =>
        Assert.Equal(Report.Omitted, Report.Identifier(input));

    [Theory]
    [InlineData("model-1.2:latest")]
    [InlineData("com.typewhisper.sherpa-onnx")]
    [InlineData("parakeet-tdt-0.6b")]
    [InlineData("plugin:com.typewhisper.whisper-cpp:ggml-base")]
    [InlineData("en-US")]
    public void Identifiers_keep_technical_ids(string input) => Assert.Equal(input, Report.Identifier(input));

    [Fact]
    public void Identifiers_omit_overlong_values_and_keep_null()
    {
        Assert.Equal(Report.Omitted, Report.Identifier(new string('a', 161)));
        Assert.Null(Report.Identifier(null));
    }

    [Fact]
    public void Failing_section_is_listed_by_type_and_hresult_without_its_message()
    {
        var capture = new SupportReportCapture();
        var profiles = capture.Try<Report.ProfilesInfo>("profiles", () =>
            throw new IOException("Cannot read /home/private-user/profiles.json: private-api-key"));
        var api = capture.Try("api", () => new Report.ApiInfo(true, true, 9876, true));
        var report = Empty() with { Profiles = profiles, Api = api, CollectionErrors = capture.Errors };

        var json = report.ToJson();

        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("api").GetProperty("running").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("profiles", out _));
        var error = Assert.Single(document.RootElement.GetProperty("collectionErrors").EnumerateArray());
        Assert.Equal("profiles", error.GetProperty("section").GetString());
        Assert.Equal("System.IO.IOException", error.GetProperty("errorType").GetString());
        Assert.Matches("^0x[0-9A-F]{8}$", error.GetProperty("hResult").GetString());
        Assert.False(error.TryGetProperty("message", out _));
    }

    [Fact]
    public async Task Failing_async_section_is_listed_too()
    {
        var capture = new SupportReportCapture();

        var log = await capture.TryAsync("diagnosticLog", () =>
            Task.FromException<Report.LogInfo?>(new UnauthorizedAccessException("/home/private")));

        Assert.Null(log);
        Assert.Equal("System.UnauthorizedAccessException", Assert.Single(capture.Errors).ErrorType);
    }

    [Fact]
    public void Disabled_log_exports_state_without_entries()
    {
        var report = Empty() with
        {
            App = new Report.AppInfo("0.17.0", "local", false, 42),
            Log = new Report.LogInfo(false, 7, [new DiagnosticLogLine(DateTimeOffset.UtcNow, "dictation.start")]),
        };

        using var document = JsonDocument.Parse(report.ToJson());

        Assert.Equal("0.17.0", document.RootElement.GetProperty("app").GetProperty("version").GetString());
        Assert.False(document.RootElement.GetProperty("log").GetProperty("enabled").GetBoolean());
        Assert.Empty(document.RootElement.GetProperty("log").GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public void Export_reapplies_log_admission_and_writes_one_valid_document()
    {
        var destination = Path.Join(_directory, "report.json");
        File.WriteAllText(destination, "previous");
        var report = Empty() with
        {
            Log = new Report.LogInfo(true, 7, [
                new DiagnosticLogLine(
                    DateTimeOffset.UtcNow,
                    "dictation.failed",
                    Data: new Dictionary<string, string>
                    {
                        ["transcript"] = "private text",
                        ["engine.provider"] = "com.typewhisper.sherpa-onnx",
                    },
                    Error: "private exception message"
                ),
            ]),
        };

        report.Export(destination);

        var json = File.ReadAllText(destination);
        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("linux", document.RootElement.GetProperty("platform").GetString());
        var entry = Assert.Single(document.RootElement.GetProperty("log").GetProperty("entries").EnumerateArray());
        Assert.Equal("com.typewhisper.sherpa-onnx", entry.GetProperty("data").GetProperty("engine.provider").GetString());
        Assert.False(entry.TryGetProperty("error", out _));
        Assert.Single(Directory.GetFiles(_directory));
    }
}
