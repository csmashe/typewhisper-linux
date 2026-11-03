using Xunit;

namespace TypeWhisper.Cli.Tests;

/// <summary>Dispatch runs against an empty XDG_CONFIG_HOME, so no discovery file exists.</summary>
public sealed class ProgramDispatchTests : IDisposable
{
    private readonly string _configHome =
        Path.Join(Path.GetTempPath(), "typewhisper-cli-dispatch-" + Guid.NewGuid().ToString("N"));
    private readonly string? _originalConfigHome =
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

    public ProgramDispatchTests()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _configHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _originalConfigHome);
        if (Directory.Exists(_configHome))
        {
            Directory.Delete(_configHome, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownCommand_ReportsItselfWithoutRequiringTheSocket()
    {
        var error = await CaptureErrorAsync(["typo"]);

        Assert.Contains("Unknown command: typo", error, StringComparison.Ordinal);
        Assert.DoesNotContain("socket", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KnownCommand_ReportsTheMissingSocket()
    {
        var error = await CaptureErrorAsync(["status"], 2);

        Assert.Contains("TypeWhisper API socket not found", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("history search x --query y", "cannot be used together")]
    [InlineData("history --limit 201", "between 0 and 200")]
    [InlineData("history --offset -1", "non-negative")]
    [InlineData("dictation result abc", "positive integer")]
    [InlineData("dictation result 0", "positive integer")]
    [InlineData("dictation stop extra", "Unexpected operand")]
    [InlineData("status --no-corrections", "Option '--no-corrections' is not valid for 'status'.")]
    [InlineData("models load", "requires --engine")]
    [InlineData("models delete --engine e", "requires --model")]
    [InlineData("models unload --model m", "Option '--model' is not valid for 'models'.")]
    [InlineData("last --limit 1", "Option '--limit' is not valid for 'last'.")]
    [InlineData("history last --query x", "Option '--query' is not valid for 'history'.")]
    [InlineData("transcribe --response-format srt --json", "cannot be combined")]
    [InlineData("dictation start --workflow x", "Unknown option '--workflow'.")]
    public async Task InvalidGrammar_FailsBeforeDiscovery(string command, string message)
    {
        var error = await CaptureErrorAsync(command.Split(' '));
        Assert.Contains(message, error);
        Assert.DoesNotContain("socket", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranscribeWithoutOperandOrRedirectedInput_FailsBeforeDiscovery()
    {
        var originalError = Console.Error;
        await using var error = new StringWriter();
        try
        {
            Console.SetError(error);
            Assert.Equal(1, await Program.RunAsync(["transcribe"], () => false));
            Assert.Contains("Provide an audio file or pipe audio to stdin.", error.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    private static async Task<string> CaptureErrorAsync(string[] args, int expectedExitCode = 1)
    {
        var originalError = Console.Error;
        await using var writer = new StringWriter();
        try
        {
            Console.SetError(writer);
            Assert.Equal(expectedExitCode, await Program.RunAsync(args));
        }
        finally
        {
            Console.SetError(originalError);
        }

        return writer.ToString();
    }
}
