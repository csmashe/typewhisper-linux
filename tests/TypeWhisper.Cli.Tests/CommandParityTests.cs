using System.Net;
using TypeWhisper.Cli.Commands;
using TypeWhisper.Cli.Models;
using TypeWhisper.Cli.Output;
using TypeWhisper.Cli.Services;
using Xunit;

namespace TypeWhisper.Cli.Tests;

public sealed class CommandParityTests
{
    [Theory]
    [InlineData("history search hello --limit 3 --offset 2", "GET /v1/history?limit=3&offset=2&q=hello", "", "{\"records\":[]}", "No history entries.")]
    [InlineData("history --query hello", "GET /v1/history?limit=50&offset=0&q=hello", "", "{\"records\":[]}", "No history entries.")]
    [InlineData("history last", "GET /v1/history?limit=1&offset=0", "", "{\"records\":[{\"timestamp\":\"today\",\"text\":\"latest\"}]}", "latest")]
    [InlineData("last", "GET /v1/history?limit=1&offset=0", "", "{\"records\":[{\"text\":\"latest\"}]}", "latest")]
    [InlineData("history", "GET /v1/history?limit=50&offset=0", "", "{\"records\":[{\"timestamp\":\"today\",\"text\":\"hello\"},{\"timestamp\":\"yesterday\",\"text\":\"world\"}]}", "today  hello\nyesterday  world")]
    [InlineData("dictation start", "POST /v1/dictation/start", "", "{\"session_id\":7}", "started: 7")]
    [InlineData("dictation stop", "POST /v1/dictation/stop", "", "{\"stopped\":true}", "stopped")]
    [InlineData("dictation status", "GET /v1/dictation/status", "", "{\"state\":\"recording\"}", "recording")]
    [InlineData("dictation result 7", "GET /v1/dictation/transcription?sessionId=7", "", "{\"state\":\"completed\",\"text\":\"hello\"}", "hello")]
    [InlineData("models load --engine e --model m", "POST /v1/models/load", "{\"engine\":\"e\",\"model\":\"m\"}", "{\"status\":\"ready\",\"engine\":\"e\",\"model\":\"m\"}", "ready: e m")]
    [InlineData("models load --engine e", "POST /v1/models/load", "{\"engine\":\"e\"}", "{\"status\":\"ready\",\"engine\":\"e\"}", "ready: e")]
    [InlineData("models unload --engine e", "POST /v1/models/unload", "{\"engine\":\"e\"}", "{\"status\":\"unloaded\",\"engine\":\"e\",\"model\":\"m\"}", "unloaded: e m")]
    [InlineData("models unload", "POST /v1/models/unload", "{}", "{\"status\":\"unloaded\",\"engine\":null,\"model\":null}", "unloaded:")]
    [InlineData("models delete --engine e --model m", "DELETE /v1/models?engine=e&model=m", "", "{\"status\":\"deleted\",\"engine\":\"e\",\"model\":\"m\"}", "deleted: e m")]
    [InlineData("models list", "GET /v1/models", "", "{\"models\":[]}", "No models available.")]
    public async Task WireContractAndOutput(string args, string line, string body, string response, string plain)
    {
        foreach (var json in new[] { false, true })
        {
            await using var stub = new UnixHttpStub(responseBody: response);
            var result = await RunAsync(stub.SocketPath, args + (json ? " --json" : ""));
            var request = await stub.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, result.Code);
            Assert.Equal("", result.Error);
            Assert.Equal(line + " HTTP/1.1", request.RequestLine);
            Assert.Equal(body, request.Body);
            if (body.Length > 0) Assert.StartsWith("application/json", request.Headers["Content-Type"]);
            Assert.Equal((json ? JsonFormatting.PrettyJson(response) : plain.Replace("\n", Environment.NewLine)) + Environment.NewLine, result.Output);
        }
    }

    [Theory]
    [InlineData("history", "{\"records\":[]}", "No history entries.")]
    [InlineData("history last", "{\"records\":[]}", "No history entries.")]
    [InlineData("last", "{\"records\":[]}", "No history entries.")]
    [InlineData("dictation result 7", "{\"state\":\"in_progress\"}", "in_progress")]
    [InlineData("dictation result 7", "{\"state\":\"not_found\"}", "not_found")]
    [InlineData("dictation result 7", "{\"state\":\"completed\",\"text\":\"\"}", "completed")]
    [InlineData("dictation result 7", "{\"state\":\"canceled\",\"text\":\"\",\"message\":\"Canceled\"}", "canceled: Canceled")]
    [InlineData("dictation result 7", "{\"state\":\"discarded\",\"text\":\"\",\"message\":\"No speech detected.\"}", "discarded: No speech detected.")]
    public async Task EmptyOrPendingOutput(string args, string body, string expected)
    {
        await using var stub = new UnixHttpStub(responseBody: body);
        var result = await RunAsync(stub.SocketPath, args);
        Assert.Equal(0, result.Code);
        Assert.Equal(expected + Environment.NewLine, result.Output);
    }

    [Theory]
    [InlineData("{\"state\":\"failed\",\"error\":\"bad audio\"}", "bad audio")]
    [InlineData("{\"state\":\"failed\",\"message\":\"recording failed\"}", "recording failed")]
    [InlineData("{\"state\":\"failed\"}", "Dictation failed.")]
    public async Task FailedDictationReturnsServerError(string body, string expected)
    {
        await using var stub = new UnixHttpStub(responseBody: body);
        var result = await RunAsync(stub.SocketPath, "dictation result 7");
        Assert.Equal(3, result.Code);
        Assert.Contains(expected, result.Error);
        Assert.Empty(result.Output);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("models")]
    [InlineData("history")]
    [InlineData("last")]
    [InlineData("dictation start")]
    [InlineData("dictation stop")]
    [InlineData("dictation status")]
    [InlineData("dictation result 7")]
    [InlineData("models load --engine e")]
    [InlineData("models unload")]
    [InlineData("models delete --engine e --model m")]
    [InlineData("transcribe -")]
    public async Task FailureExitCodesAreConsistent(string args)
    {
        await using (var stub = new UnixHttpStub(HttpStatusCode.InternalServerError, "{\"error\":\"server failed\"}"))
        {
            var result = await RunAsync(stub.SocketPath, args);
            Assert.Equal(3, result.Code);
            Assert.Contains("server failed", result.Error);
        }
        await using (var stub = new UnixHttpStub(responseBody: "not json"))
        {
            var result = await RunAsync(stub.SocketPath, args);
            Assert.Equal(3, result.Code);
            Assert.Contains("Protocol error", result.Error);
        }
        var missing = await RunAsync(Path.Join(Path.GetTempPath(), Guid.NewGuid() + ".sock"), args);
        Assert.Equal(2, missing.Code);
        await using (var stub = new UnixHttpStub { StallResponse = true })
        {
            var timeout = await RunAsync(stub.SocketPath, args);
            Assert.Equal(2, timeout.Code);
            Assert.Contains(args.StartsWith("transcribe") ? "timed out" : "did not respond", timeout.Error);
            var cancelled = await RunAsync(stub.SocketPath, args, ct: new CancellationToken(true));
            Assert.Equal(1, cancelled.Code);
            Assert.Contains("Cancelled.", cancelled.Error);
        }
        await using (var stub = new UnixHttpStub())
        {
            var mismatch = await RunAsync(stub.SocketPath, args, validateServer: false);
            Assert.Equal(2, mismatch.Code);
            Assert.Equal(0, stub.RequestCount);
        }
    }

    [Theory]
    [InlineData("history", "{}")]
    [InlineData("history", "{\"records\":{}}")]
    [InlineData("history", "{\"records\":[3]}")]
    [InlineData("dictation start", "{}")]
    [InlineData("dictation start", "{\"session_id\":\"7\"}")]
    [InlineData("dictation start", "{\"session_id\":1.5}")]
    [InlineData("dictation status", "{}")]
    [InlineData("dictation result 7", "{\"state\":3}")]
    [InlineData("models load --engine e", "{\"status\":\"ready\"}")]
    [InlineData("models unload", "{\"engine\":\"e\"}")]
    [InlineData("models delete --engine e --model m", "{\"status\":1,\"engine\":\"e\"}")]
    public async Task InvalidShapeIsProtocolError(string args, string body)
    {
        await using var stub = new UnixHttpStub(responseBody: body);
        var result = await RunAsync(stub.SocketPath, args + " --json");
        Assert.Equal(3, result.Code);
        Assert.Contains("Protocol error", result.Error);
        Assert.Empty(result.Output);
    }

    [Theory]
    [InlineData("history --limit 0 --offset 2147483647")]
    [InlineData("history --limit 200")]
    [InlineData("dictation result 2147483647")]
    [InlineData("models list")]
    [InlineData("models unload")]
    [InlineData("transcribe --no-corrections")]
    public void BoundaryFormsParse(string args) => Assert.Null(CliOptions.Parse(args.Split(' ')).ErrorMessage);

    private static async Task<Result> RunAsync(string path, string args, bool validateServer = true, CancellationToken ct = default)
    {
        var options = CliOptions.Parse(args.Split(' '));
        Assert.Null(options.ErrorMessage);
        var api = new ApiClient(path, null, _ => validateServer);
        using var stdin = new MemoryStream("RIFF....WAVEaudio"u8.ToArray());
        var budget = TimeSpan.FromMilliseconds(150);
        var originalOut = Console.Out;
        var originalError = Console.Error;
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            // Unconditional test backstop: forwarding an already-cancelled ct would abort the wait before the command could report "Cancelled.".
            var code = await (options.Command switch
            {
                "history" or "last" => HistoryCommand.RunAsync(api, options, ct, budget),
                "dictation" => DictationCommand.RunAsync(api, options, ct, budget),
                "models" => ModelsCommand.RunAsync(api, options, ct, budget),
                "status" => StatusCommand.RunAsync(api, options.Json, ct, budget),
                "transcribe" => TranscribeCommand.RunAsync(api, options, stdin, budget, ct: ct),
                _ => throw new InvalidOperationException(),
            }).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            return new Result(code, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            api.Http.Dispose();
            api.TranscribeHttp.Dispose();
        }
    }

    private sealed record Result(int Code, string Output, string Error);
}
