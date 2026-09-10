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
        var error = await CaptureErrorAsync(["status"]);

        Assert.Contains("TypeWhisper API socket not found", error, StringComparison.Ordinal);
    }

    private static async Task<string> CaptureErrorAsync(string[] args)
    {
        var originalError = Console.Error;
        await using var writer = new StringWriter();
        try
        {
            Console.SetError(writer);
            Assert.Equal(1, await Program.RunAsync(args));
        }
        finally
        {
            Console.SetError(originalError);
        }

        return writer.ToString();
    }
}
